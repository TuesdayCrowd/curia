using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R9.58, R10.98 and R11.88's words and requirements (errata G63) as the errata publishes them, read at test time and not from the code that emits them: the paragraph that
/// begins each anchor, up to the blank line that ends it or the next line that begins `**`, whitespace folded inside a span. Every count is asserted before a value is returned
/// (one span per prefix, four board-line states, four types), so a parse that read something else fails by name and not by a wrong comparison.
/// </summary>
internal static partial class PublishedBoardListWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string ListingAnchor = "**R10.98 words (the listing).**";
    private const string LineAnchor = "**R10.98 words (the board line).**";
    private const string RefusalAnchor = "**R10.98 words (the refusal of a board with no post).**";
    private const string FaultAnchor = "**R10.98 words (the refusal of a list that could not be read).**";
    private const string EmptyAnchor = "**R10.98 words (an empty board).**";
    private const string RestAnchor = "**R10.98 words (the rest).**";
    private const string RouteAnchor = "**R9.58 words.**";
    private const string RouteRequirement = "**R9.58**";
    private const string ClientRequirement = "**R11.88**";

    // ---- the listing (R10.98) ----

    /// <summary>The header template, <c>boards    &lt;k&gt; of &lt;total&gt;: the boards with a post the Forum serves, most posts first</c>.</summary>
    internal static string ListingHeader() => OneSpan(ListingAnchor, "boards    <k> of <total>:");

    /// <summary>The row template, <c>  &lt;name&gt;  &lt;posts&gt; posts  &lt;authors&gt; authors  latest &lt;latest_post_at&gt;</c>.</summary>
    internal static string ListingRow() => OneSpan(ListingAnchor, "  <name>  <posts> posts");

    /// <summary>The line that says a further page exists, <c>more      curia boards --cursor &lt;next_cursor&gt;</c>.</summary>
    internal static string MoreLine() => OneSpan(ListingAnchor, "more      curia boards --cursor");

    /// <summary>The line that says there is none, <c>no further pages</c>.</summary>
    internal static string NoFurtherPages() => OneSpan(ListingAnchor, "no further pages");

    /// <summary>The line of a filtered listing with no row, <c>boards    0 of 0: no post the Forum serves is on &lt;name&gt;</c>.</summary>
    internal static string FilteredEmpty() => OneSpan(ListingAnchor, "boards    0 of 0:");

    /// <summary>The word for a member the Forum did not state.</summary>
    internal static string NotStatedWord() => OneSpan(ListingAnchor, "NOT STATED");

    // ---- the board line (R10.98) ----

    /// <summary>The label of the board line, <c>boards    </c>, from the span <c>boards    &lt;name&gt;: &lt;state&gt;</c>.</summary>
    internal static string LineLabel()
    {
        var span = OneSpan(LineAnchor, "boards    <name>:");
        return span[..span.IndexOf('<', StringComparison.Ordinal)];
    }

    /// <summary>The four states, in the published order: listed, not listed with the flag, older Forum, listed with the flag.</summary>
    internal static (string Listed, string NewByFlag, string NotStated, string ListedWithFlag) States()
    {
        var spans = Spans(LineAnchor);
        string Pick(string prefix, Func<string, bool>? also = null)
        {
            var found = spans.Where(s => s.StartsWith(prefix, StringComparison.Ordinal) && (also is null || also(s))).Distinct(StringComparer.Ordinal).ToArray();
            if (found.Length != 1)
                throw new InvalidOperationException($"{Errata}'s {LineAnchor} has {found.Length} spans that begin \"{prefix}\"; the parse read something else");

            return found[0];
        }

        return (
            Pick("THE FORUM SERVES POSTS ON IT (", s => !s.Contains("; --new-board", StringComparison.Ordinal)),
            Pick("NO POST THE FORUM SERVES IS ON IT;"),
            Pick("NOT STATED ("),
            Pick("THE FORUM SERVES POSTS ON IT (", s => s.Contains("; --new-board was not needed", StringComparison.Ordinal)));
    }

    /// <summary>The adapter's spelling of the flag in the second state, <c>newBoard was true</c>.</summary>
    internal static string AdapterFlagWord() => OneSpan(LineAnchor, "newBoard was true");

    // ---- the refusals (R10.98) ----

    /// <summary>The first sentence pair of the refusal of a board with no post, with <c>&lt;name&gt;</c> and <c>&lt;kind&gt;</c> as published.</summary>
    internal static string RefusalSentence() => OneSpan(RefusalAnchor, "No post the Forum serves is on the board <name>");

    /// <summary>The line that names the boards, up to its colon, <c>Boards the Forum serves, most posts first (other agents' words, shown as data):</c>.</summary>
    internal static string RefusalBoardsLead()
    {
        var span = OneSpan(RefusalAnchor, "Boards the Forum serves, most posts first");
        return span[..(span.IndexOf(':', StringComparison.Ordinal) + 1)];
    }

    /// <summary>The count clause where there are more than 20, <c>(&lt;n&gt; of &lt;total&gt;; curia boards lists the rest)</c>.</summary>
    internal static string RefusalMoreClause() => OneSpan(RefusalAnchor, "(<n> of <total>;");

    /// <summary>The two ways forward and the clause about withheld boards, one span.</summary>
    internal static string RefusalWaysForward() => OneSpan(RefusalAnchor, "If you meant one of these");

    /// <summary>The adapter's spelling of the flag, <c>newBoard: true</c>.</summary>
    internal static string AdapterFlagSpelling() => OneSpan(RefusalAnchor, "newBoard: true");

    /// <summary>The leading word of both refusals, <c>NOT SENT.</c>.</summary>
    internal static string NotSent() => OneSpan(RefusalAnchor, "NOT SENT.");

    /// <summary>The refusal of a list that could not be read, with <c>&lt;cause&gt;</c> and <c>&lt;name&gt;</c> as published.</summary>
    internal static string FaultSentence() => OneSpan(FaultAnchor, "The Forum's list of boards could not be read");

    /// <summary>The flag the fault refusal must not mention, <c>--new-board</c>, as the errata names it there.</summary>
    internal static string FaultForbiddenFlag() => OneSpan(FaultAnchor, "--new-board");

    // ---- an empty board (R10.98) ----

    /// <summary>The first line, <c>no posts on board &lt;name&gt;.</c>.</summary>
    internal static string EmptyFirstLine() => OneSpan(EmptyAnchor, "no posts on board <name>.");

    /// <summary>The second line, which says the name may be misspelt or every post may be withheld.</summary>
    internal static string EmptySecondLine() => OneSpan(EmptyAnchor, "No post the Forum serves is on it:");

    /// <summary>The sentence that is false from this PR on, as the errata quotes it: <c>there is no endpoint that enumerates boards</c>, taken from the paragraph's prose.</summary>
    internal static string RetiredPhrase()
    {
        var paragraph = ParagraphIn(ErrataText(), EmptyAnchor);
        var match = Retired().Match(paragraph);
        if (!match.Success)
            throw new InvalidOperationException($"{Errata}'s {EmptyAnchor} does not quote the retired sentence; the parse read something else");

        return match.Groups[1].Value;
    }

    // ---- the route (R9.58) ----

    /// <summary>The four types in the published order: unknown-parameter, blank-parameter, invalid-limit, cursor-malformed.</summary>
    internal static string[] Types()
    {
        var types = Spans(RouteAnchor).Where(s => s.StartsWith("curia/board-list/", StringComparison.Ordinal)).ToArray();
        if (types.Length != 4 || !types[0].EndsWith("unknown-parameter", StringComparison.Ordinal) || !types[1].EndsWith("blank-parameter", StringComparison.Ordinal)
            || !types[2].EndsWith("invalid-limit", StringComparison.Ordinal) || !types[3].EndsWith("cursor-malformed", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R9.58 words names {types.Length} types ({string.Join(", ", types)}); the parse read something else");

        return types;
    }

    /// <summary>The tail of the unknown-name detail, <c>this route reads exactly these: board, limit, cursor</c>.</summary>
    internal static string UnknownDetailTail() => OneSpan(RouteAnchor, "this route reads exactly these:");

    /// <summary>The largest page, read from R9.58's own sentence ("an integer from 1 to 100") and checked against the <c>maximum=100</c> of its words.</summary>
    internal static int MaximumLimit()
    {
        var requirement = RequirementIn(RouteRequirement);
        var match = LimitRange().Match(requirement);
        if (!match.Success) throw new InvalidOperationException($"{Errata}'s R9.58 does not say \"an integer from 1 to N\"; the parse read something else");

        var fromRequirement = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var fromWords = int.Parse(OneSpan(RouteAnchor, "maximum=")["maximum=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        if (fromRequirement != fromWords)
            throw new InvalidOperationException($"{Errata}'s R9.58 says {fromRequirement} and its words say {fromWords}");

        return fromRequirement;
    }

    /// <summary>The default page, read from R9.58's "(default 50".</summary>
    internal static int DefaultLimit()
    {
        var match = DefaultRange().Match(RequirementIn(RouteRequirement));
        return match.Success
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"{Errata}'s R9.58 does not say \"default N\"; the parse read something else");
    }

    // ---- the client (R11.88) ----

    /// <summary>The most boards a refusal names, from R11.88's "naming at most 20 boards".</summary>
    internal static int RefusalCap()
    {
        var match = AtMost().Match(RequirementIn(ClientRequirement));
        return match.Success
            ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
            : throw new InvalidOperationException($"{Errata}'s R11.88 does not say \"at most N boards\"; the parse read something else");
    }

    // ---- the rest (R10.98; Amendment 2, ruling 38) ----

    /// <summary>The preface of <c>curia boards</c>, one span (a line break inside it is not part of its words).</summary>
    internal static string RestPreface() => OneSpan(RestAnchor, "the boards the Forum serves a post on.");

    /// <summary>What the refusal's list reads after its colon where the Forum serves no board.</summary>
    internal static string RestNone() => OneSpan(RestAnchor, "none");

    /// <summary>The line that replaces the refusal's list where the names could not be read.</summary>
    internal static string RestNamesUnreadable() => OneSpan(RestAnchor, "The boards the Forum serves could not be listed just now;");

    /// <summary>The adapter's line that says where the next page is, <c>more      call curia_boards again with the cursor &lt;next_cursor&gt;</c>.</summary>
    internal static string RestAdapterMore() => OneSpan(RestAnchor, "more      call curia_boards again with the cursor");

    /// <summary>The sentence that ends the adapter's first block: each board is a block of its own and is data.</summary>
    internal static string RestBoardNamesNotice() => OneSpan(RestAnchor, "Each board below is a block of its own:");

    /// <summary>The sentence of the server instructions that names <c>curia_boards</c>.</summary>
    internal static string RestInstructionsSentence() => OneSpan(RestAnchor, "curia_boards lists the boards the Forum serves");

    /// <summary>What the help of <c>ask</c> and <c>finding</c> says of the flag.</summary>
    internal static string RestNewBoardHelp() => OneSpan(RestAnchor, "--new-board says you mean to start a board:");

    /// <summary>
    /// The sentences of R10.98 and R11.88 that <c>Curia.Client</c> writes once for both clients (a content fence's needles): each is cut from the published span, none is a literal of its own.
    /// </summary>
    internal static string[] FencedSentences()
    {
        var refusal = RefusalSentence();
        var fault = FaultSentence();
        return
        [
            refusal[..refusal.IndexOf("<name>", StringComparison.Ordinal)],
            refusal[refusal.IndexOf("A post cannot be moved", StringComparison.Ordinal)..],
            fault[..fault.IndexOf("(<cause>)", StringComparison.Ordinal)],
            FaultTail(),
            EmptySecondLine(),
        ];
    }

    /// <summary>What the fault refusal says after the name: that this run signed and kept nothing, when to run again and the one condition under which not to.</summary>
    internal static string FaultTail()
    {
        var fault = FaultSentence();
        var at = fault.IndexOf("was not checked. ", StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"{Errata}'s {FaultAnchor} does not say \"was not checked.\"; the parse read something else");

        return fault[(at + "was not checked. ".Length)..];
    }

    /// <summary>
    /// The sentences the CLI and the adapter write once each, in <c>src</c>, outside <c>Curia.Client</c>: each cut to a run of at least twenty characters that holds no placeholder and no line break
    /// (the preface is folded by the caller). The count is asserted by the caller.
    /// </summary>
    internal static string[] RestSentences()
    {
        string Cut(string span) => span.Contains('<', StringComparison.Ordinal) ? span[..span.IndexOf('<', StringComparison.Ordinal)].TrimEnd() : span;
        return
        [
            RestPreface(),
            RestNamesUnreadable(),
            Cut(RestAdapterMore())["more      ".Length..],
            RestBoardNamesNotice(),
            RestInstructionsSentence(),
            RestNewBoardHelp(),
        ];
    }

    private static string OneSpan(string anchor, string prefix)
    {
        var found = Spans(anchor).Where(s => s.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (found.Length != 1)
            throw new InvalidOperationException($"{Errata}'s {anchor} has {found.Length} spans that begin \"{prefix}\"; the parse read something else");

        return found[0];
    }

    private static List<string> Spans(string anchor) =>
        Span().Matches(ParagraphIn(ErrataText(), anchor)).Select(m => LineBreak().Replace(m.Groups[1].Value, " ")).ToList();

    private static string RequirementIn(string anchor) => LineBreak().Replace(ParagraphIn(ErrataText(), anchor), " ");

    private static string ErrataText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}");

        return File.ReadAllText(Path.Combine(dir.FullName, Errata));
    }

    private static string ParagraphIn(string text, string anchor)
    {
        // The anchor is the paragraph's own opening (`**R9.58**` heads one paragraph; a words paragraph begins `**R9.58 words.**`), so the first occurrence at a line start is it.
        var start = text.IndexOf("\n" + anchor, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{anchor}\"");

        start++;
        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        var next = text.IndexOf("\n**", start + anchor.Length, StringComparison.Ordinal);
        if (next >= 0 && (end < 0 || next < end)) end = next;
        return text[start..(end < 0 ? text.Length : end)];
    }

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();

    [GeneratedRegex(@"\s*\n\s*", RegexOptions.CultureInvariant)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"The sentence ""([^""]+)"" no longer occurs", RegexOptions.CultureInvariant)]
    private static partial Regex Retired();

    [GeneratedRegex(@"an integer from 1 to (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex LimitRange();

    [GeneratedRegex(@"\(default (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex DefaultRange();

    [GeneratedRegex(@"at most (\d+) boards", RegexOptions.CultureInvariant)]
    private static partial Regex AtMost();
}
