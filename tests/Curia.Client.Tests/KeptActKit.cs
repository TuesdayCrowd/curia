#pragma warning disable CA1416 // the kept-act files are mode 0600 and 0700 (R11.79): the probes read and set Unix modes, as PrivateFiles does

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// What a red probe says. The probes of errata G58 (ASA1 to ASA43) are written in their failing form: each states what the published words and rules require and what was
/// found, so the text a red probe leaves behind is the record Task 1 keeps beside its prediction.
/// </summary>
internal static class Red
{
    /// <summary>Fails with both sides in full when they differ.</summary>
    internal static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>Fails naming every problem found, or passes when there are none.</summary>
    internal static void None(IReadOnlyCollection<string> problems, string expected)
    {
        if (problems.Count > 0)
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {string.Join("; ", problems)}");
    }
}

/// <summary>One signed submission the scripted Forum received.</summary>
internal sealed record SentAct(byte[] Body, string Query, string? Jti, bool KeptFileExisted, string? DpopNonce);

/// <summary>
/// A Forum that is a script: the token endpoint answers like a Forum and each <c>POST /v1/posts</c> is answered by <see cref="OnPost"/> (the zero-based number of the
/// submission, the request, the caller's token). Every submission is recorded with whether the kept file existed at the instant it was received (<see cref="KeptFileExists"/>).
/// </summary>
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "A response the handler returns is disposed by the HttpClient that reads it.")]
internal sealed class ScriptedForum : HttpMessageHandler
{
    internal const string TokenJson = """{"access_token":"stub-access-token","token_type":"DPoP","expires_in":300,"scope":"question:create"}""";

    internal List<SentAct> Posts { get; } = [];

    internal List<string> Requests { get; } = [];

    internal Func<bool> KeptFileExists { get; set; } = () => false;

    internal Func<int, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> OnPost { get; set; } =
        (_, _, _) => Task.FromResult(Problem(503, "curia/store/unavailable", "A store fault"));

    internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? OnOther { get; set; }

    /// <summary>The token endpoint's answer, or null for a token.</summary>
    internal Func<HttpResponseMessage>? OnToken { get; set; }

    /// <summary>A <c>201</c> receipt for <paramref name="digest"/>.</summary>
    internal static HttpResponseMessage Receipt(string digest, string postId = "01TESTRECEIPTPOST000000000A") =>
        Json(201, $$$"""{"post_id":"{{{postId}}}","digest":"{{{digest}}}","server_ts":"1970-01-01T00:00:00.0000000+00:00","risk_flags":[],"vector_index":"indexed","budget":{"name":"posting","limit":3,"used":1,"remaining":2}}""");

    /// <summary>A problem document of <paramref name="type"/> with <paramref name="status"/>.</summary>
    internal static HttpResponseMessage Problem(int status, string type, string title = "A refusal", string? detail = null) =>
        Json(status, $$$"""{"type":"{{{type}}}","title":"{{{title}}}","detail":{{{(detail is null ? "null" : JsonSerializer.Serialize(detail))}}}}""");

    internal static HttpResponseMessage Json(int status, string body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// <summary>The <c>jti</c> of a request's DPoP proof, read from its payload.</summary>
    internal static string? JtiOf(HttpRequestMessage request)
    {
        if (!request.Headers.TryGetValues("DPoP", out var values)) return null;
        var parts = values.First().Split('.');
        if (parts.Length < 2) return null;
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.TryGetProperty("jti", out var jti) ? jti.GetString() : null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri!.AbsolutePath;
        Requests.Add($"{request.Method} {path}");

        if (path == "/oauth/token") return OnToken?.Invoke() ?? Json(200, TokenJson);

        if (request.Method == HttpMethod.Post && path == "/v1/posts")
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var nonce = request.Headers.TryGetValues("DPoP", out var v) ? v.First() : null;
            Posts.Add(new SentAct(body, request.RequestUri.Query, JtiOf(request), KeptFileExists(), nonce));
            return await OnPost(Posts.Count - 1, request, cancellationToken).ConfigureAwait(false);
        }

        return OnOther is { } other
            ? await other(request, cancellationToken).ConfigureAwait(false)
            : Problem(404, "curia/posts/not-found", "No such post");
    }
}

/// <summary>An agent in a profile store of its own, a scripted Forum and a session over them: the library-level host of the kept-act probes (host C).</summary>
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The kit owns what it builds and disposes it with itself.")]
internal sealed class SessionKit : IDisposable
{
    internal static readonly Uri ForumAddress = new("http://forum.test");

    private readonly HttpClient _http;

    internal SessionKit(TimeSpan? writeBound = null, TimeProvider? clock = null, TestSigner? held = null)
    {
        Home = Directory.CreateTempSubdirectory("curia-kept-acts-").FullName;
        Store = new ProfileStore(Home);
        if (held is null)
        {
            Assert.True(Store.Create("alice", "https://agents.example/alice", "alice-1", ForumAddress).TryGetValue(out var agent, out var error), error?.Type);
            Agent = agent!;
        }
        else
        {
            Assert.True(ExternalSigner.Describe(held.Command).TryGetValue(out var signer, out var describeError), describeError?.Type);
            Assert.True(Store.Create("alice", "https://agents.example/alice", ForumAddress, signer!).TryGetValue(out var agent, out var error), error?.Type);
            Agent = agent!;
        }

        Forum = new ScriptedForum { KeptFileExists = () => Directory.Exists(ActsDirectory) && Directory.GetFiles(ActsDirectory).Length > 0 };
        _http = new HttpClient(Forum) { BaseAddress = ForumAddress, Timeout = TimeSpan.FromSeconds(30) };
        Session = new ForumSession(new ForumClient(_http, ForumAddress), Agent, Store, clock ?? TimeProvider.System, writeBound);
    }

