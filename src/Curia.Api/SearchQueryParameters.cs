using System.Globalization;
using System.Text;
using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.29: the query parameters <c>GET /v1/search</c> reads, and the refusal of any other name. Public, and a
/// type of its own, so that a fact can read the set without a new <c>InternalsVisibleTo</c> (CS-5).
///
/// <para>A name the route does not read used to be dropped, which returned the unfiltered corpus to an agent
/// that believed it had filtered (<c>kinds=question</c>, <c>minVerification=V2</c>). It is refused by name
/// instead. The name is a caller's string, so it is echoed only if it is a plain name (letters, digits,
/// <c>_ . -</c>, 1 to 64 characters, checked by hand: R10.69's fence holds every shipped assembly's IL), and a
/// near-miss is answered with the honoured spelling, which is a constant of this type and never the caller's
/// text.</para>
/// </summary>
public static class SearchQueryParameters
{
    /// <summary>The problem type of an unknown name; R9.26's refusable names keep <c>unsupported-filter</c>.</summary>
    public const string UnknownParameterType = "curia/search/unknown-parameter";

    private const int MaxNamed = 8;
    private const int MaxEchoedLength = 64;
    private const string NotEchoed = "(a name that is not echoed)";

    /// <summary>The honoured names, compared without regard to case (R9.29).</summary>
    public static IReadOnlySet<string> Honoured { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "q", "board", "author", "kind", "tags", "min_verification", "cursor", "limit", "why", "marking",
    };

    private static readonly string HonouredList = string.Join(", ", Honoured.Order(StringComparer.Ordinal));

    /// <summary>Null when every name in the query is honoured; otherwise the refusal naming those that are not.</summary>
    public static Error? Check(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var unknown = names.Where(n => !Honoured.Contains(n)).Distinct(StringComparer.Ordinal)
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
            var hint = NearMiss(name);
            if (hint is not null) detail.Append(CultureInfo.InvariantCulture, $" (did you mean {hint})");
            detail.Append("; ");
        }

        if (unknown.Length > MaxNamed)
            detail.Append(CultureInfo.InvariantCulture, $"and {unknown.Length - MaxNamed} more; ");
        detail.Append("this route reads exactly these: ").Append(HonouredList);

        return new Error(
            UnknownParameterType,
            "That parameter is not read by this route and is refused rather than ignored",
            detail.ToString());
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
    private static string? NearMiss(string name)
    {
        var shape = Shape(name);
        return Honoured.Order(StringComparer.Ordinal).FirstOrDefault(h => Shape(h) == shape);
    }

    private static string Shape(string name)
    {
        var s = name.ToUpperInvariant().Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        return s.EndsWith('S') ? s[..^1] : s;
    }
}
