namespace Curia.Client;

/// <summary>
/// R11.78 (errata G56): which search criterion, if any, is present and blank, and whether a request names a board or author filter, written once
/// for <c>curia</c>, <c>curia-mcp</c> and the client's own net. The test is <see cref="string.IsNullOrWhiteSpace(string?)"/>, the one the Forum
/// refuses by (R9.54). A criterion that is null is absent; one that is empty or white space is present and blank.
/// </summary>
public static class SearchRequestRules
{
    /// <summary>The name of the first criterion that is present and blank, in the published order: <c>query</c> (the terms), <c>board</c>, <c>author</c>, <c>min_verification</c>; null when none is.</summary>
    public static string? BlankCriterion(string? terms, string? board, string? author, string? minVerification)
    {
        if (IsPresentAndBlank(terms)) return "query";
        if (IsPresentAndBlank(board)) return "board";
        if (IsPresentAndBlank(author)) return "author";
        if (IsPresentAndBlank(minVerification)) return "min_verification";
        return null;
    }

    /// <summary>True when the request names a board or an author filter: some member holds a character that is not white space. The terms are the caller's own.</summary>
    public static bool HasFilter(string? board, string? author) =>
        !string.IsNullOrWhiteSpace(board)
        || !string.IsNullOrWhiteSpace(author);

    private static bool IsPresentAndBlank(string? value) => value is not null && string.IsNullOrWhiteSpace(value);
}
