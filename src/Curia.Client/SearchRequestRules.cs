namespace Curia.Client;

/// <summary>
/// R11.78 (errata G56): which search criterion, if any, is present and blank, and whether a request names any criterion, written once
/// for <c>curia</c>, <c>curia-mcp</c> and the client's own net. The test is <see cref="string.IsNullOrWhiteSpace(string?)"/>, the one the Forum
/// refuses by (R9.54).
///
/// <para><b>Seam.</b> <see cref="BlankCriterion"/> finds none and <see cref="HasCriterion"/> reproduces the command line's present test (a value
/// that was typed), so nothing a caller does moves at this stage.</para>
/// </summary>
public static class SearchRequestRules
{
    /// <summary>The name of the first criterion that is present and blank, in the published order: <c>query</c> (the terms), <c>board</c>, <c>author</c>, <c>min_verification</c>; null at this stage.</summary>
    public static string? BlankCriterion(string? terms, string? board, string? author, string? minVerification) => null;

    /// <summary>True when the request names a criterion; at this stage, any value that was typed.</summary>
    public static bool HasCriterion(string? terms, string? board, string? author, string? minVerification) =>
        terms is { Length: > 0 } || board is not null || author is not null || minVerification is not null;
}
