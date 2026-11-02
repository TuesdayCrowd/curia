using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R8.85, R9.54 and R11.78's words (errata G56) as the errata publishes them, read at test time and not from the code that emits them: the
/// paragraph that begins each anchor, up to the blank line that ends it, whitespace folded. A fact takes the problem types, the statuses, the
/// ten class slugs, the exit codes and the sentences from here and compares them with what the Forum and the readers do. Every count is asserted
/// before a value is returned (ten classes, two types, one status each, two exit codes), so a parse that read something else fails by name and
/// not by a wrong comparison.
/// </summary>
internal static partial class PublishedBoardWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string BoardAnchor = "**R8.85 words.**";
    private const string BlankAnchor = "**R9.54 words.**";
    private const string ClientAnchor = "**R11.78 words.**";

    /// <summary>The ten class slugs of R8.85, in the published order: the backtick spans after the detail's <c>board: </c> that are slugs.</summary>
    internal static string[] Classes()
    {
        var spans = Spans(BoardAnchor);
        var from = spans.IndexOf("board: ");
        if (from < 0) throw new InvalidOperationException($"{Errata}'s R8.85 words has no span \"board: \"");

        var slugs = spans.Skip(from + 1).TakeWhile(s => s != "ToLowerInvariant").Where(s => Slug().IsMatch(s)).ToArray();
        if (slugs.Length != 10)
            throw new InvalidOperationException($"{Errata}'s R8.85 words names {slugs.Length} classes ({string.Join(", ", slugs)}); the parse read something else");

        return slugs;
    }

    /// <summary>The type of a board that is not a name: the one <c>curia/content/</c> span of R8.85 words that is not the client's.</summary>
    internal static string BoardType()
    {
        var types = Spans(BoardAnchor).Where(s => ContentType().IsMatch(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (types.Length != 1)
            throw new InvalidOperationException($"{Errata}'s R8.85 words names {types.Length} content types ({string.Join(", ", types)}); the parse read something else");

        return types[0];
    }

    /// <summary>The status of R8.85 words and of R9.54 words (both 400): asserted to be the one three-digit span of each.</summary>
    internal static int BoardStatus() => OneStatus(BoardAnchor);

    /// <summary>The status of R9.54 words.</summary>
    internal static int BlankStatus() => OneStatus(BlankAnchor);

    /// <summary>R8.85's title: the span that begins "A board is a name".</summary>
    internal static string BoardTitle() => OneSpan(BoardAnchor, "A board is a name");

    /// <summary>The title the client prints for its envelope refusal: the span that begins "The envelope is not".</summary>
    internal static string ClientTitle() => OneSpan(BoardAnchor, "The envelope is not");

    /// <summary>The type of the client error whose title is printed: the span <c>curia/client/envelope-invalid</c>.</summary>
    internal static string ClientType() => OneSpan(BoardAnchor, "curia/client/");

    /// <summary>The exit code <c>curia</c> gives the envelope refusal: the 3 in "<c>curia</c> exits <c>3</c>".</summary>
    internal static int CliEnvelopeExit()
    {
        var match = ExitsThree().Match(Paragraph(BoardAnchor));
        if (!match.Success) throw new InvalidOperationException($"{Errata}'s R8.85 words has no \"exits `N`\"");
        return int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>The characters R8.85's <c>reserved</c> class names: the one-character spans between "one of" and the closing parenthesis.</summary>
    internal static string[] ReservedCharacters()
    {
        var match = Reserved().Match(Paragraph(BoardAnchor));
        if (!match.Success) throw new InvalidOperationException($"{Errata}'s R8.85 words has no \"`reserved` (one of ...)\"");
        var characters = Span().Matches(match.Groups["s"].Value).Select(m => m.Groups[1].Value).ToArray();
        if (characters.Length != 5)
            throw new InvalidOperationException($"{Errata}'s R8.85 words reserves {characters.Length} characters ({string.Join(" ", characters)}); the parse read something else");

        return characters;
    }

    /// <summary>The types of R9.54 words in the order search, inbox.</summary>
    internal static (string Search, string Inbox) BlankTypes()
    {
        var types = Spans(BlankAnchor).Where(s => s.StartsWith("curia/", StringComparison.Ordinal)).ToArray();
        if (types.Length != 2 || !types[0].StartsWith("curia/search/", StringComparison.Ordinal) || !types[1].StartsWith("curia/inbox/", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R9.54 words names {types.Length} types ({string.Join(", ", types)}); the parse read something else");

        return (types[0], types[1]);
    }

    /// <summary>R9.54's title.</summary>
    internal static string BlankTitle() => OneSpan(BlankAnchor, "A parameter is present and blank");

    /// <summary>The four parameters of R9.54 words in the published order: the spans after <c>parameter=</c> up to the next sentence.</summary>
    internal static string[] SearchBlankParameters()
    {
        var spans = Spans(BlankAnchor);
        var from = spans.IndexOf("parameter=");
        if (from < 0) throw new InvalidOperationException($"{Errata}'s R9.54 words has no span \"parameter=\"");
        var names = spans.Skip(from + 1).Take(4).ToArray();
        if (names.Length != 4 || names[0] != "q") throw new InvalidOperationException($"{Errata}'s R9.54 words lists {string.Join(", ", names)}; the parse read something else");
        return names;
    }

    /// <summary>The five CLI refusals of R11.78 words (four for <c>curia search</c>, one for <c>curia inbox</c>), each a span that begins <c>error:</c>, in the published order.</summary>
    internal static string[] CliBlankRefusals()
    {
        var lines = Spans(ClientAnchor).Where(s => s.StartsWith("error:", StringComparison.Ordinal)).ToArray();
        if (lines.Length != 5)
            throw new InvalidOperationException($"{Errata}'s R11.78 words names {lines.Length} command-line refusals; the parse read something else");

        return lines;
    }

    /// <summary>The tool's refusal of a blank criterion for <paramref name="name"/> (<c>query</c>, <c>board</c>, <c>author</c>, <c>min_verification</c>): the published sentence with the name filled in.</summary>
    internal static string ToolBlankRefusal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var sentence = OneSpan(ClientAnchor, "NOT SENT: criteria.query");
        return sentence.Replace("criteria.query", "criteria." + name, StringComparison.Ordinal);
    }

    /// <summary>The exit code of the blank refusals: the 1 in "The refusals of a blank criterion, exit <c>1</c>".</summary>
    internal static int BlankExit()
    {
        var match = BlankExitCode().Match(Paragraph(ClientAnchor));
        if (!match.Success) throw new InvalidOperationException($"{Errata}'s R11.78 words has no \"exit `N`\"");
        return int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>The board sentence the helps and the descriptions carry.</summary>
    internal static string BoardSentence() => OneSpan(ClientAnchor, "A board is a name:");

    /// <summary>The reply tools' sentence, with <c>&lt;post id&gt;</c> left as published.</summary>
    internal static string ReplySentence() => OneSpan(ClientAnchor, "This tool takes the board from the post it replies to");

    /// <summary>The reply tools' sentence for a given post id, which a reader writes as a display literal (errata G17).</summary>
    internal static string ReplySentenceFor(string postId) => ReplySentence().Replace("<post id>", Curia.Canon.Json.DisplayLiteral.Of(postId), StringComparison.Ordinal);

    /// <summary>The <c>curia-mcp</c> version R11.78 names: the one <c>0.N.N</c> that follows "<c>curia-mcp</c>" in the sentence "The version in R11.78 is".</summary>
    internal static string Version()
    {
        var match = VersionSentence().Match(Paragraph(ClientAnchor));
        if (!match.Success) throw new InvalidOperationException($"{Errata}'s R11.78 words has no sentence \"The version in R11.78 is `curia-mcp` 0.N.N\"");
        return match.Groups["v"].Value;
    }

    private static string OneSpan(string anchor, string prefix)
    {
        var found = Spans(anchor).Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (found.Length != 1)
            throw new InvalidOperationException($"{Errata}'s {anchor} has {found.Length} spans that begin \"{prefix}\"; the parse read something else");

        return found[0];
    }

    private static int OneStatus(string anchor)
    {
        var codes = Spans(anchor).Where(s => StatusCode().IsMatch(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (codes.Length != 1)
            throw new InvalidOperationException($"{Errata}'s {anchor} names {codes.Length} statuses ({string.Join(", ", codes)}); the parse read something else");

        return int.Parse(codes[0], CultureInfo.InvariantCulture);
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

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^[a-z]+(-[a-z]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Slug();

    [GeneratedRegex(@"^curia/content/[a-z-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ContentType();

    [GeneratedRegex(@"^\d{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex StatusCode();

    [GeneratedRegex(@"The version in R11\.78 is `curia-mcp` (?<v>0\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionSentence();

    [GeneratedRegex(@"`curia` exits `(?<n>\d)`", RegexOptions.CultureInvariant)]
    private static partial Regex ExitsThree();

    [GeneratedRegex(@"`reserved` \(one of (?<s>[^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Reserved();

    [GeneratedRegex(@"a blank criterion, exit `(?<n>\d)`", RegexOptions.CultureInvariant)]
    private static partial Regex BlankExitCode();
}
