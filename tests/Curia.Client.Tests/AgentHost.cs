#pragma warning disable CA1416 // the profile and kept-act files are mode 0600 and 0700: the probes read and set Unix modes, as PrivateFiles does

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>One request the recording Forum received.</summary>
internal sealed record SeenRequest(string Method, string Path, string Query, string? Body);

/// <summary>What one run of the real <c>curia</c> command did on a host (errata G59): the streams, kept apart, the requests the Forum saw and the profiles the run acted as.</summary>
internal sealed record AgentRun(int Exit, string Stdout, string Stderr, IReadOnlyList<SeenRequest> Requests, IReadOnlyList<string> ActedAs)
{
    internal string Text => CliRun.Fold(Stdout + " " + Stderr);
}

/// <summary>
/// A host with several profiles in a store of its own and a Forum that is a recording stub (host C of errata G59's probes): every request is recorded, the token endpoint answers
/// like a Forum, a post is answered <c>503</c> and any other path <c>404</c>. A profile is spelled exactly as it was created (plan ruling 29), and no two names differ only in case.
/// <see cref="RunAsync"/> runs <c>Program.RunAsync</c> in process with <c>CURIA_CLIENT_HOME</c> pointing at the host: the caller is in the <c>console</c> collection, the streams
/// and the environment being process-wide, and stdout and stderr are captured apart.
/// </summary>
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The host owns what it builds and disposes it with itself.")]
internal sealed class AgentHost : IDisposable
{
    internal static readonly Uri ForumAddress = new("http://forum.test");

    private readonly AgentHostForum _forum = new();
    private readonly List<EnrolledAgent> _agents = [];

    internal AgentHost(params string[] names)
    {
        Home = Directory.CreateTempSubdirectory("curia-agent-host-").FullName;
        Store = new ProfileStore(Home);
        Names = names;
        foreach (var name in names)
        {
            Assert.True(Store.Create(name, AgentIdOf(name), name + "-1", ForumAddress).TryGetValue(out var agent, out var error), error?.Type);
            _agents.Add(agent!);
        }
    }

    private AgentHost(string home, string[] names)
    {
        Home = home;
        Store = new ProfileStore(home);
        Names = names;
    }

    internal string Home { get; }

