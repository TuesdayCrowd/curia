using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.50 (errata G49): <c>GET /v1/budget</c> honours no query name, so any name is refused. The shape of <see cref="InboxQueryParameters"/> over an empty set,
/// through the engine R9.29 and R9.37 share and no copy of it. Public, and a type of its own, so a fact reads the set without a new
/// <c>InternalsVisibleTo</c> (CS-5).
/// </summary>
public static class BudgetQueryParameters
{
    /// <summary>The problem type of any query name on this route.</summary>
    public const string UnknownParameterType = "curia/budget/unknown-parameter";

    /// <summary>The honoured names, compared without regard to case: none.</summary>
    public static IReadOnlySet<string> Honoured => Names;

    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the query holds no name; otherwise the refusal naming them.</summary>
    public static Error? Check(IEnumerable<string> names)
    {
        return QueryParameterRefusal.Check(names, Names, UnknownParameterType, SearchQueryParameters.RefusalTitle, string.Empty);
    }
}
