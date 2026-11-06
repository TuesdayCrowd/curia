using System.Collections.Immutable;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// R11.78 (errata G56): which search criterion, if any, is present and blank, and whether a request names a board or author filter, written once
/// for <c>curia</c>, <c>curia-mcp</c> and the client's own net. The test is <see cref="string.IsNullOrWhiteSpace(string?)"/>, the one the Forum
/// refuses by (R9.54). A criterion that is null is absent; one that is empty or white space is present and blank.
/// </summary>
public static class SearchRequestRules
{
    /// <summary>
    /// The name of the first criterion that is present and names nothing, in the published order of R9.56 (<c>query</c> (the terms), <c>board</c>, <c>author</c>,
    /// <c>min_verification</c>, <c>kind</c>, <c>tags</c>, <c>cursor</c>); null when none is. <paramref name="kind"/> and <paramref name="tags"/> are comma-separated lists as the
    /// flag is typed and name nothing when only commas and white space are in them (R11.86); <paramref name="cursor"/> names nothing when blank.
    /// </summary>
    public static string? BlankCriterion(
        string? terms, string? board, string? author, string? minVerification, string? kind = null, string? tags = null, string? cursor = null)
    {
        if (IsPresentAndBlank(terms)) return "query";
        if (IsPresentAndBlank(board)) return "board";
        if (IsPresentAndBlank(author)) return "author";
        if (IsPresentAndBlank(minVerification)) return "min_verification";
        if (kind is not null && QueryValue.NamesNothing(kind, isList: true)) return "kind";
        if (tags is not null && QueryValue.NamesNothing(tags, isList: true)) return "tags";
        if (cursor is not null && QueryValue.NamesNothing(cursor, isList: false)) return "cursor";
        return null;
    }

    /// <summary>
    /// The library's form of <see cref="BlankCriterion(string?, string?, string?, string?, string?, string?, string?)"/>: the kinds as the items a caller gave, of which any that names
    /// nothing is refused, by the test the Forum refuses a list by (<see cref="QueryValue.NamesNothing"/>): an item that is empty, white space or only commas (<c>","</c>) would be joined into
    /// a list the Forum reads as naming something and so would be sent and silently dropped. An empty or default list names nothing at all and is not a criterion (errata G62, R11.86).
    /// </summary>
    public static string? BlankCriterion(
        string? terms, string? board, string? author, string? minVerification, ImmutableArray<string> kinds, string? cursor)
    {
        if (BlankCriterion(terms, board, author, minVerification) is { } blank) return blank;
        if (!kinds.IsDefaultOrEmpty && kinds.Any(item => QueryValue.NamesNothing(item, isList: true))) return "kind";
        return BlankCriterion(null, null, null, null, cursor: cursor);
    }

    /// <summary>True when the request names a board or an author filter: some member holds a character that is not white space. The terms are the caller's own.</summary>
    public static bool HasFilter(string? board, string? author) =>
        !string.IsNullOrWhiteSpace(board)
        || !string.IsNullOrWhiteSpace(author);

    private static bool IsPresentAndBlank(string? value) => value is not null && string.IsNullOrWhiteSpace(value);
}
