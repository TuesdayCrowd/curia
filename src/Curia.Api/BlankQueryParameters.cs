using Curia.Domain.Primitives;
using Microsoft.AspNetCore.Http;

namespace Curia.Api;

/// <summary>The route a blank-parameter check is asked of.</summary>
public enum BlankQueryRoute
{
    /// <summary><c>GET /v1/search</c>.</summary>
    Search,

    /// <summary><c>GET /v1/inbox</c>.</summary>
    Inbox,
}

/// <summary>
/// R9.54 (errata G56): a parameter that is present and blank is refused rather than dropped. Public, the shape of
/// <see cref="SearchQueryParameters"/>, so a fact can read the types and the closed lists without a new <c>InternalsVisibleTo</c> (CS-5).
///
/// <para><b>Seam.</b> <see cref="Check"/> refuses nothing at this stage; <see cref="IsBlank"/> is the one definition the route's
/// <c>Nullable</c> already used, named so the refusal and the drop cannot read it two ways.</para>
/// </summary>
public static class BlankQueryParameters
{
    /// <summary>The problem type of a blank search parameter.</summary>
    public const string SearchType = "curia/search/blank-parameter";

    /// <summary>The problem type of a blank inbox parameter.</summary>
    public const string InboxType = "curia/inbox/blank-parameter";

    /// <summary>The title of both types.</summary>
    public const string Title =
        "A parameter is present and blank; it is refused rather than dropped, because a filter dropped returns the unfiltered corpus to a caller that believes it filtered";

    /// <summary>The search route's closed list, in the order a refusal names them.</summary>
    public static IReadOnlyList<string> SearchParameters { get; } = ["q", "board", "author", "min_verification"];

    /// <summary>The inbox's closed list.</summary>
    public static IReadOnlyList<string> InboxParameters { get; } = ["board"];

    /// <summary>White space is what <see cref="string.IsNullOrWhiteSpace(string?)"/> decides: the test the route drops a blank by.</summary>
    public static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    /// <summary>Null at this stage; otherwise the refusal naming each present blank parameter of <paramref name="route"/>.</summary>
    public static Error? Check(IQueryCollection query, BlankQueryRoute route)
    {
        ArgumentNullException.ThrowIfNull(query);
        _ = route;
        return null;
    }
}
