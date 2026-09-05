using Curia.Domain.Primitives;

namespace Curia.Application.Retrieval;

/// <summary>RFC 9457 problem-type slugs the retrieval path emits.</summary>
public static class RetrievalErrors
{
    /// <summary>
    /// The database has no <c>vector</c> type: pgvector is not installed. Reported, never
    /// worked around -- a retrieval that fell back to lexical search here would pass every
    /// test without the extension.
    /// </summary>
    public static Error PgvectorMissing() => new(
        "curia/retrieval/pgvector-missing",
        "The vector index is unavailable because the database has no pgvector extension",
        "db/0003_create_retrieval_index.sql creates it; a server without it cannot serve hybrid retrieval");

    public static Error IndexUnavailable(string detail) => new(
        "curia/retrieval/index-unavailable",
        "The vector index could not be queried",
        detail);

    public static Error LimitOutOfRange(int limit, int maximum) => new(
        "curia/retrieval/limit-out-of-range",
        "A nearest-neighbour limit must be between 1 and the published maximum",
        $"limit={limit} maximum={maximum}");
}
