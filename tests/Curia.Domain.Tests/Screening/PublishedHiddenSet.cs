using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// The hidden set as the errata publishes it (R10.76), read from the errata and not from the code:
/// a fact that compares the predicate with this set fails when either is edited without the other.
/// Shaped like the flag-budget fact in <c>TierPolicyTests</c>: find the paragraph, collapse its
/// whitespace, read the sentence that follows "exactly ".
/// </summary>
internal static partial class PublishedHiddenSet
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string Paragraph = "**R10.76**";
    private const int MinimumItems = 7;

    /// <summary>The published ranges, each inclusive, in the order the errata lists them.</summary>
    public static IReadOnlyList<(int From, int To)> Ranges()
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

        var exactly = paragraph.IndexOf("exactly ", StringComparison.Ordinal);
        if (exactly < 0)
            throw new InvalidOperationException($"{Errata}'s {Paragraph} paragraph has no \"exactly \" sentence");

        var from = exactly + "exactly ".Length;
        var stop = paragraph.IndexOf('.', from);
        if (stop < 0)
            throw new InvalidOperationException($"{Errata}'s {Paragraph} \"exactly \" sentence does not end");

        var items = Item().Matches(paragraph[from..stop]);
        var ranges = new List<(int From, int To)>();

        foreach (Match item in items)
        {
            var low = int.Parse(item.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var high = item.Groups[2].Success
                ? int.Parse(item.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : low;

            if (high < low)
                throw new InvalidOperationException($"{Errata}'s {Paragraph} lists a reversed range, U+{low:X4} to U+{high:X4}");

            ranges.Add((low, high));
        }

        if (ranges.Count < MinimumItems)
            throw new InvalidOperationException(
                $"{Errata}'s {Paragraph} \"exactly \" sentence yields {ranges.Count} items, expected at least {MinimumItems}");

        if (!Contains(ranges, 0x00AD) || !Contains(ranges, 0xE0041))
            throw new InvalidOperationException(
                $"{Errata}'s {Paragraph} set does not hold U+00AD and U+E0041; the parse read something else");

        return ranges;
    }

    /// <summary>Whether <paramref name="value"/> lies in one of <paramref name="ranges"/>.</summary>
    public static bool Contains(IReadOnlyList<(int From, int To)> ranges, int value) =>
        ranges.Any(r => value >= r.From && value <= r.To);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"U\+([0-9A-F]{4,6})(?: to U\+([0-9A-F]{4,6}))?", RegexOptions.CultureInvariant)]
    private static partial Regex Item();
}
