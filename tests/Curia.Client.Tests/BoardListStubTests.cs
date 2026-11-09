using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.BoardListHarness;

namespace Curia.Client.Tests;

/// <summary>
/// R11.88 (errata G63), LOB41, in the shared stubs: <c>StubLog</c> serves <c>GET /v1/boards[?board=]</c> from its own posts (the fixture every client test takes); <c>RawStubForum</c> answers the list route as an
/// older Forum does (<c>404</c> <c>curia/request/no-route</c>), and a test that drives <c>ask</c> over it and does not assert the older-Forum line must fail when the stub is disposed, because otherwise the new
/// check would pass such a test through the branch it was not written for (trap 192).
///
/// <para><b>Oracle.</b> The stub's list is compared with the one post it holds (board <c>b</c>); the tripwire is probed by the observable it must have: a list read answered unrouted and never acknowledged makes
/// <c>Dispose</c> throw. Predicted: the first fact is a green control on the seam (the stub serves its list); the tripwire fact is red (no tripwire exists).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BoardListStubTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R11_88_LOB41_TheSharedStubServesTheListOfItsOwnPosts_Control()
    {
        using var log = new StubLog();
        var listed = await log.Client().GetBoardsAsync("b", null, null, Ct);
        var other = await log.Client().GetBoardsAsync("rsut", null, null, Ct);

        Said(
            ["b: one row with 1 post", "rsut: no row"],
            [
                $"b: {(listed.TryGetValue(out var page, out var refusal) ? (page!.Boards.Length == 1 ? $"one row with {page.Boards[0].Posts} post" : page.Boards.Length + " rows") : refusal!.ToString())}",
                $"rsut: {(other.TryGetValue(out var none, out _) ? (none!.Boards.IsEmpty ? "no row" : "a row") : "not read")}",
            ]);
    }

    [Fact]
    public async Task R11_88_LOB41_ATestThatDrivesAskOverTheRawStubWithoutAssertingTheOlderForumLineFailsAtDispose()
    {
        using var stub = new RawStubForum(UnheardBehaviour.Fin);
        using (var http = new HttpClient { BaseAddress = stub.Address })
            _ = await http.GetAsync(new Uri("/v1/boards?board=rust", UriKind.Relative), Ct);

        string outcome;
        try
        {
            stub.Dispose();
            outcome = "Dispose did not throw";
        }
        catch (Exception ex) when (ex is Xunit.Sdk.XunitException or InvalidOperationException)
        {
            outcome = ex.Message.Contains("/v1/boards", StringComparison.Ordinal) ? "Dispose threw, naming the list route" : "Dispose threw without naming the list route";
        }

        Said(["Dispose threw, naming the list route"], [outcome]);
    }
}
