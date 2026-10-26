using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.29: the query parameters <c>GET /v1/search</c> reads, and the refusal of any other name. Public, and a
/// type of its own, so that a fact can read the set without a new <c>InternalsVisibleTo</c> (CS-5).
///
/// <para>A name the route does not read used to be dropped, which returned the unfiltered corpus to an agent
/// that believed it had filtered (<c>kinds=question</c>, <c>minVerification=V2</c>). It is refused by name
/// instead. The name is a caller's string, so it is echoed only if it is a plain name (letters, digits,
/// <c>_ . -</c>, 1 to 64 characters, checked by hand: R10.69's fence holds every shipped assembly's IL), and a
/// near-miss is answered with the honoured spelling, which is a constant of this type and never the caller's
/// text.</para>
/// </summary>
public static class SearchQueryParameters
{
    /// <summary>The problem type of an unknown name; R9.26's refusable names keep <c>unsupported-filter</c>.</summary>
    public const string UnknownParameterType = "curia/search/unknown-parameter";

    internal const string RefusalTitle = "That parameter is not read by this route and is refused rather than ignored";

    /// <summary>The honoured names, compared without regard to case (R9.29).</summary>
    public static IReadOnlySet<string> Honoured { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "q", "board", "author", "kind", "tags", "min_verification", "cursor", "limit", "why", "marking",
    };

    /// <summary>Null when every name in the query is honoured; otherwise the refusal naming those that are not.</summary>
    public static Error? Check(IEnumerable<string> names) =>
        QueryParameterRefusal.Check(names, Honoured, UnknownParameterType, RefusalTitle, string.Empty);
}
