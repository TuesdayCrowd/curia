using System.Text.RegularExpressions;
using Curia.Tests.Shared;

namespace Curia.Client.Tests;

/// <summary>
/// The words of errata G77 (R11.105 words, <c>R11.99 words (rev.)</c>, <c>R11.101 words (rev.)</c>) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never typed into a
/// probe or taken from the code the probe is about. A span is the backtick text after its italic label; where the errata says "the same with X for Y" the other form is derived by that substitution and nothing else, and the
/// first-failure paragraph is composed from the three things the errata says compose it: the span of R11.99 words, R11.105's addition placed immediately before the last sentence, and the last sentence as the revising
/// paragraph rewrites it.
/// </summary>
internal static class KeyPagesWords
{
    private const string Added = "R11.105 words";
    private const string Revised = "R11.99 words (rev.)";

    /// <summary>The first line of <c>curia keys</c> and <c>curia_keys</c>.</summary>
    internal static string FirstLine() => Spans(Added, "The first line").Single();

    /// <summary>The form the overview prints on the first line of its <c>verify</c> entry: the second span of the label's section, which also names the verb and the help that prints the form.</summary>
    internal static string OverviewEntryForm()
    {
        var spans = Spans(Added, "The overview's entry");
        return spans is ["verify", { } form, "curia verify --help"]
            ? form
            : throw new InvalidOperationException($"the errata's R11.105 words, the overview's entry: {spans.Length} spans, expected `verify`, the form and `curia verify --help`: {string.Join(" | ", spans)}");
    }

    /// <summary>The addition to the first-failure paragraph: the rotation's own span, or the retirement's, which the errata gives as "the same with <c>retirement</c> for <c>rotation</c>".</summary>
    internal static string Addition(bool rotation)
    {
        var rotationText = Spans(Added, "The addition to the first-failure paragraph, a rotation").First();
        return rotation ? rotationText : rotationText.Replace("rotation", "retirement", StringComparison.Ordinal);
    }

    /// <summary>The added sentence after a <c>Help</c> span of R11.99 words, for rotate (true) or retire (false).</summary>
    internal static string HelpAdded(bool rotate) => Spans(Added, rotate ? "Help, rotate, added" : "Help, retire, added").Single();

    /// <summary>R11.99 words' <c>Help</c> span with R11.105's added sentence after it (<c>R11.99 words (rev.)</c>: "the two Help spans are those of R11.99 words with R11.105's added sentence after each").</summary>
    internal static string HelpClause(bool rotate, string label) =>
        UnheardWords.Span(UnheardWords.Settling, label) + " " + HelpAdded(rotate);

    /// <summary>The clause row 8 of the exit table gains.</summary>
    internal static string ExitEightClause() => Spans(Added, "The added clause of exit 8, after \"(R9.34).\"").Single();

    /// <summary>The sentence <c>use-key</c> prints after its result while the key set lists a skipped row.</summary>
    internal static string UseKeySkippedSentence() => Spans(Added, "The result of use-key while the key set lists a skipped row").Single();

    /// <summary>The sentence <c>curia retire --help</c> gains.</summary>
    internal static string RetirePermanence() => Spans(Added, "Help, retire, permanence").Single();

    /// <summary>The first word of a published line (a line begins with the word that names it: <c>refused</c>, <c>dropped</c>).</summary>
    internal static string LineWord(string label)
    {
        var span = UnheardWords.Span(UnheardWords.Settling, label);
        return span[..span.IndexOf(' ', StringComparison.Ordinal)];
    }

    /// <summary>
    /// The first-failure paragraph with its placeholders left in: R11.99 words' span, the last sentence replaced by R11.105's addition followed by the sentence <c>R11.99 words (rev.)</c> says it reads, with
    /// <c>&lt;the keys command&gt;</c> replaced by the command that paragraph names. Each step asserts that the errata holds what it is made from.
    /// </summary>
    internal static string AfterNotHeard(bool rotation)
    {
        var template = UnheardWords.Span(UnheardWords.Settling, rotation ? "After a rotation not heard from" : "After a retirement not heard from");
        var paragraph = PublishedAcceptBudget.Paragraph($"**{Revised}**");

        var quoted = Regex.Matches(paragraph, "\"`([^`]+)`\"", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        if (quoted.Length != 2)
            throw new InvalidOperationException($"the errata's {Revised} quote {quoted.Length} sentences, expected the old one and the one it reads");

        var command = Regex.Matches(paragraph, "being `([^`]+)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        if (command.Length != 1)
            throw new InvalidOperationException($"the errata's {Revised} name {command.Length} keys commands, expected one");

        var (oldSentence, newSentence) = (quoted[0], quoted[1]);
        if (template.IndexOf(oldSentence, StringComparison.Ordinal) < 0 || template.IndexOf(oldSentence, template.IndexOf(oldSentence, StringComparison.Ordinal) + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"the paragraph does not hold \"{oldSentence}\" exactly once: {template}");

        return template.Replace(oldSentence, Addition(rotation) + " " + newSentence.Replace("<the keys command>", command[0], StringComparison.Ordinal), StringComparison.Ordinal);
    }

    /// <summary>The backtick spans of the section after <paramref name="label"/> in the paragraph <paramref name="paragraphName"/>, up to the next italic label that begins with a capital.</summary>
    private static string[] Spans(string paragraphName, string label)
    {
        var paragraph = PublishedClientWords.Paragraph(paragraphName);
        var marker = $"*{label}:*";
        var at = paragraph.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || paragraph.IndexOf(marker, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"the errata's {paragraphName} do not hold {marker} exactly once");

        var start = at + marker.Length;
        var next = Regex.Match(paragraph[start..], @"\*[A-Z][^*`]*:\*", RegexOptions.CultureInvariant);
        var section = next.Success ? paragraph[start..(start + next.Index)] : paragraph[start..];
        var spans = Regex.Matches(section, "`([^`]+)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        return spans.Length > 0 ? spans : throw new InvalidOperationException($"the errata's {paragraphName}, {marker}: no span");
    }
}
