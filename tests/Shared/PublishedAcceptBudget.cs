using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The words of errata G46 as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> and never from the code
/// a fact is about: the acceptance budget's reason and its three numbers (R7.26), the causes a denial's title names (R7.24 (rev.)), the race's problem type
/// and bound (R8.79) and the detector version (R10.86). A fact that compared a response with a production constant could not catch a change to that
/// constant; this is the other side of the comparison. Each paragraph is the text from its bold anchor to the blank line that ends it, whitespace folded.
/// </summary>
internal static partial class PublishedAcceptBudget
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>R7.26's reason: the code span that follows "the reason" in the paragraph.</summary>
    internal static string Reason() => One(Reasons(), Paragraph("**R7.26**"), "R7.26's reason");

    /// <summary>R7.26's three numbers, T0, T1 and T2, from the one sentence that publishes them.</summary>
    internal static int[] Numbers()
    {
        var paragraph = Paragraph("**R7.26**");
        var matches = Budgets().Matches(paragraph);
        if (matches.Count != 1)
            throw new InvalidOperationException($"{Errata}'s R7.26 yields {matches.Count} acceptance-budget sentences, expected one naming three numbers");

        return [.. matches[0].Groups.Values.Skip(1).Select(g => int.Parse(g.Value, CultureInfo.InvariantCulture))];
    }

    /// <summary>
    /// What a description of the acceptance must say about the two budgets, as problems (empty when it says it). The errata's vocabulary is read from R7.26
    /// ("word the refusal as the <c>acceptance budget</c>", "that the <c>posting budget</c> is untouched"); the sentence is "spends the acceptance budget,
    /// not the posting budget", and a text that says an acceptance spends the posting budget, alone or beside its own, is a problem.
    /// </summary>
    internal static string[] SpendProblems(string text)
    {
        var paragraph = Paragraph("**R7.26**");
        var own = One(OwnBudgetTerm(), paragraph, "R7.26's name for the acceptance's own budget");
        var posting = One(PostingBudgetTerm(), paragraph, "R7.26's name for the budget an acceptance leaves untouched");
        var folded = Whitespace().Replace(text, " ");

        var problems = new List<string>();
        if (!folded.Contains($"spends the {own}, not the {posting}", StringComparison.Ordinal))
            problems.Add($"does not say \"spends the {own}, not the {posting}\"");
        if (Regex.IsMatch(folded, $@"\bspends? (?:both )?the {Regex.Escape(posting)}", RegexOptions.CultureInvariant))
            problems.Add($"says an acceptance spends the {posting}");

        return [.. problems];
    }

    /// <summary>R7.24 (rev.)'s list of causes, one per element, the parentheticals taken off.</summary>
    internal static string[] Causes()
    {
        var paragraph = Paragraph("**R7.24 (rev.)**");
        const string marker = "SHALL name its cause:";
        var at = paragraph.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException("R7.24 (rev.) no longer reads 'SHALL name its cause:'");

        var list = paragraph[(at + marker.Length)..];
        list = list[..list.IndexOf(". ", StringComparison.Ordinal)];
        list = Parenthetical().Replace(list, string.Empty);
        return list.Split([", ", " or "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>R7.24 (rev.)'s paragraph, for a fact that looks for a phrase in it.</summary>
    internal static string CauseParagraph() => Paragraph("**R7.24 (rev.)**");

    /// <summary>R8.79's problem type: the code span that follows its <c>503</c> code span.</summary>
    internal static string ContendedType() => One(Contended(), Paragraph("**R8.79**"), "R8.79's problem type");

    /// <summary>R8.79's bound: "up to N attempts in all".</summary>
    internal static int Attempts()
    {
        var matches = AttemptsPhrase().Matches(Paragraph("**R8.79**"));
        if (matches.Count != 1)
            throw new InvalidOperationException($"{Errata}'s R8.79 yields {matches.Count} attempt bounds, expected one");

        return int.Parse(matches[0].Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>R10.86's detector version: the code span that follows "the detector version SHALL be".</summary>
    internal static string DetectorVersion() => One(Version(), Paragraph("**R10.86**"), "R10.86's detector version");

    /// <summary>The paragraph that begins with <paramref name="anchor"/>, to the blank line that ends it, whitespace folded.</summary>
    internal static string Paragraph(string anchor)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}");

        var text = File.ReadAllText(Path.Combine(dir.FullName, Errata));
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{anchor}\"");

        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        return Whitespace().Replace(text[start..(end < 0 ? text.Length : end)], " ");
    }

    private static string One(Regex pattern, string paragraph, string what)
    {
        var matches = pattern.Matches(paragraph);
        if (matches.Count != 1)
            throw new InvalidOperationException($"{Errata} yields {matches.Count} readings of {what}, expected one");

        return matches[0].Groups[1].Value;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\s*\(R\d+\.\d+\)", RegexOptions.CultureInvariant)]
    private static partial Regex Parenthetical();

    [GeneratedRegex(@"with the reason `(table-1[01]/[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Reasons();

    [GeneratedRegex(@"word the refusal as the (acceptance budget)", RegexOptions.CultureInvariant)]
    private static partial Regex OwnBudgetTerm();

    [GeneratedRegex(@"that the (posting budget) is untouched", RegexOptions.CultureInvariant)]
    private static partial Regex PostingBudgetTerm();

    [GeneratedRegex(@"(\d+) acceptances at T0, (\d+) at T1 and (\d+) at T2", RegexOptions.CultureInvariant)]
    private static partial Regex Budgets();

    [GeneratedRegex(@"`503` `(curia/accept/[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Contended();

    [GeneratedRegex(@"up to (\d+) attempts in all", RegexOptions.CultureInvariant)]
    private static partial Regex AttemptsPhrase();

    [GeneratedRegex(@"detector version SHALL be `(secrets/\d{4}-\d{2}-\d{2})`", RegexOptions.CultureInvariant)]
    private static partial Regex Version();
}
