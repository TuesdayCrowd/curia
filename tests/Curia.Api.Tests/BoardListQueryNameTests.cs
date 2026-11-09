using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Curia.Api.Tests.BoardListProbe;
using static Curia.Api.Tests.NameProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R9.58 (errata G63), LOB11, at the route: <c>GET /v1/boards</c> reads the query names <c>board</c>, <c>limit</c> and <c>cursor</c> in any case and refuses every other, <c>marking</c> included, as
/// <c>curia/board-list/unknown-parameter</c> naming it and ending <c>this route reads exactly these: board, limit, cursor</c>; the refusals come in the order unknown name, blank, limit, cursor, and none of
/// them reads the log (a served request reads it once).
///
/// <para><b>Oracles.</b> The type, the tail and the names are the errata's (<see cref="PublishedBoardListWords"/>); the title is R9.29's, read from <see cref="PublishedNameWords.UnknownTitle"/>; the reads are
/// counted inside the request (<c>CountRequestReads</c>). Each refusing row has its control (a name the route reads, served). Predicted red on the seam: <c>200</c> for every name, and one read for every
/// request.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class BoardListQueryNameTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Detail(NameReply reply) =>
        reply.Json.ValueKind == JsonValueKind.Object && reply.Json.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : string.Empty;

    private static string Title(NameReply reply) =>
        reply.Json.ValueKind == JsonValueKind.Object && reply.Json.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;

    /// <summary>
    /// LOB11, the names. <c>board</c>, <c>limit</c> and <c>cursor</c> in any case are read (a blank cursor is refused as blank, which is how it is seen to be read); <c>marking</c>, <c>x</c> and <c>author</c> are
    /// <c>400</c> with the unknown-parameter type, R9.29's title, a detail that names the name and ends with the tail. Predicted red on the seam: <c>200</c> for the three unknown names.
    /// </summary>
    [Fact]
    public async Task R9_58_LOB11_ThreeNamesAreReadInAnyCaseAndEveryOtherIsRefusedByName()
    {
        var unknown = PublishedBoardListWords.Types()[0];
        var blank = PublishedBoardListWords.Types()[1];
        var tail = PublishedBoardListWords.UnknownDetailTail();
        var expected = new List<string>();
        var actual = new List<string>();

        foreach (var control in new[] { "board=x", "BOARD=x", "Limit=1", "LIMIT=1" })
        {
            var reply = await ListAsync(forum.Client, control, Ct);
            expected.Add($"?{control}: served as a listing");
            actual.Add($"?{control}: {(Rows(reply) is null ? reply.Says : "served as a listing")}");
        }

        var blankCursor = await ListAsync(forum.Client, "CURSOR=", Ct);
        expected.Add($"?CURSOR=: 400 {blank}");
        actual.Add($"?CURSOR=: {blankCursor.Says}");

        foreach (var name in new[] { "marking=datamark", "x=1", "author=a" })
        {
            var refused = await ListAsync(forum.Client, name, Ct);
            var key = name[..name.IndexOf('=', StringComparison.Ordinal)];
            expected.Add($"?{name}: 400 {unknown}, the title, names {key}, ends \"{tail}\"");
            actual.Add($"?{name}: {refused.Says}, {(Title(refused) == PublishedNameWords.UnknownTitle() || Title(refused).StartsWith("That parameter is not read by this route", StringComparison.Ordinal) ? "the title" : "not the title")}, {(Detail(refused).Contains("parameter=" + key, StringComparison.Ordinal) ? "names " + key : "does not name " + key)}, {(Detail(refused).EndsWith(tail, StringComparison.Ordinal) ? $"ends \"{tail}\"" : "does not end with the tail")}");
        }

        Said(expected, actual);
    }

    /// <summary>
    /// LOB11, the order and the reads. A request that carries every fault is answered the first of them in the order unknown name, blank, limit, cursor, and reads the log zero times; the first page reads it
    /// once. The control is the served first page. Predicted red on the seam: <c>200</c> and one read for each refusal.
    /// </summary>
    [Fact]
    public async Task R9_58_LOB11_TheRefusalsComeInTheirOrderAndReadNoLog()
    {
        var counter = new Counter();
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.CountRequestReads(counter)));
        using var client = host.CreateClient();
        var types = PublishedBoardListWords.Types();

        var rows = new (string Query, string Expected)[]
        {
            (string.Empty, "200, 1 reads"),
            ("x=1&board=&limit=abc&cursor=@@@", $"400 {types[0]}, 0 reads"),
            ("board=&limit=abc&cursor=@@@", $"400 {types[1]}, 0 reads"),
            ("limit=abc&cursor=@@@", $"400 {types[2]}, 0 reads"),
            ("cursor=@@@", $"400 {types[3]}, 0 reads"),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (query, wanted) in rows)
        {
            counter.Count = 0;
            var reply = await ListAsync(client, query, Ct);
            expected.Add($"?{query}: {wanted}");
            actual.Add($"?{query}: {(Rows(reply) is not null ? "200" : reply.Says)}, {counter.Count} reads");
        }

        Said(expected, actual);
    }
}
