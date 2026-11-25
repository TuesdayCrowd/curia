using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>
/// R10.99 and R11.103 (errata G64): what a search page says about which channel chose each result, from one definition. <c>curia search</c> and <c>curia_search</c> both ask it, so the count
/// line an agent reads over MCP is byte for byte the line a person reads at the CLI (R11.93's pattern: one definition, every surface asks it). It states no floor and no model: those are
/// <see cref="ModelWording"/>'s.
///
/// <para>Every value the Forum served is a display literal (R10.64): a mark this client does not know is quoted, never printed as it came. Every count the Forum did not serve reads
/// <c>not stated</c> in the number's place and never 0.</para>
/// </summary>
public static class SearchWording
{
    private const string WordsWire = "words";
    private const string MeaningWire = "meaning";
    private const string BothWire = "words_and_meaning";

    private const string NotStated = "not stated";

    /// <summary>R10.99: the first clause when no served result was chosen through the vector channel and the page has results.</summary>
    public const string NoVectorClause = "the vector channel chose none of them:";

    /// <summary>R10.99: the first clause when no served result has a word match and the page has results.</summary>
    public const string NoWordClause = "no result shares a word with the query:";

    /// <summary>R10.99: the beginning of the one line that says the marks tally differently from the counts served.</summary>
    public const string TallyClause = "the marks on this page tally differently from the counts served:";

    /// <summary>
    /// R10.99: what a mark means, said once for a page of a Forum that does not state <c>keyword_ignored</c> (it does not apply R9.72's list). A <c>words</c> result shares a whole word, a common
    /// word included; a meaning-only result shares none and its relation to the topic is not stated.
    /// </summary>
    public const string Gloss =
        "A result marked words shares a whole word with the query, a common word such as 'the' or 'of' included, so a page of only words results is what an unrelated query shows. " +
        MeaningGloss;

    /// <summary>
    /// R10.102: the gloss of a page that served <c>keyword_ignored</c> (a Forum that applies R9.72's list). A <c>words</c> result shares a whole word other than the common English words the
    /// keyword channel ignores; a word common on this board can still match, so a page of only words results does not show that the Forum holds the topic.
    /// </summary>
    public const string KeywordGloss =
        "A result marked words shares a whole word with the query other than the common English words the keyword channel ignores; a word that is common on this board can still match, so a page of only words results does not show that the Forum holds the topic. " +
        MeaningGloss;

    private const string MeaningGloss =
        "A result marked meaning only shares no whole word with the query: the vector channel chose it, and its relation to the topic is not stated.";

    /// <summary>R10.102: the words before the ignored words of the one line that says what the keyword channel ignored.</summary>
    public const string IgnoredLead = "the keyword channel ignored these common English words of the query:";

    /// <summary>R10.102: the words after them.</summary>
    public const string IgnoredTail = "(a published list; it matches no post by them)";

    /// <summary>R10.102: the most ignored words a reader prints. The Forum's list holds 111 words, so a longer array is not a list this Forum's R9.72 could have served; the rest is counted and not printed.</summary>
    public const int IgnoredPrintedMost = 111;

    /// <summary>R10.102's closing clause: what the ignored line ends with when the Forum stated that the keyword channel took no term. A constant, never built from a served word.</summary>
    public const string IgnoredWholeQueryClause =
        ". The whole query was made of them, so the keyword channel took no term and matched no post by words; search again with a word that names the subject";

    /// <summary>R10.99: what the score is.</summary>
    public const string ScoreSentence =
        "The score is the fusion score in millionths that orders the page; it is not a cosine and is not comparable with the vector floor.";

    /// <summary>The printed form of a result's mark: the client's words for the three wire words, and any other word the Forum served as a display literal (R10.64). Null when the Forum served none.</summary>
    public static string? Mark(string? matchedBy) => matchedBy switch
    {
        null => null,
        WordsWire => "words",
        MeaningWire => "meaning only",
        BothWire => "words and meaning",
        _ => DisplayLiteral.Of(matchedBy),
    };

    /// <summary>
    /// The count line of <paramref name="page"/>: <c>N post(s) on this page: X</c> and the first mark, then Y and the second, then Z and the third (the marks are <see cref="Mark"/>'s), all three clauses always, a count not served as <c>not stated</c>
    /// and never 0. When the counts are served and no result came from the vector channel, or none has a word match, the line begins with the one clause that says so.
    /// </summary>
    public static string CountLine(SearchPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var n = page.Results.IsDefault ? 0 : page.Results.Length;
        var counts = page.ByChannel;
        var line = string.Create(
            CultureInfo.InvariantCulture,
            $"{n} post(s) on this page: {Clause(counts?.Words, WordsWire)}, {Clause(counts?.Meaning, MeaningWire)}, {Clause(counts?.WordsAndMeaning, BothWire)}");

        return FirstClause(n, counts) is { } clause ? clause + " " + line : line;
    }

