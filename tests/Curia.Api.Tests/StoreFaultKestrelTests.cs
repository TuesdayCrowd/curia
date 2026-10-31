using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// ULG4, ULG39 (errata G52, R11.71): the facts that need a real socket, on the Forum hosted on Kestrel (Development) with the decorated store. The plan's
/// client-parser clause of ULG4 is not asserted: the body being a problem document is what a parser reads.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class StoreFaultKestrelTests(FaultableKestrelFixture forum) : IClassFixture<FaultableKestrelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<Reply> GetAsync(string path, CancellationToken token, string? probe = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (probe is not null) request.Headers.Add(FaultableForumFixture.ProbeHeader, probe);
        return await Reply.SendAsync(forum.Client, request, token);
    }

    /// <summary>
    /// ULG4. An exception nothing names, over the wire: <c>500</c> <c>curia/server/fault</c> as a JSON problem document with a body (and not a body-less answer
    /// that a client reports as an unreadable problem), no word of the exception in it.
    /// </summary>
    [Fact]
    public async Task R11_71_ULG4_AnUnclassifiedExceptionIsAProblemDocumentOverTheWire()
    {
        const string Sentinel = "kestrel-unclassified-bug";
        forum.Store.Arm(EventStoreFault.ThrowReads, () => StoreFaultKinds.Unclassified(Sentinel));
        Reply reply;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/boards/some-board/posts");
            reply = await Reply.SendAsync(forum.Client, request, Ct);
        }
        finally
        {
            forum.Store.Disarm();
        }

        var problems = new List<string>();
        if (!FaultSweep.IsProblem(reply, PublishedStoreFaults.FaultStatus, PublishedStoreFaults.FaultType))
            problems.Add($"expected {PublishedStoreFaults.FaultStatus} {PublishedStoreFaults.FaultType} as a problem document, actual {reply.Says}");
        else if (reply.Title != PublishedStoreFaults.FaultTitle)
            problems.Add($"expected the title \"{PublishedStoreFaults.FaultTitle}\", actual \"{reply.Title}\"");
        if (reply.Body.Contains(Sentinel, StringComparison.Ordinal)) problems.Add("the body carries the exception's text");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }

    /// <summary>
    /// ULG39. The cancellation is the caller's: a request the client aborts mid-read is not logged as a store fault and is not answered. The positive is in the
    /// same fact: the same read with a fault served logs exactly one <c>StoreFault</c> event, so a Forum that logs nothing at all is red. The limb reads the
    /// log only after the host itself has finished with the aborted request (the host's own completion signal, bounded), so a Forum that logs after the store
    /// has thrown is seen, whatever the order the two threads run in; no event of the middleware's, and no server-fault line, may be among them.
    /// </summary>
    [Fact]
    public async Task R11_71_ULG39_ARequestTheClientAbortsMidReadIsNotAStoreFault()
    {
        var problems = new List<string>();

        // The positive: a fault the Forum serves is logged once as StoreFault.
        var mark = forum.Log.Mark();
        forum.Store.Arm(EventStoreFault.ThrowUnreadable);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/boards/some-board/posts");
            var served = await Reply.SendAsync(forum.Client, request, Ct);
            if (served.Status != PublishedStoreFaults.UnavailableStatus) problems.Add($"a served fault: expected {PublishedStoreFaults.UnavailableStatus}, actual {served.Says}");
        }
        finally
        {
            forum.Store.Disarm();
        }

        var servedFaults = forum.Log.Since(mark).Count(e => e.EventId == PublishedStoreFaults.EventId);
        if (servedFaults != 1) problems.Add($"a served fault logged {servedFaults} {PublishedStoreFaults.EventName} events, not one");

        // The limit: the client goes away while the Forum is reading.
        mark = forum.Log.Mark();
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        forum.Store.Arm(EventStoreFault.HangUntilCancelled);
        try
        {
            var probe = Guid.NewGuid().ToString("N");
            var sending = GetAsync("/v1/boards/some-board/posts", abort.Token, probe);
            await forum.Store.HangStarted.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await abort.CancelAsync();
            await forum.Store.HangEnded.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            await forum.Completion(probe).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            try
            {
                _ = await sending;
            }
            catch (OperationCanceledException)
            {
                // The client's own abort, which is the point: it is not an answer.
            }
        }
        finally
        {
            forum.Store.Disarm();
        }

        var abortedFaults = forum.Log.Since(mark)
            .Where(e => e.EventId == PublishedStoreFaults.EventId || e.EventId == PublishedStoreFaults.ServerFaultEventId || e.Category.EndsWith(nameof(StoreFaultsMiddleware), StringComparison.Ordinal))
            .ToList();
        if (abortedFaults.Count != 0)
            problems.Add($"{abortedFaults.Count} fault event(s) were logged for a cancelled request: {string.Join(", ", abortedFaults.Select(e => $"{e.EventId} {e.Name}"))}");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
    }
}
