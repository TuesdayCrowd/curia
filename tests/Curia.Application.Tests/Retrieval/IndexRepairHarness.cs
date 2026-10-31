using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Tests.Shared;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// An in-memory index that fails when told to (the whole index, or the digests named), counts what reached it and remembers which digests it was asked to
/// store. The probes of errata G51's repair (the D92 PR, PIL11 to PIL17) hold their expectations against it and against the log, not against the code under test.
/// </summary>
internal sealed class FlakyIndex(IVectorIndex inner) : IVectorIndex
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _failing = [];
    private readonly List<string> _attempted = [];

    internal bool FailAll { get; set; }

    internal void Fail(params string[] digests)
    {
        lock (_gate) foreach (var d in digests) _failing.Add(d);
    }

    internal void Heal()
    {
        FailAll = false;
        lock (_gate) _failing.Clear();
    }

    /// <summary>Every digest an upsert was attempted for, in order, failed ones included.</summary>
    internal string[] Attempted
    {
        get
        {
            lock (_gate) return [.. _attempted];
        }
    }

    public Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default)
    {
        bool fails;
        lock (_gate)
        {
            _attempted.Add(digest);
            fails = FailAll || _failing.Contains(digest);
        }

        return fails
            ? Task.FromResult(Result<IndexedVector>.Fail(new Error("curia/retrieval/index-unavailable", "The vector index failed")))
            : inner.UpsertAsync(digest, postId, sequence, embedding, cancellationToken);
    }

    public Task<Result<System.Collections.Immutable.ImmutableArray<VectorMatch>>> NearestAsync(Embedding query, int limit, CancellationToken cancellationToken = default) =>
        inner.NearestAsync(query, limit, cancellationToken);

    public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.CountAsync(model, cancellationToken);

    public Task<Result<long>> MaxSequenceAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.MaxSequenceAsync(model, cancellationToken);

    public Task<Result<IReadOnlySet<string>>> IndexedDigestsAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.IndexedDigestsAsync(model, cancellationToken);
}

/// <summary>A log, an index that can fail, and the types of errata G51 over them, with posts appended to the log but never indexed by the harness itself.</summary>
internal sealed class IndexRepairHarness
{
    private int _next;

    internal IndexRepairHarness()
    {
        Clock = RetrievalTestLog.Clock();
        Store = new InMemoryEventStore(Clock, new PermissiveLifecycleAppendGate());
        Embedder = new HashedNGramEmbedder();
        Index = new FlakyIndex(new InMemoryVectorIndex());
        Indexer = new EmbeddingIndexer(Embedder, Index);
        Backlog = new IndexBacklog();
        Committed = new CommittedPostIndexer(Indexer, Backlog, Clock);
        CatchUp = new EmbeddingCatchUp(Indexer, Backlog);
    }

    internal ManualTimeProvider Clock { get; }

    internal InMemoryEventStore Store { get; }

    internal HashedNGramEmbedder Embedder { get; }

    internal FlakyIndex Index { get; }

    internal EmbeddingIndexer Indexer { get; }

    internal IndexBacklog Backlog { get; }

    internal CommittedPostIndexer Committed { get; }

    internal EmbeddingCatchUp CatchUp { get; }

    /// <summary>The instant the harness's clock reads now: the failure's instant when a post is committed now.</summary>
    internal DateTimeOffset Now => Clock.GetUtcNow();

    /// <summary>Appends a discussion post to the log, indexing it into a throwaway index and not into <see cref="Index"/>, and returns it as the route would hand it to the indexer.</summary>
    internal async Task<SearchablePost> AppendAsync(CancellationToken ct, string? body = null)
    {
        var id = $"repair-post-{Interlocked.Increment(ref _next):D6}";
        var throwaway = new EmbeddingIndexer(Embedder, new InMemoryVectorIndex());
        await AcceptAsync(Store, throwaway, id, ct, body: body ?? $"Pending phrase {id} about quokka {Guid.NewGuid():N}", title: $"About {id}").ConfigureAwait(false);

        var log = Require(await Store.ReadAllAsync(ct).ConfigureAwait(false));
        return SearchProjector.TryRead(log[^1]) ?? throw new InvalidOperationException("the appended event is not a searchable post");
    }

    /// <summary>The digests the index holds now.</summary>
    internal async Task<string[]> IndexedAsync(CancellationToken ct) =>
        [.. Require(await Index.IndexedDigestsAsync(Embedder.Model, ct).ConfigureAwait(false)).Order(StringComparer.Ordinal)];

    /// <summary>The log's count and highest sequence, read from the store.</summary>
    internal async Task<(int Count, long MaxSeq)> LogShapeAsync(CancellationToken ct)
    {
        var log = Require(await Store.ReadAllAsync(ct).ConfigureAwait(false));
        return (log.Count, log.Count == 0 ? 0 : log.Max(e => e.Seq.Value));
    }

    internal static string Short(string digest) => digest.Length > 13 ? digest[7..13] : digest;
}
