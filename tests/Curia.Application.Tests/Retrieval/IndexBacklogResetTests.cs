using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Domain.Search;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Application.Tests.Retrieval.IndexRepairHarness;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// Errata G54 (R11.69 (rev.)) at the backlog and the catch-up, as units over instants: CPF15 the success that shortens every waiting entry's wait, at every phase of the
/// ladder and from both sources of a success; CPF16 what is not a success; CPF17 the poison entry that is not hammered; CPF18 the ladder restarting at the first wait.
/// The figures are the entry's (<see cref="PublishedCheckWords"/>); the oracles are the index's digests and the index's own count of the upserts it was asked for.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IndexBacklogResetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static int AttemptsFor(IndexRepairHarness h, string digest) => h.Index.Attempted.Count(d => d == digest);

    /// <summary>
    /// Ticks the catch-up one second at a time from <paramref name="now"/> until every one of <paramref name="posts"/> has failed <paramref name="failures"/> times (the
    /// committed step's own failed attempt is not one), and returns the instant of the tick on which the last of them failed that often.
    /// </summary>
    private static async Task<DateTimeOffset> FailUntilAsync(IndexRepairHarness h, SearchablePost[] posts, int failures, DateTimeOffset now)
    {
        while (posts.Any(p => AttemptsFor(h, p.Digest) - 1 < failures))
        {
            now += Tick;
            await h.CatchUp.RunOnceAsync(h.Store, now, Ct).ConfigureAwait(false);
        }

        return now;
    }

    private static async Task<string> WaitingAsync(IndexRepairHarness h, string digest, DateTimeOffset from)
    {
        var indexed = await h.IndexedAsync(Ct).ConfigureAwait(false);
        if (indexed.Contains(digest)) return "stored";
        for (var s = 0; s <= 600; s++)
            if (h.Backlog.Due(from + TimeSpan.FromSeconds(s)).Any(e => e.Digest == digest))
                return $"waiting {s} s";
        return "waiting for ever";
    }

    /// <summary>
    /// CPF15. A success of the vector store makes every waiting entry due at once, whatever phase of the ladder it is in and from either source of a success (a catch-up
    /// retry that stores another post, a committed post's index step that stores one). The phase is built, not named by a window length (M2): each row fails the entries
    /// k times and stops the clock one tick after the k-th failure (k = 5 is the exercise's case, due 160 s out), and the last row stops it one tick before the entries
    /// fall due. Every waiting entry is stored within one tick of the success.
    /// </summary>
    [Fact]
    public async Task R11_69_CPF15_ASuccessMakesEveryWaitingEntryDueAtOnceAtEveryPhaseOfTheLadder()
    {
        var rows = new List<(string Name, int Failures, bool BeforeDue)>();
        for (var k = 1; k <= 5; k++) rows.Add(($"after failure {k}", k, false));
        rows.Add(("half way through the wait of failure 5", 5, true));

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var source in new[] { "a catch-up store of another post", "a committed post's index step" })
        foreach (var (name, k, beforeDue) in rows)
        {
            var h = new IndexRepairHarness();
            var waiting = new[] { await h.AppendAsync(Ct), await h.AppendAsync(Ct), await h.AppendAsync(Ct) };
            var other = await h.AppendAsync(Ct);
            h.Index.FailAll = true;
            foreach (var post in waiting) await h.Committed.IndexAsync(post, Ct);
            var now = await FailUntilAsync(h, waiting, k, h.Now);
            if (beforeDue)
            {
                var halfway = now + TimeSpan.FromSeconds(5 * Math.Pow(2, k) / 2);
                while (now + Tick < halfway)
                {
                    now += Tick;
                    await h.CatchUp.RunOnceAsync(h.Store, now, Ct);
                }
            }
            else
            {
                now += Tick;
                await h.CatchUp.RunOnceAsync(h.Store, now, Ct);
            }

            h.Index.Heal();
            var successAt = now + Tick;
            if (source.StartsWith("a catch-up", StringComparison.Ordinal))
            {
                h.Backlog.Add(new PendingEntry(other.Digest, other.PostId, other.Sequence), successAt);
                await h.CatchUp.RunOnceAsync(h.Store, successAt, Ct);
            }
            else
            {
                await h.Committed.IndexAsync(other, Ct);
            }

            var oneTickLater = successAt + Tick;
            await h.CatchUp.RunOnceAsync(h.Store, oneTickLater, Ct);
            var states = new List<string>();
            foreach (var post in waiting) states.Add(await WaitingAsync(h, post.Digest, oneTickLater));

            expected.Add($"{source}, {name}: stored, stored, stored");
            actual.Add($"{source}, {name}: {string.Join(", ", states)}");
        }

        Said(string.Join("; ", expected), string.Join("; ", actual));
    }

    /// <summary>
    /// CPF16 (a negative with its positive in one fact). A post the model cannot place (<c>NotApplicable</c>) and a step that failed are not successes of the store:
    /// the waiting entries are not retried after either; a healthy post's step is, and every waiting entry is stored within one tick of it.
    /// </summary>
    [Fact]
    public async Task R11_69_CPF16_ANotApplicablePostAndAFailedStepResetNothingAndAHealthyStepDoes()
    {
        var h = new IndexRepairHarness();
        var waiting = new[] { await h.AppendAsync(Ct), await h.AppendAsync(Ct) };
        h.Index.FailAll = true;
        foreach (var post in waiting) await h.Committed.IndexAsync(post, Ct);
        var now = await FailUntilAsync(h, waiting, 3, h.Now);
        now += Tick;
        await h.CatchUp.RunOnceAsync(h.Store, now, Ct);
        var before = waiting.Select(p => AttemptsFor(h, p.Digest)).ToArray();

        // Not applicable: nothing in it to embed. The index would store it if it could; the embedder cannot place it.
        h.Index.Heal();
        var nothing = SearchProjector.TryRead((await AcceptThenReadAsync(h))[0]) ?? throw new InvalidOperationException("not a post");
        var notApplicable = await h.Committed.IndexAsync(nothing, Ct);
        now += Tick;
        await h.CatchUp.RunOnceAsync(h.Store, now, Ct);
        var afterNotApplicable = waiting.Select(p => AttemptsFor(h, p.Digest)).ToArray();

        // A failed step: the index fails again for the next post.
        h.Index.FailAll = true;
        var failedPost = await h.AppendAsync(Ct);
        var failedStep = await h.Committed.IndexAsync(failedPost, Ct);
        now += Tick;
        await h.CatchUp.RunOnceAsync(h.Store, now, Ct);
        var afterFailedStep = waiting.Select(p => AttemptsFor(h, p.Digest)).ToArray();

        // The positive: the store works and a healthy post's step stores a vector.
        h.Index.Heal();
        var healthy = await h.AppendAsync(Ct);
        await h.Committed.IndexAsync(healthy, Ct);
        now += Tick;
        await h.CatchUp.RunOnceAsync(h.Store, now, Ct);
        var states = new List<string>();
        foreach (var post in waiting) states.Add(await WaitingAsync(h, post.Digest, now));

        var expected = $"{CommittedIndexState.NotApplicable} and {CommittedIndexState.Pending} resent nothing ({string.Join(' ', before)} attempts became {string.Join(' ', before)} then {string.Join(' ', before)}); a healthy step stored every waiting entry within one tick: stored, stored";
        var unchanged = before.SequenceEqual(afterNotApplicable) && before.SequenceEqual(afterFailedStep);
        var actual = $"{notApplicable.State} and {failedStep.State} resent {(unchanged ? "nothing" : "entries")} ({string.Join(' ', before)} attempts became {string.Join(' ', afterNotApplicable)} then {string.Join(' ', afterFailedStep)}); a healthy step stored every waiting entry within one tick: {string.Join(", ", states)}";
        Said(expected, actual);
    }

    private static async Task<IReadOnlyList<Curia.Domain.AppendedEvent>> AcceptThenReadAsync(IndexRepairHarness h)
    {
        var id = "cpf16-unplaceable-" + Guid.NewGuid().ToString("N")[..8];
        await AcceptAsync(h.Store, new EmbeddingIndexer(h.Embedder, new Curia.Application.Tests.InMemory.InMemoryVectorIndex()), id, Ct, title: "???", body: "!!!", tags: []).ConfigureAwait(false);
        var log = Require(await h.Store.ReadAllAsync(Ct).ConfigureAwait(false));
        return [log[^1]];
    }

    /// <summary>
    /// CPF17 (a negative with its positive in one fact). A poison entry (its event is not in the log, which no recovery changes) is not hammered: while another post is
    /// stored every second for a minute, the poison entry is retried no more than once per first wait plus the first time; and the positive, a waiting healthy entry
    /// is stored within two ticks of the first healthy store.
    /// </summary>
    [Fact]
    public async Task R11_69_CPF17_APoisonEntryIsRetriedNoMoreThanOncePerFirstWaitWhileAHealthyEntryIsRestored()
    {
        var h = new IndexRepairHarness();
        var waiting = await h.AppendAsync(Ct);
        h.Index.FailAll = true;
        await h.Committed.IndexAsync(waiting, Ct);
        var now = await FailUntilAsync(h, [waiting], 3, h.Now);
        var poison = new PendingEntry("sha256:" + new string('0', 64), "poison-post", 99_999);
        h.Backlog.Add(poison, now);
        h.Index.Heal();

        var start = now;
        var retries = 0;
        var storedAt = -1;
        for (var second = 1; second <= 60; second++)
        {
            now = start + TimeSpan.FromSeconds(second);
            var post = await h.AppendAsync(Ct);
            await h.Committed.IndexAsync(post, Ct);
            var result = await h.CatchUp.RunOnceAsync(h.Store, now, Ct);
            retries += result.FailedRetries.Count(f => f.Digest == poison.Digest);
            if (storedAt < 0 && (await h.IndexedAsync(Ct)).Contains(waiting.Digest)) storedAt = second;
        }

        var limit = 1 + (int)(TimeSpan.FromSeconds(60) / PublishedCheckWords.ResetLimit());
        var expected = $"the poison entry was retried at least once and no more than {limit} times in 60 s; the waiting healthy entry was stored by second 2";
        var fine = retries >= 1 && retries <= limit && storedAt is >= 1 and <= 2;
        var actual = fine
            ? expected
            : $"the poison entry was retried {retries} times in 60 s; the waiting healthy entry was stored by second {(storedAt < 0 ? "never" : storedAt.ToString(CultureInfo.InvariantCulture))} (expected by second 2)";
        Said(expected, actual);
    }

    /// <summary>
    /// CPF18. After a reset the ladder restarts at the first wait: an entry that keeps failing, reset by another post's success, is retried at the first tick after the
    /// reset, again the first wait later and then twice that, and not at its old wait (it had failed three times: 40 s).
    /// </summary>
    [Fact]
    public async Task R11_69_CPF18_AResetEntryThatFailsAgainWaitsTheFirstWaitThenTwiceThatAndNotItsOldWait()
    {
        var h = new IndexRepairHarness();
        var stubborn = await h.AppendAsync(Ct);
        h.Index.FailAll = true;
        await h.Committed.IndexAsync(stubborn, Ct);
        var now = await FailUntilAsync(h, [stubborn], 3, h.Now);
        h.Index.Heal();
        h.Index.Fail(stubborn.Digest);

        var other = await h.AppendAsync(Ct);
        var successAt = now + Tick;
        await h.Committed.IndexAsync(other, Ct);
        var seen = new List<string>();
        var known = AttemptsFor(h, stubborn.Digest);
        for (var s = 1; s <= 30; s++)
        {
            await h.CatchUp.RunOnceAsync(h.Store, successAt + TimeSpan.FromSeconds(s), Ct);
            var attempts = AttemptsFor(h, stubborn.Digest);
            if (attempts > known) seen.Add(s.ToString(CultureInfo.InvariantCulture));
            known = attempts;
        }

        var first = PublishedCheckWords.ResetWait();
        var secs = new[] { 1, 1 + (int)first.TotalSeconds, 1 + (int)first.TotalSeconds + (int)(first.TotalSeconds * 2) };
        Said(
            $"the failing entry was retried at seconds {string.Join(", ", secs)} after the success",
            $"the failing entry was retried at seconds {(seen.Count == 0 ? "none" : string.Join(", ", seen))} after the success");
    }
}
