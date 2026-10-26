using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R8.70's words as the errata publishes them, read from the errata at test time and not from the code: the paragraph that
/// begins <c>**R8.70**</c>, up to the blank line that ends it. A fact takes the published slugs, statuses and kind from here and
/// compares them with what the Forum answers, so a word the errata names and the code drops turns it red.
/// </summary>
internal static partial class PublishedParentRule
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>The two problem types the paragraph names, in the order it names them: not-found, then not-a-question. Two are asserted before they are returned.</summary>
    internal static string[] Slugs()
    {
        var slugs = Spans().Where(s => s.StartsWith("curia/ingest/", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (slugs.Length != 2)
            throw new InvalidOperationException($"{Errata}'s R8.70 names {slugs.Length} problem types ({string.Join(", ", slugs)}); the parse read something else");

        return slugs;
    }

    /// <summary>The status a refused parent answers, and the status an unreadable log answers: the two three-digit code spans, in order. Two are asserted before they are returned.</summary>
    internal static (int Refused, int Unreadable) Statuses()
    {
        var codes = Spans().Where(s => StatusCode().IsMatch(s)).Distinct(StringComparer.Ordinal).Select(s => int.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        if (codes.Length != 2)
            throw new InvalidOperationException($"{Errata}'s R8.70 names {codes.Length} statuses; the parse read something else");

        return (codes[0], codes[1]);
    }

    /// <summary>The kind an answer's parent must be: the code span <c>question</c>. Its presence is asserted before it is returned.</summary>
    internal static string AnswerParentKind()
    {
        if (!Spans().Contains("question", StringComparer.Ordinal))
            throw new InvalidOperationException($"{Errata}'s R8.70 does not name the kind `question` in a code span");

        return "question";
    }

    private static List<string> Spans() =>
        Span().Matches(Paragraph("**R8.70**")).Select(m => m.Groups[1].Value).ToList();

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
