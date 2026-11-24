using System.Text.RegularExpressions;
using Curia.Tests.Shared;

namespace Curia.Client.Tests;

/// <summary>
/// The words of errata G75 (R11.101 words and R11.96 (add.) (rev.)) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never typed into a probe or taken from the
/// code the probe is about. Each form is the one backtick span after its italic label in the paragraph; each parse asserts its count, so an edit to the paragraph that removes or adds a span turns a probe red by name.
/// </summary>
internal static class SigningKeyNotLiveWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>The line for a profile that holds a key the Forum serves, with its placeholders left in.</summary>
    internal static string HeldForm() => Span("R11.101 words", "Held keys the Forum binds");

    /// <summary>The line when the Forum serves no key the profile holds, with its placeholders left in.</summary>
    internal static string NoHeldForm() => Span("R11.101 words", "No held key served");

    /// <summary>The rotation <c>NOW IN FORCE</c> line while the signing key is not live, with <c>&lt;i&gt;</c> and <c>&lt;new kid&gt;</c> left in.</summary>
    internal static string RotationLineWhileNotLive() => Span("R11.101 words", "The rotation NOW IN FORCE line while the signing key is not live");

    /// <summary>The clause <c>curia keys --help</c> gains, as <c>R11.101 words (rev.)</c> (errata G77) revises it: the first span of the revising paragraph (it then names <c>curia use-key</c> in a span of its own), which supersedes the one in R11.101 words.</summary>
    internal static string KeysHelpClause() => Span("R11.101 words (rev.)", "The added clause of keys help", firstOfMany: true, anchor: "**R11.101 words (rev.)**");

    /// <summary>The clause row 6 of the exit table gains.</summary>
    internal static string ExitSixClause() => Span("R11.101 words", "The added clause of exit 6");

    /// <summary>The usage line of <c>curia use-key --help</c>.</summary>
    internal static string UseKeyUsage() => Span("R11.101 words", "The usage line of use-key help");

    /// <summary>The paragraph of <c>curia use-key --help</c>, as <c>R11.101 words (rev.)</c> (errata G77) revises it.</summary>
    internal static string UseKeyParagraph() => Span("R11.101 words (rev.)", "The paragraph of use-key help", firstOfMany: true, anchor: "**R11.101 words (rev.)**");

    /// <summary>What <c>curia use-key</c> prints when it has made the profile sign with the key, with <c>&lt;kid&gt;</c> left in.</summary>
    internal static string UseKeyResult() => Span("R11.101 words", "The result of use-key");

    /// <summary>The second sentence <c>use-key</c> (the first span after its label; the parenthetical after it holds two more, which name its placeholders) prints when it holds other keys the Forum serves live, with <c>&lt;other keys&gt;</c> and <c>&lt;slug&gt;</c> (and <c>&lt;that kid&gt;</c>, left for the owner) in it.</summary>
    internal static string UseKeyResultWithOthers() => Span("R11.101 words", "The result of use-key while it holds other keys the Forum serves live", firstOfMany: true);

    /// <summary>R11.96 (add.) (rev.): the sentence the rotation <c>NOW IN FORCE</c> line ends with while the signing key is not live.</summary>
    internal static string RevisedTail()
    {
        var paragraph = PublishedAcceptBudget.Paragraph("**R11.96 (add.) (rev.)**");
        var spans = Regex.Matches(paragraph, "`([^`]+)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        return spans.Length == 1
            ? spans[0]
            : throw new InvalidOperationException($"{Errata}'s R11.96 (add.) (rev.): {spans.Length} spans, expected 1");
    }

    private static string Span(string paragraphName, string label, bool firstOfMany = false, string? anchor = null)
    {
        var paragraph = PublishedAcceptBudget.Paragraph(anchor ?? $"**{paragraphName}.**");
        var marker = $"*{label}:*";
        var at = paragraph.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0 || paragraph.IndexOf(marker, at + 1, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"{Errata}'s {paragraphName} do not hold {marker} exactly once");

        var start = at + marker.Length;
        var next = Regex.Match(paragraph[start..], @"\*[A-Z][^*`]*:\*", RegexOptions.CultureInvariant);
        var section = next.Success ? paragraph[start..(start + next.Index)] : paragraph[start..];
        var spans = Regex.Matches(section, "`([^`]+)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        return spans.Length == 1 || (firstOfMany && spans.Length >= 1)
            ? spans[0]
            : throw new InvalidOperationException($"{Errata}'s {paragraphName}, {marker}: {spans.Length} spans, expected 1");
    }
}
