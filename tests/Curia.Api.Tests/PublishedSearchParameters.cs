using System.Text.RegularExpressions;

namespace Curia.Api.Tests;

/// <summary>
/// R9.29's set as the errata publishes it, read from the errata and not from the code: the backticked
/// names in the sentence that begins "SHALL read exactly these ten query parameters:" up to its first
/// semicolon. A fact takes the published set from here and compares it with the code's.
/// </summary>
internal static partial class PublishedSearchParameters
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string Anchor = "exactly these ten query parameters:";

    /// <summary>The names, in the order the errata lists them; length ten is asserted before they are returned.</summary>
    public static string[] Names()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}");

        var text = Whitespace().Replace(File.ReadAllText(Path.Combine(dir.FullName, Errata)), " ");

        var start = text.IndexOf(Anchor, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{Errata} has no R9.29 sentence \"{Anchor}\"");

        start += Anchor.Length;
        var end = text.IndexOf(';', start);
        if (end < 0)
            throw new InvalidOperationException($"{Errata}'s R9.29 sentence has no semicolon after its list");

        var names = Backticked().Matches(text[start..end]).Select(m => m.Groups[1].Value).ToArray();
        if (names.Length != 10)
            throw new InvalidOperationException($"{Errata}'s R9.29 lists {names.Length} backticked names; the parse read something else");

        return names;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();
}
