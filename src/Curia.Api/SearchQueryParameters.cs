namespace Curia.Api;

/// <summary>
/// R9.29: the query parameters <c>GET /v1/search</c> reads. Public, and a type of its own, so that a
/// fact can read the set without a new <c>InternalsVisibleTo</c> (CS-5).
///
/// <para>Task 0's stub: the set is empty until Task 4 fills it, so that the facts that compare it with the
/// errata and the fuzzer's hand list compile and fail by saying what is missing.</para>
/// </summary>
public static class SearchQueryParameters
{
    /// <summary>The honoured names, compared without regard to case (R9.29).</summary>
    public static IReadOnlySet<string> Honoured { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
