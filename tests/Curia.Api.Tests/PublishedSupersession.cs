using System.Text.RegularExpressions;

namespace Curia.Api.Tests;

/// <summary>
/// R9.32's member names as the errata publishes them, read from the errata and not from the code: in the paragraph that
/// begins <c>**R9.32**</c> (up to the blank line that ends it), the member after <c>provenance.</c> and the keys of the
/// element it shows, <c>{"digest": ..., "post_id": ...}</c>. A fact takes the published names from here and compares them
/// with what the Forum serves.
/// </summary>
internal static partial class PublishedSupersession
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string Anchor = "**R9.32**";

    /// <summary>The three names, sorted ordinally; the count of three is asserted before they are returned.</summary>
    public static string[] Names()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}");

        var text = File.ReadAllText(Path.Combine(dir.FullName, Errata));
        var start = text.IndexOf(Anchor, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{Errata} has no paragraph that begins \"{Anchor}\"");

        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        var paragraph = Whitespace().Replace(text[start..(end < 0 ? text.Length : end)], " ");

        var member = Member().Matches(paragraph).Select(m => m.Groups[1].Value);
        var element = ElementKeys().Matches(paragraph).Select(m => m.Groups[1].Value);
        var names = member.Concat(element).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (names.Length != 3)
            throw new InvalidOperationException($"{Errata}'s R9.32 names {names.Length} members ({string.Join(", ", names)}); the parse read something else");

        return names;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"`provenance\.(\w+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Member();

    [GeneratedRegex(@"""(\w+)"": ""<", RegexOptions.CultureInvariant)]
    private static partial Regex ElementKeys();
}
