using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.37: the query parameters <c>GET /v1/inbox</c> reads, and the refusal of any other name.
/// Public, and a type of its own, so a fact reads the set without a new <c>InternalsVisibleTo</c> (CS-5).
/// </summary>
public static class InboxQueryParameters
{
    /// <summary>The problem type of an unknown name on this route.</summary>
    public const string UnknownParameterType = "curia/inbox/unknown-parameter";

    /// <summary>The honoured names, compared without regard to case (R9.37).</summary>
    public static IReadOnlySet<string> Honoured { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "board", "tags", "cursor", "limit", "marking",
    };

    /// <summary>Null when every name in the query is honoured; otherwise the refusal naming those that are not.</summary>
    public static Error? Check(IEnumerable<string> names)
    {
        return QueryParameterRefusal.Check(names, Honoured, UnknownParameterType, SearchQueryParameters.RefusalTitle, string.Empty);
    }
}
