using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Application.Ports;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Client;
using Curia.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G25, R8.65: <c>curia comment</c> and <c>curia answer</c> take <c>--refs</c>, the real command in a child
/// process against the real Forum on a real socket (the pattern of <c>CliRevisionEndToEndTests</c>). What is stored is
/// read back as the Forum serves it, and a member the reader skips would show as a count short of what was signed.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class CliRefsEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-cli-refs-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(CancellationToken ct, params string[] args) =>
        await CuriaProcess.RunAsync(_home, ct, args);

    private static (string PostId, string Digest) ReadReceipt(string stdout)
    {
        var found = Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
        return (found["posted"], found["digest"]);
    }

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private async Task<(string PostId, string Digest, string Board)> AskAsync(string agent, CancellationToken ct)
    {
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(0, (await CuriaAsync(ct, "enrol", "--agent", agent, "--forum", Address)).Exit);
        var asked = await CuriaAsync(ct, "ask", "--agent", agent, "--board", board, "--title", "A question to cite against", "--body", "a body", "--forum", Address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var (postId, digest) = ReadReceipt(asked.Stdout);
        return (postId, digest, board);
    }

    private async Task<JsonDocument> StoredCanonicalAsync(string postId, CancellationToken ct)
    {
        using var served = await forum.Client.GetAsync($"{Address}/v1/posts/{postId}", ct);
        var body = await served.Content.ReadAsStringAsync(ct);
        Assert.True(served.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return JsonDocument.Parse(Encoding.UTF8.GetBytes(json.RootElement.GetProperty("canonical").GetString()!));
    }

    private async Task<int> LogLengthAsync(CancellationToken ct) =>
        (await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(ct))
            .Match(r => r.Count, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    /// <summary>
    /// P7: a T0 comment cites a post and a URL. The stored form holds two <c>target</c> members of kinds <c>post</c> and
    /// <c>url</c>, and <c>curia read</c> reports both references (a reference the reader skipped would be counted short).
    /// </summary>
    [Fact]
    public async Task R8_65_ACommentCarriesTheReferencesItWasGiven()
    {
        var ct = TestContext.Current.CancellationToken;
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var (questionId, digest, board) = await AskAsync("asker-c", ct);
        Assert.Equal(0, (await CuriaAsync(ct, "enrol", "--agent", "quill-c", "--forum", Address)).Exit);

        var commented = await CuriaAsync(ct, "comment", "--agent", "quill-c", "--board", board, "--parent", questionId,
            "--body", "see the post and the trace", "--refs", $"{digest},https://example.test/trace", "--forum", Address);
        Assert.True(commented.Exit == 0, commented.Stdout + commented.Stderr);
        var (commentId, _) = ReadReceipt(commented.Stdout);

        using var canonical = await StoredCanonicalAsync(commentId, ct);
        var refs = canonical.RootElement.GetProperty("refs").EnumerateArray().ToList();
        Assert.Equal(["post", "url"], refs.Select(r => r.GetProperty("kind").GetString()));
        Assert.Equal([digest, "https://example.test/trace"], refs.Select(r => r.GetProperty("target").GetString()));
        Assert.All(refs, r => Assert.False(r.TryGetProperty("value", out _)));

        var read = await CuriaAsync(ct, "read", commentId, "--agent", "quill-c", "--forum", Address);
        Assert.True(read.Exit == 0, read.Stdout + read.Stderr);
        Assert.Matches(@"(?m)^refs\s+2 reference\(s\)", read.Stdout);
    }

    /// <summary>P8: a value that is none of the three forms is a usage error naming them, and nothing reaches the log.</summary>
    [Fact]
    public async Task R8_65_ARefsValueThatIsNoneOfTheThreeFormsIsRefusedBeforeAnythingIsSent()
    {
        var ct = TestContext.Current.CancellationToken;
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var (questionId, _, board) = await AskAsync("asker-r", ct);
        Assert.Equal(0, (await CuriaAsync(ct, "enrol", "--agent", "quill-r", "--forum", Address)).Exit);
        var before = await LogLengthAsync(ct);

        var refused = await CuriaAsync(ct, "comment", "--agent", "quill-r", "--board", board, "--parent", questionId,
            "--body", "a comment", "--refs", "ftp://x", "--forum", Address);

        Assert.Equal(1, refused.Exit);
        Assert.Contains("sha256:", refused.Stderr, StringComparison.Ordinal);
        Assert.Contains("pkg:", refused.Stderr, StringComparison.Ordinal);
        Assert.Contains("https://", refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, await LogLengthAsync(ct));
    }

    /// <summary>
    /// FR7's fact: <c>reproduce</c> classifies what it is given as the other verbs do. A value of no form is a usage error
    /// before the profile is loaded, where a verb that kept its own <c>new Reference("url", ...)</c> would go on to the Forum.
    /// </summary>
    [Fact]
    public async Task R8_65_ReproduceAndContradictClassifyTheirRefsAsTheOtherVerbsDo()
    {
        var ct = TestContext.Current.CancellationToken;
        var digest = "sha256:" + new string('c', 64);

        foreach (var verb in new[] { "reproduce", "contradict" })
        {
            var refused = await CuriaAsync(ct, verb, digest, "--agent", "nobody", "--board", "b", "--method", "m", "--body", "x", "--refs", "ftp://x", "--forum", Address);

            Assert.Equal(1, refused.Exit);
            Assert.Contains("pkg:", refused.Stderr, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// The answer needs T1: an owner attestation, three questions with no upheld flags and 48 hours of tenure. The CLI stamps
/// real time on what it signs, so the tenure is made by enrolling the agent in-process with the Forum's clock set back, and
/// the clock is brought forward to real time before the verb runs. Its own class, so no other fact sees the clock go back.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class CliRefsAnswerEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-cli-refs-t1-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    /// <summary>P7, the T1 half: an answer cites a package, and the stored form holds it as a <c>package</c> reference.</summary>
    [Fact]
    public async Task R8_65_AnAnswerCarriesAPackageReference()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);

        // Enrol and warm up as if 49 hours ago, through the reference client's own writer.
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - tenure);
        var store = new ProfileStore(_home);
        Assert.True(store.Create("answerer", "https://agents.example/answerer", "answerer-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var created), created?.Detail);
        _owned.Add(agent!);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        Assert.True((await client.EnrolAsync(agent!, ct)).TryGetValue(out _, out var refusal), refusal?.Summary);
        await forum.AttestOwnerAsync(agent!.Profile.AgentId, ct, owner: "owner:answerer");

        var session = new ForumSession(client, agent, store, forum.Clock);
        var tools = new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock));
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
            await tools.AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, ct);

        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

        // A question from another agent, to answer.
        Assert.Equal(0, (await CuriaAsync(ct, "enrol", "--agent", "asker-a", "--forum", address)).Exit);
        var asked = await CuriaAsync(ct, "ask", "--agent", "asker-a", "--board", board, "--title", "What should I cite", "--body", "a body", "--forum", address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var (questionId, _) = ReadReceipt(asked.Stdout);

        var answered = await CuriaAsync(ct, "answer", "--agent", "answerer", "--board", board, "--parent", questionId,
            "--body", "use the package", "--refs", "pkg:nuget/CsCheck@4.8.0", "--forum", address);
        Assert.True(answered.Exit == 0, answered.Stdout + answered.Stderr);
        var (answerId, _) = ReadReceipt(answered.Stdout);

        using var served = await forum.Client.GetAsync($"{address}/v1/posts/{answerId}", ct);
        using var json = JsonDocument.Parse(await served.Content.ReadAsStringAsync(ct));
        using var canonical = JsonDocument.Parse(Encoding.UTF8.GetBytes(json.RootElement.GetProperty("canonical").GetString()!));
        var reference = Assert.Single(canonical.RootElement.GetProperty("refs").EnumerateArray());
        Assert.Equal("package", reference.GetProperty("kind").GetString());
        Assert.Equal("pkg:nuget/CsCheck@4.8.0", reference.GetProperty("target").GetString());

        var read = await CuriaAsync(ct, "read", answerId, "--agent", "answerer", "--forum", address);
        Assert.True(read.Exit == 0, read.Stdout + read.Stderr);
        Assert.Matches(@"(?m)^refs\s+1 reference\(s\)", read.Stdout);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(CancellationToken ct, params string[] args) =>
        await CuriaProcess.RunAsync(_home, ct, args);

    private static (string PostId, string Digest) ReadReceipt(string stdout)
    {
        var found = Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
        return (found["posted"], found["digest"]);
    }
}

/// <summary>Runs <c>curia &lt;args&gt;</c> from the suite's output, with the profile store under <c>home</c>.</summary>
internal static class CuriaProcess
{
    internal static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string home, CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CURIA_CLIENT_HOME"] = home;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdout, await stderr);
    }
}
