using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words and figures of errata G54 (R8.83, R9.53, R11.69 (rev.), R11.75) as the errata publishes them, read at test time from
/// <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never from the code a probe is about: the three figures of the check (read one at a time, because the
/// repeat and the settle window are two quantities that happen to be equal), the event names and ids, the members, the success reset's limit, the pieces of the
/// search line, the tool's block, the two paragraphs and the version. Each requirement is read through <see cref="PublishedAcceptBudget.Paragraph(string)"/>, so
/// an edit that removes a span turns a probe red and does not shrink what it compares; every parse asserts that it found exactly one reading before it returns.
/// A phrase is matched in its own sentence, never a bare token another sentence could supply. Printed lines are compared whitespace-folded, as the entry says.
/// </summary>
internal static partial class PublishedCheckWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>The four requirements' anchors, in the order the entry gives them (R11.69 (rev.) is the entry's third).</summary>
    internal static readonly string[] Anchors = ["**R8.83**", "**R9.53**", "**R11.69 (rev.)**", "**R11.75**"];

    /// <summary>The entry's index rows for its new requirements.</summary>
    internal static readonly string[] IndexRows = ["| R8.83 |", "| R9.53 |", "| R11.75 |"];

    internal static string Check() => PublishedAcceptBudget.Paragraph("**R8.83**");

    internal static string Members() => PublishedAcceptBudget.Paragraph("**R9.53**");

    internal static string Reset() => PublishedAcceptBudget.Paragraph("**R11.69 (rev.)**");

    internal static string Clients() => PublishedAcceptBudget.Paragraph("**R11.75**");

    internal static string CheckWords() => PublishedAcceptBudget.Paragraph("**R8.83 words.**");

    internal static string MemberWords() => PublishedAcceptBudget.Paragraph("**R9.53 words.**");

    internal static string ResetWords() => PublishedAcceptBudget.Paragraph("**R11.69 words (rev.).**");

    internal static string ClientWords() => PublishedAcceptBudget.Paragraph("**R11.75 words.**");

    // ----- R8.83 words: the figures, read one at a time -----

    /// <summary>The repeat while a check is owed (<c>SweepRepeat</c>).</summary>
    internal static TimeSpan SweepRepeat() => TimeSpan.FromSeconds(Number(RepeatPhrase(), CheckWords(), "R8.83's repeat (SweepRepeat)"));

    /// <summary>The settle window (<c>SweepSettle</c>): an obligation is cleared only by a check that began at least this long after the latest exit.</summary>
    internal static TimeSpan SweepSettle() => TimeSpan.FromSeconds(Number(SettlePhrase(), CheckWords(), "R8.83's settle window (SweepSettle)"));

    /// <summary>The floor (<c>SweepFloor</c>): a check at least this often after the last began.</summary>
    internal static TimeSpan SweepFloor() => TimeSpan.FromMinutes(Number(FloorPhrase(), CheckWords(), "R8.83's floor (SweepFloor)"));

    /// <summary>The first wait after a failed check, doubled to the interval it would have had.</summary>
    internal static TimeSpan FailedCheckWait() => TimeSpan.FromSeconds(Number(FailedWaitPhrase(), CheckWords(), "R8.83's first wait after a failed check"));

    /// <summary>The bound of the process's memory of posts the model cannot place.</summary>
    internal static int UnplaceableBound() => (int)Number(UnplaceableBoundPhrase(), CheckWords(), "R8.83's bound of the unplaceable memory");

    /// <summary>The three log events, each with the id the words give: <c>VectorIndexCheckOwed</c>, <c>VectorIndexCheckFound</c> and <c>VectorIndexCheckFailed</c>.</summary>
    internal static (string Name, int Id)[] Events()
    {
        var found = EventPhrase().Matches(CheckWords());
        if (found.Count != 3) throw new InvalidOperationException($"{Errata}'s R8.83 words yield {found.Count} log events, expected three");
        return [.. found.Select(m => (m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))];
    }

    // ----- R9.53 words: the members -----

    /// <summary>The two members of the search response, in the order the words give them: the checked instant, then the due instant.</summary>
    internal static (string CheckedAt, string CheckDueAt) MemberNames()
    {
        var match = MembersPhrase().Matches(MemberWords());
        if (match.Count != 1) throw new InvalidOperationException($"{Errata}'s R9.53 words yield {match.Count} readings of the members, expected one");
        return (match[0].Groups[1].Value, match[0].Groups[2].Value);
    }

    // ----- R11.69 words (rev.): the success reset -----

    /// <summary>The least interval between two resets of the backlog's waits.</summary>
    internal static TimeSpan ResetLimit() => TimeSpan.FromSeconds(Number(ResetLimitPhrase(), ResetWords(), "R11.69's reset limit"));

    /// <summary>The wait a reset returns an entry to.</summary>
    internal static TimeSpan ResetWait() => TimeSpan.FromSeconds(Number(ResetWaitPhrase(), ResetWords(), "R11.69's reset wait"));

    // ----- R11.75 words: the line, the block, the paragraph, the version -----

    /// <summary>The count clause for a positive count, with <c>&lt;n&gt;</c> replaced by <paramref name="count"/>.</summary>
    internal static string CountClause(int count) =>
        One(CountClausePhrase(), ClientWords(), "R11.75's count clause").Replace("<n>", count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>The count clause for 0 where both members are stated and nothing is owed.</summary>
    internal static string NonePending() => One(NonePendingPhrase(), ClientWords(), "R11.75's none pending clause");

    /// <summary>The count clause for 0 otherwise.</summary>
    internal static string NoneCounted() => One(NoneCountedPhrase(), ClientWords(), "R11.75's none counted clause");

    /// <summary>The whole line for a count the Forum did not serve.</summary>
    internal static string LineNotStated() => One(LineNotStatedPhrase(), ClientWords(), "R11.75's line for no count");

    /// <summary>The owed piece for a due member of <c>null</c>.</summary>
    internal static string NotOwed() => One(NotOwedPhrase(), ClientWords(), "R11.75's not-owed piece");

    /// <summary>The owed piece for an instant, with <c>&lt;instant&gt;</c> replaced by <paramref name="instant"/>.</summary>
    internal static string Owed(string instant) =>
        One(OwedPhrase(), ClientWords(), "R11.75's owed piece").Replace("<instant>", instant, StringComparison.Ordinal);

    /// <summary>The owed piece for a due member the Forum did not serve.</summary>
    internal static string OwedNotStated() => One(OwedNotStatedPhrase(), ClientWords(), "R11.75's owed piece for no member");

    /// <summary>The checked piece, with <c>&lt;instant&gt;</c> replaced by <paramref name="instant"/>.</summary>
    internal static string Checked(string instant) =>
        One(CheckedPhrase(), ClientWords(), "R11.75's checked piece").Replace("<instant>", instant, StringComparison.Ordinal);

    /// <summary>The checked piece for a member the Forum did not serve.</summary>
    internal static string CheckedNotStated() => One(CheckedNotStatedPhrase(), ClientWords(), "R11.75's checked piece for no member");

    /// <summary>The sentence the tool's first block adds where the due member is an instant.</summary>
    internal static string OwedSentence() => One(OwedSentencePhrase(), ClientWords(), "R11.75's owed sentence");

    /// <summary>The paragraph <c>curia_search</c>'s description and <c>curia search</c>'s help carry.</summary>
    internal static string Paragraph() => One(ParagraphPhrase(), ClientWords(), "R11.75's paragraph");

    /// <summary>
    /// The line <c>curia search</c> prints, composed from R11.75's pieces and nothing else: <c>vector  &lt;count clause&gt;; &lt;owed piece&gt;; &lt;checked piece&gt;</c>.
    /// <paramref name="count"/> null is a count the Forum did not serve (the whole line is the entry's); <paramref name="due"/> and <paramref name="checkedAt"/> null
    /// are a member not served, and <paramref name="dueIsNull"/> is a due member of JSON <c>null</c>.
    /// </summary>
    internal static string Line(int? count, string? due, bool dueIsNull, string? checkedAt)
    {
        if (count is null) return LineNotStated();

        var bothStated = checkedAt is not null && (due is not null || dueIsNull);
        var clause = count > 0
            ? CountClause(count.Value)
            : bothStated && dueIsNull ? NonePending() : NoneCounted();
        var owed = dueIsNull ? NotOwed() : due is not null ? Owed(due) : OwedNotStated();
        var checkedPiece = checkedAt is not null ? Checked(checkedAt) : CheckedNotStated();
        return $"vector  {clause}; {owed}; {checkedPiece}";
    }

    /// <summary>Folds the runs of whitespace of a printed line to one space, as the entry says a probe does before it compares.</summary>
    internal static string Fold(string text) => PublishedIndexLag.Fold(text);

    private static string One(Regex pattern, string paragraph, string what)
    {
        var matches = pattern.Matches(paragraph);
        if (matches.Count != 1)
            throw new InvalidOperationException($"{Errata} yields {matches.Count} readings of {what}, expected one");

        return matches[0].Groups[1].Value;
    }

    private static double Number(Regex pattern, string paragraph, string what) =>
        double.Parse(One(pattern, paragraph, what).Replace(",", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);

    [GeneratedRegex(@"the repeat while owed, (\d+) seconds \(`SweepRepeat`\)", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatPhrase();

    [GeneratedRegex(@"the settle window, (\d+) seconds \(`SweepSettle`\)", RegexOptions.CultureInvariant)]
    private static partial Regex SettlePhrase();

    [GeneratedRegex(@"the floor, (\d+) minutes \(`SweepFloor`\)", RegexOptions.CultureInvariant)]
    private static partial Regex FloorPhrase();

    [GeneratedRegex(@"the first wait after a failed check, (\d+) seconds, doubled to the interval it would have had", RegexOptions.CultureInvariant)]
    private static partial Regex FailedWaitPhrase();

    [GeneratedRegex(@"the bound of the process's memory of posts the model cannot place, ([\d,]+)\.", RegexOptions.CultureInvariant)]
    private static partial Regex UnplaceableBoundPhrase();

    [GeneratedRegex(@"`(VectorIndexCheck(?:Owed|Found|Failed))` \(id (\d+)[;)]", RegexOptions.CultureInvariant)]
    private static partial Regex EventPhrase();

    [GeneratedRegex(@"The members are `([a-z_]+)` and `([a-z_]+)`\.", RegexOptions.CultureInvariant)]
    private static partial Regex MembersPhrase();

    [GeneratedRegex(@"no more than once in (\d+) seconds", RegexOptions.CultureInvariant)]
    private static partial Regex ResetLimitPhrase();

    [GeneratedRegex(@"to due at once and (\d+) seconds", RegexOptions.CultureInvariant)]
    private static partial Regex ResetWaitPhrase();

    [GeneratedRegex(@"The count clause is `(<n> committed post\(s\) not yet in the vector index: found by words, not yet by meaning)` for a positive count", RegexOptions.CultureInvariant)]
    private static partial Regex CountClausePhrase();

    [GeneratedRegex(@"for 0, `(none pending)` where both members are stated and `vector_check_due_at` is `null`", RegexOptions.CultureInvariant)]
    private static partial Regex NonePendingPhrase();

    [GeneratedRegex(@"and `(none counted as pending)` otherwise", RegexOptions.CultureInvariant)]
    private static partial Regex NoneCountedPhrase();

    [GeneratedRegex(@"the whole line is `(vector NOT STATED)`", RegexOptions.CultureInvariant)]
    private static partial Regex LineNotStatedPhrase();

    [GeneratedRegex(@"The owed piece is `(no check is owed)` for `null`", RegexOptions.CultureInvariant)]
    private static partial Regex NotOwedPhrase();

    [GeneratedRegex(@"`(a check is owed \([^`]*\) and next runs at <instant>)` for an instant", RegexOptions.CultureInvariant)]
    private static partial Regex OwedPhrase();

    [GeneratedRegex(@"`(whether a check is owed is NOT STATED)` for an absent member", RegexOptions.CultureInvariant)]
    private static partial Regex OwedNotStatedPhrase();

    [GeneratedRegex(@"The checked piece is `(the Forum last compared its log with the vector index at <instant>)`", RegexOptions.CultureInvariant)]
    private static partial Regex CheckedPhrase();

    [GeneratedRegex(@"or `(when the Forum last compared its log with the vector index is NOT STATED)` for an absent member", RegexOptions.CultureInvariant)]
    private static partial Regex CheckedNotStatedPhrase();

    [GeneratedRegex(@"it adds the sentence `(A check is owed: [^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex OwedSentencePhrase();

    [GeneratedRegex(@"help of `curia search` is `(The vector line states [^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphPhrase();
}
