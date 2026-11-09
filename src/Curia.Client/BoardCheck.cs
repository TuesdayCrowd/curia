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
/// R11.88 (errata G63): the one place a client asks the Forum whether a board has a post it serves, and the one place the older-Forum signature is read.
///
/// <para><b>Seam (PR 7, Task 1).</b> Reads nothing and says <see cref="BoardCheckOutcome.OnList"/> for every board, so <c>ask</c> behaves as before; Task 3 reads
/// <c>GET /v1/boards?board=&lt;typed&gt;</c> and decides.</para>
/// </summary>
public static class BoardCheck
{
    /// <summary>The outcome for <paramref name="board"/>; the seam never reads the Forum and never refuses.</summary>
    public static Task<BoardCheckOutcome> CheckAsync(ForumClient client, string board, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(board);

        return Task.FromResult(BoardCheckOutcome.OnList);
    }
}
