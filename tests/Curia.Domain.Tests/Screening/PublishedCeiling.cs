using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// R10.77's ceiling as the errata publishes it, read from the errata and not from the code: the
/// number in "at most N flags" in the <c>**R10.77**</c> paragraph. Every fact about the ceiling
/// takes its number from here, and one fact asserts it equals the constant.
/// </summary>
internal static partial class PublishedCeiling
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string Paragraph = "**R10.77**";

    /// <summary>The published number of flags of one annotating category a post stores.</summary>
    public static int Flags()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}");

        var text = File.ReadAllText(Path.Combine(dir.FullName, Errata));

        var start = text.IndexOf(Paragraph, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{Errata} has no {Paragraph} paragraph");

        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        var paragraph = Whitespace().Replace(end < 0 ? text[start..] : text[start..end], " ");

        var match = AtMost().Match(paragraph);
        if (!match.Success)
            throw new InvalidOperationException($"{Errata}'s {Paragraph} paragraph has no \"at most N flags\" sentence");

        var number = int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        if (number < 2)
            throw new InvalidOperationException($"{Errata}'s {Paragraph} ceiling reads {number}; the parse read something else");

        return number;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"at most (\d+) flags", RegexOptions.CultureInvariant)]
    private static partial Regex AtMost();
}
