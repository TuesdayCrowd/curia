using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R8.73, R8.74, R9.44, R9.45, R10.84, R11.57, R11.58 and R11.59's words (errata G43) as the errata publishes them, read at test
/// time and not from the code that emits them: the paragraph that begins each anchor, up to the blank line that ends it. A fact takes
/// the problem types and the phrases from here and compares them with what the Forum and the readers do, so a word the errata names
/// and the code drops, or the other way, turns it red. Every count is asserted before a value is returned, so a parse that read
/// something else fails by name and not by a wrong comparison.
/// </summary>
internal static partial class PublishedSmallWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>The eight anchors, each asserted to exist and to be one paragraph that names something.</summary>
    internal static readonly string[] Anchors =
        ["**R8.73**", "**R8.74**", "**R9.44**", "**R9.45**", "**R10.84**", "**R11.57**", "**R11.58**", "**R11.59**"];

    /// <summary>The eight paragraphs are read and each is non-empty: the parsing helper's count assertion (FPJ40).</summary>
    internal static void AssertAnchorsPresent()
    {
        foreach (var anchor in Anchors)
            if (Paragraph(anchor).Length <= anchor.Length)
                throw new InvalidOperationException($"{Errata}'s {anchor} is empty");
    }

    /// <summary>The type a submission with a tag the filter cannot name is refused with: the one <c>curia/content/</c> span of R8.73.</summary>
    internal static string TagUnfilterableType() => One("**R8.73**", "curia/content/");

    /// <summary>The status R8.73 refuses with: the one three-digit code span of the entry.</summary>
    internal static int TagUnfilterableStatus() => OneStatus("**R8.73**");

    /// <summary>The type the inbox answers a refused page size with: R9.44's one <c>curia/inbox/</c> span.</summary>
    internal static string InboxLimitType() => One("**R9.44**", "curia/inbox/");

    /// <summary>The type the search route keeps for a refused page size: R9.44's one <c>curia/search/</c> span.</summary>
    internal static string SearchLimitType() => One("**R9.44**", "curia/search/");

    /// <summary>The status R9.44 answers a refused page size with: the one three-digit code span of the entry.</summary>
    internal static int LimitStatus() => OneStatus("**R9.44**");

    /// <summary>
    /// The phrases R11.59 says a description and a help entry SHALL carry about a tag, in the entry's order. Each is a clause of the
    /// sentence that begins "SHALL state that a tag", cut at the commas and "and"s the entry uses; six are asserted before they are returned.
    /// </summary>
    internal static string[] TagPhrases()
    {
        var paragraph = Paragraph("**R11.59**");
        var match = TagSentence().Match(paragraph);
        if (!match.Success)
            throw new InvalidOperationException($"{Errata}'s R11.59 has no sentence \"SHALL state that a tag ...; the help of\"");

        var phrases = new List<string>();
        var sentence = match.Groups["s"].Value; // "is not empty, holds no comma, no leading or trailing white space and no U+0000, and that search compares tags without regard to case and in normalization form C while storage does not fold case"
        var split = sentence.IndexOf(", and that search", StringComparison.Ordinal);
        if (split < 0)
            throw new InvalidOperationException($"{Errata}'s R11.59 no longer says \", and that search\"");

        var tag = sentence[..split]; // is not empty, holds no comma, no leading or trailing white space and no U+0000
        var search = sentence[(split + ", and that search ".Length)..]; // compares tags without regard to case and in normalization form C while storage does not fold case
        foreach (var part in tag.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            phrases.AddRange(part.Split(" and ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

        var compares = search.Split(" while ", StringSplitOptions.TrimEntries)[0]; // compares tags without regard to case and in normalization form C
        phrases.AddRange(compares[(compares.IndexOf("tags ", StringComparison.Ordinal) + "tags ".Length)..].Split(" and ", StringSplitOptions.TrimEntries));
        phrases.Add(search.Split(" while ", StringSplitOptions.TrimEntries)[1]); // storage does not fold case

        // is not empty | holds no comma | no leading or trailing white space | no U+0000 | without regard to case | in normalization form C | storage does not fold case
        if (phrases.Count != 7)
            throw new InvalidOperationException($"{Errata}'s R11.59 reads as {phrases.Count} tag phrases ({string.Join(" | ", phrases)}); the parse read something else");

        return [.. phrases];
    }

    /// <summary>The act the entry for exit 4 SHALL name: R11.59's "an act open only to the owner of the resource".</summary>
    internal static string ExitFourPhrase()
    {
        var match = ExitFour().Match(Paragraph("**R11.59**"));
        if (!match.Success)
            throw new InvalidOperationException($"{Errata}'s R11.59 has no sentence \"in the entry for exit 4 that ... is also refused with it\"");

        return match.Groups["p"].Value;
    }

    /// <summary>R11.58's two nouns: the flag a cursor is refused by and the post identifier, each asserted to be named.</summary>
    internal static string BlankCursorFlag()
    {
        var spans = Spans("**R11.58**").Where(s => s.StartsWith("--", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (spans.Length != 1)
            throw new InvalidOperationException($"{Errata}'s R11.58 names {spans.Length} flags ({string.Join(", ", spans)}); the parse read something else");

        return spans[0];
    }

    private static string One(string anchor, string prefix)
    {
        var spans = Spans(anchor).Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (spans.Length != 1)
            throw new InvalidOperationException($"{Errata}'s {anchor} names {spans.Length} problem types under {prefix} ({string.Join(", ", spans)}); the parse read something else");

        return spans[0];
    }

    private static int OneStatus(string anchor)
    {
        var codes = Spans(anchor).Where(s => StatusCode().IsMatch(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (codes.Length != 1)
            throw new InvalidOperationException($"{Errata}'s {anchor} names {codes.Length} statuses ({string.Join(", ", codes)}); the parse read something else");

        return int.Parse(codes[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<string> Spans(string anchor) =>
        Span().Matches(Paragraph(anchor)).Select(m => m.Groups[1].Value).ToList();

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

    [GeneratedRegex(@"SHALL state that a tag (?<s>.+?); the help of", RegexOptions.CultureInvariant)]
    private static partial Regex TagSentence();

    [GeneratedRegex(@"in the entry for exit 4 that (?<p>.+?) is also refused with it", RegexOptions.CultureInvariant)]
    private static partial Regex ExitFour();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();

    [GeneratedRegex(@"^\d{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex StatusCode();
}
