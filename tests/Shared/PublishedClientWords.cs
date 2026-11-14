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

    // ----- R10.95 words (errata G58, which replaced G53's R10.93 words and R10.93 wait): the unknown line -----

    /// <summary>The <c>unknown</c> line after its digest, with <c>&lt;instant&gt;</c> and <c>&lt;digest&gt;</c> left in (R10.95 words).</summary>
    internal static string UnknownLine => PublishedResendWords.UnknownLine;

    /// <summary>The line with <c>&lt;instant&gt;</c> replaced by <paramref name="instant"/>.</summary>
    internal static string UnknownLineAt(string instant) => UnknownLine.Replace("<instant>", instant, StringComparison.Ordinal);

    /// <summary>The two clauses' starts, in the order the entry gives them: resend, spelling. The count is asserted.</summary>
    internal static string[] UnknownClauses()
    {
        var line = UnknownLine;
        var starts = new[] { "If it is the digest of an act this host kept", "If you meant to cite a post" };
        return starts.All(start => line.Contains(start, StringComparison.Ordinal))
            ? starts
            : throw new InvalidOperationException($"{Errata}'s R10.95 unknown line does not hold its two clauses: {string.Join(", ", starts.Where(start => !line.Contains(start, StringComparison.Ordinal)))}");
    }

    /// <summary>The published form of the instant: a second, UTC, ending Z. G58's R10.95 words state <c>&lt;instant&gt;</c> and the form of G53's example is kept.</summary>
    internal static string SecondInstantExample => "2026-11-01T09:30:00Z";

    // ----- R11.80 clause words (errata G58): the store sentence and its pairs -----

    /// <summary>The store sentence for a signed post or vote: R11.80 clause words (post), whitespace folded.</summary>
    internal static string StoreSentence => PublishedResendWords.Clauses().Post;

    /// <summary>
    /// The reading pairs: the phrase a replayed resolve prints and the phrase the refusal of a repeated flag prints, each with the meaning the clause gives it, in
    /// that order. The count is asserted: two.
    /// </summary>
    internal static (string Phrase, string Meaning)[] ReadingPairs()
    {
        var (_, resolve, flag, _) = PublishedResendWords.Clauses();
        var pairs = new[]
        {
            Pair(resolve, @"""(?<phrase>[^""]+)"" means (?<meaning>[^,]+?)(?:,|\.)"),
            Pair(flag, @"""(?<phrase>[^""]+)"" means (?<meaning>[^,]+?)(?:,|\.)"),
        };
        return pairs;

        static (string Phrase, string Meaning) Pair(string clause, string pattern)
        {
            var matches = Regex.Matches(clause, pattern, RegexOptions.CultureInvariant);
            return matches.Count == 1
                ? (matches[0].Groups["phrase"].Value, matches[0].Groups["meaning"].Value)
                : throw new InvalidOperationException($"{Errata}'s R11.80 clause words yield {matches.Count} reading pairs in a clause, expected one");
        }
    }

    /// <summary>The phrase a replayed resolve prints.</summary>
    internal static string ResolveReplayPhrase => ReadingPairs()[0].Phrase;

    /// <summary>The phrase the refusal of a repeated flag prints.</summary>
    internal static string FlagRepeatPhrase => ReadingPairs()[1].Phrase;

    /// <summary>The condition a resolve's reading holds under: the last sentence of the resolve clause.</summary>
    internal static string ResolveCondition
    {
        get
        {
            var clause = PublishedResendWords.Clauses().Resolve;
            var at = clause.IndexOf("This reading holds only if", StringComparison.Ordinal);
            return at >= 0 ? clause[at..] : throw new InvalidOperationException($"{Errata}'s R11.80 clause words (resolve) have no condition");
        }
    }

    /// <summary>The clause for a request that is none of the others: R11.80 clause words (other).</summary>
    internal static string OtherKindClause => PublishedResendWords.Clauses().Other;

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
    internal static string PastHeadClosing => One(Group(ClosingPhrase(), Paragraph("R11.94 words"), "R11.94's closing"));

    /// <summary>The suffix that says another check could not be made.</summary>
    internal static string AnotherCheckSuffix => One(Group(AnotherCheckPhrase(), Paragraph("R11.94 words"), "R11.94's another-check suffix"));

    // ----- R11.94 words (errata G67): when the latest head was signed -----

    /// <summary>The <c>&lt;signed&gt;</c> form for a head signed a second or more before the check began, with <c>&lt;instant&gt;</c> and <c>&lt;k&gt;</c> left in.</summary>
    internal static string SignedSecondsForm => One(Group(SignedSecondsPhrase(), Paragraph("R11.94 words"), "R11.94's seconds form"));

    /// <summary>The <c>&lt;signed&gt;</c> form for a head signed less than a second before the check began.</summary>
    internal static string SignedRecentForm => One(Group(SignedRecentPhrase(), Paragraph("R11.94 words"), "R11.94's less-than-a-second form"));

    /// <summary>The <c>&lt;signed&gt;</c> form for a head stamped after the check began.</summary>
    internal static string SignedAfterForm => One(Group(SignedAfterPhrase(), Paragraph("R11.94 words"), "R11.94's after-began form"));

    /// <summary>The <c>&lt;signed&gt;</c> form for a timestamp the client cannot read.</summary>
    internal static string SignedNotStatedForm => One(Group(SignedNotStatedPhrase(), Paragraph("R11.94 words"), "R11.94's NOT STATED form"));

    /// <summary>The sentence the inclusion line gains, <c>That head was &lt;signed&gt;.</c></summary>
    internal static string ThatHeadSentence => One(Group(ThatHeadPhrase(), Paragraph("R11.94 words"), "R11.94's inclusion sentence"));

    /// <summary>The published form of the head's signed instant, from "UTC to the millisecond, truncated and not rounded, in the form `...`".</summary>
    internal static string SignedInstantExample => One(Group(SignedInstantPhrase(), Paragraph("R11.94 words"), "R11.94's instant form"));

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
        SignedSecondsForm, SignedRecentForm, SignedAfterForm, SignedNotStatedForm, ThatHeadSentence,
        .. UnknownClauses(), .. WhenForms(),
        KeyLineRotationLifecycleFailed, KeyLineRotationLifecycleCouldNot, LifecycleMarkPastHead,
        SkippedNotMade, SkippedMade, SkippedPastList, SkippedNoMembers,
    ];

    // ----- R11.95 words (errata G68): the key line follows the lifecycle line, and the Forum's mark past the head -----

    /// <summary>The key line for a rotation's binding when the lifecycle line failed, with <c>&lt;i&gt;</c> and <c>&lt;kid&gt;</c> left in (R11.95 words).</summary>
    internal static string KeyLineRotationLifecycleFailed => One(Group(Labelled("Key line, a rotation's binding, lifecycle failed:"), Paragraph("R11.95 words"), "R11.95's failed key line"));

    /// <summary>The key line for a rotation's binding when the lifecycle line could not be checked, with <c>&lt;i&gt;</c> and <c>&lt;kid&gt;</c> left in (R11.95 words).</summary>
    internal static string KeyLineRotationLifecycleCouldNot => One(Group(Labelled("Key line, a rotation's binding, lifecycle could not be checked:"), Paragraph("R11.95 words"), "R11.95's could-not-check key line"));

    /// <summary>The lifecycle line for the Forum's mark past the head, with <c>&lt;d&gt;</c> and <c>&lt;n&gt;</c> left in (R11.95 words).</summary>
    internal static string LifecycleMarkPastHead => One(Group(Labelled("Lifecycle line, the Forum's mark past the head:"), Paragraph("R11.95 words"), "R11.95's mark-past-the-head line"));

    // ----- R11.96 words (errata G68): the SKIPPED lines of curia keys -----

    /// <summary>The <c>SKIPPED</c> line for a row the profile did not make, with <c>&lt;type&gt;</c>, <c>&lt;i&gt;</c> and <c>&lt;predicate&gt;</c> left in (R11.96 words).</summary>
    internal static string SkippedNotMade => One(Group(Labelled("A row the profile did not make:"), Paragraph("R11.96 words"), "R11.96's not-made line"));

    /// <summary>The <c>SKIPPED</c> line for a row the profile made, with <c>&lt;type&gt;</c>, <c>&lt;i&gt;</c> and <c>&lt;predicate&gt;</c> left in (R11.96 words).</summary>
    internal static string SkippedMade => One(Group(Labelled("A row the profile made:"), Paragraph("R11.96 words"), "R11.96's made line"));

    /// <summary>The line past the list, with <c>&lt;n&gt;</c> left in (R11.96 words).</summary>
    internal static string SkippedPastList => One(Group(Labelled("Past the list:"), Paragraph("R11.96 words"), "R11.96's past-the-list line"));

    /// <summary>The line for a key set without the members (R11.96 words).</summary>
    internal static string SkippedNoMembers => One(Group(Labelled("No members:"), Paragraph("R11.96 words"), "R11.96's no-members line"));

    private static Regex Labelled(string label) => new(@"\*" + Regex.Escape(label) + @"\* `([^`]+)`", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

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

    [GeneratedRegex(@"The `<signed>` is `([^`]+)` for a head signed a second or more", RegexOptions.CultureInvariant)]
    private static partial Regex SignedSecondsPhrase();

    [GeneratedRegex(@"`([^`]+)` for one signed less than a second before", RegexOptions.CultureInvariant)]
    private static partial Regex SignedRecentPhrase();

    [GeneratedRegex(@"`([^`]+)` for one stamped after", RegexOptions.CultureInvariant)]
    private static partial Regex SignedAfterPhrase();

    [GeneratedRegex(@"and `(signed at NOT STATED[^`]+)` where the timestamp is absent", RegexOptions.CultureInvariant)]
    private static partial Regex SignedNotStatedPhrase();

    [GeneratedRegex(@"between its first sentence and its second, the sentence `(That head was <signed>\.)`", RegexOptions.CultureInvariant)]
    private static partial Regex ThatHeadPhrase();

    [GeneratedRegex(@"UTC to the millisecond, truncated and not rounded, in the form `(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z)`", RegexOptions.CultureInvariant)]
    private static partial Regex SignedInstantPhrase();
}
