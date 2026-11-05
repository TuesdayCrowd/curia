using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R8.88, R9.55, R9.57 and R10.97's words (errata G62) as the errata publishes them, read at test time and not from the code that emits them: the paragraph that
/// begins each anchor, up to the blank line that ends it, whitespace folded. A fact takes the member names, the problem types, the sentences of the filters
/// line, the inbox, the floor line and the receipt from here. Every count is asserted before a value is returned (two members, two types, four clauses, three
/// receipt lines), so a parse that read something else fails by name and not by a wrong comparison.
/// </summary>
internal static partial class PublishedNameWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string FiltersAnchor = "**R10.97 words (the filters line).**";
    private const string InboxAnchor = "**R10.97 words (the inbox).**";
    private const string FloorAnchor = "**R10.97 words (the floor line).**";
    private const string ReceiptAnchor = "**R10.97 words (the receipt).**";
    private const string MemberAnchor = "**R9.55 words.**";
    private const string BoardNewAnchor = "**R8.88 words.**";
    private const string ReadAnchor = "**R9.57 words.**";

    /// <summary>R9.55's member name: the one span that follows "The member is".</summary>
    internal static string UnmatchedMember() => OneSpan(MemberAnchor, "unmatched_filters");

    /// <summary>The two names an <c>unmatched_filters</c> array may hold, in the published order (<c>board</c>, <c>author</c>).</summary>
    internal static string[] UnmatchedNames()
    {
        var names = Spans(MemberAnchor).Where(s => s is "board" or "author").Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length != 2 || names[0] != "board" || names[1] != "author")
            throw new InvalidOperationException($"{Errata}'s R9.55 words names {names.Length} filter names ({string.Join(", ", names)}); the parse read something else");

        return names;
    }

    /// <summary>R8.88's member name.</summary>
    internal static string BoardNewMember() => OneSpan(BoardNewAnchor, "board_new");

    /// <summary>The two problem types of R9.57 words, in the order single read and batch, thread: <c>curia/posts/unknown-parameter</c>, <c>curia/threads/unknown-parameter</c>.</summary>
    internal static (string Posts, string Threads) UnknownTypes()
    {
        var types = Spans(ReadAnchor).Where(s => s.StartsWith("curia/", StringComparison.Ordinal)).ToArray();
        if (types.Length != 2 || !types[0].StartsWith("curia/posts/", StringComparison.Ordinal) || !types[1].StartsWith("curia/threads/", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R9.57 words names {types.Length} types ({string.Join(", ", types)}); the parse read something else");

        return (types[0], types[1]);
    }

    /// <summary>R9.57's title (R9.29's): the span that begins "That parameter is not read".</summary>
    internal static string UnknownTitle() => OneSpan(ReadAnchor, "That parameter is not read");

    /// <summary>The tail of R9.57's detail: the span that begins "this route reads exactly these:".</summary>
    internal static string UnknownDetailTail() => OneSpan(ReadAnchor, "this route reads exactly these:");

    /// <summary>The clause for a filter whose name is not in <c>unmatched_filters</c>, with <c>&lt;name&gt;</c> and <c>&lt;typed&gt;</c> as published.</summary>
    internal static string MatchedClause() => OneSpan(FiltersAnchor, "<name> <typed>: a post the Forum serves bears it");

    /// <summary>The unmatched board clause with <c>&lt;hint&gt;</c> as published.</summary>
    internal static string BoardClause() => OneSpan(FiltersAnchor, "board <typed>: NO POST THE FORUM SERVES IS ON IT;");

    /// <summary>The hint of a board name the grammar refuses, with <c>&lt;classes&gt;</c> as published.</summary>
    internal static string BoardHintWithClasses() => OneSpan(FiltersAnchor, "it is not a name the Forum accepts for a new board");

    /// <summary>The hint of a board that is a name: the span that begins "check the spelling".</summary>
    internal static string BoardHintPlain() => OneSpan(FiltersAnchor, "check the spelling");

    /// <summary>The unmatched author clause.</summary>
    internal static string AuthorClause() => OneSpan(FiltersAnchor, "author <typed>: NO POST THE FORUM SERVES BEARS IT;");

    /// <summary>The clause for a member the Forum did not state, with <c>&lt;name&gt;</c> and <c>&lt;typed&gt;</c> as published.</summary>
    internal static string NotStatedClause() => OneSpan(FiltersAnchor, "<name> <typed>: NOT STATED");

    /// <summary>The label of the filters line: <c>filters</c> followed by three spaces, as the first span of the paragraph writes it.</summary>
    internal static string FiltersLabel()
    {
        var line = Spans(FiltersAnchor).First(s => s.StartsWith("filters ", StringComparison.Ordinal));
        return line[..line.IndexOf('<', StringComparison.Ordinal)];
    }

    /// <summary>The inbox's sentence that nothing needs an answer, the one span of R10.97 words (the inbox).</summary>
    internal static string NothingNeedsAnAnswer() => OneSpan(InboxAnchor, "no open questions match these filters.");

    /// <summary>The floor line's template, <c>floor     min_verification &lt;level&gt; ...</c>, one span.</summary>
    internal static string FloorTemplate() => OneSpan(FloorAnchor, "floor     min_verification");

    /// <summary>The two phrases the floor line no longer holds: <c>applies to</c> and <c>; not to </c>.</summary>
    internal static string[] FloorRetiredPhrases()
    {
        var phrases = Spans(FloorAnchor).Where(s => s is "applies to" or "; not to ").ToArray();
        if (phrases.Length != 2)
            throw new InvalidOperationException($"{Errata}'s R10.97 words (the floor line) retires {phrases.Length} phrases; the parse read something else");

        return phrases;
    }

    /// <summary>The three receipt lines in the published order: true, false, not stated.</summary>
    internal static (string True, string False, string NotStated) ReceiptLines()
    {
        var lines = Spans(ReceiptAnchor).Where(s => s.StartsWith("board_new ", StringComparison.Ordinal)).ToArray();
        if (lines.Length != 3 || !lines[0].StartsWith("board_new true", StringComparison.Ordinal) || !lines[1].StartsWith("board_new false", StringComparison.Ordinal) || !lines[2].StartsWith("board_new NOT STATED", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R10.97 words (the receipt) has {lines.Length} board_new lines; the parse read something else");

        return (lines[0], lines[1], lines[2]);
    }

    private static string OneSpan(string anchor, string prefix)
    {
        var found = Spans(anchor).Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (found.Length != 1)
            throw new InvalidOperationException($"{Errata}'s {anchor} has {found.Length} spans that begin \"{prefix}\"; the parse read something else");

        return found[0];
    }

    /// <summary>The code spans of the paragraph. Runs of spaces inside a span are kept (the labels of the lines are columns); only a line break inside a span is folded.</summary>
    private static List<string> Spans(string anchor) =>
        Span().Matches(Paragraph(anchor)).Select(m => LineBreak().Replace(m.Groups[1].Value, " ")).ToList();

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
        return text[start..(end < 0 ? text.Length : end)];
    }

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();

    [GeneratedRegex(@"\s*\n\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();
}
