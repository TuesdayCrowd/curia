using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R9.43, R10.83, R11.55 and R11.56's words (errata G42) as the errata publishes them, read at test time and not from the code that emits
/// them: the paragraph that begins each anchor, up to the blank line that ends it. A fact takes the member's name, the fixed phrase and the
/// capitalised words from here and compares them with what the Forum serves and the readers print, so a word the errata names and the code
/// drops, or the other way, turns it red. Every count is asserted before a value is returned, so a parse that read something else fails by
/// name and not by a wrong comparison.
/// </summary>
internal static partial class PublishedForkWords
{
    private static readonly string[] Capitalised = ["FORKED", "NOT STATED"];

    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>
    /// The batch member's name: the one bare-word code span that R9.43 (which defines the member) and R10.83 (which words it) both hold.
    /// One is asserted before it is returned.
    /// </summary>
    internal static string MemberName()
    {
        var defined = Spans("**R9.43**").Where(IsBareWord).ToHashSet(StringComparer.Ordinal);
        var worded = Spans("**R10.83**").Where(IsBareWord).ToHashSet(StringComparer.Ordinal);
        var common = defined.Intersect(worded, StringComparer.Ordinal).ToArray();
        if (common.Length != 1)
            throw new InvalidOperationException($"{Errata}'s R9.43 and R10.83 share {common.Length} bare-word code spans ({string.Join(", ", common)}); the parse read something else");

        return common[0];
    }

    /// <summary>
    /// The phrase a fork branch's line says, with <c>N</c> where the count goes: <c>one of N revisions of the same post</c>, read from R10.83's
    /// prose. Asserted present exactly once before it is returned.
    /// </summary>
    internal static string OneOfPhrasePattern()
    {
        var matches = OneOf().Matches(Paragraph("**R10.83**"));
        if (matches.Count != 1)
            throw new InvalidOperationException($"{Errata}'s R10.83 holds {matches.Count} phrases of the form \"one of N revisions of the same post\"");

        return matches[0].Value;
    }

    /// <summary>The phrase for a branch of <paramref name="total"/> revisions: <c>one of 3 revisions of the same post</c>.</summary>
    internal static string OneOfPhrase(int total) =>
        OneOfPhrasePattern().Replace(" N ", $" {total.ToString(System.Globalization.CultureInfo.InvariantCulture)} ", StringComparison.Ordinal);

    /// <summary>The capitalised words R10.83 says a line says: <c>FORKED</c> and <c>NOT STATED</c>, in the order they appear. Two are asserted before they are returned.</summary>
    internal static string[] CapitalisedWords()
    {
        var paragraph = Paragraph("**R10.83**");
        var words = Capitalised.Where(w => paragraph.Contains(w, StringComparison.Ordinal)).ToArray();
        if (words.Length != 2)
            throw new InvalidOperationException($"{Errata}'s R10.83 holds {words.Length} of FORKED and NOT STATED");

        return words;
    }

    /// <summary>The words R10.83 forbids on a <c>current</c> line, in the old spelling the erratum replaces: <c>cite as-is</c> is not in the entry, so it is parsed from R11.56's quoted prohibition.</summary>
    internal static string ForbiddenWords()
    {
        var quoted = Quoted().Matches(Paragraph("**R11.56**")).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        if (quoted.Length != 1)
            throw new InvalidOperationException($"{Errata}'s R11.56 quotes {quoted.Length} phrases ({string.Join(", ", quoted)}); the parse read something else");

        return quoted[0];
    }

    /// <summary>R11.55's bound on the walk, in posts: the number word before "posts" in "to a bound of eight posts". One is asserted before it is returned.</summary>
    internal static int WalkBound()
    {
        var found = Bound().Matches(Paragraph("**R11.55**"));
        if (found.Count != 1)
            throw new InvalidOperationException($"{Errata}'s R11.55 holds {found.Count} bounds of the form \"a bound of <number> posts\"");

        return found[0].Groups[1].Value switch
        {
            "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, "seven" => 7, "eight" => 8, "nine" => 9, "ten" => 10,
            var other => throw new InvalidOperationException($"{Errata}'s R11.55 bound is \"{other}\", which this parse does not know"),
        };
    }

    private static bool IsBareWord(string span) => BareWord().IsMatch(span);

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

        // The entry's paragraph for a requirement runs to the next blank line, and R9.43 and R10.83 have continuation lines
        // (a second and a third line of the same entry) that this keeps: the anchor to the heading of the next requirement or section.
        var end = NextBoundary().Match(text, start + anchor.Length);
        return Whitespace().Replace(text[start..(end.Success ? end.Index : text.Length)], " ");
    }

    [GeneratedRegex(@"\n\n(?=\*\*R\d+\.\d+\*\*|#{1,4} )", RegexOptions.CultureInvariant)]
    private static partial Regex NextBoundary();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();

    [GeneratedRegex(@"^[a-z_]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BareWord();

    [GeneratedRegex(@"one of N revisions of the same post", RegexOptions.CultureInvariant)]
    private static partial Regex OneOf();

    [GeneratedRegex(@"""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"a bound of (\w+) posts", RegexOptions.CultureInvariant)]
    private static partial Regex Bound();
}
