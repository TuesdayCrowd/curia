using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// R8.77's and R8.78's problem types as the errata publishes them (errata G45), read from the errata at test time and not from the code that emits
/// them: the two paragraphs that begin <c>**R8.77**</c> and <c>**R8.78**</c>, up to the blank line that ends each. A slug is the code span that follows
/// the <c>409</c> code span in the paragraph, so a fact can compare what the Forum answers with the published spelling, and a constant whose value is
/// changed moves the code and not the errata.
/// </summary>
internal static partial class PublishedRevisionSlugs
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    /// <summary>R8.77's first <c>409</c> slug: a revision of a post that already has a served one. Two <c>409</c> slugs are asserted in the paragraph.</summary>
    internal static string AlreadyRevised() => Conflicts("**R8.77**", 2)[0];

    /// <summary>R8.77's second <c>409</c> slug: another revision of the post is being recorded.</summary>
    internal static string RevisionInFlight() => Conflicts("**R8.77**", 2)[1];

    /// <summary>R8.78's one <c>409</c> slug: the digest is a post the Forum has stored.</summary>
    internal static string AlreadyRecorded() => Conflicts("**R8.78**", 1)[0];

    private static string[] Conflicts(string anchor, int expected)
    {
        var slugs = Conflict().Matches(Paragraph(anchor)).Select(m => m.Groups[1].Value).ToArray();
        if (slugs.Length != expected)
            throw new InvalidOperationException($"{Errata}'s {anchor} names {slugs.Length} 409 problem types ({string.Join(", ", slugs)}), not {expected}; the parse read something else");

        return slugs;
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

    [GeneratedRegex(@"`409` `(curia/ingest/[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Conflict();
}
