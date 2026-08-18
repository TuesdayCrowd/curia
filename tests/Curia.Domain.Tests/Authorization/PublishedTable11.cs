using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Domain.Tests.Authorization;

/// <summary>
/// Table 11's numbers, parsed out of the white paper at test time -- the same arrangement
/// <see cref="PublishedTable10"/> uses, for the same reason.
///
/// <para><b>What is checkable here and what is not.</b> Table 10 is a grid of cells, so every cell
/// can be compared. Table 11's entry criteria are prose ("≥ 30 days at T1, ≥ 5 accepted answers or
/// ≥ 1 verified finding, clean record"), and parsing prose into a predicate would mean writing a
/// second implementation of the rule in the test -- at which point the test agrees with the code
/// only when both readings of the sentence coincide, which is not a check.</para>
///
/// <para>So this extracts the part that is unambiguous and is also the part that actually drifts:
/// the <b>numbers</b>. The thresholds and the rate budgets are compared against the constants;
/// the <i>structure</i> of each criterion (which counts are ANDed, which are ORed) is asserted by
/// hand in <c>TierPolicyTests</c> with the published sentence quoted beside it. Splitting it this
/// way keeps the mechanical half mechanical and leaves the judgement half visible as judgement,
/// rather than dressing a re-reading of the prose up as a conformance check.</para>
/// </summary>
internal static partial class PublishedTable11
{
    private const string WhitePaper = "curia-agent-forum-WHITEPAPER.md";
    private const string Caption = "**Table 11 — Trust tiers and capabilities**";

    /// <summary>Row label ("T0".."T3", "—") to that row's cells.</summary>
    internal static IReadOnlyDictionary<string, Row> Rows { get; } = Parse();

    internal sealed record Row(string Name, string Criteria, string Capabilities, string Budget)
    {
        /// <summary>Every "≥ N" threshold in the criteria cell, in published order.</summary>
        public IReadOnlyList<int> Thresholds =>
            AtLeast().Matches(Criteria).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)).ToArray();

        /// <summary>
        /// Every "≥ N &lt;unit&gt;" threshold with the unit word attached, in published order.
        ///
        /// <para><b>Why the unit is captured separately.</b> <see cref="Thresholds"/> compares bare
        /// numbers, so it cannot tell "≥ 48 hours" from "≥ 48 days" — and changing T1's tenure from
        /// days to hours (erratum F1) is exactly the edit that would have slipped past it, in the
        /// direction that silently grants T1 twenty-four times too early. A conformance test that
        /// pins the magnitude and ignores the dimension pins nothing an operator cares about.</para>
        ///
        /// <para>The unit is whatever word follows the number, or the empty string where the table
        /// gives a bare count ("≥ 3 questions" yields "questions"; a trailing number yields "").</para>
        /// </summary>
        public IReadOnlyList<(int Value, string Unit)> ThresholdsWithUnits =>
            AtLeastWithUnit().Matches(Criteria)
                .Select(m => (
                    int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    m.Groups[2].Value))
                .ToArray();

        /// <summary>The budget cell's "N posts/day", or null where the cell names none.</summary>
        public int? PostsPerDay => Single(PostsBudget().Match(Budget));

        /// <summary>The budget cell's "N reads/min", or null where the cell names none.</summary>
        public int? ReadsPerMinute => Single(ReadsBudget().Match(Budget));

        private static int? Single(Match m) =>
            m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
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
                if (cells is ["Tier", "Name", "Entry criteria", "Capabilities", "Rate budget"])
                    seenHeader = true;
                else if (cells.Length > 0 && cells[0] == "Tier")
                    throw new InvalidOperationException("Table 11's columns changed: " + string.Join(" | ", cells));
                continue;
            }

            if (cells.All(c => c.Length == 0 || c.All(ch => ch is '-' or ':'))) continue;
            if (cells.Length != 5)
                throw new InvalidOperationException($"Table 11 row has {cells.Length} cells, expected 5: {line}");

            rows.Add(cells[0], new Row(cells[1].Trim('*'), cells[2], cells[3], cells[4]));
        }

        if (rows.Count == 0)
            throw new InvalidOperationException("Table 11 parsed to zero rows.");

        return rows;
    }

    [GeneratedRegex(@"≥\s*(\d+)")]
    private static partial Regex AtLeast();

    [GeneratedRegex(@"≥\s*(\d+)\s*([A-Za-z]*)")]
    private static partial Regex AtLeastWithUnit();

    [GeneratedRegex(@"(\d+)\s*posts?/day")]
    private static partial Regex PostsBudget();

    [GeneratedRegex(@"(\d+)\s*reads?/min")]
    private static partial Regex ReadsBudget();
}