    /// <summary>
    /// A copy of the host as it stands (every profile's files, kept acts included) in a directory of its own, for running a command that changes state, such as a discard, without
    /// changing the host a later scenario runs on. A clone holds no key in process: it can be run, not planted in.
    /// </summary>
    internal AgentHost Clone()
    {
        var home = Directory.CreateTempSubdirectory("curia-agent-host-").FullName;
        foreach (var directory in Directory.EnumerateDirectories(Home, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(home, Path.GetRelativePath(Home, directory)));
        foreach (var file in Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(home, Path.GetRelativePath(Home, file));
            File.Copy(file, target);
            File.SetUnixFileMode(target, File.GetUnixFileMode(file));
        }

        return new AgentHost(home, Names);
    }

    internal ProfileStore Store { get; }

    internal string[] Names { get; private set; }

    /// <summary>Replaces the Forum's answer to a path other than the token endpoint and <c>POST /v1/posts</c>.</summary>
    internal Func<HttpRequestMessage, HttpResponseMessage>? OnOther { get => _forum.OnOther; set => _forum.OnOther = value; }

    /// <summary>Replaces the Forum's answer to <c>POST /v1/posts</c> (the request and the body it carried); the default is <c>503 curia/store/unavailable</c>.</summary>
    internal Func<HttpRequestMessage, string?, HttpResponseMessage>? OnPost { get => _forum.OnPost; set => _forum.OnPost = value; }

    /// <summary>Adds a profile whose registered key is held by <paramref name="signer"/> (R11.20), after the host's own: a draft signed as it is a signature the signer counts.</summary>
    internal void AddHeld(string name, TestSigner signer)
    {
        Assert.True(ExternalSigner.Describe(signer.Command).TryGetValue(out var described, out var describeError), describeError?.Type);
        Assert.True(Store.Create(name, AgentIdOf(name), ForumAddress, described!).TryGetValue(out var agent, out var error), error?.Type);
        Names = [.. Names, name];
        _agents.Add(agent!);
    }

    /// <summary>Takes up a profile a run created (<c>enrol</c>) so the host reads it as one of its own: <see cref="ActedAs"/> then finds a run that acted as it. The agent id it was enrolled with is <see cref="AgentIdOf"/>.</summary>
    internal void AdoptEnrolled(string name)
    {
        Assert.True(Store.Load(name).TryGetValue(out var agent, out var error), error?.Type);
        Names = [.. Names, name];
        _agents.Add(agent!);
    }

    internal static string AgentIdOf(string name) => "https://agents.example/" + Uri.EscapeDataString(name);

    internal EnrolledAgent AgentOf(string name) => _agents[Array.IndexOf(Names, name)];

    /// <summary>Plants a kept act for <paramref name="name"/> (a comment signed by that profile), in the directory the profile's session reads; the digest is the prefixed one.</summary>
    internal (string Digest, string Path) PlantAct(string name, int attempts = 1, string? lastAnswer = "503", int? unanswered = null, string? body = null, string headerKind = "comment", string headerBoard = "dotnet", string draftBoard = "dotnet")
    {
        var draft = new PostDraft { Kind = PostKind.Comment, Board = draftBoard, Parent = "01PARENT00000000000000000A", Body = body ?? "A comment by " + name + "." };
        Assert.True(SubmissionBuilder.Build(AgentOf(name), draft, DateTimeOffset.UtcNow).TryGetValue(out var signed, out var error), error?.Type);
        var path = KeptActFiles.Plant(Path.Combine(Store.DirectoryFor(name), "acts"), signed!, headerKind, ForumAddress.ToString().TrimEnd('/'), AgentIdOf(name), attempts, lastAnswer, board: headerBoard, unanswered: unanswered);
        return (signed!.PrefixedDigest, path);
    }

    /// <summary>The bytes of everything under each profile, as one hash per profile: a path and its content, so any file written, changed or removed changes it (the token cache excepted).</summary>
    internal Dictionary<string, string> Fingerprint() =>
        Names.ToDictionary(n => n, FingerprintOf, StringComparer.Ordinal);

    internal string FingerprintOf(string name)
    {
        var root = Store.DirectoryFor(name);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "token.json").Order(StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(root, file) + "\n"));
            sha.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>Runs <c>curia</c> with <paramref name="args"/> exactly as given.</summary>
    internal async Task<AgentRun> RunAsync(params string[] args)
    {
        var savedHome = Environment.GetEnvironmentVariable("CURIA_CLIENT_HOME");
        var savedFactory = Program.HttpClientFactory;
        var before = _forum.Seen.Count;
        // A cached token would hide which profile a run asked for one as: each run starts without one (the cache is not what a probe holds, and the fingerprint leaves it out).
        foreach (var name in Names) Store.DeleteToken(name);
        try
        {
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", Home);
            Program.HttpClientFactory = forum => new HttpClient(new NonDisposing(_forum)) { BaseAddress = forum, Timeout = TimeSpan.FromSeconds(30) };
            var run = await ConsoleCapture.CaptureAsync(() => Program.RunAsync(args)).ConfigureAwait(false);
            var seen = _forum.Seen.Skip(before).ToArray();
            return new AgentRun(run.Exit, run.Stdout, run.Stderr, seen, ActedAs(seen, run.Stdout + " " + run.Stderr));
        }
        finally
        {
            Program.HttpClientFactory = savedFactory;
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", savedHome);
        }
    }

