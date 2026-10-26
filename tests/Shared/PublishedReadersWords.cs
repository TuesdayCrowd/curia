using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// Errata G40's words (R8.71, R9.41, R10.82, R11.52, R11.53) as the errata publishes them, read from the errata at test time and not from the
/// code: the paragraph that begins <c>**R8.71**</c> and so on, up to the blank line that ends it. A fact takes the published member name, the
/// published states and the published obligations from here, so a word the errata names and the code drops turns it red. Each parse asserts
/// its count before it returns, so a rewording that the parse no longer reads fails here and not as a silent empty list.
/// </summary>
internal static partial class PublishedReadersWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>R8.71's member: the one code span of its first sentence that is a snake_case name (<c>replaced_answer</c>). Count one is asserted.</summary>
    internal static string R871Member()
    {
        var names = Spans(Paragraph("**R8.71**")).Where(t => SnakeCase().IsMatch(t) && t != "null").Distinct(StringComparer.Ordinal).ToArray();
        return One(names, "R8.71's member name");
    }

    /// <summary>
    /// The four states R8.71 says a reader's receipt tells apart, as the phrases the errata uses: that the replaced answer is named and no longer
    /// stands, that no earlier answer stood, that a replay says nothing of replacement, and <c>NOT STATED</c>. Each is kept only if the paragraph
    /// holds it, and four are asserted before they are returned.
    /// </summary>
    internal static string[] R871States()
    {
        var paragraph = Paragraph("**R8.71**");
        string[] candidates = ["no longer stands", "no earlier answer stood", "nothing of replacement", "NOT STATED"];
        return Kept(paragraph, candidates, 4, "R8.71");
    }

    /// <summary>R9.41's member: the one snake_case code span that is not <c>accepted_answer</c> or <c>post_id</c> (<c>superseded_by</c>). Count one is asserted.</summary>
    internal static string R941Member()
    {
        var names = Spans(Paragraph("**R9.41**"))
            .Where(t => SnakeCase().IsMatch(t) && t is not ("accepted_answer" or "post_id") && !t.Contains('.', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return One(names, "R9.41's member name");
    }

    /// <summary>The upper-case words R10.82 requires a statement to hold: <c>FORKED</c> and <c>NOT STATED</c>. Two are asserted.</summary>
    internal static string[] R1082Tokens()
    {
        string[] candidates = ["FORKED", "NOT STATED"];
        return Kept(Paragraph("**R10.82**"), candidates, 2, "R10.82");
    }

    /// <summary>The tools R11.52 names: the three that read before they sign, <c>curia_verify</c> and <c>curia_search</c>, in order of appearance. Five are asserted.</summary>
    internal static string[] R1152Tools()
    {
        var tools = Spans(Paragraph("**R11.52**")).Where(t => t.StartsWith("curia_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        return Counted(tools, 5, "R11.52's tools");
    }

    /// <summary>The two arguments R11.52 names beside its tools: <c>expectedDigest</c> and the dotted <c>criteria.cursor</c>. Two are asserted.</summary>
    internal static string[] R1152Arguments()
    {
        var args = Spans(Paragraph("**R11.52**"))
            .Where(t => !t.StartsWith("curia_", StringComparison.Ordinal) && t is not ("NOT SENT" or "sha256:" or "isError"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return Counted(args, 2, "R11.52's arguments");
    }

    /// <summary>
    /// R11.53's five obligations, by tool: the clause of the paragraph that begins with the tool's code span, up to the semicolon or the
    /// sentence that follows. Five are asserted, and a clause is returned whole so a fact can check the words it requires against it.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> R1153Obligations()
    {
        var paragraph = Paragraph("**R11.53**");
        var body = paragraph[..paragraph.IndexOf("A probe SHALL", StringComparison.Ordinal)];
        var clauses = body.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var byTool = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var clause in clauses)
        {
            var match = ToolSpan().Match(clause);
            if (match.Success) byTool[match.Groups[1].Value] = clause;
        }

        if (byTool.Count != 5)
            throw new InvalidOperationException($"{Errata}'s R11.53 holds {byTool.Count} per-tool clauses ({string.Join(", ", byTool.Keys)}); the parse read something else");

        return byTool;
    }

    private static string[] Kept(string paragraph, string[] candidates, int expected, string requirement)
    {
        var kept = candidates.Where(c => paragraph.Contains(c, StringComparison.Ordinal)).ToArray();
        if (kept.Length != expected)
            throw new InvalidOperationException($"{Errata}'s {requirement} holds {kept.Length} of the {expected} words the fact reads");

        return kept;
    }

    private static string One(string[] found, string what) =>
        Counted(found, 1, what)[0];

    private static string[] Counted(string[] found, int expected, string what)
    {
        if (found.Length != expected)
            throw new InvalidOperationException($"{Errata} holds {found.Length} of {what} ({string.Join(", ", found)}); expected {expected}");

        return found;
    }

    private static IEnumerable<string> Spans(string paragraph) => Span().Matches(paragraph).Select(m => m.Groups[1].Value);

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

    [GeneratedRegex(@"^[a-z]+(_[a-z]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex SnakeCase();

    [GeneratedRegex(@"`(curia_[a-z]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex ToolSpan();
}
