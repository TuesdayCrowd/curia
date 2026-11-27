using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words and figures of errata G84 (R6.68 (rev.), R11.111) read at test time from the errata and the white paper, and never from the code a probe is about: the bound (R6.39's number from the
/// white paper plus one, checked against the 1,048,577 R6.68 (rev.) states), the problem type the Forum issues, and the one sentence a client prints for it. Each parse asserts its count before it
/// returns, so an edit that removes or duplicates a span turns a probe red by name and does not shrink what it compares.
/// </summary>
internal static partial class PublishedRequestBound
{
    private static string Bound() => PublishedAcceptBudget.Paragraph("**R6.68 (rev.)**");

    /// <summary>R6.68's figure: R6.39's submission size (white paper) plus one byte, held equal to the number the requirement states.</summary>
    internal static long Cap()
    {
        var derived = PublishedEdge.SubmissionCapBytes() + 1;
        var stated = Stated().Matches(Bound());
        if (stated.Count != 1) throw new InvalidOperationException($"R6.68 (rev.) states its figure {stated.Count} times, expected once");
        var figure = long.Parse(stated[0].Groups[1].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
        return figure == derived ? figure : throw new InvalidOperationException($"R6.68 (rev.) states {figure} and the white paper's R6.39 plus one is {derived}");
    }

    /// <summary>The problem type R6.68 (rev.) says the Forum issues, as its first code span with the <c>curia/request/</c> prefix.</summary>
    internal static string TooLargeType()
    {
        var found = Type().Matches(Bound());
        if (found.Count != 1) throw new InvalidOperationException($"R6.68 (rev.) names its problem type {found.Count} times, expected once");
        return found[0].Groups[1].Value;
    }

    /// <summary>R11.111's sentence: the italic span after "SHALL word it, once and from one definition, as:".</summary>
    internal static string Sentence()
    {
        var found = Italic().Matches(PublishedAcceptBudget.Paragraph("**R11.111**"));
        if (found.Count != 1) throw new InvalidOperationException($"R11.111 holds {found.Count} italic sentences, expected one");
        return found[0].Groups[1].Value;
    }

    [GeneratedRegex(@"\b(1,048,577) bytes", RegexOptions.CultureInvariant)]
    private static partial Regex Stated();

    [GeneratedRegex(@"`(curia/request/too-large)`", RegexOptions.CultureInvariant)]
    private static partial Regex Type();

    [GeneratedRegex(@"\*(The Forum reads no more than[^*]+)\*", RegexOptions.CultureInvariant)]
    private static partial Regex Italic();
}
