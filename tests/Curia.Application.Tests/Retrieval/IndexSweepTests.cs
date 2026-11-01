using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// Errata G54 (R8.83, R9.53) at the catch-up, as units over instants and a generated log (the "A committed post is found" PR, Task 0c): CPF9 the scan that finds a
/// post past a page and does not end at a failing one, CPF10 the post the model cannot place, CPF11 the post whose index step is running, CPF12 the check that cannot
/// complete, CPF13 the settle rule, CPF14 the cadence counted at the reader, CPF21 the instant a check records and CPF30 the paging of the scan. Every figure is the
/// entry's (<see cref="PublishedCheckWords"/>); the oracles are the index's digests and the reader's own counts, never the sweep's report of itself.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IndexSweepTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Short(string digest) => digest.Length > 13 ? digest[7..13] : digest;

    private static string Secs(TimeSpan span) => span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Due(IndexSweep sweep, DateTimeOffset from) => sweep.DueAt is { } due ? Secs(due - from) + " s" : "none";

    /// <summary>
    /// CPF9. A find past a page, and one failing post does not end the scan (trap 77): 10,100 events with the unindexed post A at 10,050 and a second unindexed post B
    /// at 10,100 whose index store fails. After the first tick the Forum knows of both (held in the backlog or already stored), after the second A is in the index and
    /// B is not, B alone is held and waits on the ladder, and the check that completed began at the first tick.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF9_AFindPastAPageIsEnrolledAndOneFailingPostDoesNotEndTheScan()
    {
        var rig = new CheckRig(new GeneratedLog(10_100));
        await rig.IndexAllExceptAsync(new HashSet<int> { 10_050, 10_100 }, Ct);
        var a = GeneratedLog.DigestOf(10_050);
        var b = GeneratedLog.DigestOf(10_100);
        rig.Index.Fail(b);
        var exit = CheckRig.Start + TimeSpan.FromMinutes(1);
        var first = exit + Tick;
        var second = first + Tick;

        rig.Sweep.Owe(exit);
        await rig.TickAsync(first, Ct);
        var held = await rig.HeldAsync(Ct);
        var knownAfterFirst = rig.Enrolled().Count(d => d == a || d == b) + new[] { a, b }.Count(held.Contains);
        await rig.TickAsync(second, Ct);
        held = await rig.HeldAsync(Ct);
        var enrolled = rig.Enrolled();
        var bWaiting = !rig.Backlog.Due(second + Tick).Any(e => e.Digest == b);

        var expected = $"after the first tick the Forum knows of 2 of the 2 absent posts; after the second A ({Short(a)}) is in the index, B ({Short(b)}) is not, the backlog holds B alone and B waits; the check began at {first:o}";
        var actual = $"after the first tick the Forum knows of {knownAfterFirst} of the 2 absent posts; after the second A ({Short(a)}) is {(held.Contains(a) ? "in" : "not in")} the index, B ({Short(b)}) is {(held.Contains(b) ? "in" : "not")}, the backlog holds {(enrolled.Length == 0 ? "nothing" : enrolled.SequenceEqual([b]) ? "B alone" : string.Join(' ', enrolled.Select(Short)))} and B {(bWaiting ? "waits" : "is not waiting")}; the check began at {rig.Sweep.CheckedAt:o}";
        Said(expected, actual);
    }

    /// <summary>
    /// CPF10 (a negative with its positive in one fact). A post the model cannot place (a title and a body with no letter or digit) is never enrolled, across three
    /// floors, at any tick, and is never in the index; the placeable post beside it is found. On the seam tree the positive leg is red.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF10_APostTheModelCannotPlaceIsNeverEnrolledAndThePlaceableOneBesideItIsFound()
    {
        var log = new GeneratedLog(8);
        log.Unplaceable.Add(1);
        var rig = new CheckRig(log);
        await rig.IndexAllExceptAsync(new HashSet<int> { 2 }, Ct);
        var unplaceable = GeneratedLog.DigestOf(1);
        var control = GeneratedLog.DigestOf(2);
        var floor = PublishedCheckWords.SweepFloor();
        var end = CheckRig.Start + (floor * 3) + TimeSpan.FromMinutes(1);

        var everEnrolled = false;
        rig.Sweep.Owe(CheckRig.Start);
        for (var now = CheckRig.Start + Tick; now <= end; now += Tick)
        {
            await rig.TickAsync(now, Ct);
            everEnrolled |= rig.Enrolled().Contains(unplaceable);
        }

        var held = await rig.HeldAsync(Ct);
        var expected = "the unplaceable post was never enrolled and is not in the index; the placeable post is in the index";
        var actual = $"the unplaceable post was {(everEnrolled ? "enrolled" : "never enrolled")} and is {(held.Contains(unplaceable) ? "" : "not ")}in the index; the placeable post is {(held.Contains(control) ? "" : "not ")}in the index";
        Said(expected, actual);
    }

    /// <summary>
    /// CPF11 (a negative with its positive in one fact). A post whose index step is running is not enrolled by a check that runs meanwhile (the gated index holds the
    /// step open), the unindexed post beside it is, and once released the held post is indexed once.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF11_APostWhoseIndexStepIsRunningIsNotEnrolledAndIsIndexedOnce()
    {
        var rig = new CheckRig(new GeneratedLog(6));
        await rig.IndexAllExceptAsync(new HashSet<int> { 1, 2 }, Ct);
        var inFlight = GeneratedLog.DigestOf(1);
        var other = GeneratedLog.DigestOf(2);
        var gated = new GatedIndex(rig.Index);
        var committed = new CommittedPostIndexer(new EmbeddingIndexer(rig.Embedder, gated), rig.Backlog, rig.Clock, rig.Sweep);

        var step = committed.IndexAsync(rig.Log.PostAt(1), Ct);
        await gated.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var exit = CheckRig.Start + TimeSpan.FromMinutes(1);
        rig.Sweep.Owe(exit);
        await rig.TickAsync(exit + Tick, Ct);
        var held = await rig.HeldAsync(Ct);
        var duringEnrolled = rig.Enrolled();
        var otherKnown = duringEnrolled.Contains(other) || held.Contains(other);

        gated.Release();
        var outcome = await step.ConfigureAwait(true);
        await rig.TickAsync(exit + Tick + Tick, Ct);
        var attempts = rig.Index.Attempted.Count(d => d == inFlight);

        var expected = "while its step ran the post was not enrolled and the unindexed post beside it was known; released, the step Indexed it and the index was asked for it once";
        var actual = $"while its step ran the post was {(duringEnrolled.Contains(inFlight) ? "enrolled" : "not enrolled")} and the unindexed post beside it was {(otherKnown ? "known" : "not known")}; released, the step {outcome.State} it and the index was asked for it {(attempts == 1 ? "once" : attempts + " times")}";
        Said(expected, actual);
    }

    /// <summary>
    /// CPF12. A scan that cannot complete moves nothing it did not do: the log unreadable at the scan's second page leaves the last-checked instant and the
    /// obligation as they were, the next attempt is 5 s later and then each wait doubled to the interval it would have had, what the scan found stays known, and
    /// restored the next attempt completes, records its beginning and clears the obligation.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF12_AScanThatCannotCompleteMovesNeitherTheInstantNorTheObligationAndIsRetriedOnTheLadder()
    {
        var log = new GeneratedLog(10_200);
        var rig = new CheckRig(log);
        await rig.IndexAllExceptAsync(new HashSet<int> { 500 }, Ct);
        var found = GeneratedLog.DigestOf(500);
        var started = rig.Sweep.CheckedAt;
        var exit = CheckRig.Start + TimeSpan.FromMinutes(1);
        log.UnreadableFromScanRead = 2;
        rig.Sweep.Owe(exit);

        var expectedWaits = new List<string>();
        var actualWaits = new List<string>();
        var movedChecked = false;
        var now = exit + Tick;
        var wait = PublishedCheckWords.FailedCheckWait();
        var interval = PublishedCheckWords.SweepRepeat();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await rig.TickAsync(now, Ct);
            movedChecked |= rig.Sweep.CheckedAt != started;
            expectedWaits.Add(Secs(wait) + " s");
            actualWaits.Add(Due(rig.Sweep, now));
            if (rig.Sweep.DueAt is not { } next) break;
            now = next;
            wait = TimeSpan.FromTicks(Math.Min(wait.Ticks * 2, interval.Ticks));
        }

        var held = await rig.HeldAsync(Ct);
        var knownAfterFailures = rig.Enrolled().Contains(found) || held.Contains(found);

        log.UnreadableFromScanRead = 0;
        var healed = rig.Sweep.DueAt ?? now;
        await rig.TickAsync(healed, Ct);

        var expected = $"the instant was not moved by a failed scan; the next attempts were {string.Join(", ", expectedWaits)} away; the find stayed known; the healed attempt recorded its beginning ({healed:o}) and cleared the obligation";
        var actual = $"the instant was {(movedChecked ? "moved" : "not moved")} by a failed scan; the next attempts were {string.Join(", ", actualWaits)} away; the find {(knownAfterFailures ? "stayed known" : "was forgotten")}; the healed attempt recorded {(rig.Sweep.CheckedAt == healed ? "its beginning (" + healed.ToString("o", CultureInfo.InvariantCulture) + ")" : rig.Sweep.CheckedAt.ToString("o", CultureInfo.InvariantCulture))} and {(rig.Sweep.DueAt is null ? "cleared" : "left")} the obligation";
        Said(expected, actual);
    }

    /// <summary>
    /// CPF13. The settle rule as a table: an obligation is cleared only by a check that began at least the settle window (R8.83 words) after the latest exit. The
    /// exits and the instants at which a check begins are the scenarios'; the edge is the published figure itself and one millisecond short of it.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF13_AnObligationIsClearedOnlyByACheckThatBeganAtLeastTheSettleWindowAfterTheLatestExit()
    {
        var settle = PublishedCheckWords.SweepSettle();
        var t = CheckRig.Start + TimeSpan.FromMinutes(1);

        async Task<string> OwedAfterAsync(Func<CheckRig, Task> act)
        {
            var rig = new CheckRig(new GeneratedLog(3));
            await rig.IndexAllExceptAsync(new HashSet<int>(), Ct).ConfigureAwait(false);
            await act(rig).ConfigureAwait(false);
            return rig.Sweep.DueAt is null ? "clear" : "owed";
        }

        var rows = new (string Name, string Expected, Func<CheckRig, Task> Act)[]
        {
            ("exit at t, check began t+1 s", "owed", async r => { r.Sweep.Owe(t); await r.TickAsync(t + Tick, Ct).ConfigureAwait(false); }),
            ("exits at t and t+50 s, check began t+61 s", "owed", async r => { r.Sweep.Owe(t); r.Sweep.Owe(t + TimeSpan.FromSeconds(50)); await r.TickAsync(t + TimeSpan.FromSeconds(61), Ct).ConfigureAwait(false); }),
            ("exits at t and t+50 s, check began t+110 s", "clear", async r => { r.Sweep.Owe(t); r.Sweep.Owe(t + TimeSpan.FromSeconds(50)); await r.TickAsync(t + TimeSpan.FromSeconds(110), Ct).ConfigureAwait(false); }),
            ("exit at t, the scan began t+100 s and an exit arrived during it at t+105 s", "owed", async r =>
            {
                r.Sweep.Owe(t);
                r.Log.OnScanRead = _ => r.Sweep.Owe(t + TimeSpan.FromSeconds(105));
                await r.TickAsync(t + TimeSpan.FromSeconds(100), Ct).ConfigureAwait(false);
            }),
            ("exit at t, check began at exactly the settle window", "clear", async r => { r.Sweep.Owe(t); await r.TickAsync(t + settle, Ct).ConfigureAwait(false); }),
            ("exit at t, check began 1 ms short of the settle window", "owed", async r => { r.Sweep.Owe(t); await r.TickAsync(t + settle - TimeSpan.FromMilliseconds(1), Ct).ConfigureAwait(false); }),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (name, want, act) in rows)
        {
            expected.Add($"{name}: {want}");
            actual.Add($"{name}: {await OwedAfterAsync(act)}");
        }

        Said(string.Join("; ", expected), string.Join("; ", actual));
    }

    /// <summary>
    /// CPF14. The cadence, counted at the reader (a different artifact from the schedule): with nothing owed the log is scanned at the floor after the last check began
    /// and at no other time over twelve fake minutes; while owed, at the first tick and then every repeat until an obligation clears, and then at the floor.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF14_TheLogIsScannedOnTheFloorWhenNothingIsOwedAndOnTheRepeatWhileOwed()
    {
        var floor = PublishedCheckWords.SweepFloor();
        var repeat = PublishedCheckWords.SweepRepeat();
        var log = new GeneratedLog(3);
        var rig = new CheckRig(log);
        await rig.IndexAllExceptAsync(new HashSet<int>(), Ct);

        var idleEnd = CheckRig.Start + TimeSpan.FromMinutes(12);
        for (var now = CheckRig.Start + Tick; now <= idleEnd; now += Tick) await rig.TickAsync(now, Ct);
        var idle = log.ScanStarts.Select(s => Secs(s - CheckRig.Start)).ToList();
        var lastBegan = rig.Sweep.CheckedAt;

        var exit = idleEnd;
        log.ScanStarts.Clear();
        rig.Sweep.Owe(exit);
        var owedEnd = exit + TimeSpan.FromMinutes(7);
        for (var now = exit + Tick; now <= owedEnd; now += Tick) await rig.TickAsync(now, Ct);
        var owed = log.ScanStarts.Select(s => Secs(s - exit)).ToList();

        string Scans(IReadOnlyCollection<string> at) => at.Count == 0 ? "no scan" : $"scans at {string.Join(", ", at)} s";
        var expectedIdle = new[] { Secs(floor), Secs(floor * 2) };
        var expectedOwed = new[] { Secs(Tick), Secs(Tick + repeat), Secs(Tick + repeat + floor) };
        Said(
            $"nothing owed: {Scans(expectedIdle)}; owed from the exit: {Scans(expectedOwed)}",
            $"nothing owed: {Scans(idle)}; owed from the exit: {Scans(owed)}");
        _ = lastBegan;
    }

    /// <summary>
    /// CPF21. The instant a check records is the one it began at, taken from the clock port before the scan: a scan made to take fake time (the reader advances the
    /// clock by 30 s inside it) records the instant handed in at the start, not the one the scan ended at, and not the system clock's.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF21_TheInstantACheckRecordsIsTheOneItBeganAtFromTheClockPort()
    {
        var log = new GeneratedLog(3);
        var rig = new CheckRig(log);
        await rig.IndexAllExceptAsync(new HashSet<int>(), Ct);
        var exit = CheckRig.Start + TimeSpan.FromMinutes(1);
        rig.Sweep.Owe(exit);
        rig.Clock.Set(exit + Tick);
        var began = rig.Clock.GetUtcNow();
        log.OnScanRead = _ => rig.Clock.Advance(TimeSpan.FromSeconds(30));

        await rig.TickAsync(began, Ct);

        var recorded = rig.Sweep.CheckedAt;
        var which = recorded == began ? "the instant the check began" : recorded == rig.Clock.GetUtcNow() ? "the instant the scan ended" : recorded == CheckRig.Start ? "the process start" : "an instant from no clock the probe moved";
        Said($"the check recorded the instant the check began ({began:o})", $"the check recorded {which} ({recorded:o})");
    }

    /// <summary>
    /// CPF30 (a control). The scan is paged and holds no more than the page it is on: over 40,050 events (five pages) the reader is asked for no more than a page at a
    /// time, the log is read in five pages, and when the fifth is asked for no more than three of the four pages served before it are alive (the scan holds the previous
    /// page in its state and the runtime's first frame may hold one more; a scan that kept every page would hold four). Over the extracted rule itself, so the check, the
    /// start reconcile and the overflow pass are held by one fact. Not "the first page is dead": under tiered compilation the on-stack replacement of the scan's long loop
    /// leaves the first frame's slots reported, which a streaming scan cannot help (measured: two alive at every read from the third).
    /// </summary>
    [Fact]
    public async Task R8_83_CPF30_TheScanHoldsNoMoreThanAPageOrTwoAndAsksForNoMoreThanAPage()
    {
        var log = new GeneratedLog(40_050);
        var rig = new CheckRig(log);
        await rig.IndexAllExceptAsync(new HashSet<int> { 15_000 }, Ct);
        var aliveAtFifth = -1;
        log.OnScanRead = number =>
        {
            if (number != 5) return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            aliveAtFifth = log.PagesAlive;
        };

        var scan = await rig.Indexer.FindAbsentAsync(log, null, null, Ct);

        var expected = $"the scan found 1 post and no failure; {EventReaderExtensions.PageSize} was the largest ask; 5 reads; at the fifth read no more than 3 of the 4 earlier pages were alive";
        var actual = $"the scan found {scan.Found.Count} post{(scan.Found.Count == 1 ? string.Empty : "s")} and {(scan.Failure is null ? "no failure" : scan.Failure.Type)}; {log.LargestAsk} was the largest ask; {log.ForwardReads} reads; " +
            (aliveAtFifth is >= 0 and <= 3 ? "at the fifth read no more than 3 of the 4 earlier pages were alive" : $"at the fifth read {aliveAtFifth} of the 4 earlier pages were alive");
        Said(expected, actual);
    }
}
