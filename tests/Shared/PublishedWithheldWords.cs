using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R8.72, R9.42 and R11.54's words (errata G41) as the errata publishes them, read at test time and not from the code that emits them: the
/// paragraph that begins each anchor, up to the blank line that ends it. A fact takes the problem types, the statuses and the tools from here
/// and compares them with what the Forum and the readers do, so a word the errata names and the code drops, or the other way, turns it red.
/// Every count is asserted before a value is returned, so a parse that read something else fails by name and not by a wrong comparison.
/// The refusal's title is not in the entry's text and so is not parsed; a fact asserts the id and no author's word instead.
/// </summary>
internal static partial class PublishedWithheldWords
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>R9.42's two read types, in the order it names them: withheld, then not-found. Two are asserted before they are returned.</summary>
    private static string[] ReadTypes()
    {
        var types = Spans("**R9.42**").Where(s => s.StartsWith("curia/posts/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (types.Length != 2)
            throw new InvalidOperationException($"{Errata}'s R9.42 names {types.Length} problem types under curia/posts/ ({string.Join(", ", types)}); the parse read something else");

        return types;
    }

    /// <summary>The type a post that exists and is not served answers: R9.42's first. R11.54 must name the same one.</summary>
    internal static string PostWithheldType()
    {
        var type = ReadTypes()[0];
        var reader = Spans("**R11.54**").Where(s => s.StartsWith("curia/posts/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (reader.Length != 1 || !string.Equals(reader[0], type, StringComparison.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R11.54 names {string.Join(", ", reader)} where R9.42 names {type}");

        return type;
    }

    /// <summary>The type an id no post bears still answers: R9.42's second.</summary>
    internal static string PostNotFoundType() => ReadTypes()[1];

    /// <summary>The type a reply under a post that is not served is refused with: the one `curia/ingest/` span of R8.72.</summary>
    internal static string ParentWithheldType()
    {
        var types = Spans("**R8.72**").Where(s => s.StartsWith("curia/ingest/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (types.Length != 1)
            throw new InvalidOperationException($"{Errata}'s R8.72 names {types.Length} problem types ({string.Join(", ", types)}); the parse read something else");

        return types[0];
    }

    /// <summary>The status R8.72 refuses a reply with: the one three-digit code span of the entry.</summary>
    internal static int ParentWithheldStatus() => OneStatus("**R8.72**");

    /// <summary>The status R9.42 answers a read with: the one three-digit code span of the entry.</summary>
    internal static int PostWithheldStatus() => OneStatus("**R9.42**");

    private const string DescriptionsSentence = "The descriptions of";

    /// <summary>The tools R11.54 says SHALL answer a withheld post <c>NOT SENT</c>: the <c>curia_</c> code spans before its sentence on descriptions. Three are asserted before they are returned.</summary>
    internal static string[] ReplyTools() => Tools(before: true);

    /// <summary>The tools whose descriptions R11.54 says SHALL state it: the <c>curia_</c> code spans of its sentence on descriptions. Three are asserted before they are returned.</summary>
    internal static string[] DescribedTools() => Tools(before: false);

    private static string[] Tools(bool before)
    {
        var paragraph = Paragraph("**R11.54**");
        var split = paragraph.IndexOf(DescriptionsSentence, StringComparison.Ordinal);
        if (split < 0)
            throw new InvalidOperationException($"{Errata}'s R11.54 has no sentence that begins \"{DescriptionsSentence}\"");

        var part = before ? paragraph[..split] : paragraph[split..];
        var tools = Span().Matches(part).Select(m => m.Groups[1].Value).Where(s => s.StartsWith("curia_", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (tools.Length != 3)
            throw new InvalidOperationException($"{Errata}'s R11.54 names {tools.Length} tools {(before ? "before" : "in")} its sentence on descriptions ({string.Join(", ", tools)}); the parse read something else");

        return tools;
    }

    private static int OneStatus(string anchor)
    {
        var codes = Spans(anchor).Where(s => StatusCode().IsMatch(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (codes.Length != 1)
            throw new InvalidOperationException($"{Errata}'s {anchor} names {codes.Length} statuses ({string.Join(", ", codes)}); the parse read something else");

        return int.Parse(codes[0], System.Globalization.CultureInfo.InvariantCulture);
    }

    private static List<string> Spans(string anchor) =>
        Span().Matches(Paragraph(anchor)).Select(m => m.Groups[1].Value).ToList();

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

    [GeneratedRegex(@"^\d{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex StatusCode();
}
