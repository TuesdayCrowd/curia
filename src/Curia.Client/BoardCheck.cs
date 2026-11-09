using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>What the Forum's list of boards said of a typed board (R11.88, errata G63).</summary>
public enum BoardCheckOutcome
{
    /// <summary>The Forum served the board's row.</summary>
    OnList,

    /// <summary>The Forum served the list and the board has no row.</summary>
    NotOnList,

    /// <summary>The Forum has no list (<c>404</c> as <c>curia/request/no-route</c> and no other <c>404</c>).</summary>
    NotStated,

    /// <summary>The list could not be read for any other reason.</summary>
    Unreadable,
}

/// <summary>
/// The answer of <see cref="BoardCheck.CheckAsync"/>: the outcome, the board's row where the Forum served it (<see cref="BoardCheckOutcome.OnList"/>) and the refusal where the list
/// could not be read (<see cref="BoardCheckOutcome.Unreadable"/>). A member that does not belong to the outcome is <see langword="null"/>.
/// </summary>
public sealed record BoardCheckResult
{
    /// <summary>A result is <see cref="BoardCheckOutcome.OnList"/> only with its row (R10.98: silence is never an outcome).</summary>
    public BoardCheckResult(BoardCheckOutcome outcome, BoardListRow? row = null, Refusal? fault = null)
    {
        if (outcome is BoardCheckOutcome.OnList && row is null)
            throw new ArgumentException("a board that is on the list has its row", nameof(row));

        Outcome = outcome;
        Row = row;
        Fault = fault;
    }

    /// <summary>What the Forum's list said.</summary>
    public BoardCheckOutcome Outcome { get; }

    /// <summary>The board's row, where the Forum served it.</summary>
    public BoardListRow? Row { get; }

    /// <summary>The refusal, where the list could not be read.</summary>
    public Refusal? Fault { get; }
}

/// <summary>
/// R11.88 (errata G63): the one place a client asks the Forum whether a board has a post it serves, and the one place the older-Forum signature is read.
///
/// <para><b>A question about one name, never membership of a page</b> (finding 4). The typed board is read by <c>GET /v1/boards?board=&lt;typed&gt;</c>: a row means the Forum serves a post
/// on it. A client that compared the name with the first page of a bounded list would refuse every real board that is not on it.</para>
///
/// <para><b>The older Forum is read by type.</b> <c>404</c> with the problem type <c>curia/request/no-route</c> is a Forum that does not serve the route; any other <c>404</c> (a proxy's, a wrong
/// path's, <c>curia/posts/not-found</c>) is not, and is a list that could not be read. So is a <c>405</c>, a <c>5xx</c>, a <c>403</c>, no answer within <see cref="Bound"/>, and a
/// <c>200</c> that is not the document.</para>
/// </summary>
public static class BoardCheck
{
    /// <summary>The problem type a Forum that does not serve a route answers <c>404</c> with.</summary>
    public const string NoRouteType = "curia/request/no-route";

    /// <summary>The largest page the listing serves (R9.58); the one constant both clients read.</summary>
    public const int MaximumLimit = 100;

    /// <summary>The most boards a refusal names (R11.88).</summary>
    public const int NamedBoards = 20;

    /// <summary>How long the check waits for the Forum before it says the list could not be read: a hung list must not hold the post for the command's whole timeout.</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// <summary>What the Forum's list said of <paramref name="board"/>, by one read of that name.</summary>
    public static async Task<BoardCheckResult> CheckAsync(ForumClient client, string board, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(board);

        var read = await ReadBoundedAsync(client, board, null, ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var page, out var refusal))
            return IsOlderForum(refusal!)
                ? new BoardCheckResult(BoardCheckOutcome.NotStated)
                : new BoardCheckResult(BoardCheckOutcome.Unreadable, fault: refusal);

        if (page!.Boards.IsEmpty) return new BoardCheckResult(BoardCheckOutcome.NotOnList);

        // The filter is exact: a row for another name is not what was asked, and a client that took it for the board's would be wrong in the one direction that costs a post.
        var row = page.Boards.FirstOrDefault(r => string.Equals(r.Board, board, StringComparison.Ordinal));
        return row is not null
            ? new BoardCheckResult(BoardCheckOutcome.OnList, row)
            : new BoardCheckResult(BoardCheckOutcome.Unreadable, fault: new Refusal(
                RefusalKind.Malformed, 0, ClientErrors.ResponseMalformed("the board listing answered a row for another board than the one asked")));
    }

    /// <summary>The first page of the list, <see cref="NamedBoards"/> boards, read only to word a refusal; a fault is the caller's to word without the names.</summary>
    public static Task<ForumResult<BoardPage>> FirstPageAsync(ForumClient client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        return ReadBoundedAsync(client, null, NamedBoards, ct);
    }

    /// <summary>Whether a refusal is a Forum that does not serve the list: <c>404</c> as <see cref="NoRouteType"/> and no other <c>404</c>.</summary>
    public static bool IsOlderForum(Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        return refusal.Kind is RefusalKind.NotFound && refusal.Status == 404 && string.Equals(refusal.Error.Type, NoRouteType, StringComparison.Ordinal);
    }

    private static async Task<ForumResult<BoardPage>> ReadBoundedAsync(ForumClient client, string? board, int? limit, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(Bound);
        try
        {
            return await client.GetBoardsAsync(board, limit, null, bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ForumResult<BoardPage>.Refused(new Refusal(
                RefusalKind.Transport, 0, ClientErrors.Transport($"no answer within {Bound.TotalSeconds.ToString("0", System.Globalization.CultureInfo.InvariantCulture)} seconds")));
        }
    }
}
