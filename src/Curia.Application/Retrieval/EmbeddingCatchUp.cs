using System.Collections.ObjectModel;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Retrieval;

/// <summary>What one pass of the catch-up stored: the digest of each vector it brought into the index.</summary>
public sealed record CatchUpResult(IReadOnlyList<string> StoredDigests)
{
    /// <summary>The retries of this pass that failed: the post, the digest and the fault's type and fixed detail, and never any text the post carried.</summary>
    public IReadOnlyList<RetryFault> FailedRetries { get; init; } = [];

    /// <summary>The check of errata G54 (R8.83) this pass ran, or <see langword="null"/> when none was due.</summary>
    public CheckReport? Check { get; init; }
}

/// <summary>
/// What one run of the check reported (R8.83): the instant it began; how many posts it enrolled, and, where it could not complete, the type and fixed detail of the
/// fault (never any text a post carried).
/// </summary>
public sealed record CheckReport(DateTimeOffset Began, int Enrolled, string? FaultType = null, string? FaultDetail = null)
{
    /// <summary>Whether the check could not complete.</summary>
    public bool Failed => FaultType is not null;
}

/// <summary>One retry that failed (R11.69): a post id, a digest, and the type and fixed detail of the fault.</summary>
public sealed record RetryFault(string PostId, string Digest, string FaultType, string FaultDetail);

/// <summary>
/// The repair that brings a pending post into the index without a restart (errata G51, R11.69). Each entry the backlog says is due is retried from the log's own
/// event at its sequence (the backlog holds no text); an entry whose retry fails stays, and one whose vector is stored goes. Where the backlog dropped an
/// entry for its bound, the pass also repairs by absence of the digest. It writes the vector index and nothing else: it appends no event. It takes
/// <c>now</c> as an argument and reads no clock, so the schedule is a unit over instants and the hosted service is a thin loop.
/// </summary>
public sealed class EmbeddingCatchUp
{
    private readonly EmbeddingIndexer _indexer;
    private readonly IndexBacklog _backlog;

    // The by-absence pass backs off as an entry does (R11.69): after n failures in a row it waits the first wait doubled n-1 times, to the cap. One loop calls
    // RunOnceAsync, so these are not shared.
    private int _repairFailures;
    private DateTimeOffset _repairDue = DateTimeOffset.MinValue;

