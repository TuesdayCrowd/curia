using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R9.35's, R9.36's, R10.81's and R11.50's words as the errata publishes them, read from the errata at test time and not from the code:
/// the paragraph that begins with the requirement's number, up to the blank line that ends it. A fact takes the published names from here
/// and compares them with what the Forum binds, serves or a reader prints, so a name the errata gains or loses and the code does not turns
/// it red.
/// </summary>
internal static partial class PublishedSearchPage
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>
    /// The query members R9.35 binds: the code spans of its clause before "and of nothing else", in the order they appear. Seven are asserted
    /// before they are returned, and the clause must also name "the floor in force", which is a phrase and not a parameter name.
    /// </summary>
    internal static string[] BoundMembers()
    {
        var paragraph = Paragraph("**R9.35**");
        var end = paragraph.IndexOf("and of nothing else", StringComparison.Ordinal);
        if (end < 0) throw new InvalidOperationException($"{Errata}'s R9.35 no longer says \"and of nothing else\"");

        var clause = paragraph[..end];
        if (!clause.Contains("the floor in force", StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R9.35 no longer binds \"the floor in force\"");

        var names = Span().Matches(clause).Select(m => m.Groups[1].Value).Where(n => n.All(c => char.IsAsciiLetterLower(c) || c == '_')).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length != 7)
            throw new InvalidOperationException($"{Errata}'s R9.35 binds {names.Length} members ({string.Join(", ", names)}); the parse read something else");

        return names;
    }

    /// <summary>The members R9.35 says are NOT bound: the code spans between "and of nothing else" and "SHALL NOT be bound". Two are asserted.</summary>
    internal static string[] UnboundMembers()
    {
        var paragraph = Paragraph("**R9.35**");
        var start = paragraph.IndexOf("and of nothing else", StringComparison.Ordinal);
        var end = paragraph.IndexOf("SHALL NOT be bound", StringComparison.Ordinal);
        if (start < 0 || end < start) throw new InvalidOperationException($"{Errata}'s R9.35 no longer names what is not bound");

        var names = Span().Matches(paragraph[start..end]).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        if (names.Length != 2)
            throw new InvalidOperationException($"{Errata}'s R9.35 leaves {names.Length} members unbound ({string.Join(", ", names)}); the parse read something else");

        return names;
    }

    /// <summary>R9.35's refusal type, the code span that begins <c>curia/search/cursor-query-mismatch</c>.</summary>
    internal static string MismatchType()
    {
        var types = Span().Matches(Paragraph("**R9.35**")).Select(m => m.Groups[1].Value).Where(t => t.StartsWith("curia/search/cursor-query", StringComparison.Ordinal)).ToArray();
        if (types.Length != 1) throw new InvalidOperationException($"{Errata}'s R9.35 names {types.Length} mismatch types");
        return types[0];
    }

    /// <summary>The member names R9.36 publishes: the member after <c>provenance.</c> and the keys of the element it shows. Three are asserted.</summary>
    internal static string[] AcceptedAnswerNames()
    {
        var paragraph = Paragraph("**R9.36**");
        var member = Member().Matches(paragraph).Select(m => m.Groups[1].Value);
        var element = ElementKeys().Matches(paragraph).Select(m => m.Groups[1].Value);
        var names = member.Concat(element).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (names.Length != 3)
            throw new InvalidOperationException($"{Errata}'s R9.36 names {names.Length} members ({string.Join(", ", names)}); the parse read something else");

        return names;
    }

    /// <summary>
    /// The words R10.81 says a reader prints: its code spans (<c>none accepted</c>, <c>NOT STATED</c>) and the phrase "not served". Three are
    /// asserted before they are returned.
    /// </summary>
    internal static string[] R1081Tokens()
    {
        var paragraph = Paragraph("**R10.81**");
        var spans = Span().Matches(paragraph).Select(m => m.Groups[1].Value).Where(s => s.Contains(' ', StringComparison.Ordinal));
        var tokens = spans.Concat(paragraph.Contains("not served", StringComparison.Ordinal) ? ["not served"] : []).Distinct(StringComparer.Ordinal).ToArray();
        if (tokens.Length != 3)
            throw new InvalidOperationException($"{Errata}'s R10.81 names {tokens.Length} tokens ({string.Join(", ", tokens)}); the parse read something else");

        return tokens;
    }

    private static string Paragraph(string anchor)
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

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();

    [GeneratedRegex(@"`provenance\.(\w+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Member();

    [GeneratedRegex(@"""(\w+)"": ""<", RegexOptions.CultureInvariant)]
    private static partial Regex ElementKeys();
}
