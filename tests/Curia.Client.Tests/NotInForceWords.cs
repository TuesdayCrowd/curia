using System.Text.RegularExpressions;
using Curia.Tests.Shared;

namespace Curia.Client.Tests;

/// <summary>
/// The words of errata G73 (R11.98 words and R11.96 (add.) words) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never typed into a probe or taken from the
/// code the probe is about. Each form is the backtick spans after its italic label, in the order the paragraph gives them; the two "no index" forms are the index-named form with the replacement the paragraph
/// states ("the first line with <c>at log index &lt;i&gt;</c> replaced by ..."), applied to the span the paragraph names, and nothing else is derived. Each parse asserts its counts, so an edit to the paragraph that
/// removes or adds a span turns a probe red by name and does not shrink what it compares.
/// </summary>
internal static class NotInForceWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>A rotation the Forum recorded and does not act on: the line that says so, the line that leads into the retire command, and the closing line. The command is printed between the second and the third.</summary>
    internal sealed record RotationWords(string Says, string Keeps, string Report);

    /// <summary>A retirement the Forum recorded and does not act on: the line that says so and the line that follows it.</summary>
    internal sealed record RetirementWords(string Says, string Follows);

    internal static RotationWords RotationNamed()
    {
        var spans = SpansOf("R11.98 words", "A rotation, the index named", expected: 3);
        return new(spans[0], spans[1], spans[2]);
    }

    internal static RotationWords RotationNoIndex()
    {
        var named = RotationNamed();
        var (from, to) = Replacement("A rotation, no index");
        return named with { Says = Replace(named.Says, from, to, "the rotation's first line") };
    }

    internal static RetirementWords RetirementNamed()
    {
        var spans = SpansOf("R11.98 words", "A retirement, the index named", expected: 2);
        return new(spans[0], spans[1]);
    }

    internal static RetirementWords RetirementNoIndex()
    {
        var named = RetirementNamed();
        var (from, to) = Replacement("A retirement, no index");
        return named with { Says = Replace(named.Says, from, to, "the retirement's first line") };
    }

    /// <summary>R11.96 (add.) words, a rotation, with <c>&lt;the retire command for the new kid&gt;</c> still in it.</summary>
    internal static string NowInForceRotation() => SpansOf("R11.96 (add.) words", "A rotation", expected: 1)[0];

    /// <summary>R11.96 (add.) words, a retirement.</summary>
    internal static string NowInForceRetirement() => SpansOf("R11.96 (add.) words", "A retirement", expected: 1)[0];

    /// <summary>Replaces each placeholder the words use with its value; a placeholder the template does not hold is an error, so a probe cannot fill a word that is not there.</summary>
    internal static string Fill(string template, params (string Placeholder, string Value)[] values)
    {
        var filled = template;
        foreach (var (placeholder, value) in values)
        {
            if (!filled.Contains(placeholder, StringComparison.Ordinal))
                throw new InvalidOperationException($"the published words do not hold {placeholder}: {template}");

            filled = filled.Replace(placeholder, value, StringComparison.Ordinal);
        }

        return filled;
    }

    private static string[] SpansOf(string paragraphName, string label, int expected)
    {
        var paragraph = PublishedAcceptBudget.Paragraph($"**{paragraphName}.**");
        var marker = $"*{label}:*";
        var at = paragraph.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || paragraph.IndexOf(marker, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"{Errata}'s {paragraphName} do not hold {marker} exactly once");

        var start = at + marker.Length;
        var next = Regex.Match(paragraph[start..], @"\*[A-Z][^*`]*:\*", RegexOptions.CultureInvariant);
        var section = next.Success ? paragraph[start..(start + next.Index)] : paragraph[start..];
        var spans = Regex.Matches(section, "`([^`]+)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        return spans.Length == expected
            ? spans
            : throw new InvalidOperationException($"{Errata}'s {paragraphName}, {marker}: {spans.Length} spans, expected {expected}");
    }

    private static (string From, string To) Replacement(string label)
    {
        var spans = SpansOf("R11.98 words", label, expected: 2);
        return (spans[0], spans[1]);
    }

    private static string Replace(string line, string from, string to, string what)
    {
        var at = line.IndexOf(from, StringComparison.Ordinal);
        if (at < 0 || line.IndexOf(from, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"{what} does not hold \"{from}\" exactly once: {line}");

        return line.Replace(from, to, StringComparison.Ordinal);
    }
}
