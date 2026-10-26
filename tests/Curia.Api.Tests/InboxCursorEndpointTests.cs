using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.38 (errata G37): <c>GET /v1/inbox</c> answers a <c>cursor</c> it did not mint <c>400</c> as
/// <c>curia/inbox/cursor-malformed</c>, echoing no part of the value, and never reads it as a first page; an absent or blank
/// one is a first page; a cursor the route minted continues. The live run at Task 0 sent <c>garbage</c>, <c>@@@</c> and a
/// search cursor, each of which answered page one with exit 0, and found that a walk that follows a corrupted cursor repeats
/// page one for as long as it is followed.
///
/// <para><b>Oracles.</b> The ids are those the test asked, in the order it asked them. The rows of <see cref="Refused"/>
/// are the test's own hand list of spellings the old decoder accepted or read as a first page, and one cursor really minted
/// by <c>/v1/search</c> at test time. A refusal row has its control: a cursor the inbox minted continues.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class InboxCursorEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string MalformedType = "curia/inbox/cursor-malformed";

    private static string Base64(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text));

    /// <summary>Rows are <c>raw:</c> (sent as written), <c>b64:</c> (the base64 of the text) or <c>search</c> (a cursor minted by <c>/v1/search</c> when the fact runs).</summary>
    public static TheoryData<string> Refused => new()
    {
        "raw:@@@",
        "raw:not-base64!!",
        "raw:garbage",
        "b64:1",
        "b64:a:b",
        "b64:1:2:3",
        "b64:+1:2",
        "b64: 1:2",
        "b64:01:2",
        "b64:-1:2",
        "b64:1:-2",
        "b64:1: 2",
        "b64:99999999999:1",
        "b64:1:99999999999999999999",
        "b64:c6:1:0123456789abcdef",
        "search",
    };

    /// <summary>Five open questions by two askers on a board of their own, in the order asked, and a reader who asked none of them.</summary>
    private async Task<(InboxProbe Probe, InboxProbe.Member Reader, string Board, string[] Ids)> SceneAsync(CancellationToken ct)
    {
        var probe = new InboxProbe(forum);
        var a = await probe.EnrolAsync("cursor-a", ct);
        var b = await probe.EnrolAsync("cursor-b", ct);
        var reader = await probe.EnrolAsync("cursor-reader", ct);
        var board = "board-" + InboxProbe.Suffix();
        var order = new[] { a, b, a, b, a };
        for (var i = 0; i < order.Length; i++)
            await probe.AskAsync(order[i], board, $"Walk {i} {InboxProbe.Suffix()}", ct);

        return (probe, reader, board, [.. probe.Questions.Select(q => q.Id)]);
    }

    private async Task<string> CursorValueAsync(string row, CancellationToken ct)
    {
        if (row == "search")
        {
            // A real retrieval cursor, minted by the route that mints them: three questions to page over, one per page.
            var probe = new InboxProbe(forum);
            var asker = await probe.EnrolAsync("cursor-search", ct);
            var word = "quokka" + InboxProbe.Suffix();
            for (var i = 0; i < 2; i++)
                await probe.AskAsync(asker, "board-" + InboxProbe.Suffix(), $"About {word} {i}", ct);

            using var response = await forum.Client.GetAsync(new Uri($"/v1/search?q={word}&limit=1", UriKind.Relative), ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            Assert.True(response.StatusCode == HttpStatusCode.OK, body);
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("next_cursor").GetString()
                ?? throw new InvalidOperationException("the search page minted no cursor: the fixture holds too few posts for the word");
        }

        var (kind, text) = (row[..row.IndexOf(':', StringComparison.Ordinal)], row[(row.IndexOf(':', StringComparison.Ordinal) + 1)..]);
        return kind == "raw" ? text : Base64(text);
    }

    // ---- PD8, PD12, PD13: a cursor the route did not mint --------------------------------------

    /// <summary>PD8: each spelling the route does not write is a <c>400</c>, with a control that a cursor the inbox minted continues.</summary>
    [Theory]
    [MemberData(nameof(Refused))]
    public async Task R9_38_AnInboxCursorTheRouteDidNotMintIsRefused(string row)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(row);
        var (probe, reader, board, ids) = await SceneAsync(ct);

        var first = await probe.InboxPageAsync(reader, $"?board={board}&limit=1", ct);
        Assert.NotNull(first.NextCursor);
        var control = await probe.InboxPageAsync(reader, $"?board={board}&limit=1&cursor={Uri.EscapeDataString(first.NextCursor)}", ct);
        Assert.Equal(new[] { ids[1] }, control.Ids);

        var cursor = await CursorValueAsync(row, ct);
        var (status, body) = await probe.InboxRawAsync(reader, $"?board={board}&limit=1&cursor={Uri.EscapeDataString(cursor)}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(MalformedType, json.RootElement.GetProperty("type").GetString());
    }

    /// <summary>PD12: the refusal quotes no part of the cursor: not the string, not the text it decodes to.</summary>
    [Fact]
    public async Task R9_38_TheRefusalEchoesNoPartOfTheCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var (probe, reader, board, _) = await SceneAsync(ct);
        var marker = "zz-marker-" + InboxProbe.Suffix();
        var cursor = Base64($"{marker}:1");

        var (status, body) = await probe.InboxRawAsync(reader, $"?board={board}&cursor={Uri.EscapeDataString(cursor)}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.DoesNotContain(marker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(cursor, body, StringComparison.Ordinal);
        Assert.DoesNotContain(cursor.TrimEnd('='), body, StringComparison.Ordinal);
    }

    /// <summary>
    /// PD13: a refusal is never a server fault. The status of every spelling in <see cref="Refused"/> is under 500: a decoder that
    /// threw on a hostile cursor would answer <c>500</c>, and one that failed the request on a refusal would answer <c>503</c>. That a refusal
    /// is a <c>400</c> is PD8's; this holds the other side, and is green on the tree that has no refusal at all.
    /// </summary>
    [Fact]
    public async Task R9_38_ARefusalIsA400NotA503OrA500()
    {
        var ct = TestContext.Current.CancellationToken;
        var (probe, reader, board, _) = await SceneAsync(ct);

        foreach (var row in new[] { "raw:@@@", "raw:not-base64!!", "raw:garbage", "b64:1", "b64:a:b", "b64:1:2:3", "b64:+1:2", "b64:-1:2", "b64:99999999999:1", "b64:1:99999999999999999999", "b64:c6:1:0123456789abcdef", "search" })
        {
            var cursor = await CursorValueAsync(row, ct);
            var (status, body) = await probe.InboxRawAsync(reader, $"?board={board}&limit=1&cursor={Uri.EscapeDataString(cursor)}", ct);

            Assert.True((int)status < 500, $"{row}: {(int)status}: {body}");
            Assert.NotEqual(HttpStatusCode.ServiceUnavailable, status);
        }
    }

    // ---- PD9, PD10: what a minted cursor and an absent one do ----------------------------------

    /// <summary>PD9: a walk at <c>limit=2</c> over five open questions lists each once, in the order asked, over at least three pages, and ends with no cursor.</summary>
    [Fact]
    public async Task R9_38_AMintedCursorContinuesAndTheWalkIsCompleteAndUnrepeated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (probe, reader, board, ids) = await SceneAsync(ct);

        var pages = await probe.WalkAsync(reader, $"board={board}", 2, ct);

        Assert.True(pages.Count >= 3, $"a walk of {pages.Count} page(s) proves nothing about continuing");
        Assert.Equal(ids, pages.SelectMany(p => p.Ids));
        Assert.Null(pages[^1].NextCursor);
    }

    /// <summary>PD10: an absent, empty or blank cursor is a first page.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("&cursor=")]
    [InlineData("&cursor=%20")]
    public async Task R9_38_ABlankOrAbsentCursorIsAFirstPage(string suffix)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(suffix);
        var (probe, reader, board, ids) = await SceneAsync(ct);

        var page = await probe.InboxPageAsync(reader, $"?board={board}&limit=2{suffix}", ct);

        Assert.Equal(ids[..2], page.Ids);
    }
}
