using System.Collections.Immutable;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Application.Ports;

/// <summary>A vector under a named model (R9.5). Never compared across models.</summary>
public sealed record Embedding(EmbeddingModel Model, ImmutableArray<float> Vector);

/// <summary>
/// The embedding primitive behind a port (R11.2): the domain decides what is embedded and how
/// vectors are fused; an adapter produces the vectors. <c>HashedNGramEmbedder</c> is the
/// dependency-free adapter; an ONNX adapter is the scoping document's intended production one.
/// </summary>
public interface ITextEmbedder
{
    EmbeddingModel Model { get; }

    Result<Embedding> Embed(string text);
}

/// <summary>One stored vector's identity: the post it embeds and where that post sits in the log.</summary>
public sealed record IndexedVector(string Digest, string PostId, long Sequence);

/// <summary>A nearest-neighbour answer: the stored vector and its cosine distance (0 = identical direction) from the query.</summary>
public sealed record VectorMatch(string Digest, string PostId, long Sequence, double Distance);

/// <summary>
/// The vector index: a read model over the event log (R11.10), rebuildable by replay, keyed by
/// envelope digest and model. Postgres with <c>pgvector</c> in production; an in-memory adapter
/// for the application tests (R11.4).
/// </summary>
public interface IVectorIndex
{
    /// <summary>Stores or replaces the vector for <paramref name="digest"/> under <paramref name="embedding"/>'s model.</summary>
    Task<Result<IndexedVector>> UpsertAsync(
        string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default);

    /// <summary>The <paramref name="limit"/> stored vectors nearest to <paramref name="query"/> under its model, nearest first.</summary>
    Task<Result<ImmutableArray<VectorMatch>>> NearestAsync(
        Embedding query, int limit, CancellationToken cancellationToken = default);

    /// <summary>How many vectors the index holds under <paramref name="model"/>; what a rebuild compares against the log.</summary>
    Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default);

    /// <summary>
    /// The highest <c>seq</c> indexed under <paramref name="model"/>, or 0 when none: the log is
    /// append-only and appends are serialized (R6.47), so everything after it is exactly what a
    /// reconcile still has to index.
    /// </summary>
    Task<Result<long>> MaxSequenceAsync(EmbeddingModel model, CancellationToken cancellationToken = default);
}
