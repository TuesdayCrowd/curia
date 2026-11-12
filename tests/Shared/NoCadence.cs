using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R11.94's negative, written out beside its sentence "The words SHALL NOT state a cadence" (a restatement is the best a negative can be): the Forum does not say how often the
/// operator signs a head, because the interval is the operator's schedule and not a property of the log, and a client that printed one would be asserting what it cannot know.
/// <see cref="Says"/> matches a phrase that states how often something happens; <see cref="DigitsAfterTheClause"/> leaves what a clause may carry (the instant, the age, the tree
/// size) out and reports any digit that remains. The controls are the sentences a matcher must catch, so that one that matches nothing is red.
/// </summary>
internal static class NoCadence
{
    /// <summary>A sentence the matcher must catch, each in a different shape.</summary>
    internal static readonly string[] Controls =
    [
        "The operator signs a head every 5 minutes.",
        "Heads are signed about every five minutes.",
        "a head roughly each 300 seconds",
    ];

    private static readonly string[] Patterns =
    [
        @"\bevery\s+(\d+|[a-z]+)\s+(second|minute|hour|day|week)s?\b",
        @"\b(each|per)\s+(\d+\s+|[a-z]+\s+)?(second|minute|hour|day|week)s?\b",
        @"\b(hourly|daily|minutely|weekly|periodically|on a schedule|at regular intervals)\b",
    ];

    /// <summary>Whether <paramref name="text"/> states how often something happens.</summary>
    internal static bool Says(string text) =>
        Patterns.Any(p => Regex.IsMatch(text, p, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)));

    /// <summary>
    /// What is left of a head-signed clause's digits once the three things it may state are removed: the instant (<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>), the age (<c>&lt;k&gt; s before</c>) and the
    /// tree size (<c>tree size &lt;n&gt;</c>). Empty when the clause holds no other number.
    /// </summary>
    internal static string DigitsAfterTheClause(string clause)
    {
        var rest = Regex.Replace(clause, @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z", string.Empty, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        rest = Regex.Replace(rest, @"\d+ s before", " s before", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        rest = Regex.Replace(rest, @"tree size \d+", "tree size", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        return new string([.. rest.Where(char.IsAsciiDigit)]);
    }
}
