using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.38 and decision 11 of errata G43: a blank <c>cursor</c> is the first page on both routes (PJ5); an inbox cursor is a position, not a page number
/// (PJ6); and a page that exactly exhausts the list offers a cursor whose page is empty (PJ43). The last two are recorded non-properties, each pinned
/// by a probe that flips on purpose if it is decided. (A forged position is read, not refused, because the Forum cannot tell a forged one from a minted
/// one and a search cursor's offset and bound are no secret.)
///
/// <para><b>Oracles.</b> The ids are those the test asked, in the order it asked them; the positions are decoded from cursors the route really minted
/// (<c>base64(&lt;score&gt;:&lt;sequence&gt;)</c>) and forged by editing that text; the malformed rows are the test's own hand list.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class InboxCursorPositionTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Encode(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text));

    private static (long Score, long Sequence) Decode(string cursor)
    {
        var parts = Encoding.ASCII.GetString(Convert.FromBase64String(cursor)).Split(':');
        Assert.Equal(2, parts.Length);
        return (long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), long.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>Questions asked in order, two to an asker (a T0 agent has three a day), on a board of their own, and a reader who asked none.</summary>
    private async Task<(InboxProbe Probe, InboxProbe.Member Reader, string Board, string[] Ids)> SceneAsync(int questions)
    {
        var probe = new InboxProbe(forum);
        var reader = await probe.EnrolAsync("position-reader", Ct);
        var board = "board-" + InboxProbe.Suffix();
        InboxProbe.Member? asker = null;
        for (var i = 0; i < questions; i++)
        {
            if (i % 2 == 0) asker = await probe.EnrolAsync("position-asker", Ct);
            await probe.AskAsync(asker!, board, $"Position {i} {InboxProbe.Suffix()}", Ct);
        }

        return (probe, reader, board, [.. probe.Questions.Select(q => q.Id)]);
    }

    // ---- PJ5 ------------------------------------------------------------------------------------

    /// <summary>PJ5: the Forum still reads a blank cursor as absent on both routes. Green first; FPJ5 makes the Forum refuse a blank one.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("%20")]
    public async Task R9_38_TheForumStillReadsABlankCursorAsAbsent(string blank)
    {
        ArgumentNullException.ThrowIfNull(blank);
        var (probe, reader, board, ids) = await SceneAsync(4);

        var inboxPlain = await probe.InboxPageAsync(reader, $"?board={board}&limit=2", Ct);
        var inboxBlank = await probe.InboxPageAsync(reader, $"?board={board}&limit=2&cursor={blank}", Ct);
        Assert.Equal(inboxPlain.Ids, inboxBlank.Ids);
        Assert.Equal(ids[..2], inboxBlank.Ids);

        string[] SearchIds(string text)
        {
            using var json = JsonDocument.Parse(text);
            return [.. json.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!)];
        }

        using var plain = await forum.Client.GetAsync(new Uri($"/v1/search?board={board}&limit=2", UriKind.Relative), Ct);
        using var withBlank = await forum.Client.GetAsync(new Uri($"/v1/search?board={board}&limit=2&cursor={blank}", UriKind.Relative), Ct);
        Assert.Equal(HttpStatusCode.OK, withBlank.StatusCode);
        Assert.Equal(SearchIds(await plain.Content.ReadAsStringAsync(Ct)), SearchIds(await withBlank.Content.ReadAsStringAsync(Ct)));
    }

    // ---- PJ6 ------------------------------------------------------------------------------------

    /// <summary>
    /// PJ6: an inbox cursor is a keyset position (score descending, then sequence ascending), not a page number. Ahead of every score is the first page;
    /// at or past the last position is an empty page with no cursor; a minted cursor is page two and so is its decoded-and-reencoded copy; a spelling the
    /// route does not write, a negative score and a search cursor are <c>400 curia/inbox/cursor-malformed</c>. Recorded non-property, confirmed live.
    /// </summary>
    [Fact]
    public async Task R9_38_AnInboxCursorIsAPositionNotAPageNumber()
    {
        var (probe, reader, board, ids) = await SceneAsync(5);

        // Walk with limit=1 and keep every minted cursor, so each position is the one the route minted.
        var minted = new List<string>();
        string? cursor = null;
        for (var i = 0; i < ids.Length; i++)
        {
            var page = await probe.InboxPageAsync(reader, $"?board={board}&limit=1" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), Ct);
            Assert.Equal([ids[i]], page.Ids);
            if (page.NextCursor is null) break;
            minted.Add(page.NextCursor);
            cursor = page.NextCursor;
        }

        Assert.True(minted.Count >= 4, $"{minted.Count} cursors were minted over five questions at limit 1");
        var (score, firstSequence) = Decode(minted[0]);
        var (_, lastSequence) = Decode(minted[^1]);

        async Task<(string[] Ids, string? Next)> At(string raw)
        {
            var page = await probe.InboxPageAsync(reader, $"?board={board}&limit=1&cursor={Uri.EscapeDataString(raw)}", Ct);
            return (page.Ids, page.NextCursor);
        }

        // Ahead of every score: the first page.
        foreach (var ahead in new[] { $"5:{firstSequence}", $"9999:{firstSequence}", "1:0", "0:0" })
            Assert.Equal([ids[0]], (await At(Encode(ahead))).Ids);

        // A minted cursor is page two, and so is its copy decoded and encoded again.
        Assert.Equal([ids[1]], (await At(minted[0])).Ids);
        Assert.Equal([ids[1]], (await At(Encode($"{score}:{firstSequence}"))).Ids);

        // At or past the last position: nothing, and nothing further.
        foreach (var behind in new[] { minted[^1], Encode($"{score}:{lastSequence + 1}"), Encode($"{score}:{lastSequence + 1000}") })
        {
            var (empty, next) = await At(behind);
            Assert.Empty(empty);
            Assert.Null(next);
        }

        // A spelling the route does not write is refused, a forged position is not.
        using var search = await forum.Client.GetAsync(new Uri($"/v1/search?board={board}&limit=1", UriKind.Relative), Ct);
        using var searchJson = JsonDocument.Parse(await search.Content.ReadAsStringAsync(Ct));
        var searchCursor = searchJson.RootElement.GetProperty("next_cursor").GetString()!;
        foreach (var raw in new[] { Encode("-1:2"), Encode("0:2:x"), "abc", searchCursor })
        {
            var (status, body) = await probe.InboxRawAsync(reader, $"?board={board}&limit=1&cursor={Uri.EscapeDataString(raw)}", Ct);
            Assert.Equal(HttpStatusCode.BadRequest, status);
            using var json = JsonDocument.Parse(body);
            Assert.Equal("curia/inbox/cursor-malformed", json.RootElement.GetProperty("type").GetString());
        }
    }

    // ---- PJ43 -----------------------------------------------------------------------------------

    /// <summary>
    /// PJ43, recorded non-property (decision 11(c)): 16 open questions at <c>limit=2</c> give 8 full pages; page 8 offers a <c>next_cursor</c> and page 9
    /// is empty with none. One extra request, terminating; a fix would fetch one row more than the page. Green first; it flips on purpose if a fix lands
    /// (no falsification row: it is a fact about the paging).
    /// </summary>
    [Fact]
    public async Task R9_38_AnExactFitLastPageOffersACursorWhosePageIsEmpty()
    {
        var (probe, reader, board, ids) = await SceneAsync(16);

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        for (; pages < 12; pages++)
        {
            var page = await probe.InboxPageAsync(reader, $"?board={board}&limit=2" + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)), Ct);
            seen.AddRange(page.Ids);
            if (pages < 8)
            {
                Assert.Equal(2, page.Ids.Length);
                Assert.NotNull(page.NextCursor);
            }
            else
            {
                Assert.Empty(page.Ids);
                Assert.Null(page.NextCursor);
                break;
            }

            cursor = page.NextCursor;
        }

        Assert.Equal(8, pages);
        Assert.Equal(ids, seen);
    }
}
