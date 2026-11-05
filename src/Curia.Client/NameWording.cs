using System.Collections.Immutable;
using Curia.Domain.Content;
using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>
/// R10.97 (errata G62): the words both clients print for what the Forum said about the names a request filtered by, what the floor was tested against and whether
/// a board was new, written once. <c>curia</c>, <c>curia-mcp</c> and the resend's receipt call this class and none writes a sentence of its own, so the three cannot
/// drift (a content fence, <c>NameWordsFenceTests</c>, holds the sentences here and nowhere else). A value the client did not compose is a display literal; a
/// member the Forum did not state is <c>NOT STATED</c> and is never read as matched.
/// </summary>
public static class NameWording
{
    /// <summary>The label of the filters line: <c>filters</c> and three spaces, as the entry writes it.</summary>
    public const string FiltersLabel = "filters   ";

    private const string Board = "board";
    private const string Author = "author";

    /// <summary>
    /// The clauses of the filters line, in the published order (<c>board</c>, then <c>author</c>), one for each filter the request named; null where it named neither.
    /// <paramref name="unmatched"/> is the Forum's <c>unmatched_filters</c>, null where the Forum stated none. A filter that is null or blank was not named.
    /// </summary>
    public static string? FiltersClauses(string? board, string? author, ImmutableArray<string>? unmatched)
    {
        var clauses = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(board)) clauses.Add(Clause(Board, board, unmatched));
        if (!string.IsNullOrWhiteSpace(author)) clauses.Add(Clause(Author, author, unmatched));
        return clauses.Count == 0 ? null : string.Join("; ", clauses);
    }

    /// <summary>The whole filters line (<see cref="FiltersLabel"/> and the clauses), or null where the request named neither filter.</summary>
    public static string? FiltersLine(string? board, string? author, ImmutableArray<string>? unmatched) =>
        FiltersClauses(board, author, unmatched) is { } clauses ? FiltersLabel + clauses : null;

    /// <summary>True where the Forum stated that a filter the request named bears no served post; the inbox's sentence that nothing needs an answer is printed only where this is false.</summary>
    public static bool AnyUnmatched(string? board, string? author, ImmutableArray<string>? unmatched) =>
        unmatched is { } stated
        && ((!string.IsNullOrWhiteSpace(board) && stated.Contains(Board, StringComparer.Ordinal))
            || (!string.IsNullOrWhiteSpace(author) && stated.Contains(Author, StringComparer.Ordinal)));

    /// <summary><c>curia_search</c>'s member: the names the Forum listed joined by a comma, <c>none</c>, or <c>NOT STATED</c> where the Forum stated none.</summary>
    public static string ToolMember(ImmutableArray<string>? unmatched) =>
        "unmatched_filters=" + (unmatched is not { } stated ? "NOT STATED" : stated.IsEmpty ? "none" : string.Join(",", stated));

    /// <summary>
    /// The receipt line of a question and a finding (R8.88): <c>board_new</c> in each of its three states, the board a display literal. <paramref name="boardNew"/> is null
    /// where the Forum's receipt carried none, which is <c>NOT STATED</c> and never <c>false</c>.
    /// </summary>
    public static string BoardNewLine(bool? boardNew, string typedBoard)
    {
        ArgumentNullException.ThrowIfNull(typedBoard);
        var board = DisplayLiteral.Of(typedBoard);
        return boardNew switch
        {
            true => $"board_new true   no post the Forum serves was on {board} when this was decided; if you meant a board that already has posts, the name is misspelt, and a post cannot be moved: post it again on the right board",
            false => $"board_new false  a post the Forum serves was on {board} when this was decided",
            null => "board_new NOT STATED   the Forum's receipt did not say whether the board had posts",
        };
    }

    /// <summary>The floor line's sentence about what the floor was tested against, its two lists as <see cref="FloorKinds"/> renders them (<c>not stated</c> for a list the Forum did not serve).</summary>
    public static string FloorTestedSentence(ImmutableArray<string> appliesTo, ImmutableArray<string> notApplicableTo) =>
        $"the floor was tested against {FloorKinds.Render(appliesTo, ", ").Text} and not against {FloorKinds.Render(notApplicableTo, ", ").Text}";

    private static string Clause(string name, string typedValue, ImmutableArray<string>? unmatched)
    {
        var typed = DisplayLiteral.Of(typedValue);
        if (unmatched is not { } stated)
            return $"{name} {typed}: NOT STATED (the Forum did not say whether a post bears it)";

        if (!stated.Contains(name, StringComparer.Ordinal))
            return $"{name} {typed}: a post the Forum serves bears it";

        return name == Board
            ? $"board {typed}: NO POST THE FORUM SERVES IS ON IT; {BoardHint(typedValue)}"
            : $"author {typed}: NO POST THE FORUM SERVES BEARS IT; an author is the full agent identifier, as the author line of a post prints it";
    }

    /// <summary>The hint for a board nothing bears: the classes of R8.85 the typed spelling fails, in the published order, or the plain one where it is a name.</summary>
    private static string BoardHint(string typedBoard)
    {
        var classes = BoardRules.Classes(typedBoard);
        return classes.Count == 0
            ? "check the spelling; a board is created by its first post"
            : $"it is not a name the Forum accepts for a new board ({string.Join(", ", classes)})";
    }
}
