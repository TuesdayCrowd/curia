using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R8.87 and R11.85's words (errata G61) as the errata publishes them, read at test time and not from the code that emits them: the paragraph that begins each
/// anchor, up to the blank line that ends it, whitespace folded. A fact takes the problem type, the status, the title, the detail keys, the refusal
/// sentence and the help sentence from here and compares them with what the Forum and the clients say. Every count is asserted before a value is returned (one
/// type, one status, one title, two detail keys, one refusal sentence), so a parse that read something else fails by name and not by a wrong comparison.
/// </summary>
internal static partial class PublishedReplyBoardWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string RouteAnchor = "**R8.87 words.**";
    private const string RefusalAnchor = "**R11.85 words (the refusal of a differing board).**";
    private const string UnreadAnchor = "**R11.85 words (the board could not be read).**";
    private const string RequirementAnchor = "**R11.85** ";
    private const string HelpAnchor = "**R11.85 words (help and descriptions).**";

    /// <summary>The type of the refusal: the one <c>curia/ingest/</c> span of R8.87 words.</summary>
    internal static string Type() => OneSpan(RouteAnchor, "curia/ingest/");

    /// <summary>The status of the refusal: the one three-digit span of R8.87 words.</summary>
    internal static int Status()
    {
        var codes = Spans(RouteAnchor).Where(s => ThreeDigits().IsMatch(s)).ToArray();
        if (codes.Length != 1)
            throw new InvalidOperationException($"{Errata}'s R8.87 words names {codes.Length} statuses; the parse read something else");

        return int.Parse(codes[0], CultureInfo.InvariantCulture);
    }

    /// <summary>The title of the refusal: the span that begins "A reply or a revision".</summary>
    internal static string Title() => OneSpan(RouteAnchor, "A reply or a revision");

    /// <summary>The detail key for an answer and a comment (<c>parent=</c>) and for a revision (<c>original=</c>), in that order: the spans of R8.87 words that end <c>&lt;post id&gt;</c>.</summary>
    internal static (string Parent, string Original) DetailKeys()
    {
        var keys = Spans(RouteAnchor).Where(s => s.EndsWith("=<post id>", StringComparison.Ordinal)).Select(s => s[..^"<post id>".Length]).ToArray();
        if (keys.Length != 2)
            throw new InvalidOperationException($"{Errata}'s R8.87 words names {keys.Length} detail keys; the parse read something else");

        return (keys[0], keys[1]);
    }

    /// <summary>The exit code of the refusal of a differing board: the <c>N</c> in "Exit <c>N</c>".</summary>
    internal static int RefusalExit()
    {
        var match = ExitCode().Match(Paragraph(RefusalAnchor));
        if (!match.Success)
            throw new InvalidOperationException($"{Errata}'s R11.85 words (the refusal of a differing board) has no \"Exit `N`\"");

        return int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The refusal of a differing board with its three values filled in. Each argument is the text as it is printed, a display literal with its quotes
    /// (the values are ones the client did not compose), so the comparison is with the line the reader sees.
    /// </summary>
    internal static string RefusalLine(string typedLiteral, string postIdLiteral, string boardLiteral)
    {
        var template = OneSpan(RefusalAnchor, "error: --board ");
        return template
            .Replace("<typed>", typedLiteral, StringComparison.Ordinal)
            .Replace("<post id>", postIdLiteral, StringComparison.Ordinal)
            .Replace("<board>", boardLiteral, StringComparison.Ordinal);
    }

    /// <summary>The sentence after the read's own refusal when the board could not be read: the one span that begins "The board is taken from that post".</summary>
    internal static string UnreadSentence() => OneSpan(UnreadAnchor, "The board is taken from that post");

    /// <summary>The sentence of the verbs' help: the one span that begins <c>--board, if given</c>.</summary>
    internal static string HelpSentence() => OneSpan(HelpAnchor, "--board, if given");

    /// <summary>
    /// Whether R11.85's own paragraph says <c>curia-mcp</c> is <paramref name="version"/>. A containment test and not a second reader of a version out of an errata paragraph:
    /// <c>PublishedReadersLastWords.Version()</c> is the one reader (the Mcp suite's RLW40 holds that), and it is re-pointed at R11.85 by the build.
    /// </summary>
    internal static bool RequirementNamesMcpVersion(string version) =>
        Paragraph(RequirementAnchor).Contains($"`curia-mcp` is {version}", StringComparison.Ordinal);

    /// <summary>The words <c>Nothing was sent.</c> at the end of both client sentences, asserted as a published span's ending.</summary>
    internal static string NothingSent() =>
        UnreadSentence().EndsWith("Nothing was sent.", StringComparison.Ordinal)
            ? "Nothing was sent."
            : throw new InvalidOperationException($"{Errata}'s R11.85 words (the board could not be read) does not end \"Nothing was sent.\"");

    private static string OneSpan(string anchor, string startsWith)
    {
        var matches = Spans(anchor).Where(s => s.StartsWith(startsWith, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException($"{Errata}'s paragraph {anchor} has {matches.Length} spans that begin \"{startsWith}\"; the parse read something else");

        return matches[0];
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

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();

    [GeneratedRegex(@"^\d{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ThreeDigits();

    [GeneratedRegex(@"Exit `(?<n>\d+)`", RegexOptions.CultureInvariant)]
    private static partial Regex ExitCode();
}
