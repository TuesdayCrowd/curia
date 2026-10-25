using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R9.34's and R11.49's words as the errata publishes them, read from the errata at test time and not from the code: the paragraph that
/// begins <c>**R9.34**</c> or <c>**R11.49**</c>, up to the blank line that ends it. A fact takes the published tokens from here and
/// compares them with what a reader prints, so a word the errata names and the code drops turns it red.
/// </summary>
internal static partial class PublishedRecheck
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>
    /// The tokens R9.34 names: its upper-case words (<c>FAILED</c>, <c>FORKED</c>) and its code spans other than the two readers' own names
    /// (<c>not served</c>, <c>digests[&lt;n&gt;]</c>) and the state it warns against trusting on the Forum's word alone (<c>current</c>), in the order they appear. Five are asserted before they are returned.
    /// </summary>
    internal static string[] R934Tokens()
    {
        var paragraph = Paragraph("**R9.34**");
        var caps = Caps().Matches(paragraph).Select(m => m.Value);
        var spans = Span().Matches(paragraph).Select(m => m.Groups[1].Value).Where(t => !t.Contains("recheck", StringComparison.Ordinal));
        var tokens = caps.Concat(spans).Distinct(StringComparer.Ordinal).ToArray();
        if (tokens.Length != 5)
            throw new InvalidOperationException($"{Errata}'s R9.34 names {tokens.Length} tokens ({string.Join(", ", tokens)}); the parse read something else");

        return tokens;
    }

    /// <summary>
    /// The three phrases R11.49 says the closing line states: that every line is the Forum's word as of the call, that no signature was checked, and
    /// that <c>curia_verify</c> checks those. Each is kept only if the paragraph holds it, and three are asserted before they are returned.
    /// </summary>
    internal static string[] R1149ClosingPhrases()
    {
        var paragraph = Paragraph("**R11.49**");
        string[] candidates = ["Forum's word as of the call", "no signature", "curia_verify"];
        var kept = candidates.Where(c => paragraph.Contains(c, StringComparison.Ordinal)).ToArray();
        if (kept.Length != 3)
            throw new InvalidOperationException($"{Errata}'s R11.49 holds {kept.Length} of the three closing phrases");

        return kept;
    }

    /// <summary>A token as a pattern a printed line must match: <c>digests[&lt;n&gt;]</c> stands for any position.</summary>
    internal static Regex PatternOf(string token) =>
        new(Regex.Escape(token).Replace("<n>", @"\d+", StringComparison.Ordinal), RegexOptions.CultureInvariant);

    private static string Paragraph(string anchor)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}");

        var text = File.ReadAllText(Path.Combine(dir.FullName, Errata));
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{anchor}\"");

        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        return Whitespace().Replace(text[start..(end < 0 ? text.Length : end)], " ");
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\b[A-Z]{6,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Caps();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();
}
