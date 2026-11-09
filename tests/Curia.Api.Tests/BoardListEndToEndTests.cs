using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Curia.Api.Tests.BoardListProbe;
using static Curia.Api.Tests.NameProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R9.58 (errata G63), LOB4 to LOB10 and LOB14 to LOB17, at the route: <c>GET /v1/boards</c> lists the boards a post of a discussion kind that moderation serves is on, with the counts, in the
/// order and the bound R9.58 states, from a snapshot cursor, with an exact <c>board</c> filter, and holds its authorization, its reads and its faults to the per-board listing's.
///
/// <para><b>Oracles.</b> Never the function that folds the listing. LOB4's is a <c>GROUP BY</c> over the events table (<see cref="BoardListProbe.SqlRowsAsync"/>); LOB5's are two other routes (the per-board
/// listing and <c>GET /v1/search</c>'s <c>unmatched_filters</c>); LOB6 stores, withholds and restores through the moderation writer; LOB7 and LOB8 count what the test stored. Each refusing row has its
/// control (the same request that is served, or the first page) in the same assertion, and asserts the type string, the event count before and after and nothing echoed. Facts that need every board to be
/// their own (an order, a total, an empty log) run on a Forum of their own (<see cref="BoardListProbe.FreshAsync"/>).</para>
///
/// <para>Predicted red on the seam: the route answers an empty listing (<c>200, 0 rows, total 0</c>) for every request, so every fact that expects a row, a count or a refusal is red; LOB14 to LOB17 are green
/// controls (the route asks the pair, reads the log once, appends nothing and is a <c>503</c> under an unreadable log).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class BoardListEndToEndTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Truth(bool value) => value ? "listed" : "not listed";

    /// <summary>The words check: the types, the tail and the limits are the ones the facts use (a control, green on the seam).</summary>
    [Fact]
    public void R9_58_LOB4_ThePublishedWordsAreTheOnesTheFactsUse()
    {
        Assert.Equal(["curia/board-list/unknown-parameter", "curia/board-list/blank-parameter", "curia/board-list/invalid-limit", "curia/board-list/cursor-malformed"], PublishedBoardListWords.Types());
        Assert.Equal((50, 100, "this route reads exactly these: board, limit, cursor"), (PublishedBoardListWords.DefaultLimit(), PublishedBoardListWords.MaximumLimit(), PublishedBoardListWords.UnknownDetailTail()));
    }

    /// <summary>
    /// LOB4, derived from SQL. A corpus with no moderation: a board of three questions by three authors and a comment, a board of two questions by one author, a board holding only a comment, a board
    /// holding only a vote, and a board of one question. The rows of the response for these five boards equal the <c>GROUP BY</c> over the events table, in order (board, posts, authors, latest). The
    /// first assertion is that the SQL oracle itself holds four rows, so a corpus the SQL cannot see is not an agreement. Predicted red on the seam: no rows.
    /// </summary>
    [Fact]
    public async Task R9_58_LOB4_TheRowsEqualAGroupByOverTheEventsTable()
    {
        var (b1, b2, b3, b4, b5) = (Name("lob4a"), Name("lob4b"), Name("lob4c"), Name("lob4d"), Name("lob4e"));
        var first = await StoreAsync(forum, b1, 3, 3, Ct);
        forum.Clock.Advance(TimeSpan.FromMinutes(5));   // the greatest instant of a board is not its first
        await StoreCommentAsync(forum, b1, first[0].PostId, Ct);
        await StoreAsync(forum, b2, 2, 1, Ct);
        await StoreCommentAsync(forum, b3, first[0].PostId, Ct);
        await StoreVoteAsync(forum, b4, first[0].Digest, Ct);
        await StoreAsync(forum, b5, 1, 1, Ct);
        string[] mine = [b1, b2, b3, b4, b5];

        var sql = await SqlRowsAsync(forum, mine, Ct);
        var reply = await ListAsync(forum.Client, "limit=100", Ct);
        var served = (Rows(reply) ?? []).Where(r => mine.Contains(r.Board)).ToList();

        Assert.True(sql.Count == 4, $"NOT REACHED: the SQL oracle holds {sql.Count} rows where the corpus has four boards with a discussion post");
        Said(
            sql.Select(r => $"{r.Board} {r.Posts} posts {r.Authors} authors latest {r.Latest.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)[..23]}"),
            served.Select(r => $"{r.Board} {r.Posts} posts {r.Authors} authors latest {DateTimeOffset.Parse(r.Latest, CultureInfo.InvariantCulture).UtcDateTime.ToString("O", CultureInfo.InvariantCulture)[..23]}"));
    }

    /// <summary>
    /// LOB5, derived from the per-board listing and from search. For each listed board <c>posts</c>, <c>authors</c> and <c>latest_post_at</c> are the per-board listing's length, its distinct
    /// <c>provenance.author</c> and its greatest <c>server_ts</c>, and <c>search?board=</c> states <c>unmatched_filters</c> <c>[]</c>; for a board that is not listed (a typo, a board of votes only, a board
    /// whose only post is withheld) the per-board listing is empty and search states <c>[board]</c>. The boards R8.85 cannot address (<c>a/b</c>, <c>.</c>, <c>..</c>) are judged by search and SQL alone; the
    /// per-board route's own answers for them are recorded in the message and not read as a count (finding 11). Predicted red on the seam: the listed boards are not listed.
    /// </summary>
    [Fact]
    public async Task R9_58_LOB5_TheCountsAreTheBoardsOwnListingsAndTheUnlistedNamesAreUnmatchedByName()
    {
        var (listed, votesOnly, withheld, typo) = (Name("lob5a"), Name("lob5v"), Name("lob5w"), Name("lob5t"));
        var stored = await StoreAsync(forum, listed, 4, 2, Ct);
        forum.Clock.Advance(TimeSpan.FromMinutes(5));
        await StoreCommentAsync(forum, listed, stored[0].PostId, Ct);
        await StoreVoteAsync(forum, votesOnly, stored[0].Digest, Ct);
        var hidden = (await StoreAsync(forum, withheld, 1, 1, Ct))[0];
        await forum.WithholdAsync(hidden.PostId, Ct);

        var listing = Rows(await ListAsync(forum.Client, "limit=100", Ct)) ?? [];
        var expected = new List<string>();
        var actual = new List<string>();

        var own = await PerBoardAsync(forum.Client, listed, Ct);
        var row = listing.FirstOrDefault(r => r.Board == listed);
        expected.Add($"{listed}: {own!.Value.Posts} posts {own.Value.Authors} authors latest {own.Value.Latest.UtcDateTime:O}");
        actual.Add($"{listed}: {(row is null ? "no row" : $"{row.Posts} posts {row.Authors} authors latest {DateTimeOffset.Parse(row.Latest, CultureInfo.InvariantCulture).UtcDateTime:O}")}");

        foreach (var board in new[] { listed, votesOnly, withheld, typo })
        {
            var inListing = listing.Any(r => r.Board == board);
            var perBoard = await PerBoardAsync(forum.Client, board, Ct);
            var search = await SearchAsync(forum.Client, "board=" + Uri.EscapeDataString(board), Ct);
            var should = board == listed;
            expected.Add($"{board}: {Truth(should)}, per-board {(should ? "non-empty" : "empty")}, search {(should ? "[]" : "[board]")}");
            actual.Add($"{board}: {Truth(inListing)}, per-board {(perBoard is { Posts: > 0 } ? "non-empty" : "empty")}, search {search.Unmatched}");
        }

        // The names the per-board route cannot address: stored through the seam, read by search and SQL only; the route's own answers are recorded, not asserted.
        var recorded = new List<string>();
        foreach (var odd in new[] { "lob5/" + Guid.NewGuid().ToString("N")[..6], ".", ".." })
        {
            await StoreAsync(forum, odd, 1, 1, Ct);
            var sql = await SqlRowsAsync(forum, [odd], Ct);
            var search = await SearchAsync(forum.Client, "board=" + Uri.EscapeDataString(odd), Ct);
            var perBoard = await GetAsync(forum.Client, "/v1/boards/" + Uri.EscapeDataString(odd) + "/posts", ct: Ct);
            recorded.Add($"{odd}: per-board route answers {(int)perBoard.Status}");
            expected.Add($"{odd}: listed, sql {sql[0].Posts} post, search []");
            actual.Add($"{odd}: {Truth(listing.Any(r => r.Board == odd) || Rows(await ListAsync(forum.Client, "board=" + Uri.EscapeDataString(odd), Ct)) is { Count: > 0 })}, sql {sql[0].Posts} post, search {search.Unmatched}");
        }

        try
        {
            Said(expected, actual);
        }
        catch (Xunit.Sdk.XunitException ex)
        {
            throw new Xunit.Sdk.XunitException(ex.Message + Environment.NewLine + "Recorded, not asserted: " + string.Join("; ", recorded));
        }
    }

    /// <summary>
    /// LOB6, withholding. A board whose only post is withheld is not listed; restoring the post lists it with <c>posts</c> 1; a board with one withheld post and one served post says <c>posts</c> 1. The
    /// restore step is the control: the same board, served again. Predicted red on the seam: the restored board and the mixed board are not listed.
    /// </summary>
    [Fact]
    public async Task R9_58_LOB6_AWithheldPostIsNotCountedAndARestoredOneIs()
    {
        var (only, mixed) = (Name("lob6o"), Name("lob6m"));
        var onlyPost = (await StoreAsync(forum, only, 1, 1, Ct))[0];
        var mixedPosts = await StoreAsync(forum, mixed, 2, 2, Ct);
        await forum.WithholdAsync(onlyPost.PostId, Ct);
        await forum.WithholdAsync(mixedPosts[0].PostId, Ct);

        async Task<string> Describe(string board) => Rows(await ListAsync(forum.Client, "board=" + board, Ct)) is { Count: > 0 } rows ? $"{rows[0].Posts} posts" : "not listed";

        var withheldOnly = await Describe(only);
        var mixedRow = await Describe(mixed);
        await forum.RestoreAsync(onlyPost.PostId, Ct);
        var restored = await Describe(only);

        Said(["withheld only: not listed", "mixed: 1 posts", "restored: 1 posts"], [$"withheld only: {withheldOnly}", $"mixed: {mixedRow}", $"restored: {restored}"]);
    }

    /// <summary>
    /// LOB7, order and bound, on a Forum of its own: 200 boards of one post and three real boards of 5, 3 and 3 (stored through the seam). The first rows are the three in order (the tie by ordinal name);
    /// the default page is 50 rows and <c>limit=100</c> is 100; <c>limit=0</c>, <c>101</c>, <c>abc</c> and an empty <c>limit=</c> are <c>invalid-limit</c> with <c>maximum=100</c> beside the served control
    /// <c>limit=50</c>; <c>total</c> is 203; paging by <c>next_cursor</c> returns every board once. Predicted red on the seam: no rows, total 0.
    /// </summary>
    [Fact]
    public async Task R9_58_LOB7_TheListingIsOrderedByPostsThenNameAndBoundedAndPaged()
    {
        await using var fresh = await FreshAsync();
        foreach (var (board, posts) in new[] { ("real-a", 5), ("real-b", 3), ("real-c", 3) })
            await StoreAsync(fresh, board, posts, 2, Ct);
        for (var i = 0; i < 200; i++)
            await StoreAsync(fresh, $"flood-{i:D3}", 1, 1, Ct);

        var invalidType = PublishedBoardListWords.Types()[2];
        var first = await ListAsync(fresh.Client, string.Empty, Ct);
        var wide = await ListAsync(fresh.Client, "limit=100", Ct);
        var expected = new List<string> { "default: 200, 50 rows, total 203, next_cursor; first three real-a 5, real-b 3, real-c 3", "limit=100: 200, 100 rows, total 203, next_cursor" };
        var actual = new List<string>
        {
            $"default: {Summary(first)}; first three {string.Join(", ", (Rows(first) ?? []).Take(3).Select(r => $"{r.Board} {r.Posts}"))}",
            $"limit=100: {Summary(wide)}",
        };

        foreach (var limit in new[] { "0", "101", "abc", string.Empty })
        {
            var refused = await ListAsync(fresh.Client, "limit=" + limit, Ct);
            expected.Add($"limit={limit}: 400 {invalidType}, maximum=100");
            actual.Add($"limit={limit}: {refused.Says}, {(refused.Body.Contains("maximum=100", StringComparison.Ordinal) ? "maximum=100" : "no maximum")}");
        }

        var control = await ListAsync(fresh.Client, "limit=50", Ct);
        expected.Add("limit=50: 200, 50 rows, total 203, next_cursor");
        actual.Add($"limit=50: {Summary(control)}");

        // Paging: the union of the pages is the whole list, once each.
        var seen = new List<string>();
        var cursor = default(string);
        for (var page = 0; page < 6; page++)
        {
            var reply = await ListAsync(fresh.Client, "limit=100" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), Ct);
            seen.AddRange((Rows(reply) ?? []).Select(r => r.Board));
            cursor = NextCursor(reply);
            if (cursor is null) break;
        }

        expected.Add("paged: 203 boards, 203 distinct");
        actual.Add($"paged: {seen.Count} boards, {seen.Distinct(StringComparer.Ordinal).Count()} distinct");
        Said(expected, actual);
    }

    /// <summary>
    /// LOB8, snapshot, on a Forum of its own: 60 boards, a first page of 50 and its cursor; a post on a board of the second page and a new board added after the first page was read change no row of the second
    /// page and not <c>total</c> when it is read with the same cursor; a fresh query shows them. Predicted red on the seam: no rows and no cursor.
    /// </summary>
    [Fact]
    public async Task R9_58_LOB8_ASecondPageIsCutFromTheCorpusTheFirstWasCutFrom()
    {
        await using var fresh = await FreshAsync();
        for (var i = 0; i < 60; i++)
            await StoreAsync(fresh, $"snap-{i:D2}", 1, 1, Ct);

        var first = await ListAsync(fresh.Client, "limit=50", Ct);
        var cursor = NextCursor(first);
        var before = cursor is null ? null : await ListAsync(fresh.Client, "limit=50&cursor=" + Uri.EscapeDataString(cursor), Ct);

        await StoreAsync(fresh, "snap-59", 1, 1, Ct);
        await StoreAsync(fresh, "snap-late", 1, 1, Ct);
        var after = cursor is null ? null : await ListAsync(fresh.Client, "limit=50&cursor=" + Uri.EscapeDataString(cursor), Ct);
        var fresh1 = await ListAsync(fresh.Client, "limit=100", Ct);

        Said(
            ["page 2 is the same rows and the same total after the writes", "a fresh query lists 61 boards and snap-59 has 2 posts"],
            [
                $"page 2 is {(before is not null && after is not null && Describe(Rows(before) ?? []) == Describe(Rows(after) ?? []) && Total(before) == Total(after) && Rows(before)!.Count > 0 ? "the same rows and the same total after the writes" : $"not the same: before {(before is null ? "no cursor" : Summary(before))}, after {(after is null ? "no cursor" : Summary(after))}")}",
                $"a fresh query lists {Total(fresh1)} boards and snap-59 has {(Rows(fresh1) ?? []).FirstOrDefault(r => r.Board == "snap-59")?.Posts ?? 0} posts",
            ]);
    }

    /// <summary>
    /// LOB9, cursor refusals: the search route's cursor, the inbox's, a malformed one and a bound beyond the log's newest event are each <c>400</c> <c>curia/board-list/cursor-malformed</c>, the type string, no
    /// echo of the cursor, the event count unchanged, beside the first page (the control, served). Predicted red on the seam: <c>200</c> for each.
    /// </summary>
    [Fact]
    public async Task R9_58_LOB9_ACursorTheRouteDidNotMintIsRefusedAndNothingIsEchoed()
    {
        await StoreAsync(forum, Name("lob9"), 1, 1, Ct);
        var newest = CorpusBound(await ListAsync(forum.Client, string.Empty, Ct));
        var type = PublishedBoardListWords.Types()[3];
        var cursors = new (string Name, string Cursor)[]
        {
            ("a search cursor", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("c1:0:0123456789abcdef"))),
            ("an inbox cursor", Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("1:1"))),
            ("a malformed one", "@@@"),
            ("a bound beyond the newest event", new BoardListCursor(Math.Max(newest, 0) + 1000, 0).Encode()),
        };

        var events = await EventCountAsync(forum, Ct);
        var expected = new List<string> { "the first page: 200 as a listing" };
        var actual = new List<string> { $"the first page: {(Rows(await ListAsync(forum.Client, string.Empty, Ct)) is null ? "not a listing" : "200 as a listing")}" };
        foreach (var (name, cursor) in cursors)
        {
            var reply = await ListAsync(forum.Client, "cursor=" + Uri.EscapeDataString(cursor), Ct);
            expected.Add($"{name}: 400 {type}, no echo");
            actual.Add($"{name}: {reply.Says}, {(reply.Body.Contains(cursor, StringComparison.Ordinal) ? "echoes it" : "no echo")}");
        }

        expected.Add("events unchanged");
        actual.Add((await EventCountAsync(forum, Ct)) == events ? "events unchanged" : "events changed");
        Said(expected, actual);
    }

    /// <summary>
    /// LOB10, the filter. <c>board=&lt;stored&gt;</c> is its row (total 1); the upper-case spelling and a decomposed spelling of a stored name are no row (total 0); a present blank <c>board</c> or <c>cursor</c> is
    /// <c>blank-parameter</c> naming it (<c>parameter=board</c>, <c>parameter=board, cursor</c>); a NUL, an invalid sequence and an over-cap name are <c>200</c> and empty, never a <c>5xx</c> (the
    /// route's totality over the closed fuzz's alphabet is the fuzz's); and for every listed name <c>search?board=</c> states <c>[]</c> while for every other it states <c>[board]</c>. Predicted red on the seam:
    /// the stored board has no row, the blank forms are <c>200</c>.
    /// </summary>
    [Fact]
    public async Task R9_58_LOB10_TheBoardFilterIsExactAndABlankOneIsRefused()
    {
        var composed = "lob10-caf" + char.ConvertFromUtf32(0xE9);
        var decomposed = "lob10-cafe" + char.ConvertFromUtf32(0x301);
        var board = Name("lob10");
        await StoreAsync(forum, board, 2, 1, Ct);
        await StoreAsync(forum, composed, 1, 1, Ct);
        var blank = PublishedBoardListWords.Types()[1];

        var expected = new List<string>();
        var actual = new List<string>();

        async Task Row(string label, string query, string wanted)
        {
            var reply = await ListAsync(forum.Client, query, Ct);
            expected.Add($"{label}: {wanted}");
            actual.Add($"{label}: {(Rows(reply) is { } rows ? $"200, {rows.Count} rows, total {Total(reply)}{(rows.Count > 0 ? ", " + rows[0].Board : string.Empty)}" : reply.Says + (reply.Body.Contains("parameter=", StringComparison.Ordinal) ? " " + reply.Body[(reply.Body.IndexOf("parameter=", StringComparison.Ordinal))..].Split('"')[0] : string.Empty))}");
        }

        await Row("the stored board", "board=" + board, $"200, 1 rows, total 1, {board}");
        await Row("upper case", "board=" + board.ToUpperInvariant(), "200, 0 rows, total 0");
        await Row("decomposed", "board=" + Uri.EscapeDataString(decomposed), "200, 0 rows, total 0");
        await Row("blank board", "board=", $"400 {blank} parameter=board");
        await Row("blank cursor", "cursor=", $"400 {blank} parameter=cursor");
        await Row("both blank", "board=&cursor=", $"400 {blank} parameter=board, cursor");
        await Row("a NUL", "board=%00", "200, 0 rows, total 0");
        await Row("an invalid sequence", "board=%ED%A0%80", "200, 0 rows, total 0");
        await Row("over the cap", "board=" + new string('r', 70), "200, 0 rows, total 0");

        var all = new HashSet<string>([board, composed], StringComparer.Ordinal);   // what this test stored: the oracle is not the listing under test
        var agreements = new List<string>();
        foreach (var name in new[] { board, composed, board.ToUpperInvariant(), decomposed, Name("lob10-typo") })
        {
            var search = await SearchAsync(forum.Client, "board=" + Uri.EscapeDataString(name), Ct);
            agreements.Add($"{name}: search {(all.Contains(name) ? "[]" : "[board]")} = {search.Unmatched}");
        }

        expected.AddRange(agreements.Select(a => a[..a.LastIndexOf(" = ", StringComparison.Ordinal)]));
        actual.AddRange(agreements.Select(a => a[..a.IndexOf(" search ", StringComparison.Ordinal)] + " search " + a[(a.LastIndexOf(" = ", StringComparison.Ordinal) + 3)..]));
        Said(expected, actual);
    }

    // ---- LOB14 to LOB17: controls on the seam ----

    /// <summary>A decorator that records every request the policy is asked and denies <c>board</c> | <c>list</c> while <c>deny</c> says so.</summary>
    private sealed class RecordingDecisions(IPolicyDecisionPoint inner, List<(ResourceKind, ActionKind)> asked, Func<bool> deny) : IPolicyDecisionPoint
    {
        public ValueTask<Result<AuthorizationDecision>> EvaluateAsync(AuthorizationRequest request, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            lock (asked) asked.Add((request.Resource, request.Action));
            return deny() && request.Resource == ResourceKind.Board && request.Action == ActionKind.List
                ? ValueTask.FromResult(Result<AuthorizationDecision>.Ok(new AuthorizationDecision(DecisionEffect.Deny, "table-10/denied", GrantQualifier.None)))
                : inner.EvaluateAsync(request, cancellationToken);
        }
    }

    private WebApplicationFactory<Program> Host(List<(ResourceKind, ActionKind)> asked, Func<bool> deny, Counter? counter = null) =>
        forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            if (counter is not null) services.CountRequestReads(counter);
            var decisions = services.Last(d => d.ServiceType == typeof(IPolicyDecisionPoint));
            if (decisions.ImplementationFactory is null) throw new InvalidOperationException("IPolicyDecisionPoint is registered by a factory; this decorator wraps what it builds");
            services.Remove(decisions);
            services.AddSingleton<IPolicyDecisionPoint>(sp => new RecordingDecisions((IPolicyDecisionPoint)decisions.ImplementationFactory(sp), asked, deny));
        }));

    /// <summary>
    /// LOB14, authorization: an anonymous <c>GET /v1/boards</c> is asked as <c>board</c> | <c>list</c> and nothing else; a policy that denies that pair denies this route and the per-board listing alike (one
    /// decision governs both, R7.6, a stated non-property: <c>board</c> | <c>read</c> is exercised by no route). A control on the seam: the route asks the pair and shares it. (The "pair the model lacks is a failure and
    /// not a denial" row is <c>Table10ConformanceTests</c>'s and is unchanged by this PR.)
    /// </summary>
    [Fact]
    public async Task R9_58_LOB14_TheRouteIsAuthorizedAsBoardListAndTheDecisionIsShared_Control()
    {
        var asked = new List<(ResourceKind, ActionKind)>();
        var deny = false;
        await using var host = Host(asked, () => Volatile.Read(ref deny));
        using var client = host.CreateClient();
        var board = Name("lob14");
        await StoreAsync(forum, board, 1, 1, Ct);

        asked.Clear();
        var allowed = await ListAsync(client, string.Empty, Ct);
        string[] pairs;
        lock (asked) pairs = [.. asked.Select(p => $"{p.Item1}|{p.Item2}").Distinct()];
        Volatile.Write(ref deny, true);
        var denied = await ListAsync(client, string.Empty, Ct);
        var perBoard = await GetAsync(client, $"/v1/boards/{board}/posts", ct: Ct);

        Said(
            ["allowed: 200, asked Board|List", "denied: this route 403 curia/authz/denied, the per-board listing 403 curia/authz/denied"],
            [$"allowed: {(int)allowed.Status}, asked {string.Join(", ", pairs)}", $"denied: this route {denied.Says}, the per-board listing {perBoard.Says}"]);
    }

    /// <summary>
    /// LOB15, no event, no hold. List reads (served, filtered, refused as unknown, blank, over the limit) append nothing: the event count is the same after them; on a Forum of its own with nothing in the log the
    /// listing is <c>200</c> with no row and <c>total</c> 0. A control on the seam. (That the route takes no agent's write hold is held by the route taking no agent: it is anonymous, and the live run's burst leg, L8, is
    /// where a list read beside a held write is seen.)
    /// </summary>
    [Fact]
    public async Task R9_58_LOB15_AListReadAppendsNothingAndAnEmptyForumIsAnEmptyListing_Control()
    {
        await StoreAsync(forum, Name("lob15"), 1, 1, Ct);
        var before = await EventCountAsync(forum, Ct);
        foreach (var query in new[] { string.Empty, "board=nowhere", "x=1", "board=", "limit=0", "cursor=@@@" })
            _ = await ListAsync(forum.Client, query, Ct);

        await using var fresh = await FreshAsync();
        var empty = await ListAsync(fresh.Client, string.Empty, Ct);

        Said(["events unchanged", "an empty Forum: 200, 0 rows, total 0"], [(await EventCountAsync(forum, Ct)) == before ? "events unchanged" : "events changed", $"an empty Forum: {Summary(empty)}"]);
    }

    /// <summary>LOB16, reads: one whole-log read per served request, counted by <c>CountRequestReads</c> inside the request, equal to the per-board listing's. A control on the seam; FLOB25 turns it red.</summary>
    [Fact]
    public async Task R9_58_LOB16_ARequestReadsTheLogOnceAsThePerBoardListingDoes_Control()
    {
        var counter = new Counter();
        await using var host = Host([], () => false, counter);
        using var client = host.CreateClient();
        var board = Name("lob16");
        await StoreAsync(forum, board, 1, 1, Ct);

        counter.Count = 0;
        _ = await ListAsync(client, string.Empty, Ct);
        var list = counter.Count;
        counter.Count = 0;
        _ = await GetAsync(client, $"/v1/boards/{board}/posts", ct: Ct);
        var perBoard = counter.Count;

        Said("the per-board listing reads the log once and this route reads it once", $"the per-board listing reads the log {(perBoard == 1 ? "once" : perBoard + " times")} and this route reads it {(list == 1 ? "once" : list + " times")}");
    }

    /// <summary>
    /// LOB49 (Amendment 2, ruling 34): the route reads <c>limit</c> in canonical numerals. <c>limit=+5</c>, <c>%205</c>, <c>5%20</c>, <c>05</c> and <c>0x5</c> are each <c>400</c>
    /// <c>curia/board-list/invalid-limit</c> with the event count unchanged, beside the served controls <c>5</c> and <c>100</c> (FLOB78's first form: <c>limit=+5</c> served on a route that promised no leniency).
    /// </summary>
    [Fact]
    public async Task R9_58_LOB49_TheLimitIsReadInCanonicalNumeralsAndNothingElse()
    {
        await StoreAsync(forum, Name("lob49"), 1, 1, Ct);
        var invalid = PublishedBoardListWords.Types()[2];
        var events = await EventCountAsync(forum, Ct);
        var expected = new List<string>();
        var actual = new List<string>();

        foreach (var (label, query, served) in new[] { ("5", "limit=5", true), ("100", "limit=100", true), ("+5", "limit=+5", false), ("%205", "limit=%205", false), ("5%20", "limit=5%20", false), ("05", "limit=05", false), ("0x5", "limit=0x5", false) })
        {
            var reply = await ListAsync(forum.Client, query, Ct);
            expected.Add($"limit={label}: {(served ? "200 as a listing" : "400 " + invalid)}");
            actual.Add($"limit={label}: {(Rows(reply) is not null ? "200 as a listing" : reply.Says)}");
        }

        expected.Add("events unchanged");
        actual.Add((await EventCountAsync(forum, Ct)) == events ? "events unchanged" : "events changed");
        Said(expected, actual);
    }

    /// <summary>
    /// LOB45 (Amendment 2, ruling 28), a stored payload board that differs from its canonical form, asked of the real store through the direct-append seam: one post whose board is the decomposed spelling.
    /// For each spelling R9.58's own sentence is the expectation: the board is listed exactly when <c>search?board=</c> states <c>unmatched_filters</c> <c>[]</c> and exactly when the per-board listing is not empty
    /// (for a name that route can address). A SQL count of <c>payload-&gt;&gt;'board'</c> is recorded beside them. <b>Red is a finding and stops the build.</b>
    /// </summary>
    [Fact]
    public async Task R9_58_LOB45_AStoredPayloadBoardThatDiffersFromItsCanonicalFormIsListedAsTheOtherReadersSayIt()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var composed = "lob45-" + suffix + "-caf" + char.ConvertFromUtf32(0xE9);
        var decomposed = "lob45-" + suffix + "-cafe" + char.ConvertFromUtf32(0x301);
        // The wire is signed over the composed spelling (the canonical form is NFC), and its envelope is then written with the decomposed one: the stored payload member is read from the envelope as it
        // arrived, and the canonical form from the envelope as it canonicalizes, so the two differ in the one place R9.58's equivalence could fail. The seam does not verify, as a post stored before R8.85 was not.
        var agent = ForumAgent.Create("https://agents.example/lob45-" + suffix, "lob");
        var wire = System.Text.Encoding.UTF8.GetString(agent.SignQuestion(composed, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), forum.Now));
        Assert.True(wire.Contains(composed, StringComparison.Ordinal), "NOT REACHED: the signed wire does not carry the composed spelling to replace");
        _ = await BudgetBursts.StoreAsync(forum, System.Text.Encoding.UTF8.GetBytes(wire.Replace(composed, decomposed, StringComparison.Ordinal)), Ct);

        // The question LOB45 asks is about a payload that differs from its canonical form, so the store must hold the post under the decomposed spelling and not under the composed one.
        var held = (await SqlRowsAsync(forum, [decomposed], Ct)).Sum(r => r.Posts);
        var heldComposed = (await SqlRowsAsync(forum, [composed], Ct)).Sum(r => r.Posts);
        Assert.True(held == 1 && heldComposed == 0, $"NOT REACHED: the events table holds {held} post(s) under the decomposed spelling and {heldComposed} under the composed one");

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, spelling) in new[] { ("composed", composed), ("decomposed", decomposed) })
        {
            var escaped = Uri.EscapeDataString(spelling);
            var listed = (Rows(await ListAsync(forum.Client, "board=" + escaped, Ct)) ?? []).Count > 0;
            var perBoard = (await PerBoardAsync(forum.Client, spelling, Ct)) is { Posts: > 0 };
            var searched = (await SearchAsync(forum.Client, "board=" + escaped, Ct)).Unmatched == "[]";
            var sql = (await SqlRowsAsync(forum, [spelling], Ct)).Sum(r => r.Posts);
            expected.Add($"{label}: the list, the per-board listing and search agree");
            actual.Add($"{label}: {(listed == perBoard && listed == searched ? "the list, the per-board listing and search agree" : $"they disagree (list {Truth(listed)}, per-board {(perBoard ? "non-empty" : "empty")}, search {(searched ? "matched" : "unmatched")}, SQL count {sql})")}");
        }

        Said(expected, actual);
    }
}
