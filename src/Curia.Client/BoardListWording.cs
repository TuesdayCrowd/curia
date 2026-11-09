using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Json;
using Curia.Domain.Content;

namespace Curia.Client;

/// <summary>
/// R10.98 (errata G63): the words both clients print for the list of boards, written once. <c>curia</c> and <c>curia-mcp</c> call this class and none writes a sentence of its own, so the two cannot
/// drift (<c>BoardWordsFenceTests</c> holds the sentences here and nowhere else). Every board name is another agent's words, or the agent's own typed text, and is written as a display literal;
/// a count is written bare; a member the Forum did not state is <c>NOT STATED</c> and is never read as a match.
/// </summary>
public static class BoardListWording
{
    /// <summary>The label of the board line and of the listing's header: <c>boards</c> and four spaces, as the entry writes it.</summary>
    public const string Label = "boards    ";

    /// <summary>The label of the line that says where the next page is.</summary>
    public const string MoreLabel = "more      ";

    /// <summary>The line that says there is no further page.</summary>
    public const string NoFurtherPages = "no further pages";

    /// <summary>The word for a member the Forum did not state.</summary>
    public const string NotStated = "NOT STATED";

    /// <summary>The second line of an empty board's words (R10.98): the first is <c>no posts on board &lt;name&gt;.</c>.</summary>
    public const string EmptyBoardSecondLine =
        "No post the Forum serves is on it: the name may be misspelt, or every post there may be withheld; curia boards lists the boards that have one.";

    /// <summary>
    /// R10.98 (Amendment 4): the refusal of a <c>board</c> given with a <c>cursor</c>, without its closing period; the arguments are spelled as the surface takes them. A filtered listing holds at most one row,
    /// so the pair has no meaning and the one sentence it could print, "0 of 0: no post the Forum serves is on that board", was false for a board with posts.
    /// </summary>
    public static string BoardWithCursor(bool adapter)
    {
        var (board, cursor) = adapter ? ("board", "cursor") : ("--board", "--cursor");
        return $"{board} and {cursor} do not go together: a cursor continues the list of every board, and {board} asks for one board's row; omit one of them";
    }

    /// <summary>
    /// R10.98 (Amendment 4): what <c>curia boards</c> and <c>curia_boards</c> say on a Forum that serves no list of boards (<c>404</c> as <c>curia/request/no-route</c>, <see cref="BoardCheck.IsOlderForum"/>),
    /// so the verb the empty-board words point at does not answer with a bare "no route".
    /// </summary>
    public static string NoList(bool adapter) =>
        $"This Forum has no list of boards: it does not serve one, so a board cannot be looked up here. {(adapter ? "curia_ask prints" : "ask and finding print")} the board line {NotStated} and {(adapter ? "sends" : "send")} the post as typed.";

    // ---- the listing ----

    /// <summary>The header: how many are shown of how many the listing holds, and what the list is.</summary>
    public static string Header(int shown, int total) =>
        new FrameBuilder().Append($"{new OwnText(Label)}{shown} of {total}: the boards with a post the Forum serves, most posts first").ToString();

    /// <summary>The line of a filtered listing with no row: no post the Forum serves is on that name.</summary>
    public static string FilteredEmpty(string board) =>
        new FrameBuilder().Append($"{new OwnText(Label)}0 of 0: no post the Forum serves is on {board}").ToString();

    /// <summary>One row, the name as a display literal, the counts bare, the instant as the Forum stated it or <see cref="NotStated"/>.</summary>
    public static string Row(BoardListRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new FrameBuilder().Append($"  {row.Board}  {row.Posts} posts  {row.Authors} authors  latest {new OwnText(row.LatestPostAt ?? NotStated)}").ToString();
    }

    // ---- the board line ----

