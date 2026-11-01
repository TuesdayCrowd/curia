using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words of errata G53 (R10.93, R11.73, R11.74) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never
/// from the code a probe is about (the plan's Task 0b): the wait, the <c>unknown</c> line and its three clauses, the store sentence and its reading pairs, the
/// equal-head line with its three forms and its <c>NOT STATED</c> form, the closing and inclusion fragments past the head, the token sentence and the
/// <c>whoami</c> line. Each value is read from its requirement's <c>words</c> (or <c>wait</c>) paragraph in context, so a bare token another paragraph could supply is
/// not matched, and each parse asserts its count before it returns: an edit that removes or duplicates a span turns a probe red by name and does not shrink what it
/// compares. Self-contained, so every test assembly that links it needs nothing else. G52's spans are <c>PublishedStoreFaults</c>'s and are not read here.
/// </summary>
internal static partial class PublishedClientWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    // ----- R10.93 wait, and the three places the figure is published -----

    /// <summary>R10.93 wait: the whole wait, "60 seconds" as published.</summary>
    internal static string Wait => One(Group(WaitPhrase(), Paragraph("R10.93 wait"), "R10.93 wait's figure")).Trim();

    /// <summary>The number of seconds in <see cref="Wait"/>.</summary>
    internal static int WaitSeconds => int.Parse(Digits().Match(Wait).Value, CultureInfo.InvariantCulture);

    /// <summary>
    /// The figure each of the three published places states, in order: R10.93 wait, R10.93 words, R11.73 words. The count is asserted: three, and each a
    /// whole-number of seconds.
    /// </summary>
    internal static int[] WaitFigures()
    {
        var figures = new[]
        {
            WaitSeconds,
            int.Parse(One(Group(AskAgainPhrase(), Paragraph("R10.93 words"), "R10.93 words' wait")), CultureInfo.InvariantCulture),
            int.Parse(One(Group(RefusalAgePhrase(), Paragraph("R11.73 words"), "R11.73 words' wait")), CultureInfo.InvariantCulture),
        };

        return figures.Length == 3 ? figures : throw new InvalidOperationException($"{Errata} publishes the wait in {figures.Length} places, expected three");
    }

    // ----- R10.93 words: the unknown line -----

    /// <summary>The <c>unknown</c> line after its digest, with <c>&lt;instant&gt;</c> left in, from "The `unknown` line, after the digest: `...`".</summary>
    internal static string UnknownLine => One(Group(UnknownLinePhrase(), Paragraph("R10.93 words"), "R10.93's unknown line"));

    /// <summary>The line with <c>&lt;instant&gt;</c> replaced by <paramref name="instant"/>.</summary>
    internal static string UnknownLineAt(string instant) => UnknownLine.Replace("<instant>", instant, StringComparison.Ordinal);

    /// <summary>The three clauses' starts, in the order the entry gives them: resend, wait, spelling. The count is asserted.</summary>
    internal static string[] UnknownClauses()
    {
        var paragraph = Paragraph("R10.93 words");
        var starts = new[]
        {
            One(Group(ResendStartPhrase(), paragraph, "the resend clause's start")),
            One(Group(WaitStartPhrase(), paragraph, "the wait clause's start")),
            One(Group(SpellingStartPhrase(), paragraph, "the spelling clause's start")),
        };

        return starts.Length == 3 ? starts : throw new InvalidOperationException($"{Errata} yields {starts.Length} clause starts, expected three");
    }

    /// <summary>The published form of the instant, from "in the form `2026-11-01T09:30:00Z`".</summary>
    internal static string SecondInstantExample => One(Group(SecondInstantPhrase(), Paragraph("R10.93 words"), "R10.93's instant form"));

    // ----- R11.73 words: the store sentence and its pairs -----

    /// <summary>The store sentence (the first backtick span of R11.73 words), whitespace folded.</summary>
    internal static string StoreSentence => One(Group(StoreSentencePhrase(), Paragraph("R11.73 words"), "R11.73's store sentence"));

    /// <summary>
    /// The reading pairs: the phrase a replayed resolve prints and the phrase the refusal of a repeated flag prints, each with the meaning the entry gives it, in
    /// that order. The count is asserted: two.
    /// </summary>
    internal static (string Phrase, string Meaning)[] ReadingPairs()
    {
        var matches = PairPhrase().Matches(Paragraph("R11.73 words"));
        if (matches.Count != 2) throw new InvalidOperationException($"{Errata}'s R11.73 words yield {matches.Count} reading pairs, expected two");
        return [.. matches.Select(m => (m.Groups["phrase"].Value, m.Groups["meaning"].Value))];
    }

    /// <summary>The phrase a replayed resolve prints.</summary>
    internal static string ResolveReplayPhrase => ReadingPairs()[0].Phrase;

    /// <summary>The phrase the refusal of a repeated flag prints.</summary>
    internal static string FlagRepeatPhrase => ReadingPairs()[1].Phrase;

    /// <summary>The condition a resolve's reading holds under, from "a resolve's reading holds only if ...", up to the next comma: one reading.</summary>
    internal static string ResolveCondition => One(Group(ConditionPhrase(), Paragraph("R11.73 words"), "R11.73's condition"));

    /// <summary>The clause that says an answer of any other kind says nothing of the first call.</summary>
    internal static string OtherKindClause => One(Group(OtherKindPhrase(), Paragraph("R11.73 words"), "R11.73's other-kind clause"));

    // ----- R11.74 words: the verification, the token and whoami -----

    /// <summary>The equal-head line with <c>&lt;instant&gt;</c>, <c>&lt;when&gt;</c> and <c>&lt;n&gt;</c> left in.</summary>
    internal static string EqualHeadLine => One(Group(EqualHeadPhrase(), Paragraph("R11.74 words"), "R11.74's equal-head line"));

    /// <summary>The three <c>&lt;when&gt;</c> forms, in the order the entry gives them: a second or more, less than a second, after the check began. The count is asserted.</summary>
    internal static string[] WhenForms()
    {
        var paragraph = Paragraph("R11.74 words");
        var forms = new[]
        {
            One(Group(WhenSecondsPhrase(), paragraph, "the seconds form")),
            One(Group(WhenRecentPhrase(), paragraph, "the less-than-a-second form")),
            One(Group(WhenAfterPhrase(), paragraph, "the after-began form")),
        };

        return forms.Length == 3 ? forms : throw new InvalidOperationException($"{Errata} yields {forms.Length} when-forms, expected three");
    }

    /// <summary>The equal-head line for a file that records no instant, with <c>&lt;n&gt;</c> left in.</summary>
    internal static string NotStatedLine => One(Group(NotStatedPhrase(), Paragraph("R11.74 words"), "R11.74's NOT STATED line"));

    /// <summary>The published form of the head's instant, from "in the form `2026-11-01T09:30:00.123Z`".</summary>
    internal static string MillisecondInstantExample => One(Group(MillisecondInstantPhrase(), Paragraph("R11.74 words"), "R11.74's instant form"));

    /// <summary>The closing line past the head with <c>&lt;n&gt;</c> left in.</summary>
    internal static string PastHeadClosing => One(Group(ClosingPhrase(), Paragraph("R11.74 words"), "R11.74's closing"));

    /// <summary>The suffix that says another check could not be made.</summary>
    internal static string AnotherCheckSuffix => One(Group(AnotherCheckPhrase(), Paragraph("R11.74 words"), "R11.74's another-check suffix"));

    /// <summary>The two inclusion fragments, folded: "leaf &lt;n&gt; is not covered by the signed head at tree size &lt;m&gt;" and "no published head commits to it yet".</summary>
    internal static string[] InclusionFragments()
    {
        var matches = InclusionPhrase().Match(Paragraph("R11.74 words"));
        return matches.Success
            ? [matches.Groups["leaf"].Value, matches.Groups["yet"].Value]
            : throw new InvalidOperationException($"{Errata}'s R11.74 words yield no inclusion fragments");
    }

    /// <summary>The token sentence (R11.74 words), replacing R11.72's.</summary>
    internal static string TokenSentence => One(Group(TokenPhrase(), Paragraph("R11.74 words"), "R11.74's token sentence"));

    /// <summary>The <c>whoami</c> line for an expired cached token.</summary>
    internal static string WhoamiExpired => One(Group(WhoamiPhrase(), Paragraph("R11.74 words"), "R11.74's whoami line"));

    /// <summary>
    /// Every sentence and long phrase G53 introduces, which a test must not write by hand (WAF30's list). Each is long enough not to be a bare token. The two
    /// reading-pair phrases are not here: they are words the clients already printed under errata G33 and G40, and older facts hold them as the receipts' words.
    /// </summary>
    internal static string[] AllPublished() =>
    [
        UnknownLine, StoreSentence, EqualHeadLine, NotStatedLine, PastHeadClosing, AnotherCheckSuffix, TokenSentence, WhoamiExpired,
        .. UnknownClauses(), .. WhenForms(),
    ];

    // ----- text helpers -----

    /// <summary>The paragraph of the errata that begins <c>**{name}.**</c> (for example <c>R11.74 words</c>), to its blank line, whitespace folded. Exactly one must exist.</summary>
    internal static string Paragraph(string name)
    {
        var text = ErrataText();
        var anchor = $"**{name}.**";
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{anchor}\"");
        if (text.IndexOf(anchor, start + anchor.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException($"{Errata} has more than one paragraph that begins \"{anchor}\"");

        var end = text.IndexOf("\n\n", start + anchor.Length, StringComparison.Ordinal);
        return Whitespace().Replace(text[start..(end < 0 ? text.Length : end)], " ");
    }

    /// <summary>True where <paramref name="text"/> (whitespace folded) holds <paramref name="published"/> (whitespace folded).</summary>
    internal static bool Says(string text, string published) =>
        Whitespace().Replace(text, " ").Contains(Whitespace().Replace(published, " "), StringComparison.Ordinal);

    /// <summary>Whitespace folded to single spaces.</summary>
    internal static string Fold(string text) => Whitespace().Replace(text, " ");

    private static MatchCollection Group(Regex pattern, string paragraph, string what)
    {
        var matches = pattern.Matches(paragraph);
        return matches.Count == 1
            ? matches
            : throw new InvalidOperationException($"{Errata} yields {matches.Count} readings of {what}, expected one");
    }

    private static string One(MatchCollection matches) => matches[0].Groups[1].Value;

    private static string ErrataText()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}")
            : File.ReadAllText(Path.Combine(dir.FullName, Errata));
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    [GeneratedRegex(@"The wait is `(\d+ seconds)`", RegexOptions.CultureInvariant)]
    private static partial Regex WaitPhrase();

    [GeneratedRegex(@"ask again when the refusal is (\d+) seconds old", RegexOptions.CultureInvariant)]
    private static partial Regex AskAgainPhrase();

    [GeneratedRegex(@"only if the Forum still does not know it when the refusal is (\d+) seconds old", RegexOptions.CultureInvariant)]
    private static partial Regex RefusalAgePhrase();

    [GeneratedRegex(@"The `unknown` line, after the digest: `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex UnknownLinePhrase();

    [GeneratedRegex(@"The resend clause begins `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex ResendStartPhrase();

    [GeneratedRegex(@"the wait clause begins `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex WaitStartPhrase();

    [GeneratedRegex(@"the spelling clause begins `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex SpellingStartPhrase();

    [GeneratedRegex(@"in the form `(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ)`", RegexOptions.CultureInvariant)]
    private static partial Regex SecondInstantPhrase();

    [GeneratedRegex(@"\*\*R11\.73 words\.\*\* The store sentence, replacing the sentence of R11\.72 words for `curia/store/unavailable`: `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex StoreSentencePhrase();

    [GeneratedRegex(@"`(?<phrase>[^`]+)` means (?<meaning>the first call was recorded)", RegexOptions.CultureInvariant)]
    private static partial Regex PairPhrase();

    [GeneratedRegex(@"a resolve's reading holds only if (no other call changed the thread's accepted answer between the two calls)", RegexOptions.CultureInvariant)]
    private static partial Regex ConditionPhrase();

    [GeneratedRegex(@"(an answer of any other kind says nothing of the first call)", RegexOptions.CultureInvariant)]
    private static partial Regex OtherKindPhrase();

    [GeneratedRegex(@"The equal-head line: `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex EqualHeadPhrase();

    [GeneratedRegex(@"The `<when>` is `([^`]+)` for a head stored a second or more", RegexOptions.CultureInvariant)]
    private static partial Regex WhenSecondsPhrase();

    [GeneratedRegex(@"`([^`]+)` for a head stored less than a second before", RegexOptions.CultureInvariant)]
    private static partial Regex WhenRecentPhrase();

    [GeneratedRegex(@"and `([^`]+)` for a head stored after", RegexOptions.CultureInvariant)]
    private static partial Regex WhenAfterPhrase();

    [GeneratedRegex(@"the line says `([^`]+)` and the rest of the sentence is as above", RegexOptions.CultureInvariant)]
    private static partial Regex NotStatedPhrase();

    [GeneratedRegex(@"UTC to the millisecond in the form `(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z)`", RegexOptions.CultureInvariant)]
    private static partial Regex MillisecondInstantPhrase();

    [GeneratedRegex(@"The closing line past the head: `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex ClosingPhrase();

    [GeneratedRegex(@"it ends with `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex AnotherCheckPhrase();

    [GeneratedRegex(@"the fragments `(?<leaf>[^`]+)` and `(?<yet>[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InclusionPhrase();

    [GeneratedRegex(@"The token sentence, replacing the sentence of R11\.72 words for the token: `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPhrase();

    [GeneratedRegex(@"The `whoami` line for an expired cached token: `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex WhoamiPhrase();
}
