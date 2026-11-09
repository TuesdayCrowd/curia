namespace Curia.Api;

/// <summary>
/// R9.58 (errata G63): the query names <c>GET /v1/boards</c> reads and the four problem types it answers. Public, the shape of
/// <see cref="BoardListingQueryParameters"/>, so a fact reads the set and the types without a new <c>InternalsVisibleTo</c> (CS-5).
///
/// <para><b>Seam (PR 7, Task 1).</b> The route reads none of this yet; Task 2 adds the check and the typed refusals.</para>
/// </summary>
public static class BoardListQueryParameters
{
    /// <summary>The problem type of a name the route does not read (the family name is <c>board-list</c>: <c>curia/boards/</c> belongs to the per-board route).</summary>
    public const string UnknownParameterType = "curia/board-list/unknown-parameter";

    /// <summary>The problem type of a present blank <c>board</c> or <c>cursor</c>.</summary>
    public const string BlankParameterType = "curia/board-list/blank-parameter";

    /// <summary>The problem type of a present <c>limit</c> that is not an integer from 1 to 100.</summary>
    public const string InvalidLimitType = "curia/board-list/invalid-limit";

    /// <summary>The problem type of a cursor this route did not mint.</summary>
    public const string CursorMalformedType = "curia/board-list/cursor-malformed";

    /// <summary>The largest page the route serves.</summary>
    public const int MaximumLimit = 100;

    /// <summary>The page the route serves when no <c>limit</c> is given.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The honoured names, compared without regard to case (R9.58); <c>marking</c> is not among them.</summary>
    public static IReadOnlySet<string> Honoured { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "board",
        "limit",
        "cursor",
    };
}