    /// <summary>
    /// The profiles a run acted as, found two ways that do not ask the code under test: the agent a token request's client assertion names, and an agent id or a digest of a planted act
    /// that the run printed.
    /// </summary>
    private List<string> ActedAs(IReadOnlyList<SeenRequest> seen, string output)
    {
        var acted = new List<string>();
        foreach (var request in seen.Where(r => r.Path == "/oauth/token" && r.Body is not null))
        {
            var form = request.Body!.Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
            if (!form.TryGetValue("client_assertion", out var jwt)) continue;
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            var sub = doc.RootElement.GetProperty("sub").GetString();
            foreach (var name in Names.Where(n => string.Equals(AgentIdOf(n), sub, StringComparison.Ordinal)))
                if (!acted.Contains(name)) acted.Add(name);
        }

        foreach (var name in Names)
        {
            var file = Directory.Exists(Path.Combine(Store.DirectoryFor(name), "acts")) ? Directory.GetFiles(Path.Combine(Store.DirectoryFor(name), "acts")) : [];
            var heldDigests = file.Select(f => Path.GetFileNameWithoutExtension(f)).ToArray();
            if ((output.Contains(AgentIdOf(name), StringComparison.Ordinal) || heldDigests.Any(d => output.Contains(d, StringComparison.Ordinal))) && !acted.Contains(name))
                acted.Add(name);
        }

        return acted;
    }

    public void Dispose()
    {
        foreach (var agent in _agents) agent.Dispose();
        _forum.Dispose();
        Directory.Delete(Home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The Forum: records every request and answers like a Forum that stores nothing.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "A response the handler returns is disposed by the HttpClient that reads it.")]
    private sealed class AgentHostForum : HttpMessageHandler
    {
        internal List<SeenRequest> Seen { get; } = [];

        internal Func<HttpRequestMessage, HttpResponseMessage>? OnOther { get; set; }

        internal Func<HttpRequestMessage, string?, HttpResponseMessage>? OnPost { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var path = request.RequestUri!.AbsolutePath;
            Seen.Add(new SeenRequest(request.Method.Method, path, request.RequestUri.Query, body));

            if (path == "/oauth/token") return ScriptedForum.Json(200, ScriptedForum.TokenJson);
            if (request.Method == HttpMethod.Post && path == "/v1/posts") return OnPost?.Invoke(request, body) ?? ScriptedForum.Problem(503, "curia/store/unavailable", "A store fault");
            return OnOther?.Invoke(request) ?? ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");
        }
    }

    private sealed class NonDisposing(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) => base.Dispose(false);
    }
}

/// <summary>The 19 identity verbs and the arguments that carry each past its own usage checks to the point it acts (errata G59, R11.82 (a)).</summary>
internal static class IdentityVerbs
{
    private const string Parent = "01PARENT00000000000000000A";

    /// <summary>The digest of a made-up act, for the verbs that take one (never kept anywhere).</summary>
    internal static readonly string SomeDigest = "sha256:" + new string('c', 64);

    /// <summary>Each of the 19 verbs with the arguments of a valid call; <paramref name="digest"/> is the digest <c>resend</c> and <c>acts --discard</c> are given.</summary>
    internal static IReadOnlyList<(string Verb, string[] Args)> Calls(string digest) =>
    [
        ("whoami", []),
        ("ask", ["--board", "dotnet", "--title", "A title?", "--body", "A body."]),
        ("finding", ["--board", "dotnet", "--title", "A finding", "--body", "A body."]),
        ("comment", ["--board", "dotnet", "--parent", Parent, "--body", "A body."]),
        ("answer", ["--board", "dotnet", "--parent", Parent, "--body", "A body."]),
        ("revision", ["--board", "dotnet", "--parent", Parent, "--body", "A body."]),
        ("endorse", [SomeDigest, "--board", "dotnet"]),
        ("reproduce", [SomeDigest, "--board", "dotnet", "--method", "ran it", "--body", "A body.", "--refs", "https://example.org/evidence"]),
        ("contradict", [SomeDigest, "--board", "dotnet", "--method", "ran it", "--body", "A body.", "--refs", "https://example.org/evidence"]),
        ("inbox", []),
        ("resolve", [Parent]),
        ("flag", [Parent, "--kind", "spam", "--rationale", "A reason."]),
        ("flags", []),
        ("resend", [digest]),
        ("acts", []),
        ("budget", []),
        ("keys", []),
        ("rotate", []),
        ("retire", ["--kid", "some-kid"]),
    ];
}
