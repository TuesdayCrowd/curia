using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Curia.Domain.Content;
using Curia.Domain.Verification;

namespace Curia.Domain.Search;

/// <summary>
/// R9.35 (errata G36): the binding a search cursor carries to the query that minted it, sixteen lowercase hexadecimal digits,
/// a function of the query's members and of nothing else.
///
/// <para>The members are <c>q</c> exactly as sent, <c>board</c>, <c>author</c>, <c>kind</c> and <c>tags</c> (each a set, <c>tags</c> without
/// regard to case as the filter compares them), the floor the request named, the floor in force and the page size as the Forum applies
/// it. <c>why_ranked</c> and <c>marking</c> change neither the ordering nor the set and are not parameters. The digest is the first
/// eight bytes of SHA-256 over a canonical string in which every field is length-prefixed and an absent field differs from an empty
/// one, so no two queries share a string. It is not a secret and not an authenticator.</para>
/// </summary>
public static class QueryBinding
{
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

        var canonical = new StringBuilder();
        Field(canonical, text);
        Field(canonical, board);
        Field(canonical, author);
        Set(canonical, kinds.Select(PostKinds.Wire));
        Set(canonical, tags.Select(t => t.ToUpperInvariant()));
        Field(canonical, requestedFloor is { } asked ? ((int)asked).ToString(CultureInfo.InvariantCulture) : null);
        Field(canonical, ((int)floorInForce).ToString(CultureInfo.InvariantCulture));
        Field(canonical, pageSize.ToString(CultureInfo.InvariantCulture));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(digest.AsSpan(0, 8));
    }

    /// <summary>An absent value is <c>-</c>; a present one is <c>+</c>, its UTF-8 length, <c>:</c> and the value.</summary>
    private static void Field(StringBuilder into, string? value)
    {
        if (value is null)
        {
            into.Append('-');
            return;
        }

        into.Append('+').Append(Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);
    }

    /// <summary>A set: its distinct members in ordinal order, behind their count.</summary>
    private static void Set(StringBuilder into, IEnumerable<string> members)
    {
        var distinct = members.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        into.Append('[').Append(distinct.Length.ToString(CultureInfo.InvariantCulture)).Append(']');
        foreach (var member in distinct) Field(into, member);
    }
}
