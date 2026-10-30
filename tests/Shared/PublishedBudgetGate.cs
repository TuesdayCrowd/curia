using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words of errata G48 as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never from the code
/// a fact is about (BRB48, BRB54): R7.27's wait and queue, its two problem types and their statuses, the two budget reasons a loser that waited is told,
/// and R11.64's clauses and tool names. Reuses <see cref="PublishedAcceptBudget.Paragraph(string)"/> for the paragraph reading.
/// </summary>
internal static partial class PublishedBudgetGate
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    private static string Gate() => PublishedAcceptBudget.Paragraph("**R7.27**");

    private static string Words() => PublishedAcceptBudget.Paragraph("**R11.64**");

    /// <summary>How long a request waits for its turn: the number of seconds in "for at most N seconds from its arrival".</summary>
    internal static TimeSpan Wait() =>
        TimeSpan.FromSeconds(int.Parse(Only(WaitPhrase(), Gate(), "R7.27's wait"), CultureInfo.InvariantCulture));

    /// <summary>How many requests of one agent wait at once in one process: the number in "at most N requests of one agent SHALL wait at once".</summary>
    internal static int Waiters() =>
        int.Parse(Only(WaitersPhrase(), Gate(), "R7.27's queue"), CultureInfo.InvariantCulture);

    /// <summary>The problem type of the wait that ran out or the queue that was full: the code span after the code span <c>409</c>.</summary>
    internal static string InFlightType() => Only(InFlight(), Gate(), "R7.27's in-flight type");

    /// <summary>The problem type of a gate whose store cannot be reached: the code span after the code span <c>503</c>.</summary>
    internal static string UnavailableType() => Only(Unavailable(), Gate(), "R7.27's unavailable type");

    /// <summary>The status of <see cref="InFlightType"/>: the code span before it.</summary>
    internal static int InFlightStatus() => int.Parse(Only(InFlightStatusPhrase(), Gate(), "R7.27's in-flight status"), CultureInfo.InvariantCulture);

    /// <summary>The status of <see cref="UnavailableType"/>: the code span before it.</summary>
    internal static int UnavailableStatus() => int.Parse(Only(UnavailableStatusPhrase(), Gate(), "R7.27's unavailable status"), CultureInfo.InvariantCulture);

    /// <summary>The two reasons a request that waited and then found the budget spent is refused with, as the same request sent alone would be.</summary>
    internal static (string Posting, string Acceptance) BudgetReasons()
    {
        var match = Reasons().Matches(Gate());
        if (match.Count != 1) throw new InvalidOperationException($"{Errata}'s R7.27 yields {match.Count} readings of the two budget reasons, expected one");
        return (match[0].Groups[1].Value, match[0].Groups[2].Value);
    }

    /// <summary>The status a budget refusal is answered with: the code span before the two reasons.</summary>
    internal static int BudgetStatus() => int.Parse(Only(BudgetStatusPhrase(), Gate(), "R7.27's budget status"), CultureInfo.InvariantCulture);

    /// <summary>R7.27's paragraph, for a fact that looks for a phrase in it.</summary>
    internal static string GateParagraph() => Gate();

    /// <summary>R11.64's paragraph, for a fact that looks for a phrase in it.</summary>
    internal static string WordsParagraph() => Words();

    /// <summary>
    /// A clause R11.64 gives the words of a refusal, checked to be in the paragraph so that a clause cannot drift from the errata: the clause is returned
    /// when the paragraph holds it, and the read throws when it does not.
    /// </summary>
    internal static string Clause(string clause)
    {
        if (!Words().Contains(clause, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{Errata}'s R11.64 no longer holds the clause \"{clause}\"");

        return clause;
    }

    /// <summary>The tools R11.64 names as the ones that send a post or an acceptance: the code spans in the parenthesis after "an acceptance".</summary>
    internal static string[] ToolNames()
    {
        var match = Tools().Matches(Words());
        if (match.Count != 1) throw new InvalidOperationException($"{Errata}'s R11.64 yields {match.Count} lists of tools, expected one");
        return [.. Span().Matches(match[0].Groups[1].Value).Select(m => m.Groups[1].Value)];
    }

    private static string Only(Regex pattern, string paragraph, string what)
    {
        var found = pattern.Matches(paragraph);
        if (found.Count != 1)
            throw new InvalidOperationException($"{Errata} yields {found.Count} readings of {what}, expected one");

        return found[0].Groups[1].Value;
    }

    [GeneratedRegex(@"for at most (\d+) seconds from its arrival", RegexOptions.CultureInvariant)]
    private static partial Regex WaitPhrase();

    [GeneratedRegex(@"at most (\d+) requests of one agent SHALL wait at once", RegexOptions.CultureInvariant)]
    private static partial Regex WaitersPhrase();

    [GeneratedRegex(@"`409` `(curia/budget/[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex InFlight();

    [GeneratedRegex(@"`503` `(curia/budget/[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Unavailable();

    [GeneratedRegex(@"refused `(409)` `curia/budget/", RegexOptions.CultureInvariant)]
    private static partial Regex InFlightStatusPhrase();

    [GeneratedRegex(@"refused `(503)` `curia/budget/", RegexOptions.CultureInvariant)]
    private static partial Regex UnavailableStatusPhrase();

    [GeneratedRegex(@"\(`(403)`, `table-11/", RegexOptions.CultureInvariant)]
    private static partial Regex BudgetStatusPhrase();

    [GeneratedRegex(@"`(table-11/rate-budget-exhausted)` or `(table-11/accept-budget-exhausted)`", RegexOptions.CultureInvariant)]
    private static partial Regex Reasons();

    [GeneratedRegex(@"send a post or an acceptance \(([^)]*)\)", RegexOptions.CultureInvariant)]
    private static partial Regex Tools();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();
}
