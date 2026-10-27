using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G43 end to end, one Forum on Kestrel, driven by the real <c>curia</c> command in a child process and by <c>curia-mcp</c> over its protocol:
/// a comma tag, an <c>HTTPS://</c> reference, a <c>%00</c> id and a blank cursor, each by both doors, with their controls; and the inbox's own limit
/// type printed by the command. The events table is the oracle for "nothing was stored": its count is unchanged across every refusal and rises across
/// every post that was allowed.
///
/// <para><b>Oracles.</b> The count is <c>SELECT count(*) FROM events</c>; what is stored is read back from <c>GET /v1/posts/{id}</c>'s canonical form,
/// not from a receipt; the types and phrases are parsed from the errata. A NUL cannot be spelled on a command line, so its door here is the tool's
/// (R11.57 states the command line is outside it).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class SmallOnesEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-small-ones-").FullName;
    private readonly string _mcpHome = Directory.CreateTempSubdirectory("curia-small-ones-mcp-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        Directory.Delete(_mcpHome, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static readonly string[] CommaTags = ["x", "a,b"];
    private static readonly string[] PlainTags = ["x", "y"];
    private static readonly string[] MixedCaseRef = ["Http://example.org/b"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [GeneratedRegex(@"^posted\s+""?(?<id>[^\s""]+)", RegexOptions.Multiline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex Posted();

    private Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args) => CuriaProcess.RunAsync(_home, Ct, args);

    private async Task<long> EventCountAsync()
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task<JsonDocument> CanonicalAsync(string postId)
    {
        using var served = await forum.Client.GetAsync($"{Address}/v1/posts/{postId}", Ct);
        var body = await served.Content.ReadAsStringAsync(Ct);
        Assert.True(served.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return JsonDocument.Parse(json.RootElement.GetProperty("canonical").GetString()!);
    }

    private async Task<EnrolledAgent> EnrolMcpAgentAsync()
    {
        var slug = $"mcp-{Guid.NewGuid().ToString("N")[..8]}";
        Assert.True(
            new ProfileStore(_mcpHome).Create(slug, $"https://agents.example/{slug}", $"{slug}-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error),
            error?.Detail);
        _owned.Add(agent!);
        var enrolled = await new ForumClient(forum.Client, forum.Client.BaseAddress!).EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var refusal), refusal?.Summary);
        return agent!;
    }

    private ForumTools ToolsFor(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_mcpHome), forum.Clock);
        return new ForumTools(client, MarkingMode.Datamark, new HeadStore(_mcpHome), new ForumWriter(agent, session, forum.Clock));
    }

    // ---- PJ39 -----------------------------------------------------------------------------------

    /// <summary>PJ39: the command line's doors. Refusals store nothing and exit as the errata says; the controls post; the reference is stored as typed.</summary>
    [Fact]
    public async Task R8_73_OneForumTwoDoorsTheCommandLine()
    {
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var board = "small-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(0, (await CuriaAsync("enrol", "--agent", "small-cli", "--forum", Address)).Exit);
        var start = await EventCountAsync();

        // A comma tag, written as the only thing a command line can write it as, a display literal: refused locally, nothing stored.
        var comma = await CuriaAsync("ask", "--agent", "small-cli", "--board", board, "--title", "Comma tag", "--body", "a body", "--tags", "\"a,b\"", "--forum", Address);
        Assert.True(comma.Exit == 3, $"exit {comma.Exit}:\n{comma.Stdout}{comma.Stderr}");
        Assert.Contains("tags[0]", comma.Stderr, StringComparison.Ordinal);
        Assert.Equal(start, await EventCountAsync());

        // The control: a plain tag posts.
        var asked = await CuriaAsync("ask", "--agent", "small-cli", "--board", board, "--title", "Plain tag", "--body", "a body", "--tags", "x,y z", "--forum", Address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var questionId = Posted().Match(asked.Stdout).Groups["id"].Value;
        var afterAsk = await EventCountAsync();
        Assert.True(afterAsk > start);

        // An HTTPS:// reference: posts, and the stored member is the author's bytes.
        var commented = await CuriaAsync("comment", "--agent", "small-cli", "--board", board, "--parent", questionId, "--body", "see this", "--refs", "HTTPS://example.org/a", "--forum", Address);
        Assert.True(commented.Exit == 0, commented.Stdout + commented.Stderr);
        using (var canonical = await CanonicalAsync(Posted().Match(commented.Stdout).Groups["id"].Value))
        {
            var target = canonical.RootElement.GetProperty("refs")[0];
            Assert.Equal("url", target.GetProperty("kind").GetString());
            Assert.Equal("HTTPS://example.org/a", target.GetProperty("target").GetString());
        }

        var afterComment = await EventCountAsync();
        Assert.Equal(afterAsk - start, afterComment - afterAsk);

        // A blank id and a blank cursor: exit 1, nothing sent, nothing stored.
        var blankId = await CuriaAsync("read", " ", "--forum", Address);
        Assert.True(blankId.Exit == 1, $"exit {blankId.Exit}:\n{blankId.Stdout}{blankId.Stderr}");
        var blankCursor = await CuriaAsync("search", "x", "--cursor", "", "--forum", Address);
        Assert.True(blankCursor.Exit == 1, $"exit {blankCursor.Exit}:\n{blankCursor.Stdout}{blankCursor.Stderr}");
        Assert.Contains(PublishedSmallWords.BlankCursorFlag(), blankCursor.Stderr, StringComparison.Ordinal);

        // The inbox's own limit type, printed by the command.
        var limit = await CuriaAsync("inbox", "--agent", "small-cli", "--limit", "0", "--forum", Address);
        Assert.Contains(PublishedSmallWords.InboxLimitType(), limit.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(PublishedSmallWords.SearchLimitType(), limit.Stderr, StringComparison.Ordinal);

        Assert.Equal(afterComment, await EventCountAsync());
    }

    /// <summary>PJ39: the tool's doors, same shape: a comma tag and a NUL id are <c>NOT SENT</c>; the controls post; an <c>HTTPS://</c> reference is stored as typed.</summary>
    [Fact]
    public async Task R8_73_OneForumTwoDoorsTheMcpAdapter()
    {
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var board = "small-" + Guid.NewGuid().ToString("N")[..8];
        var agent = await EnrolMcpAgentAsync();
        await using var session = await McpProtocolSession.StartAsync(ToolsFor(agent), agent.Profile.AgentId);
        var start = await EventCountAsync();

        var comma = await session.CallAsync("curia_ask", new() { ["board"] = board, ["title"] = "Comma tag", ["body"] = "a body", ["tags"] = CommaTags });
        Assert.True(comma.IsError && comma.Text.StartsWith("NOT SENT", StringComparison.Ordinal), comma.Text);
        Assert.Contains("tags[1]", comma.Text, StringComparison.Ordinal);
        Assert.Equal(start, await EventCountAsync());

        var asked = await session.CallAsync("curia_ask", new() { ["board"] = board, ["title"] = "Plain tag", ["body"] = "a body", ["tags"] = PlainTags });
        Assert.False(asked.IsError, asked.Text);
        var questionId = Regex.Match(asked.Text, "[0-9A-HJKMNP-TV-Z]{26}", RegexOptions.None, TimeSpan.FromSeconds(2)).Value;
        var afterAsk = await EventCountAsync();
        Assert.True(afterAsk > start);

        var commented = await session.CallAsync("curia_comment", new() { ["parentPostId"] = questionId, ["body"] = "see this", ["refs"] = MixedCaseRef });
        Assert.False(commented.IsError, commented.Text);
        var afterComment = await EventCountAsync();
        Assert.Equal(afterAsk - start, afterComment - afterAsk);

        var nulId = await session.CallAsync("curia_read", new() { ["postId"] = "AB" + (char)0 + "CD" });
        Assert.True(nulId.IsError && nulId.Text.StartsWith("NOT SENT", StringComparison.Ordinal), nulId.Text);
        var nulBoard = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = "x", ["board"] = "AB" + (char)0 + "CD" } });
        Assert.True(nulBoard.IsError && nulBoard.Text.StartsWith("NOT SENT", StringComparison.Ordinal), nulBoard.Text);
        var blankCursor = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = "x", ["cursor"] = " " } });
        Assert.True(blankCursor.IsError && blankCursor.Text.StartsWith("NOT SENT", StringComparison.Ordinal), blankCursor.Text);

        Assert.Equal(afterComment, await EventCountAsync());
    }
}
