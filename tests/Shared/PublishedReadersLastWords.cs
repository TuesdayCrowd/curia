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
    internal static string DefinitionSentence() => One(DefinitionPhrase(), G50(), "G50's definition of a digest-only entry");

    /// <summary>The tail of the definition that points at <c>recheck</c>, as <c>recheck</c>'s own clause (R10.89) says it: "recheck of that digest says whether it is withheld".</summary>
    internal static string PointerTail()
    {
        var sentence = DefinitionSentence();
        var at = sentence.IndexOf("; ", StringComparison.Ordinal);
        if (at < 0) throw new InvalidOperationException($"{Errata}'s definition sentence has no '; ' between its two parts: {sentence}");
        return sentence[(at + 2)..];
    }

    /// <summary>The same pointer in <c>recheck</c>'s own clause (R10.89: "of that digest"), for counting how often a line says it.</summary>
    internal static string PointerTailOfThatDigest() => PointerTail().Replace("of its digest", "of that digest", StringComparison.Ordinal);

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

    /// <summary>R11.66's version: the number after "`curia-mcp` is". The one reader of a version out of an errata paragraph (ruling 23(b)).</summary>
    internal static string Version() => One(VersionPhrase(), ReadWords(), "R11.66's version");

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
