using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Search;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// R9.58, R14.10, R9.52 (errata G63), LOB17 and LOB18: the request fuzzer and the faulted-store sweep know the route. The exemplar exists and carries the three names the route reads; the inventory lists them
/// (<c>SurfaceInventory.RequestReadQuery</c>, so the closed pass must see each read); the cursor the exemplar presents decodes and is one the fuzzer's context could have minted for the exemplar's <c>limit</c>; and
/// under an unreadable log the route answers <c>503</c> <c>curia/log/unreadable</c> in the sweep's own swept set, so that an omission from the sweep is not a pass.
///
/// <para><b>Oracles.</b> The names are R9.58's (<see cref="PublishedBoardListWords.Types"/> for the type, the route's own three names from its published sentence); the cursor is decoded by the Domain's reader. LOB17
/// is a control on the seam (the hand lists were updated with the route, which reads the log); LOB18's cursor facts are red on the seam (<see cref="BoardListCursor.Decode"/> refuses every cursor).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("fuzz")]
public sealed class BoardListFuzzFactsTests(FaultableForumFixture forum) : IClassFixture<FaultableForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string RouteKey = "GET /v1/boards";

    /// <summary>LOB18: the exemplar exists, carries <c>board</c>, <c>limit</c> and <c>cursor</c> and no <c>marking</c>; the inventory lists the three; the exemplar's cursor decodes (a control for the first two, red for the third on the seam).</summary>
    [Fact]
    public async Task R14_10_LOB18_TheExemplarCarriesTheThreeNamesTheInventoryListsAndACursorTheRouteWouldRead()
    {
        using var context = FuzzContext.Offline(forum);
        var row = Exemplars.All.SingleOrDefault(r => r.Route == RouteKey);
        var model = row is null ? null : await row.Build(context, Ct);
        var names = model is null ? [] : model.Query.Select(q => q.Key).Order(StringComparer.Ordinal).ToArray();
        var inventory = SurfaceInventory.RequestReadQuery.Where(r => r.Method == "GET" && r.Pattern == "/v1/boards").Select(r => r.Name).Order(StringComparer.Ordinal).ToArray();
        var cursor = model?.Query.FirstOrDefault(q => q.Key == "cursor").Value;
        var decoded = cursor is null ? "no cursor" : BoardListCursor.Decode(cursor).Match(c => c is null ? "an absent cursor" : "a cursor", e => e.Type);

        NameProbe.Said(
            ["exemplar: board, cursor, limit", "inventory: board, cursor, limit", "cursor: a cursor"],
            [$"exemplar: {string.Join(", ", names)}", $"inventory: {string.Join(", ", inventory)}", $"cursor: {decoded}"]);
    }

    /// <summary>
    /// LOB17: with the whole-log read faulted (as the sweep arms it, <c>ReportForward</c>, and as the host's own store faults, <c>ThrowUnreadable</c>), the sweep's swept set holds the route and each of its rows is
    /// <c>503</c> <c>curia/log/unreadable</c>, never a <c>200</c> over an empty list. A control on the seam.
    /// </summary>
    [Fact]
    public async Task R9_52_LOB17_UnderAnUnreadableLogTheRouteIs503AndTheSweepHoldsIt_Control()
    {
        using var context = await FuzzContext.SeedAsync(forum, Ct);
        var said = new List<string>();
        foreach (var mode in new[] { EventStoreFault.ReportForward, EventStoreFault.ThrowUnreadable })
        {
            var swept = await FaultSweep.RunAsync(forum, context, r => r.Route == RouteKey, () => forum.Store.Arm(mode), forum.Store.Disarm, Ct);
            said.Add($"{mode}: {swept.Count} swept, {(swept.Count > 0 && swept.All(s => FaultSweep.IsProblem(s.Reply, PublishedStoreFaults.UnavailableStatus, PublishedStoreFaults.LogUnreadableType)) ? "all 503 log-unreadable" : string.Join("; ", swept.Select(s => s.Says)))}");
        }

        NameProbe.Said(
            [$"ReportForward: 1 swept, all 503 log-unreadable", $"ThrowUnreadable: 1 swept, all 503 log-unreadable"],
            said);
    }
}
