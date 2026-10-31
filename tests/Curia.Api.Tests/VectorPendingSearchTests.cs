using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Tests.Shared;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G51 (R9.51) at the search route: probes PIL23 to PIL25. <c>vector_pending</c> is the serving process's count of committed posts it holds as pending.
/// Each fact runs on a host of its own whose clock has timers (ruling 15), so the backlog it counts is the one the fact made, and heals it by advancing that clock
/// in one-second steps (the catch-up's tick). The oracles are the search response's own member, read from several queries, and the events table for what was stored.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class VectorPendingSearchTests(AgentWriteGateForumFixture forum) : IClassFixture<AgentWriteGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static async Task<string> PendingAsync(HttpClient client, string query)
    {
        using var search = await IndexLag.SearchAsync(client, query, Ct);
        return IndexLag.Pending(search);
    }

    /// <summary>
    /// PIL23. The count: <c>vector_pending</c> is present, an integer, and 0 on a healthy Forum; a post with nothing to embed leaves it 0; it is N after N failed commits,
    /// whatever the query (a filter, a cursor, a query that matches nothing); and 0 again after the catch-up, with no restart.
    /// </summary>
    [Fact]
    public async Task R9_51_PIL23_TheSearchStatesTheProcessesPendingCount()
    {
        var clock = TimerClockHost.At(forum);
        await using var host = TimerClockHost.Of(forum, clock);
        using var client = host.CreateClient();
        var index = AgentWriteGateForumFixture.IndexOf(host.Services);
        var board = Probe.Board();
        var term = "quokka" + Probe.Suffix();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-count", Ct);
        var other = await AcceptedAnswerScene.EnrolAsync(forum, "lag-count2", Ct);

        var healthy = await PendingAsync(client, $"board={board}");
        await Probe.SendViaAsync(forum, client, actor, actor.Agent.SignQuestion(board, "!!!", "???", forum.Now, tags: []), null, Ct);
        var afterNoFeatures = await PendingAsync(client, $"board={board}");

        index.FailUpserts = true;
        await Probe.SendViaAsync(forum, client, actor, actor.Agent.SignQuestion(board, $"{term} one {Probe.Unrelated()}", "About " + Probe.Unrelated(), forum.Now), null, Ct);
        await Probe.SendViaAsync(forum, client, other, other.Agent.SignQuestion(board, $"{term} two {Probe.Unrelated()}", "About " + Probe.Unrelated(), forum.Now), null, Ct);
        index.FailUpserts = false;

        var plain = await PendingAsync(client, $"q={term}");
        var filtered = await PendingAsync(client, $"q={term}&board={board}");
        var nothing = await PendingAsync(client, "q=zzzznothingmatchesthis" + Probe.Suffix());
        using var first = await IndexLag.SearchAsync(client, $"q={term}&board={board}&limit=1", Ct);
        var cursor = first.RootElement.TryGetProperty("next_cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var withCursor = cursor is null ? "no cursor" : await PendingAsync(client, $"q={term}&board={board}&limit=1&cursor={Uri.EscapeDataString(cursor)}");

        await IndexLag.AdvanceUntilAsync(clock, async () => await PendingAsync(client, $"board={board}") == "0", 12, Ct);
        var healed = await PendingAsync(client, $"board={board}");

        Said(
            "healthy 0; after a post with nothing to embed 0; after 2 failed commits 2 (plain 2, filtered 2, no match 2, cursor 2); after the catch-up 0",
            $"healthy {healthy}; after a post with nothing to embed {afterNoFeatures}; after 2 failed commits {plain} (plain {plain}, filtered {filtered}, no match {nothing}, cursor {withCursor}); after the catch-up {healed}");
    }

    /// <summary>
    /// PIL24. No hit is marked, and the lexical channel finds the pending post: a search for the pending post's phrase returns it with a lexical term and no vector term,
    /// and no member of the hit or its post names the lag. The positive half is the post's own receipt, <c>201</c> <c>pending</c>.
    /// </summary>
    [Fact]
    public async Task R9_51_PIL24_APendingPostIsFoundByWordsAndNoHitIsMarked()
    {
        var clock = TimerClockHost.At(forum);
        await using var host = TimerClockHost.Of(forum, clock);
        using var client = host.CreateClient();
        var index = AgentWriteGateForumFixture.IndexOf(host.Services);
        var board = Probe.Board();
        var term = "quokka" + Probe.Suffix();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-lex", Ct);

        index.FailUpserts = true;
        var sent = await Probe.SendViaAsync(forum, client, actor, actor.Agent.SignQuestion(board, $"{term} marzipan {Probe.Unrelated()}", "About " + term, forum.Now), null, Ct);
        index.FailUpserts = false;
        var postId = (await IndexLag.PostIdsOfAsync(forum, actor.Agent.AgentId)).FirstOrDefault() ?? "none";

        using var search = await IndexLag.SearchAsync(client, $"q={term}&board={board}&why=true", Ct);
        var hit = search.RootElement.TryGetProperty("results", out var results) ? results.EnumerateArray().FirstOrDefault(r => r.GetProperty("post").GetProperty("post_id").GetString() == postId) : default;
        var found = hit.ValueKind == JsonValueKind.Object;
        var lexical = found && hit.TryGetProperty("why_ranked", out var why) && why.TryGetProperty("lexical", out _);
        var vector = found && hit.TryGetProperty("why_ranked", out var why2) && why2.TryGetProperty("vector", out _);
        var names = found ? hit.EnumerateObject().Select(p => p.Name).Concat(hit.GetProperty("post").EnumerateObject().Select(p => p.Name)) : [];
        var marked = names.Any(n => n.Contains("pending", StringComparison.OrdinalIgnoreCase) || n.Contains("vector_index", StringComparison.OrdinalIgnoreCase) || n.Contains("lag", StringComparison.OrdinalIgnoreCase));

        Said(
            $"the post's receipt 201 {PublishedIndexLag.Values().Pending}; found by a lexical term and no vector term; no member of the hit names the lag",
            $"the post's receipt {IndexLag.Says(sent)}; {(found ? "found" : "not found")} {(lexical ? "by a lexical term" : "without a lexical term")} and {(vector ? "a vector term" : "no vector term")}; {(marked ? "a member of the hit names the lag" : "no member of the hit names the lag")}");
    }

    /// <summary>
    /// PIL25. The cursor survives the count: a cursor minted while two posts are pending is accepted when none is, and the page is the same. The pending posts are
    /// unrelated to the query and on another board, so healing them moves the count and not the list the cursor indexes.
    /// </summary>
    [Fact]
    public async Task R9_51_PIL25_ACursorMintedAtOneCountIsAcceptedAtAnother()
    {
        var clock = TimerClockHost.At(forum);
        await using var host = TimerClockHost.Of(forum, clock);
        using var client = host.CreateClient();
        var index = AgentWriteGateForumFixture.IndexOf(host.Services);
        var board = Probe.Board();
        var term = "quokka" + Probe.Suffix();
        var poster = await AcceptedAnswerScene.EnrolAsync(forum, "lag-cursor", Ct);
        for (var i = 0; i < 3; i++)
            await Probe.SendViaAsync(forum, client, poster, poster.Agent.SignQuestion(board, $"{term} number {i} {Probe.Unrelated()}", $"About {term} {i}", forum.Now), null, Ct);

        index.FailUpserts = true;
        for (var i = 0; i < 2; i++)
        {
            var stranger = await AcceptedAnswerScene.EnrolAsync(forum, "lag-cursor-p", Ct);
            await Probe.SendViaAsync(forum, client, stranger, stranger.Agent.SignQuestion(Probe.Board(), Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), null, Ct);
        }

        index.FailUpserts = false;
        var query = $"q={term}&board={board}&limit=1";
        using var minted = await IndexLag.SearchAsync(client, query, Ct);
        var cursor = minted.RootElement.TryGetProperty("next_cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var cursorQuery = cursor is null ? null : $"{query}&cursor={Uri.EscapeDataString(cursor)}";

        using var atTwo = cursorQuery is null ? null : await IndexLag.SearchAsync(client, cursorQuery, Ct);
        await IndexLag.AdvanceUntilAsync(clock, async () => await PendingAsync(client, $"board={board}") == "0", 12, Ct);
        using var atZero = cursorQuery is null ? null : await IndexLag.SearchAsync(client, cursorQuery, Ct);

        string Describe(JsonDocument? page) => page is null ? "no cursor" : $"{(page.RootElement.TryGetProperty("results", out _) ? "a page" : "refused")} with vector_pending {IndexLag.Pending(page)}";
        var same = atTwo is not null && atZero is not null && IndexLag.Ids(atTwo).SequenceEqual(IndexLag.Ids(atZero)) && IndexLag.Ids(atTwo).Length > 0;

        Said(
            "the minted cursor is accepted at 2 pending (a page with vector_pending 2) and at 0 (a page with vector_pending 0), the same page",
            $"the minted cursor is accepted at 2 pending ({Describe(atTwo)}) and at 0 ({Describe(atZero)}), {(same ? "the same page" : "not the same page")}");
    }
}
