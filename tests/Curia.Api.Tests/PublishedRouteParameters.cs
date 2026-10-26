using System.Text.RegularExpressions;

namespace Curia.Api.Tests;

/// <summary>
/// R9.37's two sets, its refusal phrase and R9.39's three member names, as the errata publishes them and read from the
/// errata, not from the code (as <see cref="PublishedSearchParameters"/> does for R9.29). A fact takes the published values
/// from here and compares them with the code's. Each parse asserts its own count before it returns, so a drift in the
/// errata's wording fails here by name and not as a vacuous pass.
/// </summary>
internal static partial class PublishedRouteParameters
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string BoardListingAnchor = "SHALL read exactly these query parameters:";
    private const string InboxAnchor = "SHALL read exactly these:";
    private const string PhraseStart = "SHALL say that the route ";
    private const string PhraseEnd = ", and SHALL name";

    private static string Text()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}");

        return Whitespace().Replace(File.ReadAllText(Path.Combine(dir.FullName, Errata)), " ");
    }

    private static string[] NamesAfter(string text, string anchor, int expected)
    {
        var at = text.IndexOf("**R9.37**", StringComparison.Ordinal);
        if (at < 0)
            throw new InvalidOperationException($"{Errata} has no R9.37");

        var start = text.IndexOf(anchor, at, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{Errata}'s R9.37 has no \"{anchor}\"");

        start += anchor.Length;
        var end = text.IndexOf(';', start);
        if (end < 0)
            throw new InvalidOperationException($"{Errata}'s R9.37 sentence \"{anchor}\" has no semicolon after its list");

        var names = Backticked().Matches(text[start..end]).Select(m => m.Groups[1].Value).ToArray();
        if (names.Length != expected)
            throw new InvalidOperationException($"{Errata}'s R9.37 lists {names.Length} backticked names after \"{anchor}\", not {expected}; the parse read something else");

        return names;
    }

    /// <summary>The one name the board listing reads.</summary>
    public static string[] BoardListing() => NamesAfter(Text(), BoardListingAnchor, 1);

    /// <summary>The five names the inbox reads.</summary>
    public static string[] Inbox() => NamesAfter(Text(), InboxAnchor, 5);

    /// <summary>What R9.37 says the board listing's refusal SHALL say the route is: "lists the whole board and does not page, filter or search".</summary>
    public static string BoardListingRefusalPhrase()
    {
        var text = Text();
        var at = text.IndexOf("**R9.37**", StringComparison.Ordinal);
        var start = at < 0 ? -1 : text.IndexOf(PhraseStart, at, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{Errata}'s R9.37 has no \"{PhraseStart}\"");

        start += PhraseStart.Length;
        var end = text.IndexOf(PhraseEnd, start, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException($"{Errata}'s R9.37 phrase after \"{PhraseStart}\" has no \"{PhraseEnd}\"");

        var phrase = text[start..end];
        if (!phrase.StartsWith("lists the whole board", StringComparison.Ordinal) || !phrase.EndsWith("search", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R9.37 refusal phrase reads \"{phrase}\"; the parse read something else");

        return phrase;
    }

    /// <summary>R9.39's three member names; each asserted present in R9.39's own text before it is returned.</summary>
    public static string[] CountMembers()
    {
        var text = Text();
        var at = text.IndexOf("**R9.39**", StringComparison.Ordinal);
        var end = at < 0 ? -1 : text.IndexOf("### Editorial amendments", at, StringComparison.Ordinal);
        if (at < 0 || end < 0)
            throw new InvalidOperationException($"{Errata} has no R9.39 clause");

        var clause = text[at..end];
        string[] members = ["excluded_as_own", "excluded_as_already_answered", "open_before_exclusions"];
        foreach (var member in members)
        {
            if (!clause.Contains($"`{member}`", StringComparison.Ordinal))
                throw new InvalidOperationException($"{Errata}'s R9.39 does not name `{member}`");
        }

        return members;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();
}
