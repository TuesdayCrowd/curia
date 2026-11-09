using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R8.85 (errata G56) over a real socket with the real command in a child process, NAM13, the revision leg of NAM28 and the command line's half of NAM36. A
/// qualifying name round-trips through every surface (posted by <c>curia ask</c>, listed by <c>GET /v1/boards/{board}/posts</c> with the board percent-encoded,
/// found by search and by the inbox, shown by <c>curia board</c>); <c>curia revision</c> refuses a board the grammar refuses after its (hard) read of the post it
/// revises and before it signs; and a post on a stored refused board is read by the commands as it was.
///
/// <para><b>Oracles.</b> What was stored is counted from the <c>events</c> table by SQL; ids are the receipts' (<c>posted</c> line); the listing and search are read
/// from the route's JSON by post id, never from a command's report of itself. The reserved characters are parsed from R8.85 words and their door test is
/// <c>BoardGrammarEndToEndTests</c>' (this class's DPoP URL is the socket's, which the shared helpers do not sign for); here the one reserved character with a route
/// consequence, <c>/</c>, is held by a stored board the listing cannot carry (non-property i), a control.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class BoardGrammarCliEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-board-cli-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    private Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args) => CuriaProcess.RunAsync(_home, Ct, args);

    private async Task EnrolAsync(string agent)
    {
        var enrolled = await CuriaAsync("enrol", "--agent", agent, "--forum", Address);
        Assert.Equal(0, enrolled.Exit);
    }

    private static (string PostId, string Digest) ReadReceipt(string stdout)
    {
        var found = Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
        return (found["posted"], found["digest"]);
    }

    private async Task<long> PostEventsAsync()
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = 'post.accepted';", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task<string[]> ListedAsync(string board)
    {
        var text = await forum.Client.GetStringAsync(new Uri($"{Address}/v1/boards/{Uri.EscapeDataString(board)}/posts", UriKind.Absolute), Ct);
        using var json = JsonDocument.Parse(text);
        return [.. json.RootElement.EnumerateArray().Select(e => e.GetProperty("post_id").GetString()!)];
    }

    private async Task<string[]> SearchedAsync(string board)
    {
        var text = await forum.Client.GetStringAsync(new Uri($"{Address}/v1/search?board={Uri.EscapeDataString(board)}&limit=50", UriKind.Absolute), Ct);
        using var json = JsonDocument.Parse(text);
        return [.. json.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!)];
    }

    private async Task<ForumAgent> EnrolHandAsync(string stem)
    {
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{Suffix()}", $"{stem}-{Suffix()}");
        using var response = await agent.EnrollAsync(forum.Client, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return agent;
    }

    private static string U(int scalar) => char.ConvertFromUtf32(scalar);

    // ---- NAM13 ----------------------------------------------------------------------------------

    /// <summary>The qualifying names NAM1 admits that a path, a query and a command line must carry: 64 emoji (256 bytes), <c>a.b</c>, <c>a_b</c>, <c>0</c>, a CJK name, 64 e-acute.</summary>
    private static string[] Qualifying() =>
        [string.Concat(Enumerable.Repeat(U(0x1F642), 64)), "a.b", "a_b", "0", U(0x65E5) + U(0x672C) + U(0x8A9E), string.Concat(Enumerable.Repeat(U(0xE9), 64))];

    public static TheoryData<int> QualifyingRows()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Qualifying().Length; i++) data.Add(i);
        return data;
    }

    /// <summary>
    /// NAM13: each qualifying board is posted by <c>curia ask</c>, listed by the route (percent-encoded), found by search, found by another agent's inbox and shown by
    /// <c>curia board</c>. The dangerous direction of this PR is the refusal of a name that qualifies; this fact (and the live run's L2) is its probe, and it is a control on the
    /// seam.
    /// </summary>
    [Theory]
    [MemberData(nameof(QualifyingRows))]
    public async Task R8_85_NAM13_AQualifyingNameRoundTripsThroughEverySurface(int row)
    {
        var board = Qualifying()[row];
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var asker = $"rt-asker-{row}";
        var reader = $"rt-reader-{row}";
        await EnrolAsync(asker);
        await EnrolAsync(reader);

        var asked = await CuriaAsync(FirstAsk.Args(asker, board, $"A round trip {Guid.NewGuid():N}", $"a body {Guid.NewGuid():N}", Address, makesBoard: true));
        Assert.True(asked.Exit == 0, $"a qualifying board was refused: {asked.Stdout}{asked.Stderr}");
        var (postId, _) = ReadReceipt(asked.Stdout);

        Assert.Contains(postId, await ListedAsync(board));
        Assert.Contains(postId, await SearchedAsync(board));
        var inbox = await CuriaAsync("inbox", "--agent", reader, "--board", board, "--forum", Address);
        Assert.True(inbox.Exit == 0 && inbox.Stdout.Contains(postId, StringComparison.Ordinal), $"inbox: {inbox.Stdout}{inbox.Stderr}");
        var listing = await CuriaAsync("board", board, "--forum", Address);
        Assert.True(listing.Exit == 0 && listing.Stdout.Contains(postId, StringComparison.Ordinal), $"board: {listing.Stdout}{listing.Stderr}");
    }

    /// <summary>
    /// NAM13's reason for <c>/</c> (non-property i; a control, green on the seam): a board holding a slash, stored by the direct-append seam, is not listed by the listing
    /// route (the percent-encoded path answers no post) while search finds it. This is the defect the door now prevents for a new post; the other reserved characters
    /// round-trip when percent-encoded (Task 0's M2) and are reserved by ruling 17.
    /// </summary>
    [Fact]
    public async Task R8_85_NAM13_ASlashBoardIsNotListedByThePathButIsFoundBySearch()
    {
        var agent = await EnrolHandAsync("slash");
        var board = $"a/b-{Suffix()}";
        var (postId, _) = await forum.AppendStoredPostAsync(agent.SignQuestion(board, "A body " + Unrelated(), "A title " + Unrelated(), forum.Now), Ct);

        Assert.DoesNotContain(postId, await ListedAsync(board));
        Assert.Contains(postId, await SearchedAsync(board));
    }

    /// <summary>NAM13's words check: the reserved characters R8.85 words names are the five of the ruling.</summary>
    [Fact]
    public void R8_85_NAM13_TheReservedCharactersAreTheFiveOfTheRuling() =>
        Assert.Equal([",", "/", "?", "#", "%"], PublishedBoardWords.ReservedCharacters());

    // ---- NAM28 (the revision leg) ---------------------------------------------------------------

    /// <summary>
    /// NAM28, flipped on purpose by errata G61 (R11.85): <c>curia revision --board Meta</c> reads the post it revises (a hard read, so the Api suite's leg and not the recorder's), then
    /// refuses the board before it signs, not as a board that is not a name (the grammar is not asked of a revision) but as a board that is not the post's: exit 1 and the published
    /// refusal naming the typed board, the post and its board, and the events table does not move.
    /// </summary>
    [Fact]
    public async Task R11_85_NAM28_RevisionRefusesABoardThatIsNotThePostsAfterItsReadAndBeforeASignature()
    {
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var agent = "reviser";
        await EnrolAsync(agent);
        var board = "board-" + Suffix();
        var asked = await CuriaAsync(FirstAsk.Args(agent, board, $"A question to revise {Guid.NewGuid():N}", $"a body {Guid.NewGuid():N}", Address, makesBoard: true));
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var (postId, _) = ReadReceipt(asked.Stdout);
        var before = await PostEventsAsync();

        var (exit, stdout, stderr) = await CuriaAsync(
            "revision", "--agent", agent, "--board", "Meta", "--parent", postId, "--body", $"A correction {Guid.NewGuid():N}", "--forum", Address);

        Assert.True(exit == PublishedReplyBoardWords.RefusalExit(), $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        var expected = PublishedReplyBoardWords.RefusalLine(
            Curia.Canon.Json.DisplayLiteral.Of("Meta"), Curia.Canon.Json.DisplayLiteral.Of(postId), Curia.Canon.Json.DisplayLiteral.Of(board));
        Assert.Contains(Fold(expected), Fold(stderr), StringComparison.Ordinal);
        Assert.Equal(before, await PostEventsAsync());
    }

    // ---- NAM36 (the command line's half) --------------------------------------------------------

    /// <summary>
    /// NAM36 (a control, green on the seam; held by FNAM13): the commands serve a post on a stored refused board as they did. Stored by the direct-append seam on
    /// <c>Meta-x</c> and on <c>a b-x</c>: <c>curia board</c>, <c>curia browse board</c> (plain), <c>curia search --board</c>, <c>curia inbox --board</c>, <c>curia thread</c> and <c>curia read</c> exit 0 and show the post.
    /// </summary>
    [Fact]
    public async Task R8_85_NAM36_TheCommandsServeAPostOnAStoredRefusedBoard()
    {
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var author = await EnrolHandAsync("stored");
        const string reader = "stored-reader";
        await EnrolAsync(reader);
        var term = "zq" + Guid.NewGuid().ToString("N")[..10];
        var problems = new List<string>();
        foreach (var board in new[] { $"Meta-{Suffix()}", $"a b-{Suffix()}" })
        {
            var (postId, _) = await forum.AppendStoredPostAsync(author.SignQuestion(board, $"A body about {term}", $"About {term}", forum.Now), Ct);
            var commands = new Dictionary<string, string[]>
            {
                ["board"] = ["board", board, "--forum", Address],
                ["browse board"] = ["browse", "board", board, "--forum", Address],
                ["search"] = ["search", term, "--board", board, "--forum", Address],
                ["thread"] = ["thread", postId, "--forum", Address],
                ["read"] = ["read", postId, "--forum", Address],
                // I1: an inbox filter is a filter and never a door; a grammar-refused stored board is sent and read.
                ["inbox"] = ["inbox", "--agent", reader, "--board", board, "--forum", Address],
            };
            foreach (var (name, argv) in commands)
            {
                var (exit, stdout, stderr) = await CuriaAsync(argv);
                if (exit != 0 || !stdout.Contains(postId, StringComparison.Ordinal))
                    problems.Add($"curia {name} on a stored board: exit {exit}; shows the post: {stdout.Contains(postId, StringComparison.Ordinal)}; {stderr.Trim()}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
