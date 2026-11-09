using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.58 (errata G63): the query names <c>GET /v1/boards</c> reads and the four problem types it answers. Public, the shape of
/// <see cref="BoardListingQueryParameters"/>, so a fact reads the set and the types without a new <c>InternalsVisibleTo</c> (CS-5).
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

    /// <summary>The names in the order R9.58 publishes them, which is the order a refusal lists them in.</summary>
    private static readonly string[] PublishedOrder = ["board", "limit", "cursor"];

    /// <summary>Null when every name in the query is honoured; otherwise the refusal naming those that are not (R9.29's rules; <c>marking</c> is refused).</summary>
    public static Error? Check(IEnumerable<string> names) =>
        QueryParameterRefusal.Check(names, Honoured, UnknownParameterType, SearchQueryParameters.RefusalTitle, string.Empty, PublishedOrder);

    /// <summary>
    /// R9.58, R9.44: the page size, validated and never clamped, and stricter than the search route's: a present <c>limit</c> that is not an integer from 1 to
    /// <see cref="MaximumLimit"/> is refused, an empty one included (D61 item 75's leniency is not repeated on a new route).
    /// </summary>
    public static Result<int> ReadLimit(Microsoft.AspNetCore.Http.IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (!query.TryGetValue("limit", out var values)) return Result<int>.Ok(DefaultLimit);

        var raw = values.ToString();
        if (int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var limit) && limit is >= 1 and <= MaximumLimit)
            return Result<int>.Ok(limit);

        return Result<int>.Fail(new Error(
            InvalidLimitType,
            "limit must be an integer between 1 and the published maximum",
            $"received={raw} maximum={MaximumLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
    }
}
