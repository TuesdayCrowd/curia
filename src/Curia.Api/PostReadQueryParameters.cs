using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.57 (errata G62): the query name <c>GET /v1/posts/{postId}</c>, <c>POST /v1/posts/batch</c> and <c>GET /v1/threads/{rootPostId}</c> read, and the refusal of any other
/// name, by the engine R9.29, R9.37 and R9.50 share (<see cref="QueryParameterRefusal"/>). Public, and a type of its own, so a fact reads the set and the types
/// without a new <c>InternalsVisibleTo</c> (CS-5).
/// </summary>
public static class PostReadQueryParameters
{
    /// <summary>The problem type of an unknown name on the single read and the batch.</summary>
    public const string PostsUnknownParameterType = "curia/posts/unknown-parameter";

    /// <summary>The problem type of an unknown name on the thread read.</summary>
    public const string ThreadsUnknownParameterType = "curia/threads/unknown-parameter";

    /// <summary>The honoured names, compared without regard to case (R9.57).</summary>
    public static IReadOnlySet<string> Honoured { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "marking",
    };

    /// <summary>Null when every name in the query is honoured; otherwise the refusal, typed for the single read and the batch.</summary>
    public static Error? CheckPosts(IEnumerable<string> names) =>
        QueryParameterRefusal.Check(names, Honoured, PostsUnknownParameterType, SearchQueryParameters.RefusalTitle, string.Empty);

    /// <summary>Null when every name in the query is honoured; otherwise the refusal, typed for the thread read.</summary>
    public static Error? CheckThreads(IEnumerable<string> names) =>
        QueryParameterRefusal.Check(names, Honoured, ThreadsUnknownParameterType, SearchQueryParameters.RefusalTitle, string.Empty);
}
