using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Domain.Tests.Verification;

/// <summary>
/// Table 13 as published, parsed out of the white paper at test time -- the same arrangement
/// <c>PublishedTable10</c> and <c>PublishedTable11</c> use, and for the same reason: a constant
/// checked against the constant it was transcribed from checks nothing.
/// </summary>
internal static partial class PublishedTable13
{
    private const string WhitePaper = "curia-agent-forum-WHITEPAPER.md";
    private const string Caption = "**Table 13 — Verification levels**";

    internal static IReadOnlyDictionary<string, Row> Rows { get; } = Parse();

    internal sealed record Row(string Name, string Meaning, string Weight)
    {
        /// <summary>The "≥ n" in the meaning, when there is one.</summary>
        public int? Threshold =>
            AtLeast().Match(Meaning) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static Dictionary<string, Row> Parse()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, WhitePaper)))
            dir = dir.Parent;

        var path = dir is null
            ? throw new InvalidOperationException($"{WhitePaper} not found above {AppContext.BaseDirectory}")
            : Path.Combine(dir.FullName, WhitePaper);

        var text = File.ReadAllText(path);
        var caption = text.IndexOf(Caption, StringComparison.Ordinal);
        if (caption < 0)
            throw new InvalidOperationException($"{WhitePaper} no longer contains '{Caption}'.");

        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        var seenHeader = false;

        foreach (var raw in text[(caption + Caption.Length)..].Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith('|'))
            {
                if (seenHeader && rows.Count > 0) break;
                continue;
            }

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();

            if (!seenHeader)
            {
                if (cells is ["Level", "Name", "Meaning", "Ranking weight"])
                    seenHeader = true;
                else if (cells.Length > 0 && cells[0] == "Level")
                    throw new InvalidOperationException("Table 13's columns changed: " + string.Join(" | ", cells));
                continue;
            }

            if (cells.All(c => c.Length == 0 || c.All(ch => ch is '-' or ':'))) continue;
            if (cells.Length != 4)
                throw new InvalidOperationException($"Table 13 row has {cells.Length} cells, expected 4: {line}");

            rows.Add(cells[0], new Row(cells[1], cells[2], cells[3]));
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("Table 13 parsed to zero rows.");

        return rows;
    }

    [GeneratedRegex(@"≥\s*(\d+)")]
    private static partial Regex AtLeast();
}
