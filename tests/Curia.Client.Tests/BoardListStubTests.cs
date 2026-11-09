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

    private const string OlderForumOutput = "boards    rust: NOT STATED (this Forum has no list of boards, so the board was not checked)";

    private static async Task ReadTheListAsync(RawStubForum stub)
    {
        using var http = new HttpClient { BaseAddress = stub.Address };
        _ = await http.GetAsync(new Uri("/v1/boards?board=rust", UriKind.Relative), Ct);
    }

    /// <summary>The outcome of <paramref name="act"/>: the exception it threw, or "none". Disposal is inside <paramref name="act"/>, so a throw from <c>Dispose</c> is seen as the test would see it.</summary>
    private static async Task<string> ThrownAsync(Func<Task> act)
    {
        try
        {
            await act();
            return "none";
        }
        catch (Exception ex) when (ex is Xunit.Sdk.XunitException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>
    /// LOB47 (Amendment 2, ruling 31), the tripwire does not hide a test's own failure: a test that reads the list over the raw stub, never asserts the older-Forum line and then fails for its own reason reports that
    /// reason, and not the tripwire (FLOB75's first form: <c>Dispose</c> throws and its exception replaces the test's).
    /// </summary>
    [Fact]
    public async Task R11_88_LOB47_ATestThatFailsForItsOwnReasonReportsThatReasonAndNotTheTripwire()
    {
        var thrown = await ThrownAsync(async () =>
        {
            using var stub = new RawStubForum(UnheardBehaviour.Fin);
            await ReadTheListAsync(stub);
            throw new InvalidOperationException("its own reason");
        });

        Said(["its own reason"], [thrown.Contains("its own reason", StringComparison.Ordinal) ? "its own reason" : thrown]);
    }

    /// <summary>LOB47, a test that reaches the older-Forum branch and never asserts fails, from the explicit assertion; a test that asserted passes it (the control).</summary>
    [Fact]
    public async Task R11_88_LOB47_ATestThatNeverAssertedTheOlderForumLineFailsFromTheExplicitAssertion()
    {
        var silent = await ThrownAsync(async () =>
        {
            var stub = new RawStubForum(UnheardBehaviour.Fin);
            try
            {
                await ReadTheListAsync(stub);
                stub.AssertOlderForumAsserted();
            }
            finally
            {
                Quietly(stub);
            }
        });
        var asserted = await ThrownAsync(async () =>
        {
            var stub = new RawStubForum(UnheardBehaviour.Fin);
            try
            {
                await ReadTheListAsync(stub);
                stub.ExpectOlderForumLine(OlderForumOutput);
                stub.AssertOlderForumAsserted();
            }
            finally
            {
                Quietly(stub);
            }
        });

        Said(["never asserted: the assertion fails, naming the list route", "asserted: none"], [$"never asserted: {(silent.Contains("/v1/boards", StringComparison.Ordinal) ? "the assertion fails, naming the list route" : silent)}", $"asserted: {asserted}"]);
    }

    /// <summary>LOB47, one assertion covers only the reads made before it: a second read after <c>ExpectOlderForumLine</c> is not asserted and the explicit assertion fails (FLOB76).</summary>
    [Fact]
    public async Task R11_88_LOB47_OneAssertionCoversOnlyTheReadsMadeBeforeIt()
    {
        var thrown = await ThrownAsync(async () =>
        {
            var stub = new RawStubForum(UnheardBehaviour.Fin);
            try
            {
                await ReadTheListAsync(stub);
                stub.ExpectOlderForumLine(OlderForumOutput);
                await ReadTheListAsync(stub);
                stub.AssertOlderForumAsserted();
            }
            finally
            {
                Quietly(stub);
            }
        });

        Said(["a later read: the assertion fails, naming the list route"], [$"a later read: {(thrown.Contains("/v1/boards", StringComparison.Ordinal) ? "the assertion fails, naming the list route" : thrown)}"]);
    }

    /// <summary>Disposes <paramref name="stub"/> and says nothing of what the disposal threw: the probe above has already read the assertion's word and a leftover tripwire is the thing FLOB75 measures elsewhere.</summary>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Only the stub's own disposal is guarded, so that a probe can release it after reading its word.")]
    private static void Quietly(RawStubForum stub)
    {
        try
        {
            stub.Dispose();
        }
        catch (Exception)
        {
            // The disposal's word is not the one this probe reads.
        }
    }
}
