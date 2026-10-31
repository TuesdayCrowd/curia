using System.Collections.ObjectModel;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain;

namespace Curia.Application.Retrieval;

/// <summary>What one pass of the catch-up stored: the digest of each vector it brought into the index.</summary>
public sealed record CatchUpResult(IReadOnlyList<string> StoredDigests);

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

    public EmbeddingCatchUp(EmbeddingIndexer indexer, IndexBacklog backlog)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(backlog);
        _indexer = indexer;
        _backlog = backlog;
    }

    public async Task<CatchUpResult> RunOnceAsync(IEventReader events, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var stored = new Collection<string>();
        foreach (var entry in _backlog.Due(now))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (await RetryAsync(events, entry, cancellationToken).ConfigureAwait(false))
            {
                case Retry.Stored:
                    stored.Add(entry.Digest);
                    _backlog.Succeeded(entry.Digest);
                    break;
                case Retry.NothingToStore:
                    _backlog.Succeeded(entry.Digest);
                    break;
                default:
                    _backlog.Failed(entry.Digest, now);
                    break;
            }
        }

        if (_backlog.Overflowed)
        {
            // Cleared before the pass, so an add refused while it runs sets it again; set again if the pass cannot complete.
            _backlog.ClearOverflow();
            var before = stored.Count;
            var repaired = await ReconcileAsync(events, stored, cancellationToken).ConfigureAwait(false);
            if (!repaired) _backlog.MarkOverflowed();
            foreach (var digest in stored.Skip(before))
                _backlog.Succeeded(digest);
        }

        return new CatchUpResult([.. stored]);
    }

    private enum Retry
    {
        Stored,
        NothingToStore,
        Failed,
    }

    private async Task<Retry> RetryAsync(IEventReader events, PendingEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            // ReadForwardAsync is "after": the event at the entry's sequence is the first one after the sequence before it.
            if (!EventSequence.From(entry.Sequence - 1).TryGetValue(out var before, out _)) return Retry.Failed;

            var read = await events.ReadForwardAsync(before, 1, cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var page, out _) || page.Count != 1 || page[0].Seq.Value != entry.Sequence) return Retry.Failed;
            if (SearchProjector.TryRead(page[0]) is not { } post || !string.Equals(post.Digest, entry.Digest, StringComparison.Ordinal)) return Retry.Failed;

            var indexed = await _indexer.IndexAsync(post, cancellationToken).ConfigureAwait(false);
            if (!indexed.TryGetValue(out var vector, out _)) return Retry.Failed;
            return vector is null ? Retry.NothingToStore : Retry.Stored;
        }
#pragma warning disable CA1031 // R11.69: a retry that throws is a retry that failed; the entry stays and the next wait is longer.
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            return Retry.Failed;
        }
    }

    private async Task<bool> ReconcileAsync(IEventReader events, Collection<string> stored, CancellationToken cancellationToken)
    {
        try
        {
            var reconciled = await _indexer.ReconcileByAbsenceAsync(events, stored, cancellationToken).ConfigureAwait(false);
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
