using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// Errata G47's words (R9.48, R9.49, R10.87 to R10.90, R11.62, R11.63) as the errata publishes them, read from the errata at test time and never from
/// production: the paragraph that begins <c>**R10.87**</c> and so on, up to the blank line that ends it. A probe takes the statement, the member names,
/// the served words and the version from here, so a word the errata names and the code drops turns it red, and a word the code changes cannot move its
/// own expectation (PR 3, ruling 16(a) and (d)). Each parse asserts its count before it returns, so a rewording that the parse no longer reads fails
/// here and not as a silent empty list.
/// </summary>
internal static partial class PublishedChainWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>R10.87's statement for an accepted revision: the one code span that begins <c>accepted as the text of the answer it revises</c>.</summary>
    internal static string RevisionStatement() =>
        One(Spans(Paragraph("**R10.87**")).Where(t => t.StartsWith("accepted as the text of the answer it revises", StringComparison.Ordinal)).ToArray(), "R10.87's statement");

    /// <summary>The words R10.87 says a revision's line SHALL NOT say: the one code span <c>the thread's asker</c>.</summary>
    internal static string RevisionForbidden() =>
        One(Spans(Paragraph("**R10.87**")).Where(t => t.Contains("thread's asker", StringComparison.Ordinal) && !t.StartsWith("accepted", StringComparison.Ordinal)).ToArray(), "R10.87's forbidden words");

    /// <summary>R10.88's two statements the receipt carries beside the served word: visible only to its raiser and author, and no outcome shown.</summary>
    internal static (string Visibility, string NoOutcome, string Where) FlagStatements()
    {
        var spans = Spans(Paragraph("**R10.88**")).ToArray();
        return (
            One(spans.Where(t => t.StartsWith("visible only to its raiser", StringComparison.Ordinal)).ToArray(), "R10.88's visibility statement"),
            One(spans.Where(t => t.StartsWith("the Forum shows no outcome", StringComparison.Ordinal)).ToArray(), "R10.88's no-outcome statement"),
            One(spans.Where(t => t == "curia flags").ToArray(), "R10.88's where"));
    }

    /// <summary>R10.88's three states of the served member, as the words a reader prints: not served, served, and <c>NOT STATED</c>.</summary>
    internal static (string NotServed, string Served, string NotStated) ServedWords()
    {
        var spans = Spans(Paragraph("**R10.88**")).ToArray();
        return (
            One(spans.Where(t => t == "the post is not served").ToArray(), "R10.88's not-served word"),
            One(spans.Where(t => t == "the post is served").ToArray(), "R10.88's served word"),
            One(spans.Where(t => t == "NOT STATED").ToArray(), "R10.88's NOT STATED"));
    }

    /// <summary>R9.49's member: the one snake_case code span of its paragraph that is not a route's name (<c>post_served</c>).</summary>
    internal static string ServedMember() =>
        One(Spans(Paragraph("**R9.49**")).Where(t => t == "post_served").ToArray(), "R9.49's member");

    /// <summary>R10.89's two words about the original: what the line SHALL say (<c>still served</c>) and what it SHALL NOT (<c>stands</c>).</summary>
    internal static (string Say, string NotSay) SupersededWords()
    {
        var spans = Spans(Paragraph("**R10.89**")).ToArray();
        return (
            One(spans.Where(t => t == "still served").ToArray(), "R10.89's say"),
            One(spans.Where(t => t == "stands").ToArray(), "R10.89's not-say"));
    }

    /// <summary>The words R10.89 requires the verb's help and the tool's description to define: the code spans of its sentence that says so. Two are asserted.</summary>
    internal static string[] DefinedWords()
    {
        var paragraph = Paragraph("**R10.89**");
        var start = paragraph.IndexOf("The verb's help", StringComparison.Ordinal);
        var end = paragraph.IndexOf('.', start);
        return Counted([.. Spans(paragraph[start..end]).Distinct(StringComparer.Ordinal)], 2, "R10.89's defined words");
    }

    /// <summary>R10.89's tail for a branch without a post id: the spans <c>not served</c> and <c>withheld</c> (also the defined words) and the verb <c>recheck</c>.</summary>
    internal static string RecheckVerb() =>
        One(Spans(Paragraph("**R10.89**")).Where(t => t == "recheck").Distinct(StringComparer.Ordinal).ToArray(), "R10.89's verb");

    /// <summary>R10.90's member: <c>marking</c>, with <c>token_member</c> (<c>marking_token</c>), <c>escape</c> (<c>doubled</c>) and <c>rule</c>.</summary>
    internal static (string Member, string TokenMember, string TokenMemberValue, string Escape, string EscapeValue, string Rule) Marking()
    {
        var spans = Spans(Paragraph("**R10.90**")).ToArray();
        string Named(string name) => One(spans.Where(t => t == name).Distinct(StringComparer.Ordinal).ToArray(), $"R10.90's {name}");
        return (Named("marking"), Named("token_member"), Named("marking_token"), Named("escape"), Named("doubled"), Named("rule"));
    }

    /// <summary>R11.62's three phrases: the hedge the refusal SHALL NOT carry, the named revision's standing, and the first block's text that stands.</summary>
    internal static (string Hedge, string Named, string Stands) RefusalAndBlock()
    {
        var spans = Spans(Paragraph("**R11.62**")).ToArray();
        return (
            One(spans.Where(t => t == "may itself have been revised").ToArray(), "R11.62's hedge"),
            One(spans.Where(t => t == "the revision you named").ToArray(), "R11.62's named revision"),
            One(spans.Where(t => t == "the text that stands").ToArray(), "R11.62's text that stands"));
    }

    /// <summary>A phrase R11.62's paragraph uses outside a code span (<c>latest the Forum reports</c>), asserted present in the errata before it is returned.</summary>
    internal static string RefusalPhrase(string phrase)
    {
        if (!Paragraph("**R11.62**").Contains(phrase, StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R11.62 no longer holds the phrase \"{phrase}\"");

        return phrase;
    }

    /// <summary>R11.63's two member names and the word for an absent one.</summary>
    internal static (string AppliesTo, string NotApplicableTo, string NotStated) FloorLists()
    {
        var spans = Spans(Paragraph("**R11.63**")).ToArray();
        return (
            One(spans.Where(t => t == "applies_to").ToArray(), "R11.63's applies_to"),
            One(spans.Where(t => t == "not_applicable_to").ToArray(), "R11.63's not_applicable_to"),
            One(spans.Where(t => t == "not stated").ToArray(), "R11.63's not stated"));
    }

    /// <summary>The <c>curia-mcp</c> version G48 names (BRB47, which is CHN51 re-pointed): the number after <c>curia-mcp</c> in R11.64's index row.</summary>
    internal static string Version()
    {
        var text = ErrataText();
        var row = text.IndexOf("| R11.64 |", StringComparison.Ordinal);
        if (row < 0) throw new InvalidOperationException($"{Errata} has no index row for R11.64");
        var end = text.IndexOf('\n', row);
        var match = VersionAfterName().Match(text[row..end]);
        if (!match.Success) throw new InvalidOperationException($"{Errata}'s R11.64 index row names no curia-mcp version");
        return match.Groups[1].Value;
    }

    private static string One(string[] found, string what) => Counted([.. found.Distinct(StringComparer.Ordinal)], 1, what)[0];

    private static string[] Counted(string[] found, int expected, string what)
    {
        if (found.Length != expected)
            throw new InvalidOperationException($"{Errata} holds {found.Length} of {what} ({string.Join(", ", found)}); expected {expected}");

        return found;
    }

    private static IEnumerable<string> Spans(string paragraph) => Span().Matches(paragraph).Select(m => m.Groups[1].Value);

    private static string ErrataText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}")
            : File.ReadAllText(Path.Combine(dir.FullName, Errata));
    }

    private static string Paragraph(string anchor)
    {
        var text = ErrataText();
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

    [GeneratedRegex(@"curia-mcp` (\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionAfterName();
}
