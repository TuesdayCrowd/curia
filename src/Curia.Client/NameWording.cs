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

    private const string NoOpenQuestions = "no open questions match these filters.";

    private const string EmptyPageTail = "; every name here has posts, so this empty page comes from the rest of the request, from posts the floor line counts as removed, or from the names taken together";

    /// <summary>
    /// The state of a board the Forum serves a post on, in capitals, and its negation: one definition for the filters line (R10.97) and the board line (R10.98, errata G63), so a reader who has
    /// read one has read the other and a content fence holds the words here and nowhere else.
    /// </summary>
    public const string BoardServed = "THE FORUM SERVES POSTS ON IT";

    /// <summary>The negation of <see cref="BoardServed"/>: no post the Forum serves is on the board.</summary>
    public const string BoardNotServed = "NO POST THE FORUM SERVES IS ON IT";

    private const string Board = "board";
    private const string Author = "author";

    /// <summary>
    /// The clauses of the filters line, in the published order (<c>board</c>, then <c>author</c>), one for each filter the request named; null where it named neither.
    /// <paramref name="unmatched"/> is the Forum's <c>unmatched_filters</c>, null where the Forum stated none. A filter that is null or blank was not named.
    /// <paramref name="emptyFirstPage"/> is true where the search carried no cursor and its page holds no result: then, if every filter named is stated and matched, the clauses end with
    /// the tail that says where the emptiness is (Amendment 4, ruling 38); for no other state is it printed.
    /// </summary>
    public static string? FiltersClauses(string? board, string? author, ImmutableArray<string>? unmatched, bool emptyFirstPage = false)
    {
        var clauses = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(board)) clauses.Add(Clause(Board, board, unmatched));
        if (!string.IsNullOrWhiteSpace(author)) clauses.Add(Clause(Author, author, unmatched));
        if (clauses.Count == 0) return null;

        var line = string.Join("; ", clauses);
        return emptyFirstPage && unmatched is not null && AllMatched(board, author, unmatched) ? line + EmptyPageTail : line;
    }

    /// <summary>The whole filters line (<see cref="FiltersLabel"/> and the clauses), or null where the request named neither filter. See <see cref="FiltersClauses"/> for <paramref name="emptyFirstPage"/>.</summary>
    public static string? FiltersLine(string? board, string? author, ImmutableArray<string>? unmatched, bool emptyFirstPage = false) =>
        FiltersClauses(board, author, unmatched, emptyFirstPage) is { } clauses ? FiltersLabel + clauses : null;

    /// <summary>True where the Forum stated that a filter the request named bears no served post; the inbox prints nothing after its filters line then.</summary>
    public static bool AnyUnmatched(string? board, string? author, ImmutableArray<string>? unmatched) =>
        unmatched is { } stated
        && ((!string.IsNullOrWhiteSpace(board) && stated.Contains(Board, StringComparer.Ordinal))
            || (!string.IsNullOrWhiteSpace(author) && stated.Contains(Author, StringComparer.Ordinal)));

    /// <summary>
    /// True where every filter the request named is stated and matched, or none was named: the Forum stated the member and none of the names the request used is in it. A member the Forum
    /// did not state is never read as matched where a filter was named.
    /// </summary>
    public static bool AllMatched(string? board, string? author, ImmutableArray<string>? unmatched)
    {
        var named = !string.IsNullOrWhiteSpace(board) || !string.IsNullOrWhiteSpace(author);
        return !named || (unmatched is not null && !AnyUnmatched(board, author, unmatched));
    }

    /// <summary>
    /// The sentence an inbox with no open question prints after its filters line, or null where it prints none (Amendment 4, ruling 41): the whole sentence where every filter named is stated
    /// and matched (or none was named); the first half alone where one is <c>NOT STATED</c>, because "nothing here needs an answer" is a claim a client that was not told whether the board has
    /// posts cannot make; nothing where one is unmatched, which the filters line already says.
    /// </summary>
    public static string? InboxEmptySentence(string? board, ImmutableArray<string>? unmatched)
    {
        if (AnyUnmatched(board, null, unmatched)) return null;
        return AllMatched(board, null, unmatched) ? NoOpenQuestions + " Nothing here needs an answer -- look elsewhere." : NoOpenQuestions;
    }

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
            true => $"board_new true   no post the Forum serves was on {board} when this was decided; if you meant a board that already has posts, either the name is misspelt (a post cannot be moved: post it again on the right board) or every post there is withheld (this post is on that board)",
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
            return name == Board ? $"board {typed}: {BoardServed}" : $"author {typed}: THE FORUM SERVES POSTS BY IT";

        return name == Board
            ? $"board {typed}: {BoardNotServed}; {BoardHint(typedValue)}"
            : $"author {typed}: NO POST THE FORUM SERVES BEARS IT; an author is the full agent identifier, as the author line of a post prints it";
    }

    /// <summary>The hint for a board nothing bears: the classes of R8.85 the typed spelling fails, in the published order, or the plain one where it is a name.</summary>
    private static string BoardHint(string typedBoard)
    {
        var classes = BoardRules.Classes(typedBoard);
        return classes.Count == 0
            ? "check the spelling; a board whose posts are all withheld reads the same way; a board is created by its first post"
            : $"it is not a name the Forum accepts for a new board ({string.Join(", ", classes)})";
    }
}
