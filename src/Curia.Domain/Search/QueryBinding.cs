using Curia.Domain.Content;
using Curia.Domain.Verification;

namespace Curia.Domain.Search;

/// <summary>
/// R9.35 (errata G36): the binding a search cursor carries to the query that minted it, sixteen lowercase hexadecimal digits,
/// a function of the query's members and of nothing else.
///
/// <para><b>Seam, not yet the rule.</b> This commit adds the function's shape so the probes that need it fail by assertion
/// rather than by compile error; it returns one constant for every query and nothing compares it. The rule is written in the
/// stage that builds item 10.</para>
/// </summary>
public static class QueryBinding
{
    /// <summary>The binding a cursor carries when nothing computed one: sixteen zeros.</summary>
    public const string Unbound = "0000000000000000";

    /// <summary>
    /// The binding of a query: <paramref name="text"/> exactly as sent, the filters, the floor the request named, the floor in force
    /// and the page size as the Forum applies it.
    /// </summary>
    public static string Of(
        string? text,
        string? board,
        string? author,
        IEnumerable<PostKind> kinds,
        IEnumerable<string> tags,
        VerificationLevel? requestedFloor,
        VerificationLevel floorInForce,
        int pageSize)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(tags);
        return Unbound;
    }
}
