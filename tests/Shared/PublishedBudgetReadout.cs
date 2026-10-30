using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words of errata G49 as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never from the code a
/// fact is about (SEB49, SEB50): R7.28's window, instant rule and the three budgets, R7.29's refusal token and receipt rule, R9.50's route, members,
/// order, header, statuses and problem types, R11.65's spans, the version and Table 10's new row as the errata quotes it; plus the limits of the three
/// budgets, read from Table 11 (through the Domain tests' <c>PublishedTable11</c>), R7.26 and R7.22. Each paragraph is read through
/// <see cref="PublishedAcceptBudget.Paragraph(string)"/>, so an edit that removes a span turns a fact red and does not shrink what is compared.
/// </summary>
internal static partial class PublishedBudgetReadout
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string WhitePaper = "curia-agent-forum-WHITEPAPER.md";

    internal static string Standing() => PublishedAcceptBudget.Paragraph("**R7.28**");

    internal static string Receipts() => PublishedAcceptBudget.Paragraph("**R7.29**");

    internal static string Route() => PublishedAcceptBudget.Paragraph("**R9.50**");

    internal static string Words() => PublishedAcceptBudget.Paragraph("**R11.65**");

    /// <summary>R7.28's window: "the trailing N seconds" (the first number of seconds the paragraph gives).</summary>
    internal static int WindowSeconds() =>
        int.Parse(Only(WindowPhrase(), Standing(), "R7.28's window").Replace(",", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);

    /// <summary>R9.50's route members, in the order the paragraph publishes them.</summary>
    internal static string[] ResponseMembers() => Spans(Between(Route(), "a body of the members ", ", where "));

    /// <summary>R9.50's budget names, in the order the paragraph publishes them.</summary>
    internal static string[] Names() => Spans(Between(Route(), "entries named ", ", in that order"));

    /// <summary>R9.50's entry members.</summary>
    internal static string[] EntryMembers() => Spans(Between(Route(), "each with the members ", "; every number"));

    /// <summary>R9.50's receipt members: the members of the <c>budget</c> member of a receipt.</summary>
    internal static string[] ReceiptMembers() => Spans(Between(Route(), "SHALL carry a member `budget` of the members ", ", and no "));

    /// <summary>R9.50's problem type for a query name: the code span after "`400`".</summary>
    internal static string UnknownParameterType() => Only(Type400(), Route(), "R9.50's unknown-parameter type");

    internal static string FlagStoreType() => Only(TypeFlagStore(), Route(), "R9.50's flag store type");

    internal static string LogUnreadableType() => Only(TypeLog(), Route(), "R9.50's unreadable log type");

    /// <summary>R9.50's route, "GET /v1/budget".</summary>
    internal static string RouteSpan() => Only(RoutePhrase(), Route(), "R9.50's route");

    /// <summary>R9.50's header: the code span after "carry".</summary>
    internal static string Header() => Only(HeaderPhrase(), Route(), "R9.50's header");

    /// <summary>R7.29's token a budget refusal's detail ends with: "next_slot_at=".</summary>
    internal static string RefusalToken() => Only(TokenPhrase(), Receipts(), "R7.29's refusal token");

    /// <summary>R9.50's two credential words, in the order given.</summary>
    internal static string[] CredentialWords() => Spans(Only(CredentialPhrase(), Route(), "R9.50's credential words"));

    /// <summary>R11.65's version: "`curia-mcp` is N".</summary>
    internal static string Version() => Only(VersionPhrase(), Words(), "R11.65's version");

    /// <summary>R11.65's spans, in the order given. A fact looks one up by its text, so a removed span is a thrown read and not a missed comparison.</summary>
    internal static string Span(string text)
    {
        var spans = Spans(Words());
        if (!spans.Contains(text, StringComparer.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R11.65 no longer holds the span `{text}`");

        return text;
    }

    /// <summary>A phrase R11.65 gives in plain text, checked to be in the paragraph.</summary>
    internal static string Phrase(string text)
    {
        if (!Words().Contains(text, StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R11.65 no longer holds the phrase \"{text}\"");

        return text;
    }

    /// <summary>The row G49's editorial table quotes for Table 10: the resource, the action with its qualifier, and the five cells.</summary>
    internal static (string Resource, string Action, string[] Cells) Table10Row()
    {
        var line = ErrataLines().Where(l => l.StartsWith("| Table 10 (white paper", StringComparison.Ordinal) && l.Contains("`budget` / `read`", StringComparison.Ordinal)).ToArray();
        if (line.Length != 1) throw new InvalidOperationException($"{Errata} yields {line.Length} lines quoting the Table 10 budget row, expected one");

        var match = RowPhrase().Match(line[0]);
        if (!match.Success) throw new InvalidOperationException($"{Errata}'s Table 10 row line is not in the form 'Gains the row `a` / `b` (own), `cells`'");

        return (match.Groups[1].Value, match.Groups[2].Value, [.. match.Groups[3].Value.Split('|', StringSplitOptions.TrimEntries)]);
    }

    /// <summary>The denial count G49 gives for the pinned published denials after the row: "from N to M" (M).</summary>
    internal static int DenialsAfter()
    {
        var line = ErrataLines().Single(l => l.StartsWith("| Table 10 (white paper", StringComparison.Ordinal) && l.Contains("`budget` / `read`", StringComparison.Ordinal));
        var match = MovesPhrase().Match(line);
        return match.Success ? int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) : throw new InvalidOperationException($"{Errata}'s Table 10 row line gives no 'from N to M'");
    }

    /// <summary>The white paper's Table 10 lines whose first two cells are <c>`budget`</c> and a <c>`read`</c>, with their cells.</summary>
    internal static string[][] WhitePaperBudgetRows() =>
        [.. File.ReadAllLines(Find(WhitePaper))
            .Where(l => l.StartsWith("| `budget` |", StringComparison.Ordinal))
            .Select(l => l.Trim().Trim('|').Split('|', StringSplitOptions.TrimEntries))];

    /// <summary>The three budgets' limits at T0, T1 and T2, by budget name: Table 11 for posting, R7.26 for acceptance, R7.22 for flag.</summary>
    internal static IReadOnlyDictionary<string, int[]> Limits(IReadOnlyList<int> postsFromTable11)
    {
        ArgumentNullException.ThrowIfNull(postsFromTable11);
        var flag = FlagLimitsPhrase().Match(PublishedAcceptBudget.Paragraph("**R7.22**"));
        if (!flag.Success) throw new InvalidOperationException($"{Errata}'s R7.22 no longer reads 'N flags at T0, N at T1 and N at T2'");

        return new Dictionary<string, int[]>(StringComparer.Ordinal)
        {
            ["posting"] = [.. postsFromTable11],
            ["acceptance"] = PublishedAcceptBudget.Numbers(),
            ["flag"] = [.. flag.Groups.Values.Skip(1).Select(g => int.Parse(g.Value, CultureInfo.InvariantCulture))],
        };
    }

    private static string[] ErrataLines() => File.ReadAllLines(Find(Errata));

    private static string Find(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, name)))
            dir = dir.Parent;

        return dir is null ? throw new InvalidOperationException($"{name} not found above {AppContext.BaseDirectory}") : Path.Combine(dir.FullName, name);
    }

    private static string Between(string paragraph, string from, string to)
    {
        var start = paragraph.IndexOf(from, StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException($"{Errata} no longer holds \"{from}\"");

        start += from.Length;
        var end = paragraph.IndexOf(to, start, StringComparison.Ordinal);
        return end < 0 ? throw new InvalidOperationException($"{Errata} no longer holds \"{to}\" after \"{from}\"") : paragraph[start..end];
    }

    private static string[] Spans(string text) => [.. Span().Matches(text).Select(m => m.Groups[1].Value)];

    private static string Only(Regex pattern, string paragraph, string what)
    {
        var found = pattern.Matches(paragraph);
        if (found.Count != 1) throw new InvalidOperationException($"{Errata} yields {found.Count} readings of {what}, expected one");

        return found[0].Groups[1].Value;
    }

    [GeneratedRegex(@"trailing ([\d,]+) seconds", RegexOptions.CultureInvariant)]
    private static partial Regex WindowPhrase();

    [GeneratedRegex(@"`400` `(curia/budget/[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Type400();

    [GeneratedRegex(@"`503` `(curia/flag/[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex TypeFlagStore();

    [GeneratedRegex(@"`503` `(curia/log/[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex TypeLog();

    [GeneratedRegex(@"`(GET /v1/budget)`", RegexOptions.CultureInvariant)]
    private static partial Regex RoutePhrase();

    [GeneratedRegex(@"SHALL carry `(Cache-Control: no-store)`", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderPhrase();

    [GeneratedRegex(@"end its detail with `(next_slot_at=)`", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPhrase();

    [GeneratedRegex(@"other than (`active` and `quarantined`)", RegexOptions.CultureInvariant)]
    private static partial Regex CredentialPhrase();

    [GeneratedRegex(@"`curia-mcp` is (\d+\.\d+\.\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPhrase();

    [GeneratedRegex(@"Gains the row `(\w+)` / `(\w+)` \(own\), `([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex RowPhrase();

    [GeneratedRegex(@"from (\d+) to (\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex MovesPhrase();

    [GeneratedRegex(@"(\d+) flags at T0, (\d+) at T1 and (\d+) at T2", RegexOptions.CultureInvariant)]
    private static partial Regex FlagLimitsPhrase();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();
}
