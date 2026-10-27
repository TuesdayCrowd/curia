using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Application.Ports;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G41 (R8.72, R9.42, R11.54) through the shipped doors against one real Forum on a real socket: <c>curia</c> in a child process and
/// <c>curia-mcp</c> over the protocol, reading and replying to the same withheld post. The doors must agree: both refuse a reply under it, and every door
/// that names the post by id says it is withheld and not that it does not exist.
///
/// <para><b>Oracles.</b> The log's length read from the store, the exit code of the child process and the text each door returned. The post is withheld
/// by R10.59's writer (<c>ForumFixture.WithholdAsync</c>). Each negative fact has a control that must pass: a reply under a served question is posted.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class WithheldDoorsAgreeEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-withheld-doors-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args) => CuriaProcess.RunAsync(_home, Ct, args);

    private static string PostIdOf(string stdout) =>
        Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value)["posted"];

    private async Task<int> LogLengthAsync() =>
        (await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(Ct))
            .Match(r => r.Count, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private sealed record Scene(string Board, string Author, string Withheld, string Served, string WithheldDigest);

    /// <summary>An author who asks two questions through the command, the first of which is withheld; and a second agent who is the control.</summary>
    private async Task<Scene> BuildAsync()
    {
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var board = "board-" + suffix;
        var author = "author-" + suffix;
        Assert.Equal(0, (await CuriaAsync("enrol", "--agent", author, "--forum", Address)).Exit);

        var first = await CuriaAsync("ask", "--agent", author, "--board", board, "--title", $"Why does the {suffix} span differ", "--body", $"A question about the {suffix} span.", "--forum", Address);
        Assert.True(first.Exit == 0, first.Stdout + first.Stderr);
        var second = await CuriaAsync("ask", "--agent", author, "--board", board, "--title", $"How is a lantern {suffix} lit", "--body", $"An unrelated question about lanterns {suffix}.", "--forum", Address);
        Assert.True(second.Exit == 0, second.Stdout + second.Stderr);
        var withheld = PostIdOf(first.Stdout);
        var digest = Regex.Match(first.Stdout, @"digest\s+""?(?<d>sha256:[0-9a-f]{64})").Groups["d"].Value;
        await forum.WithholdAsync(withheld, Ct);
        return new Scene(board, author, withheld, PostIdOf(second.Stdout), digest);
    }

    private async Task<McpProtocolSession> SessionAsync(string slug)
    {
        var store = new ProfileStore(_home);
        Assert.True(store.Load(slug).TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent!, store, forum.Clock);
        return await McpProtocolSession.StartAsync(
            new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent!, session, forum.Clock)), agent!.Profile.AgentId);
    }

    /// <summary>
    /// PH18. The two doors give one answer. <c>curia comment</c> under the withheld question exits 3 and prints <c>parent-withheld</c>; <c>curia_comment</c>
    /// under it is <c>NOT SENT</c> and says it is withheld; the log is the same length after both. The control, a reply under the served question, is
    /// posted by the command.
    /// </summary>
    [Fact]
    public async Task G41_TheTwoDoorsGiveOneAnswer()
    {
        var scene = await BuildAsync();
        var other = "other-" + scene.Author;
        Assert.Equal(0, (await CuriaAsync("enrol", "--agent", other, "--forum", Address)).Exit);
        var control = await CuriaAsync("comment", "--agent", other, "--board", scene.Board, "--parent", scene.Served, "--body", "A reply under a served question", "--forum", Address);
        Assert.True(control.Exit == 0, $"the control reply exited {control.Exit}:\n{control.Stdout}{control.Stderr}");
        var before = await LogLengthAsync();

        var refused = await CuriaAsync("comment", "--agent", scene.Author, "--board", scene.Board, "--parent", scene.Withheld, "--body", "A reply under a withheld question", "--forum", Address);

        Assert.True(refused.Exit == 3, $"curia comment under a withheld post exited {refused.Exit}:\n{refused.Stdout}{refused.Stderr}");
        Assert.Contains(PublishedWithheldWords.ParentWithheldType(), refused.Stdout + refused.Stderr, StringComparison.Ordinal);

        await using var mcp = await SessionAsync(scene.Author);
        var result = await mcp.CallAsync("curia_comment", new() { ["parentPostId"] = scene.Withheld, ["body"] = "A reply under a withheld question" });

        Assert.True(result.IsError && result.Text.StartsWith("NOT SENT", StringComparison.Ordinal), $"curia_comment under a withheld post was not NOT SENT:\n{result.Text}");
        Assert.Contains("withheld", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, await LogLengthAsync());
    }

    /// <summary>
    /// PH19. Every door that names the withheld post by id says withheld, and the command exits 5: <c>curia read</c>, <c>curia_read</c>,
    /// <c>curia_verify</c>, <c>curia_revise</c> and <c>curia revision</c> (which signs nothing). The control, <c>curia read</c> of the served question, exits 0.
    /// </summary>
    [Fact]
    public async Task G41_AllIdBasedDoorsSayWithheld()
    {
        var scene = await BuildAsync();
        var served = await CuriaAsync("read", scene.Served, "--forum", Address);
        Assert.True(served.Exit == 0, served.Stdout + served.Stderr);

        var read = await CuriaAsync("read", scene.Withheld, "--forum", Address);
        Assert.Equal(5, read.Exit);
        Assert.Contains("ithheld", read.Stdout + read.Stderr, StringComparison.Ordinal);

        // curia revision reads and verifies the post it would chain to, so it is refused that read: withheld, exit 5, nothing signed (ruling 1).
        var before = await LogLengthAsync();
        var revision = await CuriaAsync(
            "revision", "--agent", scene.Author, "--board", scene.Board, "--parent", scene.Withheld, "--title", "A corrected question", "--body", "A corrected body.", "--forum", Address);
        Assert.Equal(5, revision.Exit);

        // The second line of a refusal spells the problem type, which holds "withheld" whatever the words say; the words are the first line.
        Assert.Contains("ithheld", revision.Stderr.Split('\n')[0], StringComparison.Ordinal);
        Assert.Equal(before, await LogLengthAsync());

        await using var mcp = await SessionAsync(scene.Author);
        foreach (var (tool, arguments) in new (string, Dictionary<string, object?>)[]
        {
            ("curia_read", new() { ["postId"] = scene.Withheld }),
            ("curia_verify", new() { ["postId"] = scene.Withheld }),
            ("curia_revise", new() { ["postId"] = scene.Withheld, ["body"] = "A corrected question" }),
        })
        {
            var result = await mcp.CallAsync(tool, arguments);
            Assert.True(result.IsError, $"{tool} of a withheld post was not an error:\n{result.Text}");
            Assert.Contains("ithheld", result.Text, StringComparison.Ordinal);
        }
    }
}
