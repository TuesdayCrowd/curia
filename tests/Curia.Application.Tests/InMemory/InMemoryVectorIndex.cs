using System.Collections.Immutable;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// R11.4's in-memory <see cref="IVectorIndex"/>: a dictionary and a brute-force scan, with the
/// same ordering contract as the Postgres adapter -- distance, then <c>seq</c>.
/// </summary>
internal sealed class InMemoryVectorIndex : IVectorIndex
{
    private sealed record Stored(string PostId, long Sequence, ImmutableArray<float> Vector);

    private readonly Dictionary<(string Model, string Digest), Stored> _rows = new();
    private readonly Lock _gate = new();

    public const int MaximumLimit = 1_000;

    public Task<Result<IndexedVector>> UpsertAsync(
        string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        ArgumentNullException.ThrowIfNull(embedding);

        lock (_gate)
            _rows[(embedding.Model.Id, digest)] = new Stored(postId, sequence, embedding.Vector);

        return Task.FromResult(Result<IndexedVector>.Ok(new IndexedVector(digest, postId, sequence)));
    }

    public Task<Result<ImmutableArray<VectorMatch>>> NearestAsync(
        Embedding query, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (limit < 1 || limit > MaximumLimit)
            return Task.FromResult(Result<ImmutableArray<VectorMatch>>.Fail(RetrievalErrors.LimitOutOfRange(limit, MaximumLimit)));

        List<VectorMatch> matches;
        lock (_gate)
        {
            matches = _rows
                .Where(r => r.Key.Model == query.Model.Id)
                .Select(r => new VectorMatch(r.Key.Digest, r.Value.PostId, r.Value.Sequence, 1.0 - HashedNGramEmbedding.Cosine(query.Vector, r.Value.Vector)))
                .ToList();
        }

        return Task.FromResult(Result<ImmutableArray<VectorMatch>>.Ok(
            [.. matches.OrderBy(m => m.Distance).ThenBy(m => m.Sequence).Take(limit)]));
    }

    public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
            return Task.FromResult(Result<long>.Ok((long)_rows.Keys.Count(k => k.Model == model.Id)));
    }

    public Task<Result<long>> MaxSequenceAsync(EmbeddingModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        lock (_gate)
        {
            var rows = _rows.Where(r => r.Key.Model == model.Id).Select(r => r.Value.Sequence).ToList();
            return Task.FromResult(Result<long>.Ok(rows.Count == 0 ? 0 : rows.Max()));
        }
    }
}
