using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words of errata G50 (R7.30, R8.80, R10.91, R10.92, R11.66, R11.67, R11.68) as the errata publishes them, read at test time from
/// <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never from the code a probe is about (PR 3, ruling 10(a)): the reasons, the member names, the
/// kind words, the definition sentence, the spans, the bound and the version. Each requirement is read through
/// <see cref="PublishedAcceptBudget.Paragraph(string)"/>, so an edit that removes a span turns a probe red and does not shrink what it compares; each
/// parse asserts its count before it returns. <b>The one reader of a <c>curia-mcp</c> version out of an errata paragraph</b> is <see cref="Version"/>
/// (ruling 23(b)); the two older readers, <c>PublishedBudgetReadout.Version()</c> and <c>PublishedChainWords.Version()</c>, are replaced by it when the
/// version moves, and RLW40 holds that nothing else in <c>tests/</c> reads one.
/// </summary>
internal static partial class PublishedReadersLastWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    internal static string OwnResource() => PublishedAcceptBudget.Paragraph("**R7.30**");

    internal static string Receipt() => PublishedAcceptBudget.Paragraph("**R8.80**");

    internal static string Definition() => PublishedAcceptBudget.Paragraph("**R10.91**");

    internal static string ReceiptLine() => PublishedAcceptBudget.Paragraph("**R10.92**");

    internal static string ReadWords() => PublishedAcceptBudget.Paragraph("**R11.66**");

    internal static string Walk() => PublishedAcceptBudget.Paragraph("**R11.67**");

    internal static string Batch() => PublishedAcceptBudget.Paragraph("**R11.68**");

    /// <summary>The seven requirements' anchors, in the order the errata gives them, for the control that holds them present (RLW41).</summary>
    internal static readonly string[] Anchors = ["**R7.30**", "**R8.80**", "**R10.91**", "**R10.92**", "**R11.66**", "**R11.67**", "**R11.68**"];

    /// <summary>The entry's index rows, one per requirement, as the errata's consolidated index writes them.</summary>
    internal static readonly string[] IndexRows = ["| R7.30 |", "| R8.80 |", "| R10.91 |", "| R10.92 |", "| R11.66 |", "| R11.67 |", "| R11.68 |"];

    // ----- R7.30 -----

    /// <summary>R7.30's new reason: the code span that follows "SHALL refuse with the reason".</summary>
    internal static string OwnResourceFound() => One(FoundReason(), OwnResource(), "R7.30's new reason");

    /// <summary>R7.30's shared reason, which a route that has not looked keeps: the code span that follows "SHALL keep the reason".</summary>
    internal static string OwnResourceOnly() => One(OnlyReason(), OwnResource(), "R7.30's kept reason");

    /// <summary>The new reason's title, as the entry's decision 1 quotes it: <c>titled "..."</c> after the reason.</summary>
    internal static string FoundTitle() => One(Title(), G50(), "G50's title of the new reason");

    // ----- R8.80 -----

    /// <summary>R8.80's two members, in the order the paragraph gives them: the kind and the answer whose text the post is.</summary>
    internal static (string Kind, string TextOf) ReceiptMembers()
    {
        var match = Members().Matches(Receipt());
        if (match.Count != 1) throw new InvalidOperationException($"{Errata}'s R8.80 yields {match.Count} readings of its two members, expected one");
        return (match[0].Groups[1].Value, match[0].Groups[2].Value);
    }

    /// <summary>R8.80's two kind words: <c>answer</c> and <c>revision</c>, as "which is `answer` or `revision`".</summary>
    internal static (string Answer, string Revision) KindWords()
    {
        var match = Kinds().Matches(Receipt());
        if (match.Count != 1) throw new InvalidOperationException($"{Errata}'s R8.80 yields {match.Count} readings of its kind words, expected one");
        return (match[0].Groups[1].Value, match[0].Groups[2].Value);
    }

    // ----- R10.91 -----

    /// <summary>R10.91's definition of a digest-only entry, as decision 3 of the entry writes it: the span after "The words are written once".</summary>
    internal static string DefinitionSentence()
    {
        // Read from R10.91's words paragraph (the mark without its parentheses) and held equal to decision 3's own statement of it.
        var mark = DigestOnlyMarks().Full;
        var sentence = mark[1..^1];
        var decision = One(DefinitionPhrase(), G50(), "G50's definition of a digest-only entry");
        return sentence == decision ? sentence : throw new InvalidOperationException($"{Errata}: R10.91's mark \"{sentence}\" and decision 3's \"{decision}\" differ");
    }

    /// <summary>The tail of the definition that points at <c>recheck</c>, as <c>recheck</c>'s own clause (R10.89) says it: "recheck of that digest says whether it is withheld".</summary>
    internal static string PointerTail()
    {
        var sentence = DefinitionSentence();
        var at = sentence.IndexOf("; ", StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"{Errata}'s definition sentence has no '; ' between its two parts: {sentence}");
        return sentence[(at + 2)..];
    }

    /// <summary>The same pointer in <c>recheck</c>'s own clause (R10.89: "of that digest"), for counting how often a line says it.</summary>
    internal static string PointerTailOfThatDigest()
    {
        var clause = DigestOnlyMarks().Clause;
        return clause[(clause.IndexOf("; ", StringComparison.Ordinal) + 2)..];
    }

    /// <summary>The words R10.91 forbids a reader to say of a digest-only entry: the span <c>withheld</c> used as a claim. The probes look for "is withheld" outside the pointer.</summary>
    internal static string ForbiddenClaim() => "is withheld";

    // ----- R10.92 -----

    /// <summary>R10.92's word for an absent kind: the code span <c>kind NOT STATED</c>.</summary>
    internal static string KindNotStated() => One([.. Spans(ReceiptLine()).Where(t => t.StartsWith("kind NOT", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)], "R10.92's kind NOT STATED");

    /// <summary>R10.92's phrase for what a revision's line names: "the answer it is the text of".</summary>
    internal static string TextOfPhrase() => One(TextOfWords(), ReceiptLine(), "R10.92's text-of phrase");

    // ----- R11.66 -----

    /// <summary>R11.66's three first-block words: the thread that could not be read, the thread read that does not list the post, and the cut.</summary>
    internal static (string NotRead, string NotListed, string Cut) ReadLines()
    {
        var spans = Spans(ReadWords()).ToArray();
        return (
            One([.. spans.Where(t => t.StartsWith("NOT READ", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)], "R11.66's NOT READ"),
            One([.. spans.Where(t => t == "NOT LISTED").Distinct(StringComparer.Ordinal)], "R11.66's NOT LISTED"),
            One([.. spans.Where(t => t == "CUT").Distinct(StringComparer.Ordinal)], "R11.66's CUT"));
    }

    /// <summary>R11.66's cause for <c>NOT READ</c>: the words after the ellipsis of its span ("could not be read").</summary>
    internal static string NotReadCause()
    {
        var span = ReadLines().NotRead;
        var at = span.IndexOf("... ", StringComparison.Ordinal);
        return at < 0 ? throw new InvalidOperationException($"{Errata}'s R11.66 NOT READ span has no cause after an ellipsis: {span}") : span[(at + 4)..];
    }

    /// <summary>R11.66's cause for <c>NOT LISTED</c>: "does not list the post", and its statement that the thread "was read".</summary>
    internal static (string DoesNotList, string WasRead) NotListedCause()
    {
        string Phrase(string text) => ReadWords().Contains(text, StringComparison.Ordinal) ? text : throw new InvalidOperationException($"{Errata}'s R11.66 no longer holds \"{text}\"");
        return (Phrase("does not list the post"), Phrase("was read"));
    }

    /// <summary>R11.66's tool the note points at: the code span <c>curia_recheck</c>.</summary>
    internal static string NoteTool() => One([.. Spans(ReadWords()).Where(t => t == "curia_recheck").Distinct(StringComparer.Ordinal)], "R11.66's tool");

    /// <summary>R11.66's note: the clause that the Forum places a revision by its correction chain and not by the <c>parent</c> its author wrote.</summary>
    internal static string PlacementClause()
    {
        const string clause = "places a revision by its correction chain";
        return ReadWords().Contains(clause, StringComparison.Ordinal) ? clause : throw new InvalidOperationException($"{Errata}'s R11.66 no longer holds \"{clause}\"");
    }

    /// <summary>
    /// The version: the number after "`curia-mcp` is" in R11.72's paragraph (errata G52). The one reader of a version out of an errata paragraph (ruling 23(b)); it
    /// reads the paragraph of the entry that last bumped the version, so each bump re-points it (it read R11.70's until the D95 PR's Task 0b, which re-pointed it at the
    /// entry that moves the version to 0.9.3, so that the facts that read it are red until the version moves: ULG31).
    /// </summary>
    internal static string Version() => One(VersionPhrase(), PublishedAcceptBudget.Paragraph("**R11.72**"), "R11.72's version");

    // ----- R11.67 -----

    /// <summary>R11.67's bound as a number, from "reading at most eight posts after the question" (the sentence, in context, and no other `8`).</summary>
    internal static int WalkBound()
    {
        var word = One(BoundPhrase(), Walk(), "R11.67's bound");
        return word switch
        {
            "eight" => 8,
            _ => int.TryParse(word, CultureInfo.InvariantCulture, out var n) ? n : throw new InvalidOperationException($"{Errata}'s R11.67 bound is \"{word}\""),
        };
    }

    /// <summary>R11.67's bound sentence as a phrase a description carries: "at most eight posts after the question".</summary>
    internal static string BoundSentence() => One(BoundSentencePhrase(), Walk(), "R11.67's bound sentence");

    /// <summary>R11.67's three words: <c>NOT WALKED</c>, <c>NOT CHECKED</c> and <c>FORKED</c>, each a code span or an all-caps word the paragraph uses.</summary>
    internal static (string NotWalked, string NotChecked, string Forked) WalkWords()
    {
        var spans = Spans(Walk()).ToArray();
        string Word(string text) => Walk().Contains(text, StringComparison.Ordinal) ? text : throw new InvalidOperationException($"{Errata}'s R11.67 no longer holds {text}");
        return (
            One([.. spans.Where(t => t == "NOT WALKED").Distinct(StringComparer.Ordinal)], "R11.67's NOT WALKED"),
            One([.. spans.Where(t => t == "NOT CHECKED").Distinct(StringComparer.Ordinal)], "R11.67's NOT CHECKED"),
            Word("FORKED"));
    }

    /// <summary>R11.67's statement of a fork: "none was followed".</summary>
    internal static string NoneFollowed() => One(FollowedPhrase(), Walk(), "R11.67's none-followed statement");

    /// <summary>R11.67's phrase for what a chain's walk states: "the latest revision the Forum reports".</summary>
    internal static string LatestPhrase() => One(LatestRevision(), Walk(), "R11.67's latest-revision phrase");

    // ----- R11.68 -----

    /// <summary>R11.68's three clauses, in the order given, each as the paragraph writes it after "SHALL say that" (backticks removed): the heads differ, the batch is fetched afresh, and the way to one verdict.</summary>
    internal static string[] BatchClauses()
    {
        var paragraph = Batch().Replace("`", string.Empty, StringComparison.Ordinal);
        var match = ClausesPhrase().Matches(paragraph);
        if (match.Count != 1) throw new InvalidOperationException($"{Errata}'s R11.68 yields {match.Count} readings of its three clauses, expected one");
        return [match[0].Groups[1].Value, match[0].Groups[2].Value, match[0].Groups[3].Value];
    }

    // ----- the published words (errata G50, "Published words") -----

    /// <summary>
    /// The whole "words" paragraph of <paramref name="requirement"/> (for example <c>R11.67</c>): from <c>**R11.67 words.**</c> to the next bold paragraph
    /// or heading, bullets included, backticks kept, whitespace folded. Throws where the paragraph is absent.
    /// </summary>
    internal static string WordsParagraph(string requirement)
    {
        var text = ErrataText();
        var anchor = $"**{requirement} words.**";
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{anchor}\"");
        var end = text.IndexOf("\n\n**R", start + anchor.Length, StringComparison.Ordinal);
        var heading = text.IndexOf("\n### ", start + anchor.Length, StringComparison.Ordinal);
        if (end < 0 || (heading >= 0 && heading < end)) end = heading;
        return Whitespace().Replace(text[start..(end < 0 ? text.Length : end)], " ");
    }

    /// <summary>The code spans of <paramref name="requirement"/>'s words paragraph that begin with <paramref name="prefix"/>; exactly one is expected by <see cref="OneSpan"/>.</summary>
    internal static string OneSpan(string requirement, string prefix) =>
        One([.. Spans(WordsParagraph(requirement)).Where(t => t.StartsWith(prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal)], $"{requirement}'s words span beginning \"{prefix}\"");

    /// <summary>
    /// True where <paramref name="line"/> (whitespace folded) holds <paramref name="published"/>, a span that may carry <c>&lt;id&gt;</c> or <c>&lt;digest&gt;</c> placeholders,
    /// each standing for any run of text.
    /// </summary>
    internal static bool Says(string line, string published)
    {
        var parts = Placeholder().Split(published).Select(Regex.Escape);
        return new Regex(string.Join(".+?", parts), RegexOptions.CultureInvariant).IsMatch(Whitespace().Replace(line, " "));
    }

    /// <summary>R7.30's two advices: the one after <c>own-resource-found</c> and the one for the shared reason.</summary>
    internal static (string Found, string Shared) OwnResourceAdvice() =>
        (OneSpan("R7.30", "This act is open only to the agent that owns the resource it names. The resource exists"),
            OneSpan("R7.30", "This act is open only to the agent that owns the resource it names, and"));

    /// <summary>R10.91's two marks: the full one and the bare one <c>recheck</c> keeps, and <c>recheck</c>'s clause.</summary>
    internal static (string Full, string Bare, string Clause) DigestOnlyMarks() =>
        (OneSpan("R10.91", "(not served; recheck of its digest"), OneSpan("R10.91", "(not served)"), OneSpan("R10.91", "not served; recheck of that digest"));

    /// <summary>R10.92's three kind lines (answer, revision lead, NOT STATED) and the revision's trailing text-of clause.</summary>
    internal static (string Answer, string Revision, string NotStated, string TextOf, string TextOfNotStated) KindLines() =>
        (OneSpan("R10.92", "kind answer"), OneSpan("R10.92", "kind revision"), OneSpan("R10.92", "kind NOT STATED"),
            OneSpan("R10.92", "; the answer it is the text of: post"), OneSpan("R10.92", "; the answer it is the text of: NOT STATED"));

    /// <summary>R11.66's cause where the Forum answered and served no posts.</summary>
    internal static string ServedNoPosts() => OneSpan("R11.66", "the Forum served no posts");

    /// <summary>R11.66's note head and its three endings (verified with a prev, verified without, not verified).</summary>
    internal static (string Head, string WithPrev, string NoPrev, string NotVerified) PlacementNote() =>
        (OneSpan("R11.66", "the Forum places a revision"), OneSpan("R11.66", "the post's signed prev states"), OneSpan("R11.66", "the post's signed form states no prev"),
            OneSpan("R11.66", "this post did not pass verification"));

    /// <summary>R11.67's chain end: <c>the latest revision the Forum reports is post "&lt;id&gt;"</c>, and the phrase that alone names a latest revision.</summary>
    internal static (string End, string Phrase) ChainEnd()
    {
        var end = OneSpan("R11.67", "the latest revision the Forum reports is post");
        return (end, end[..end.IndexOf(" is post", StringComparison.Ordinal)]);
    }

    /// <summary>R11.67's fork line.</summary>
    internal static string ForkLine() => OneSpan("R11.67", "FORKED:");

    /// <summary>R11.67's stop clauses, in the order the paragraph lists them; the bound's is <see cref="BoundStop"/>.</summary>
    internal static string[] Stops()
    {
        string[] prefixes =
        [
            "the walk ended there:", "post \"<id>\" served no word", "the walk stopped there: the post served for the next revision does not hash",
            "the walk stopped there: the post served for the next revision is not signed", "the walk stopped there: the signed prev", "the walk stopped there: the next revision named",
            "the walk stopped there: the next revision could not be read",
        ];
        return [.. prefixes.Select(p => OneSpan("R11.67", p))];
    }

    /// <summary>R11.67's line where the bound is spent, the only one that says <c>NOT WALKED</c>.</summary>
    internal static string BoundStop() => OneSpan("R11.67", "NOT WALKED past post");

    /// <summary>R11.67's CLI line where the read of <c>--parent</c> failed.</summary>
    internal static string QuestionNotChecked() => OneSpan("R11.67", "NOT CHECKED (");

    /// <summary>R11.67's receipt closing parenthesis: where the walk confirmed a post, and where it confirmed none.</summary>
    internal static (string Read, string None) Closings() =>
        (OneSpan("R11.67", "(the Forum's word as of the read before this answer: each digest"), OneSpan("R11.67", "(the Forum's word as of the read before this answer: no post"));

    /// <summary>R11.68's sentence, as the description and the help carry it.</summary>
    internal static string BatchSentence() => OneSpan("R11.68", "Note that verifications sent together");

    // ----- text helpers -----

    /// <summary><paramref name="text"/> with backticks removed and whitespace folded to single spaces, for comparing a published clause with a description's line breaks.</summary>
    internal static string Folded(string text) => Whitespace().Replace(text.Replace("`", string.Empty, StringComparison.Ordinal), " ");

    /// <summary>The code spans of <paramref name="paragraph"/>, in order.</summary>
    internal static IEnumerable<string> Spans(string paragraph) => Span().Matches(paragraph).Select(m => m.Groups[1].Value);

    /// <summary>Every consolidated-index row of G50 that is in the errata, by its first cell, so a control can hold them present.</summary>
    internal static string[] IndexRowsPresent()
    {
        var text = ErrataText();
        return [.. IndexRows.Where(row => text.Contains("\n" + row, StringComparison.Ordinal))];
    }

    /// <summary>G50's section: from its heading to the next entry's, whitespace folded.</summary>
    internal static string G50()
    {
        var text = ErrataText();
        var start = text.IndexOf("\n## G50 ", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{Errata} has no G50 section");
        var end = text.IndexOf("\n## G5", start + 1, StringComparison.Ordinal);
        return Whitespace().Replace(text[start..(end < 0 ? text.Length : end)], " ");
    }

    private static string ErrataText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}")
            : File.ReadAllText(Path.Combine(dir.FullName, Errata));
    }

    private static string One(Regex pattern, string paragraph, string what)
    {
        var matches = pattern.Matches(paragraph);
        if (matches.Count != 1) throw new InvalidOperationException($"{Errata} yields {matches.Count} readings of {what}, expected one");
        return matches[0].Groups[1].Value;
    }

    private static string One(string[] found, string what)
    {
        if (found.Length != 1) throw new InvalidOperationException($"{Errata} holds {found.Length} of {what} ({string.Join(", ", found)}); expected one");
        return found[0];
    }

    [GeneratedRegex(@"<(?:id|digest)>", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();

    [GeneratedRegex(@"SHALL refuse with the reason `(table-10/[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex FoundReason();

    [GeneratedRegex(@"SHALL keep the reason `(table-10/[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex OnlyReason();

    [GeneratedRegex(@"`table-10/own-resource-found`, titled ""([^""]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex Title();

    [GeneratedRegex(@"SHALL carry two members, always present: `([a-z_]+)`.*?and `([a-z_]+)`, the post id", RegexOptions.CultureInvariant)]
    private static partial Regex Members();

    [GeneratedRegex(@"which is `(answer)` or `(revision)`", RegexOptions.CultureInvariant)]
    private static partial Regex Kinds();

    [GeneratedRegex(@"The words are written once \(`([^`]+)`\)", RegexOptions.CultureInvariant)]
    private static partial Regex DefinitionPhrase();

    [GeneratedRegex(@"for a revision, (the answer it is the text of)", RegexOptions.CultureInvariant)]
    private static partial Regex TextOfWords();

    [GeneratedRegex(@"`curia-mcp` is (\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPhrase();

    [GeneratedRegex(@"reading at most (\w+) posts after the question", RegexOptions.CultureInvariant)]
    private static partial Regex BoundPhrase();

    [GeneratedRegex(@"(at most \w+ posts after the question)", RegexOptions.CultureInvariant)]
    private static partial Regex BoundSentencePhrase();

    [GeneratedRegex(@"with the statement that (none was followed)", RegexOptions.CultureInvariant)]
    private static partial Regex FollowedPhrase();

    [GeneratedRegex(@"state in the receipt (the latest revision the Forum reports)", RegexOptions.CultureInvariant)]
    private static partial Regex LatestRevision();

    [GeneratedRegex(@"SHALL say that (.+?), that (.+?), and that (.+?)\.", RegexOptions.CultureInvariant)]
    private static partial Regex ClausesPhrase();
}
