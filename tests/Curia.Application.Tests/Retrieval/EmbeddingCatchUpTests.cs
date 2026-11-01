using System.Diagnostics.CodeAnalysis;
using Curia.Application.Retrieval;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Application.Tests.Retrieval.IndexRepairHarness;

namespace Curia.Application.Tests.Retrieval;

// PIL33: constructs the production type as the object under test, not as a source of expectation.

/// <summary>
/// Errata G51 (R11.69) at the catch-up, as units over instants (the D92 PR, ruling 15: no timer is involved, because the work takes the instant it is
/// run at): PIL12 the retry, PIL13 the schedule, PIL14 the post it was queued for, PIL15 the overflow and PIL17 the repair that appends nothing. The numbers
/// are the entry's (<see cref="PublishedIndexLag"/>); the oracles are the index's digests and the log, never the catch-up's report of itself.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EmbeddingCatchUpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>
    /// PIL12. A post committed at instant t whose vector failed: nothing is retried at t plus the first wait less a tenth of a second, and at t plus the first
    /// wait the vector is stored, the entry is gone and the result names the digest.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL12_ACommittedPostWhoseVectorFailedIsStoredByTheRetryAtItsInstant()
    {
        var wait = PublishedIndexLag.FirstWait();
        var h = new IndexRepairHarness();
        var post = await h.AppendAsync(Ct);
        h.Index.FailAll = true;
        var failedAt = h.Now;
        var outcome = await h.Committed.IndexAsync(post, Ct);
        var held = h.Backlog.Count;
        h.Index.Heal();

        var early = await h.CatchUp.RunOnceAsync(h.Store, failedAt + wait - TimeSpan.FromMilliseconds(100), Ct);
        var afterEarly = await h.IndexedAsync(Ct);
        var due = await h.CatchUp.RunOnceAsync(h.Store, failedAt + wait, Ct);
        var afterDue = await h.IndexedAsync(Ct);

        var expected = $"Pending and 1 held; at the wait less 100 ms the index holds 0 and the result 0; at the wait the index holds {Short(post.Digest)} (1), the result names {Short(post.Digest)} and 0 are held";
        var actual = $"{outcome.State} and {held} held; at the wait less 100 ms the index holds {afterEarly.Length} and the result {early.StoredDigests.Count}; at the wait the index holds {string.Join(' ', afterDue.Select(Short))} ({afterDue.Length}), the result names {string.Join(' ', due.StoredDigests.Select(Short))} and {h.Backlog.Count} are held";
        Said(
            expected,
            outcome.State == CommittedIndexState.Pending && held == 1 && afterEarly.Length == 0 && early.StoredDigests.Count == 0 && afterDue.SequenceEqual([post.Digest]) && due.StoredDigests.SequenceEqual([post.Digest]) && h.Backlog.Count == 0
                ? expected
                : actual);
    }

    /// <summary>
    /// PIL13. A retry that keeps failing waits the first wait, then twice that, and so on to the cap, and stays at the cap; each wait runs from the instant
    /// handed to the failed retry (the class reads no clock of its own). Measured by the upserts the index is asked for: none a tenth of a second before
    /// the wait is out, one at it.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL13_AFailingRetryWaitsTheDoublingScheduleToTheCapFromTheInstantItIsGiven()
    {
        var first = PublishedIndexLag.FirstWait();
        var cap = PublishedIndexLag.WaitCap();
        var h = new IndexRepairHarness();
        var post = await h.AppendAsync(Ct);
        h.Index.FailAll = true;
        var committedAt = h.Now;
        await h.Committed.IndexAsync(post, Ct);

        var expectedWaits = new List<TimeSpan>();
        for (var wait = first; expectedWaits.Count < 9; wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, cap.Ticks)))
            expectedWaits.Add(wait);

        var observed = new List<string>();
        var expected = new List<string>();
        var instant = committedAt;
        foreach (var wait in expectedWaits)
        {
            var due = instant + wait;
            var before = h.Index.Attempted.Length;
            await h.CatchUp.RunOnceAsync(h.Store, due - TimeSpan.FromMilliseconds(100), Ct);
            var tooEarly = h.Index.Attempted.Length - before;
            await h.CatchUp.RunOnceAsync(h.Store, due, Ct);
            var atDue = h.Index.Attempted.Length - before - tooEarly;

            expected.Add($"{wait.TotalSeconds:0}s: 0 early, 1 on time");
            observed.Add($"{wait.TotalSeconds:0}s: {tooEarly} early, {atDue} on time");
            instant = due;
        }

        Said(string.Join("; ", expected), string.Join("; ", observed));
    }

    /// <summary>
    /// PIL14. Three posts at consecutive sequences, the middle one pending: the retry stores the middle one's digest and no other (the others are already in the
    /// index and an off-by-one in the read would store one of them again and leave the middle absent), and an entry whose event cannot be read stays.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL14_TheRetryIndexesThePostItWasQueuedForAndKeepsAnEntryItCannotRead()
    {
        var wait = PublishedIndexLag.FirstWait();
        var h = new IndexRepairHarness();
        var before = await h.AppendAsync(Ct);
        var middle = await h.AppendAsync(Ct);
        var after = await h.AppendAsync(Ct);
        h.Index.Fail(middle.Digest);
        var failedAt = h.Now;
        await h.Committed.IndexAsync(before, Ct);
        await h.Committed.IndexAsync(middle, Ct);
        await h.Committed.IndexAsync(after, Ct);
        h.Index.Heal();
        var unreadable = new PendingEntry("sha256:" + new string('f', 64), "no-such-post", 99_999);
        var unreadableHeld = h.Backlog.Add(unreadable, failedAt + wait);
        var attemptsBefore = h.Index.Attempted.Length;

        var result = await h.CatchUp.RunOnceAsync(h.Store, failedAt + wait, Ct);
        var attempted = h.Index.Attempted.Skip(attemptsBefore).ToArray();

        var expected = $"stored {Short(middle.Digest)} and no other; the index holds {Short(before.Digest)} {Short(middle.Digest)} {Short(after.Digest)}; the unreadable entry stays (1 held)";
        var actual = $"stored {string.Join(' ', result.StoredDigests.Select(Short))} (upserts attempted for {string.Join(' ', attempted.Select(Short))}) and no other; the index holds {string.Join(' ', (await h.IndexedAsync(Ct)).Select(Short))}; the unreadable entry {(unreadableHeld ? "was added" : "was refused")} ({h.Backlog.Count} held)";
        Said(expected, result.StoredDigests.SequenceEqual([middle.Digest]) && h.Backlog.Count == 1 ? expected : actual);
    }

    /// <summary>
    /// PIL15. One more failure than the backlog holds: the catch-up runs the by-absence pass, every post is in the index at the end and the backlog no longer
    /// says it overflowed. The count of posts is the entry's bound plus one.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL15_AnOverflowedBacklogIsRepairedByAbsenceAndClearsItsFlag()
    {
        var bound = PublishedIndexLag.BacklogBound();
        var wait = PublishedIndexLag.FirstWait();
        var h = new IndexRepairHarness();
        h.Index.FailAll = true;
        var failedAt = h.Now;
        for (var i = 0; i < bound + 1; i++)
            await h.Committed.IndexAsync(await h.AppendAsync(Ct), Ct);
        var overflowed = h.Backlog.Overflowed;
        h.Index.Heal();

        await h.CatchUp.RunOnceAsync(h.Store, failedAt + wait, Ct);
        var indexed = (await h.IndexedAsync(Ct)).Length;

        Said(
            $"overflowed True; after one pass {bound + 1} posts are indexed and Overflowed is cleared",
            $"overflowed {overflowed}; after one pass {indexed} posts are indexed and Overflowed is {(h.Backlog.Overflowed ? "still set" : "cleared")}");
    }

    /// <summary>
    /// PIL17. A repair appends nothing: the log's count and highest sequence are the same before and after a catch-up that stored vectors. A control on the
    /// seam (nothing is stored there, so nothing is appended; the count of vectors the pass stored is reported beside it and asserted by PIL12); FPIL19 makes
    /// it red by a repair that appends a marker.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL17_ARepairAppendsNothingToTheLog()
    {
        var wait = PublishedIndexLag.FirstWait();
        var h = new IndexRepairHarness();
        h.Index.FailAll = true;
        var failedAt = h.Now;
        for (var i = 0; i < 3; i++)
            await h.Committed.IndexAsync(await h.AppendAsync(Ct), Ct);
        h.Index.Heal();
        var logBefore = await h.LogShapeAsync(Ct);

        var result = await h.CatchUp.RunOnceAsync(h.Store, failedAt + wait, Ct);
        var logAfter = await h.LogShapeAsync(Ct);

        TestContext.Current.SendDiagnosticMessage($"PIL17: the pass stored {result.StoredDigests.Count} vectors (PIL12 asserts that it stores them)");
        Said(
            $"the log holds {logBefore.Count} events with highest sequence {logBefore.MaxSeq} after the pass, as before it",
            $"the log holds {logAfter.Count} events with highest sequence {logAfter.MaxSeq} after the pass, as before it");
    }

    /// <summary>
    /// PIL37 (the review fix, M1). A by-absence pass that keeps failing is attempted on the entry's schedule, not at every tick: the first pass runs at once, the
    /// next the first wait later, then twice that, to the cap, and stays at the cap. Measured by the upserts the index is asked for, one post being unindexed and
    /// the backlog flagged as overflowed; the pass is run at every second of the schedule. The check of errata G54 (R8.83) is a second door to the same post and
    /// would be counted here, so it is held off: it is told at every second that it has just completed. Its own cadence is CPF14's.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL37_AFailingByAbsencePassIsAttemptedOnTheDoublingScheduleAndNotAtEveryTick()
    {
        var first = PublishedIndexLag.FirstWait();
        var cap = PublishedIndexLag.WaitCap();
        var h = new IndexRepairHarness();
        await h.AppendAsync(Ct);
        h.Index.FailAll = true;
        h.Backlog.MarkOverflowed();
        var start = h.Now;

        var expectedWaits = new List<TimeSpan>();
        for (var wait = first; expectedWaits.Count < 9; wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, cap.Ticks)))
            expectedWaits.Add(wait);

        var firstPass = h.Index.Attempted.Length;
        h.Sweep.Completed(start);
        await h.CatchUp.RunOnceAsync(h.Store, start, Ct);
        var attemptedAtStart = h.Index.Attempted.Length - firstPass;

        var expected = new List<string> { "at once: 1 attempt" };
        var observed = new List<string> { $"at once: {attemptedAtStart} attempt{(attemptedAtStart == 1 ? string.Empty : "s")}" };
        var instant = start;
        foreach (var wait in expectedWaits)
        {
            var due = instant + wait;
            var before = h.Index.Attempted.Length;
            for (var at = instant + TimeSpan.FromSeconds(1); at < due; at += TimeSpan.FromSeconds(1))
            {
                h.Sweep.Completed(at);
                await h.CatchUp.RunOnceAsync(h.Store, at, Ct);
            }

            var tooEarly = h.Index.Attempted.Length - before;
            h.Sweep.Completed(due);
            await h.CatchUp.RunOnceAsync(h.Store, due, Ct);
            var atDue = h.Index.Attempted.Length - before - tooEarly;

            expected.Add($"{wait.TotalSeconds:0}s: 0 early, 1 on time");
            observed.Add($"{wait.TotalSeconds:0}s: {tooEarly} early, {atDue} on time");
            instant = due;
        }

        Said(string.Join("; ", expected), string.Join("; ", observed));
    }

    /// <summary>
    /// PIL38 (the review fix, M2). A retry that fails is reported in the result, by post id, digest and the fault's type, and never by any text the post carried:
    /// the hosted service logs what the result holds.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL38_AFailedRetryIsReportedByPostDigestAndTypeAndNeverByText()
    {
        var wait = PublishedIndexLag.FirstWait();
        var h = new IndexRepairHarness();
        var post = await h.AppendAsync(Ct);
        h.Index.FailAll = true;
        var failedAt = h.Now;
        await h.Committed.IndexAsync(post, Ct);

        var result = await h.CatchUp.RunOnceAsync(h.Store, failedAt + wait, Ct);

        var expected = $"1 failed retry, naming {post.PostId} and {Short(post.Digest)} and the type curia/retrieval/index-unavailable";
        var failed = result.FailedRetries;
        var actual = $"{failed.Count} failed retr{(failed.Count == 1 ? "y" : "ies")}, naming {string.Join(' ', failed.Select(f => f.PostId))} and {string.Join(' ', failed.Select(f => Short(f.Digest)))} and the type {string.Join(' ', failed.Select(f => f.FaultType))}";
        Said(expected, failed.Count == 1 && failed[0].PostId == post.PostId && failed[0].Digest == post.Digest && failed[0].FaultType == "curia/retrieval/index-unavailable" ? expected : actual);
    }

    /// <summary>
    /// PIL42 (the falsification fix, FPIL11's second form). A vector step that throws is pending and enters the backlog like a returned error: one entry held,
    /// and once the fault clears the retry at the first wait stores the digest, with no restart.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL42_AStepThatThrowsLeavesAnEntryAndTheRetryStoresThePost()
    {
        var wait = PublishedIndexLag.FirstWait();
        var h = new IndexRepairHarness();
        var post = await h.AppendAsync(Ct);
        h.Index.ThrowAll = true;
        var failedAt = h.Now;
        var outcome = await h.Committed.IndexAsync(post, Ct);
        var held = h.Backlog.Count;
        h.Index.Heal();

        var due = await h.CatchUp.RunOnceAsync(h.Store, failedAt + wait, Ct);
        var afterDue = await h.IndexedAsync(Ct);

        var expected = $"Pending and 1 held; at the wait the index holds {Short(post.Digest)} (1) and 0 are held";
        var actual = $"{outcome.State} and {held} held; at the wait the index holds {string.Join(' ', afterDue.Select(Short))} ({afterDue.Length}) and {h.Backlog.Count} are held";
        Said(
            expected,
            outcome.State == CommittedIndexState.Pending && held == 1 && afterDue.SequenceEqual([post.Digest]) && due.StoredDigests.Count == 1 && h.Backlog.Count == 0 ? expected : actual);
    }
}