    /// <summary>The tally line (one line of its own) when the marks on the page disagree with a count the Forum served, or null when they agree or nothing was served to disagree with.</summary>
    public static string? TallyLine(SearchPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (page.ByChannel is not { } counts) return null;

        var marks = page.Results.IsDefault ? [] : page.Results.Select(r => r.MatchedBy).ToImmutableArray();
        var words = marks.Count(m => m == WordsWire);
        var meaning = marks.Count(m => m == MeaningWire);
        var both = marks.Count(m => m == BothWire);

        var differs = Differs(counts.Words, words) || Differs(counts.Meaning, meaning) || Differs(counts.WordsAndMeaning, both);
        if (!differs) return null;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{TallyClause} the results carry {Clause(words, WordsWire)}, {Clause(meaning, MeaningWire)} and {Clause(both, BothWire)}; the counts served are {Number(counts.Words)}, {Number(counts.Meaning)} and {Number(counts.WordsAndMeaning)}.");
    }

    /// <summary>R9.68's count, always stated: <c>diversified N post(s) ...</c> at 0 as at N, or <c>diversified: not stated</c> when the Forum served none.</summary>
    public static string DiversifiedLine(SearchPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        return page.Diversified is { } n
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"diversified {n} post(s) moved to a later page by the per-page share cap on one author or owner (or as a possible duplicate of a post already on the page); the cap is per page, so a larger --limit holds more of them")
            : "diversified: not stated";
    }

    /// <summary>
    /// R10.102: the one line that says which common English words the keyword channel ignored, or null when the page served none (an older Forum, an empty array). Each word is a display
    /// literal (R10.64) and at most <see cref="IgnoredPrintedMost"/> are printed, the rest being counted.
    /// </summary>
    public static string? IgnoredLine(SearchPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (page.KeywordIgnored is not { IsDefaultOrEmpty: false } words) return null;

        var shown = string.Join(", ", words.Take(IgnoredPrintedMost).Select(DisplayLiteral.Of));
        var more = words.Length > IgnoredPrintedMost
            ? string.Create(CultureInfo.InvariantCulture, $", and {words.Length - IgnoredPrintedMost} more not printed")
            : string.Empty;
        var closing = page.KeywordTerms == 0 ? IgnoredWholeQueryClause : string.Empty;
        return $"{IgnoredLead} {shown}{more} {IgnoredTail}{closing}";
    }

    /// <summary>
    /// What a page says once, before its first result (R10.99, R10.102): the ignored-words line when the page served some, the count line, the tally line when it applies, the diversified line,
    /// the gloss (R10.102's for a page that served <c>keyword_ignored</c>, R10.99's otherwise) and the score sentence. A page with no result has nothing to mark and says none of that (the
    /// readers' own "no results" says it), except the ignored-words line, the one thing a page after an all-ignored query has to say. A reader writes each as one line (the CLI) or one run of
    /// text (the MCP block).
    /// </summary>
    public static ImmutableArray<string> PageLines(SearchPage page)
    {
        ArgumentNullException.ThrowIfNull(page);

        var lines = ImmutableArray.CreateBuilder<string>();
        if (IgnoredLine(page) is { } ignored) lines.Add(ignored);

        if (page.Results.IsDefaultOrEmpty) return lines.ToImmutable();

        lines.Add(CountLine(page));
        if (TallyLine(page) is { } tally) lines.Add(tally);
        lines.Add(DiversifiedLine(page));
        lines.Add(page.KeywordIgnored is null ? Gloss : KeywordGloss);
        lines.Add(ScoreSentence);

        return lines.ToImmutable();
    }

    /// <summary>One clause of the count line: the count (or <c>not stated</c>) and the mark's printed words, so a clause and a result's mark are one definition.</summary>
    private static string Clause(int? served, string wire) => $"{Number(served)} by {Mark(wire)}";

    private static string Number(int? served) => served is { } n ? n.ToString(CultureInfo.InvariantCulture) : NotStated;

    private static bool Differs(int? served, int marked) => served is { } n && n != marked;

    private static string? FirstClause(int n, ChannelCounts? counts)
    {
        if (n == 0 || counts is not { Words: { } words, Meaning: { } meaning, WordsAndMeaning: { } both }) return null;

        if (meaning + both == 0 && words > 0) return NoVectorClause;
        if (words + both == 0 && meaning > 0) return NoWordClause;
        return null;
    }
}