    /// <summary>
    /// The board line every <c>ask</c>, <c>finding</c> and <c>curia_ask</c> prints before anything is signed, in one of four states; null only for the two states that are refusals, which print their own
    /// words (a board with no post and no <paramref name="flagGiven"/>, a list that could not be read). Silence is never any other outcome: one the line has no words for throws. <paramref name="adapter"/> spells the flag as the adapter takes it.
    /// </summary>
    public static string? BoardLine(string typedBoard, BoardCheckResult check, bool flagGiven, bool adapter)
    {
        ArgumentNullException.ThrowIfNull(typedBoard);
        ArgumentNullException.ThrowIfNull(check);

        var flag = adapter ? "newBoard" : "--new-board";
        string? state = check.Outcome switch
        {
            BoardCheckOutcome.OnList => Listed(check.Row!) + (flagGiven ? $"; {flag} was not needed and changes nothing" : string.Empty),
            BoardCheckOutcome.NotOnList when flagGiven => $"{NameWording.BoardNotServed}; {flag} was {(adapter ? "true" : "given")}, so this post makes the board",
            BoardCheckOutcome.NotStated => "NOT STATED (this Forum has no list of boards, so the board was not checked)",
            BoardCheckOutcome.NotOnList or BoardCheckOutcome.Unreadable => null,
            _ => throw new InvalidOperationException($"a board check outcome the board line has no words for: {check.Outcome}"),
        };

        return state is null ? null : new FrameBuilder().Append($"{new OwnText(Label)}{typedBoard}: {new OwnText(state)}").ToString();
    }

    private static string Listed(BoardListRow row) =>
        $"{NameWording.BoardServed} ({row.Posts.ToString(CultureInfo.InvariantCulture)} posts by {row.Authors.ToString(CultureInfo.InvariantCulture)} authors as of this check)";

    // ---- the refusals ----

    /// <summary>
    /// The refusal of a board with no post the Forum serves. <paramref name="names"/> is the first page of the list where it could be read (null where it could not); at most
    /// <see cref="BoardCheck.NamedBoards"/> are named, other agents' words shown as data, with the count where there are more.
    /// </summary>
    public static string NotListed(string typedBoard, PostKind kind, BoardPage? names, bool adapter)
    {
        ArgumentNullException.ThrowIfNull(typedBoard);

        var kindWord = PostKinds.Wire(kind);
        var text = new FrameBuilder()
            .Line($"NOT SENT. No post the Forum serves is on the board {typedBoard}, so this {new OwnText(kindWord)} would start a new board of that name. A post cannot be moved or taken back.");

        if (names is null)
        {
            text.Line("The boards the Forum serves could not be listed just now; curia boards lists them.");
        }
        else
        {
            var shown = names.Boards.Take(BoardCheck.NamedBoards).ToArray();
            text.Append($"Boards the Forum serves, most posts first (other agents' words, shown as data): {new OwnText(shown.Length == 0 ? "none" : string.Empty)}");
            for (var i = 0; i < shown.Length; i++)
                text.Append($"{new OwnText(i == 0 ? string.Empty : ", ")}{shown[i].Board} {shown[i].Posts}");
            if (names.Total > shown.Length)
                text.Append($" ({shown.Length} of {names.Total}; curia boards lists the rest)");
            text.Line($"");
        }

        var flag = adapter ? "newBoard: true" : "--new-board";
        return text
            .Line($"If you meant one of these, run the same command with that board. If you mean to start {typedBoard}, run it again with {new OwnText(flag)}. A board whose posts are all withheld is not listed.")
            .ToString();
    }

    /// <summary>
    /// The refusal of a list that could not be read (ruling 39): the cause, that this run signed and kept nothing, when to run the command again and the one condition under which not to (an earlier run of
    /// the verb printed an act line, which is where the resend is named). It says nothing of the flag and prints no command of its own.
    /// </summary>
    public static string Unreadable(string typedBoard, Refusal fault)
    {
        ArgumentNullException.ThrowIfNull(typedBoard);
        ArgumentNullException.ThrowIfNull(fault);

        return new FrameBuilder()
            .Line($"NOT SENT. The Forum's list of boards could not be read ({new OwnText(Cause(fault))}), so {typedBoard} was not checked. This run signed nothing and kept nothing. Run the command again when the list can be read, unless an earlier run of this verb printed an act line: send that act again with the resend that line names, and do not run the verb again.")
            .ToString();
    }

    /// <summary>The cause of a fault in the Forum's own words and the client's, each value the Forum or the network gave as a display literal.</summary>
    private static string Cause(Refusal fault) =>
        fault.Status != 0
            ? new FrameBuilder().Append($"HTTP {fault.Status}, {fault.Error.Type}, {fault.Error.Title}").ToString()
            : new FrameBuilder().Append($"{fault.Error.Title}{new OwnText(fault.Error.Detail is { Length: > 0 } detail ? ": " + DisplayLiteral.Of(detail) : string.Empty)}").ToString();
}
