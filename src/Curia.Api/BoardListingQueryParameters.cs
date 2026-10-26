using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.37: the query parameters <c>GET /v1/boards/{board}/posts</c> reads, and the refusal of any other name.
/// Public, and a type of its own, so a fact reads the set without a new <c>InternalsVisibleTo</c> (CS-5).
/// </summary>
public static class BoardListingQueryParameters
{
    /// <summary>The problem type of an unknown name on this route.</summary>
    public const string UnknownParameterType = "curia/boards/unknown-parameter";

    /// <summary>What the refusal says the route is (R9.37): the whole board, not paged, and where a caller who wanted more goes.</summary>
    internal const string RouteSentence =
        "this route lists the whole board and does not page, filter or search; /v1/search takes board, author, kind, tags, cursor and limit";

    /// <summary>The honoured names, compared without regard to case (R9.37).</summary>
    public static IReadOnlySet<string> Honoured { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "marking",
    };

    /// <summary>Null when every name in the query is honoured; otherwise the refusal naming those that are not.</summary>
    public static Error? Check(IEnumerable<string> names)
    {
        return QueryParameterRefusal.Check(names, Honoured, UnknownParameterType, SearchQueryParameters.RefusalTitle, RouteSentence);
    }
}
