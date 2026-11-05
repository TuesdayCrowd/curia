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
/// <para><see cref="IsBlank"/> is the one definition the route's <c>Nullable</c> uses, so the refusal and the drop cannot read it two ways.</para>
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
    public static IReadOnlyList<string> SearchParameters { get; } = ["q", "board", "author", "min_verification", "kind", "tags", "cursor"];

    /// <summary>The inbox's closed list.</summary>
    public static IReadOnlyList<string> InboxParameters { get; } = ["board", "tags", "cursor"];

    /// <summary>The parameters that are comma-separated lists (R9.56): a value of nothing but commas and white space names nothing, as an empty one does.</summary>
    private static readonly string[] ListParameters = ["kind", "tags"];

    /// <summary>White space is what <see cref="string.IsNullOrWhiteSpace(string?)"/> decides: the test the route drops a blank by.</summary>
    public static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value);

    /// <summary>
    /// R9.56: whether a present value names nothing. A scalar names nothing when it is blank; a list when, with its commas removed, it is
    /// (<c>kind=,</c> and <c>tags=,%20,</c> name nothing, <c>tags=a,,b</c> and <c>kind=question,</c> name something). One definition for the refusal and the drop.
    /// </summary>
    public static bool NamesNothing(string? value, bool isList) =>
        isList ? IsBlank(value?.Replace(",", string.Empty, StringComparison.Ordinal)) : IsBlank(value);

    /// <summary>
    /// Null when no parameter of <paramref name="route"/>'s closed list is present and blank; otherwise the refusal naming each one, in the
    /// published order and spelled as published whatever the case of the request's spelling (the query reads names without regard to case).
    /// A parameter given twice is read as the route reads it, joined with a comma. The value is never echoed.
    /// </summary>
    public static Error? Check(IQueryCollection query, BlankQueryRoute route)
    {
        ArgumentNullException.ThrowIfNull(query);

        var names = route is BlankQueryRoute.Inbox ? InboxParameters : SearchParameters;
        var blank = names.Where(n => query.ContainsKey(n) && NamesNothing(query[n].ToString(), ListParameters.Contains(n))).ToArray();
        if (blank.Length == 0) return null;

        var type = route is BlankQueryRoute.Inbox ? InboxType : SearchType;
        return new Error(type, Title, "parameter=" + string.Join(", ", blank));
    }
}
