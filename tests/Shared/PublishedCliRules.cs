using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R9.40's and R11.51's words as the errata publishes them, read from the errata at test time and not from the code: the paragraph that
/// begins <c>**R9.40**</c> or <c>**R11.51**</c>, up to the blank line that ends it. A fact takes the exit numbers, the rank and the author
/// phrase from here and compares them with what the CLI does, so a number the errata names and the code changes turns it red. Each parse
/// counts what it found before it returns it.
/// </summary>
internal static partial class PublishedCliRules
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>The exit R9.40 says a recheck with a malformed element ends with: the first <c>exit `n`</c> of the paragraph.</summary>
    internal static int R940Exit()
    {
        var match = ExitSpan().Match(Paragraph("**R9.40**"));
        if (!match.Success)
            throw new InvalidOperationException($"{Errata}'s R9.40 names no exit");

        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// R9.40's rank as the codes it names: the one it ranks below (a <c>FAILED</c> line), then the two it ranks above (a withheld or unknown
    /// citation, a disputed one), in the order the sentence gives them. Three are asserted before they are returned.
    /// </summary>
    internal static (int Below, int[] Above) R940Rank()
    {
        var match = RankSentence().Match(Paragraph("**R9.40**"));
        if (!match.Success)
            throw new InvalidOperationException($"{Errata}'s R9.40 has no sentence of the form \"rank below `a` ... above `b` ... and `c`\"");

        int[] above = [int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture)];
        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), above);
    }

    /// <summary>The exit R11.51 gives both refusals: the first <c>exit `n`</c> of the paragraph.</summary>
    internal static int R1151Exit()
    {
        var match = ExitSpan().Match(Paragraph("**R11.51**"));
        if (!match.Success)
            throw new InvalidOperationException($"{Errata}'s R11.51 names no exit");

        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>The phrase R11.51 says the author refusal states, kept only if the paragraph holds it.</summary>
    internal static string R1151AuthorPhrase()
    {
        const string phrase = "only a post's own author may revise it";
        if (!Paragraph("**R11.51**").Contains(phrase, StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R11.51 no longer holds the phrase \"{phrase}\"");

        return phrase;
    }

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

    [GeneratedRegex(@"exit `(\d+)`", RegexOptions.CultureInvariant)]
    private static partial Regex ExitSpan();

    [GeneratedRegex(@"rank below `(\d+)`.*?above `(\d+)`.*?and `(\d+)`", RegexOptions.CultureInvariant)]
    private static partial Regex RankSentence();
}
