using System.Globalization;
using System.Text;
using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.29's rules for a query name a route does not read, held in one place so the routes that apply them cannot
/// disagree: the unknown names, sorted ordinally, at most eight named then "and N more", a name echoed only if it is
/// plain, a hint for a near miss, and the list of the names the route reads. Public, as <see cref="SearchQueryParameters"/>
/// is, so a fact reads it without a new <c>InternalsVisibleTo</c> (CS-5).
///
/// <para>The name is a caller's string, so it is echoed only if it is a plain name (letters, digits, <c>_ . -</c>, 1 to
/// 64 characters, checked by hand: R10.69's fence holds every shipped assembly's IL), and a near-miss is answered with
/// the honoured spelling, which is a constant of the route's type and never the caller's text.</para>
/// </summary>
public static class QueryParameterRefusal
{
    private const int MaxNamed = 8;
    private const int MaxEchoedLength = 64;
    private const string NotEchoed = "(a name that is not echoed)";

    /// <summary>
    /// Null when every name is in <paramref name="honoured"/>; otherwise the refusal naming those that are not.
    /// <paramref name="routeSentence"/>, when not empty, is put before the list of honoured names and says what the route is.
    /// </summary>
    public static Error? Check(
        IEnumerable<string> names, IReadOnlySet<string> honoured, string type, string title, string routeSentence)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(honoured);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(routeSentence);

        var unknown = names.Where(n => !honoured.Contains(n)).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length == 0) return null;

        var detail = new StringBuilder();
        foreach (var name in unknown.Take(MaxNamed))
        {
            if (!IsPlainName(name))
            {
                detail.Append(NotEchoed).Append("; ");
                continue;
            }

            detail.Append(CultureInfo.InvariantCulture, $"parameter={name}");
            var hint = NearMiss(name, honoured);
            if (hint is not null) detail.Append(CultureInfo.InvariantCulture, $" (did you mean {hint})");
            detail.Append("; ");
        }

        if (unknown.Length > MaxNamed)
            detail.Append(CultureInfo.InvariantCulture, $"and {unknown.Length - MaxNamed} more; ");
        if (routeSentence.Length > 0) detail.Append(routeSentence).Append("; ");
        detail.Append("this route reads exactly these: ").Append(string.Join(", ", honoured.Order(StringComparer.Ordinal)));

        return new Error(type, title, detail.ToString());
    }

    private static bool IsPlainName(string name)
    {
        if (name.Length is 0 or > MaxEchoedLength) return false;
        foreach (var c in name)
        {
            var plain = c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '.' or '-';
            if (!plain) return false;
        }

        return true;
    }

    /// <summary>The honoured spelling a name is a near-miss of, compared as one case without <c>_</c>, <c>-</c> or one trailing <c>s</c>.</summary>
    private static string? NearMiss(string name, IReadOnlySet<string> honoured)
    {
        var shape = Shape(name);
        return honoured.Order(StringComparer.Ordinal).FirstOrDefault(h => Shape(h) == shape);
    }

    private static string Shape(string name)
    {
        var s = name.ToUpperInvariant().Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return s.EndsWith('S') ? s[..^1] : s;
    }
}
