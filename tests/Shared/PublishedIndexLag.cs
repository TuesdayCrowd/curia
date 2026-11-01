using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words of errata G51 (R8.81, R9.51, R11.69, R11.70) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and
/// never from the code a probe is about (the D92 PR, Task 0): the three values of <c>vector_index</c>, the member and event names, the bounds and the
/// schedule, the receipt's four lines, the search lines and the sentence a description carries. Each requirement is read through
/// <see cref="PublishedAcceptBudget.Paragraph(string)"/>, so an edit that removes a span turns a probe red and does not shrink what it compares; each parse
/// asserts its count before it returns. Printed lines are compared whitespace-folded, as the entry says. The version is not read here: the one file that reads a
/// <c>curia-mcp</c> version out of an errata paragraph is <c>PublishedReadersLastWords</c> (<c>Version()</c>, which reads R11.70's paragraph since the D92 PR).
/// </summary>
internal static partial class PublishedIndexLag
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>The four requirements' anchors, in the order the errata gives them.</summary>
    internal static readonly string[] Anchors = ["**R8.81**", "**R9.51**", "**R11.69**", "**R11.70**"];

    /// <summary>The entry's index rows, one per requirement, as the errata's consolidated index writes them.</summary>
    internal static readonly string[] IndexRows = ["| R8.81 |", "| R9.51 |", "| R11.69 |", "| R11.70 |"];

    internal static string Answer() => PublishedAcceptBudget.Paragraph("**R8.81**");

    internal static string Search() => PublishedAcceptBudget.Paragraph("**R9.51**");

    internal static string Repair() => PublishedAcceptBudget.Paragraph("**R11.69**");

    internal static string Clients() => PublishedAcceptBudget.Paragraph("**R11.70**");

    internal static string AnswerWords() => PublishedAcceptBudget.Paragraph("**R8.81 words.**");

    internal static string SearchWords() => PublishedAcceptBudget.Paragraph("**R9.51 words.**");

    internal static string RepairWords() => PublishedAcceptBudget.Paragraph("**R11.69 words.**");

    internal static string ClientWords() => PublishedAcceptBudget.Paragraph("**R11.70 words.**");

    // ----- R8.81 -----

    /// <summary>The receipt's member, as R8.81 names it: the code span after "the member".</summary>
    internal static string ReceiptMember() => One(ReceiptMemberPhrase(), Answer(), "R8.81's receipt member");

    /// <summary>The three values of the receipt's member, in the order the words give them: stored, not stored, not applicable.</summary>
    internal static (string Indexed, string Pending, string NotApplicable) Values()
    {
        var match = ValuesPhrase().Matches(AnswerWords());
        if (match.Count != 1) throw new InvalidOperationException($"{Errata}'s R8.81 words yield {match.Count} readings of the three values, expected one");
        return (match[0].Groups[1].Value, match[0].Groups[2].Value, match[0].Groups[3].Value);
    }

    /// <summary>The log event name for a post the Forum committed and could not index.</summary>
    internal static string PendingEvent() => One(PendingEventPhrase(), AnswerWords(), "R8.81's log event name");

    /// <summary>R8.81's bound on the step that stores the vector: "bounded at N seconds".</summary>
    internal static TimeSpan StepBound() => TimeSpan.FromSeconds(Number(StepBoundPhrase(), Answer(), "R8.81's bound"));

    // ----- R9.51 -----

    /// <summary>The search response's member: the code span after "The member is".</summary>
    internal static string SearchMember() => One(SearchMemberPhrase(), SearchWords(), "R9.51's member");

    // ----- R11.69 -----

    /// <summary>The log event name for a retry that stored a vector.</summary>
    internal static string CaughtUpEvent() => One(CaughtUpPhrase(), RepairWords(), "R11.69's log event name");

    /// <summary>R11.69's bound on the backlog: "bounded at N,NNN" (the thousands separator removed).</summary>
    internal static int BacklogBound() => (int)Number(BacklogBoundPhrase(), Repair(), "R11.69's backlog bound");

    /// <summary>R11.69's first wait: "the first retry N seconds after the failure".</summary>
    internal static TimeSpan FirstWait() => TimeSpan.FromSeconds(Number(FirstWaitPhrase(), Repair(), "R11.69's first wait"));

    /// <summary>R11.69's cap: "doubled to N minutes".</summary>
    internal static TimeSpan WaitCap() => TimeSpan.FromMinutes(Number(CapPhrase(), Repair(), "R11.69's cap"));

    /// <summary>R11.69's latitude: "beginning no later than N second after it falls due".</summary>
    internal static TimeSpan Tick() => TimeSpan.FromSeconds(Number(TickPhrase(), Repair(), "R11.69's tick"));

    // ----- R11.70 -----

    /// <summary>The receipt's line for <paramref name="value"/> (one of <see cref="Values"/>) and the line for a receipt that carries none, folded.</summary>
    internal static string ReceiptLine(string value) => One(LinePhrase(value), ClientWords(), $"R11.70's receipt line for {value}");

    /// <summary>The receipt's line for a receipt that carries no member.</summary>
    internal static string NotStatedLine() => One(NotStatedPhrase(), ClientWords(), "R11.70's receipt line for no member");

    /// <summary>The tool's first-block member for a count, <c>vector_pending=&lt;n&gt;</c> with the count in it.</summary>
    internal static string ToolCountLine(int count) =>
        One(ToolCountPhrase(), ClientWords(), "R11.70's tool member for a count").Replace("<n>", count.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    /// <summary>The tool's first-block member where the Forum served none.</summary>
    internal static string ToolNotStatedLine() => One(ToolNotStatedPhrase(), ClientWords(), "R11.70's tool member for none served");

    /// <summary>The sentence the five write tools, <c>curia_search</c> and the helps carry.</summary>
    internal static string Sentence() => One(SentencePhrase(), ClientWords(), "R11.70's sentence");

    /// <summary>Folds the runs of whitespace of a printed line to one space, as the entry says a probe does before it compares.</summary>
    internal static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    private static string One(Regex pattern, string paragraph, string what)
    {
        var matches = pattern.Matches(paragraph);
        if (matches.Count != 1)
            throw new InvalidOperationException($"{Errata} yields {matches.Count} readings of {what}, expected one");

        return matches[0].Groups[1].Value;
    }

    private static double Number(Regex pattern, string paragraph, string what) =>
        double.Parse(One(pattern, paragraph, what).Replace(",", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"carry the member `([a-z_]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex ReceiptMemberPhrase();

    [GeneratedRegex(@"values of `vector_index` are `([a-z_]+)`, `([a-z_]+)` and `([a-z_]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex ValuesPhrase();

    [GeneratedRegex(@"log event name is `(\w+)`", RegexOptions.CultureInvariant)]
    private static partial Regex PendingEventPhrase();

    [GeneratedRegex(@"bounded at (\d+) seconds", RegexOptions.CultureInvariant)]
    private static partial Regex StepBoundPhrase();

    [GeneratedRegex(@"The member is `([a-z_]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex SearchMemberPhrase();

    [GeneratedRegex(@"carries the event name `(\w+)`", RegexOptions.CultureInvariant)]
    private static partial Regex CaughtUpPhrase();

    [GeneratedRegex(@"bounded at ([\d,]+) \(provisional\)", RegexOptions.CultureInvariant)]
    private static partial Regex BacklogBoundPhrase();

    [GeneratedRegex(@"the first retry (\d+) seconds after the failure", RegexOptions.CultureInvariant)]
    private static partial Regex FirstWaitPhrase();

    [GeneratedRegex(@"doubled to (\d+) minutes", RegexOptions.CultureInvariant)]
    private static partial Regex CapPhrase();

    [GeneratedRegex(@"beginning no later than (\d+) second after it falls due", RegexOptions.CultureInvariant)]
    private static partial Regex TickPhrase();

    private static Regex LinePhrase(string value) =>
        new($"For `{Regex.Escape(value)}`: `(search [^`]+)`", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"For a receipt that carries no member: `(search [^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex NotStatedPhrase();

    [GeneratedRegex(@"first block: `(vector_pending=<n>)`", RegexOptions.CultureInvariant)]
    private static partial Regex ToolCountPhrase();

    [GeneratedRegex(@"or `(vector_pending=not stated)`", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNotStatedPhrase();

    [GeneratedRegex(@" is `(A receipt that says[^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex SentencePhrase();

}