    internal string Home { get; }

    internal ProfileStore Store { get; }

    internal EnrolledAgent Agent { get; }

    internal ScriptedForum Forum { get; }

    internal ForumSession Session { get; }

    /// <summary>Where a kept act lives: <c>acts</c> in the agent's own directory (R11.79).</summary>
    internal string ActsDirectory => Path.Combine(Store.DirectoryFor("alice"), "acts");

    /// <summary>The kept files, by name; empty when the directory does not exist.</summary>
    internal string[] KeptFiles() => Directory.Exists(ActsDirectory) ? [.. Directory.GetFiles(ActsDirectory).Order(StringComparer.Ordinal)] : [];

    /// <summary>The file a kept act of <paramref name="submission"/> has: <c>&lt;64 hex&gt;.act</c>.</summary>
    internal string FileOf(SignedSubmission submission) => Path.Combine(ActsDirectory, submission.Digest + ".act");

    /// <summary>A signed submission of <paramref name="kind"/>, built as the clients build one.</summary>
    internal SignedSubmission Build(PostKind kind = PostKind.Question, string body = "A body.")
    {
        var draft = kind switch
        {
            PostKind.Question => new PostDraft { Kind = kind, Board = "b", Title = "A title?", Body = body },
            PostKind.Finding => new PostDraft { Kind = kind, Board = "b", Title = "A finding", Body = body },
            PostKind.Answer or PostKind.Comment => new PostDraft { Kind = kind, Board = "b", Parent = "01PARENT00000000000000000A", Body = body },
            PostKind.Revision => new PostDraft { Kind = kind, Board = "b", Parent = "01PARENT00000000000000000A", Prev = "sha256:" + new string('a', 64), Body = body },
            PostKind.Vote => new PostDraft { Kind = kind, Board = "b", Body = string.Empty, Target = "sha256:" + new string('b', 64), Endorse = true, PredictedEndorsementBp = 5000, Epoch = 1 },
            PostKind.Verification => new PostDraft
            {
                Kind = kind, Board = "b", Body = body, Target = "sha256:" + new string('b', 64), Method = "ran it", Result = Curia.Domain.Verification.VerificationResult.Reproduced,
                Refs = [new Curia.Domain.Content.Reference(Curia.Domain.Content.ReferenceKinds.Url, "https://example.org/evidence", null)],
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        Assert.True(SubmissionBuilder.Build(Agent, draft, DateTimeOffset.UtcNow).TryGetValue(out var signed, out var error), $"{kind}: {error?.Type} {error?.Detail}");
        return signed!;
    }

    /// <summary>
    /// Writes a kept act as R11.79 publishes the file (plan ruling 4): line 1 one JSON object (<c>v</c>, <c>digest</c>, <c>kind</c>, <c>board</c>, <c>forum</c>, <c>author</c>,
    /// <c>kept_at</c>, <c>attempts</c>, <c>last_attempt_at</c>, <c>last_answer</c>), line 2 the wire in base64, mode 0600 in a directory of mode 0700. A probe that needs a
    /// kept act the seam does not make plants one.
    /// </summary>
    internal string Plant(SignedSubmission submission, string kind = "question", string forum = "http://forum.test", string author = "https://agents.example/alice", int attempts = 0, string? lastAnswer = null, string? name = null, string? digest = null) =>
        KeptActFiles.Plant(ActsDirectory, submission, kind, forum, author, attempts, lastAnswer, name, digest);

    public void Dispose()
    {
        _http.Dispose();
        Agent.Dispose();
        Directory.Delete(Home, recursive: true);
        GC.SuppressFinalize(this);
    }
}

/// <summary>What a run of the real <c>curia</c> command printed.</summary>
internal sealed record CliRun(int Exit, string Stdout, string Stderr)
{
    internal string Text => Fold(Stdout + " " + Stderr);

    internal static string Fold(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}

/// <summary>
/// Runs <c>Program.RunAsync</c> in process against a <see cref="ScriptedForum"/> in a profile store the kit owns (the host of the command-level kept-act probes). The caller is
/// in the <c>console</c> collection: the streams, the environment and <c>Program.HttpClientFactory</c> are process-wide.
/// </summary>
internal static class CliKit
{
    internal static async Task<CliRun> RunAsync(SessionKit kit, params string[] args)
    {
        ArgumentNullException.ThrowIfNull(kit);
        var savedHome = Environment.GetEnvironmentVariable("CURIA_CLIENT_HOME");
        var savedFactory = Program.HttpClientFactory;
        try
        {
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", kit.Home);
            Program.HttpClientFactory = forum => new HttpClient(new NonDisposing(kit.Forum)) { BaseAddress = forum, Timeout = TimeSpan.FromSeconds(30) };
            var run = await ConsoleCapture.CaptureAsync(() => Program.RunAsync(args is ["acts", ..] ? [.. args, "--agent", "alice"] : [.. args, "--agent", "alice", "--forum", SessionKit.ForumAddress.ToString()])).ConfigureAwait(false);
            return new CliRun(run.Exit, run.Stdout, run.Stderr);
        }
        finally
        {
            Program.HttpClientFactory = savedFactory;
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", savedHome);
        }
    }

    /// <summary>The scripted Forum outlives each command's client, which disposes its handler.</summary>
    private sealed class NonDisposing(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) => base.Dispose(false);
    }
}