    public EmbeddingCatchUp(EmbeddingIndexer indexer, IndexBacklog backlog, IndexSweep sweep)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(backlog);
        ArgumentNullException.ThrowIfNull(sweep);
        _indexer = indexer;
        _backlog = backlog;
        Sweep = sweep;
    }

    /// <summary>The state of the check against the log (errata G54), which the catch-up runs when it is due.</summary>
    public IndexSweep Sweep { get; }

    public async Task<CatchUpResult> RunOnceAsync(IEventReader events, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var stored = new Collection<string>();
        var faults = new Collection<RetryFault>();

        // The check first (R8.83): it enrols the posts the index lacks, due now, and the retries below store them in this same pass.
        var check = Sweep.IsDue(now) ? await CheckAsync(events, now, cancellationToken).ConfigureAwait(false) : null;

        foreach (var entry in _backlog.Due(now))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (outcome, fault) = await RetryAsync(events, entry, cancellationToken).ConfigureAwait(false);
            switch (outcome)
            {
                case Retry.Stored:
                    stored.Add(entry.Digest);
                    _backlog.Succeeded(entry.Digest);

                    // A store that has just answered is the best evidence there is that it is back (R11.69 (rev.)): the rest of the backlog is due now.
                    _backlog.NoteSuccess(now);
                    break;
                case Retry.NothingToStore:
                    _backlog.Succeeded(entry.Digest);
                    break;
                default:
                    _backlog.Failed(entry.Digest, now);
                    faults.Add(new RetryFault(entry.PostId, entry.Digest, fault!.Type, fault.Detail));
                    break;
            }
        }

        if (_backlog.Overflowed && now >= _repairDue)
        {
            // Cleared before the pass, so an add refused while it runs sets it again; set again if the pass cannot complete.
            _backlog.ClearOverflow();
            var before = stored.Count;
            var repaired = await ReconcileAsync(events, stored, cancellationToken).ConfigureAwait(false);
            if (repaired)
            {
                _repairFailures = 0;
            }
            else
            {
                _backlog.MarkOverflowed();
                _repairFailures++;
                var ticks = IndexBacklog.FirstWait.Ticks;
                for (var i = 1; i < _repairFailures && ticks < IndexBacklog.WaitCap.Ticks; i++)
                    ticks = Math.Min(ticks * 2, IndexBacklog.WaitCap.Ticks);
                _repairDue = now + TimeSpan.FromTicks(ticks);
            }

            foreach (var digest in stored.Skip(before))
                _backlog.Succeeded(digest);
        }

        return new CatchUpResult([.. stored]) { FailedRetries = [.. faults], Check = check };
    }

    /// <summary>
    /// R8.83's check: the one absence rule over the log and the index, enrolling each post the index lacks and the model can place into the backlog as the scan
    /// meets it and indexing nothing. A scan that cannot complete leaves the instant of the last check and the obligation as they were and what it enrolled
    /// enrolled. An add the bound refuses sets the backlog's overflow, which the by-absence pass repairs.
    /// </summary>
    private async Task<CheckReport> CheckAsync(IEventReader events, DateTimeOffset began, CancellationToken cancellationToken)
    {
        var enrolled = 0;
        try
        {
            var scan = await _indexer.FindAbsentAsync(
                events,
                Sweep,
                (post, _, _) =>
                {
                    if (_backlog.Add(new PendingEntry(post.Digest, post.PostId, post.Sequence), began)) enrolled++;
                    return Task.FromResult<Error?>(null);
                },
                cancellationToken).ConfigureAwait(false);

            if (scan.Failure is { } failure)
            {
                Sweep.Failed(began);
                return new CheckReport(began, enrolled, failure.Type, failure.Title);
            }

            Sweep.Completed(began);
            return new CheckReport(began, enrolled);
        }
#pragma warning disable CA1031 // R8.83: a check that throws did not complete; nothing is moved, and the next attempt is on the failure ladder.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            Sweep.Failed(began);
            return new CheckReport(began, enrolled, ex.GetType().FullName ?? ex.GetType().Name, "the check threw an exception");
        }
    }

    private enum Retry
    {
        Stored,
        NothingToStore,
        Failed,
    }

    private sealed record Fault(string Type, string Detail);

    private static readonly Fault NotInTheLog = new("curia/retrieval/retry-event-not-found", "the entry's event could not be read from the log as the post it names");

    private async Task<(Retry Outcome, Fault? Fault)> RetryAsync(IEventReader events, PendingEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            // ReadForwardAsync is "after": the event at the entry's sequence is the first one after the sequence before it.
            if (!EventSequence.From(entry.Sequence - 1).TryGetValue(out var before, out _)) return (Retry.Failed, NotInTheLog);

            var read = await events.ReadForwardAsync(before, 1, cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var page, out _) || page.Count != 1 || page[0].Seq.Value != entry.Sequence) return (Retry.Failed, NotInTheLog);
            if (SearchProjector.TryRead(page[0]) is not { } post || !string.Equals(post.Digest, entry.Digest, StringComparison.Ordinal)) return (Retry.Failed, NotInTheLog);

            var indexed = await _indexer.IndexAsync(post, cancellationToken).ConfigureAwait(false);
            if (!indexed.TryGetValue(out var vector, out var error)) return (Retry.Failed, new Fault(error!.Type, error.Title));
            return (vector is null ? Retry.NothingToStore : Retry.Stored, null);
        }
#pragma warning disable CA1031 // R11.69: a retry that throws is a retry that failed; the entry stays and the next wait is longer.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return (Retry.Failed, new Fault(ex.GetType().FullName ?? ex.GetType().Name, "the retry threw an exception"));
        }
    }

    private async Task<bool> ReconcileAsync(IEventReader events, Collection<string> stored, CancellationToken cancellationToken)
    {
        try
        {
            var reconciled = await _indexer.ReconcileByAbsenceAsync(events, stored, Sweep, cancellationToken).ConfigureAwait(false);
            return reconciled.TryGetValue(out _, out _);
        }
#pragma warning disable CA1031 // R11.69: a by-absence pass that throws did not complete; the overflow stays set and the next pass runs it again.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return false;
        }
    }
}
