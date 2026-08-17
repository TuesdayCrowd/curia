using System.Globalization;
using Curia.Domain.Authorization;

namespace Curia.Domain.Tests.Authorization;

/// <summary>
/// Table 10, parsed out of <c>curia-agent-forum-WHITEPAPER.md</c> at test time.
///
/// <para><b>Why this exists at all.</b> R7.12 requires every denied cell in Table 10 to have a test
/// asserting the denial. The obvious implementation -- enumerate
/// <see cref="ResourceActionModel"/> and assert each denial -- is vacuous: a cell transcribed
/// wrongly produces a test asserting the mistake, and it passes. This project has now found that
/// same shape five separate times (errata E10, E11, E13, E14, and the differential harness's own
/// comparison), and the lesson each time was that the absence of a probe is indistinguishable from
/// a probe that passed.</para>
///
/// <para>So the white paper stays the authority and the code is a transcription, with this parser
/// holding the two against each other. Neither is derived from the other, which is the only
/// arrangement in which agreement between them is evidence. A Table 10 edit that the code does not
/// follow fails the build; so does the reverse.</para>
///
/// <para>Deliberately a hand-rolled reader rather than a Markdown library: the whole point is that
/// it reads the *published* bytes, and a dependency that normalized or repaired the table on the
/// way in would quietly reintroduce the coupling this exists to break.</para>
/// </summary>
internal static class PublishedTable10
{
    private const string WhitePaper = "curia-agent-forum-WHITEPAPER.md";
    private const string Caption = "**Table 10 — Resource/action model**";

    /// <summary>The parsed table, keyed exactly as <see cref="ResourceActionModel"/> keys its own.</summary>
    internal static IReadOnlyDictionary<(ResourceKind Resource, ActionKind Action), ResourceActionRow> Rows { get; } =
        Parse(ReadTableBlock());

    private static string ReadTableBlock()
    {
        // Same shape as Curia.Canon.Tests' VectorLoader: walk up from the test binary until the
        // repository root shows itself, so the test works from any working directory.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, WhitePaper)))
            dir = dir.Parent;

        var path = dir is null
            ? throw new InvalidOperationException($"{WhitePaper} not found above {AppContext.BaseDirectory}")
            : Path.Combine(dir.FullName, WhitePaper);

        var text = File.ReadAllText(path);
        var caption = text.IndexOf(Caption, StringComparison.Ordinal);
        if (caption < 0)
            throw new InvalidOperationException(
                $"{WhitePaper} no longer contains the caption '{Caption}'. Either §7.2's table was " +
                "renamed -- in which case this constant follows it -- or the table is gone, which is " +
                "a specification change that must not be absorbed silently by a parser that shrugs.");

        return text[(caption + Caption.Length)..];
    }

    private static Dictionary<(ResourceKind, ActionKind), ResourceActionRow> Parse(string block)
    {
        var parsed = new Dictionary<(ResourceKind, ActionKind), ResourceActionRow>();
        var seenHeader = false;

        foreach (var raw in block.Split('\n'))
        {
            var line = raw.Trim();

            if (!line.StartsWith('|'))
            {
                // The table ends at the first non-row line after it started.
                if (seenHeader && parsed.Count > 0) break;
                continue;
            }

            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();

            if (!seenHeader)
            {
                // The header row names the five columns this parser assumes, in order. Asserting
                // it means a reordered or renamed column is a loud failure rather than a silent
                // mis-parse that assigns T2's cell to T1.
                if (cells is ["Resource", "Actions", "Anonymous", "T0", "T1", "T2", "T3"])
                    seenHeader = true;
                else if (cells.Length > 0 && cells[0] == "Resource")
                    throw new InvalidOperationException(
                        "Table 10's columns changed: " + string.Join(" | ", cells));
                continue;
            }

            if (cells.All(c => c.Length == 0 || c.All(ch => ch is '-' or ':'))) continue;
            if (cells.Length != 7)
                throw new InvalidOperationException($"Table 10 row has {cells.Length} cells, expected 7: {line}");

            var resource = ParseResource(cells[0]);
            var (actions, actionQualifier) = ParseActions(cells[1]);
            var (tierCells, cellQualifier) = ParseTierCells(cells[2..]);

            var qualifier = actionQualifier is not GrantQualifier.None ? actionQualifier : cellQualifier;
            var row = new ResourceActionRow(
                tierCells[0], tierCells[1], tierCells[2], tierCells[3], tierCells[4], qualifier);

            foreach (var action in actions)
                parsed.Add((resource, action), row);
        }

        if (parsed.Count == 0)
            throw new InvalidOperationException("Table 10 parsed to zero rows -- the parser found the caption but no table.");

        return parsed;
    }

    private static ResourceKind ParseResource(string cell)
    {
        var name = Unbacktick(cell);
        foreach (var r in Enum.GetValues<ResourceKind>())
            if (ResourceActionNames.Wire(r) == name)
                return r;

        throw new InvalidOperationException($"Table 10 names a resource the model has no member for: {cell}");
    }

    private static ActionKind ParseAction(string cell)
    {
        var name = Unbacktick(cell);
        foreach (var a in Enum.GetValues<ActionKind>())
            if (ResourceActionNames.Wire(a) == name)
                return a;

        throw new InvalidOperationException($"Table 10 names an action the model has no member for: {cell}");
    }

    private static (ActionKind[] Actions, GrantQualifier Qualifier) ParseActions(string cell)
    {
        // "`create` (own)" and "`accept` (own thread)" attach the parenthetical to the action;
        // "`list`, `read`" is two actions with none.
        var qualifier = GrantQualifier.None;
        var text = cell;

        var paren = text.IndexOf('(', StringComparison.Ordinal);
        if (paren >= 0)
        {
            var inner = text[(paren + 1)..].TrimEnd(')', ' ');
            qualifier = inner switch
            {
                "own" => GrantQualifier.OwnResourceOnly,
                "own thread" => GrantQualifier.OwnThreadOnly,
                _ => throw new InvalidOperationException($"Table 10 action carries an unmodelled qualifier: ({inner})"),
            };
            text = text[..paren];
        }

        var actions = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseAction)
            .ToArray();

        return (actions, qualifier);
    }

    private static (Table10Cell[] Cells, GrantQualifier Qualifier) ParseTierCells(string[] cells)
    {
        var parsed = new Table10Cell[5];
        var qualifier = GrantQualifier.None;
        Table10Cell? spanning = null;

        for (var i = 0; i < 5; i++)
        {
            var cell = cells[i];

            // The `agent`/`enroll` row writes "owner-auth only" once and leaves the remaining four
            // columns empty -- a cell spanning the tier columns, in a Markdown table that has no
            // way to say so. An empty cell therefore continues the last one rather than defaulting
            // to anything: defaulting to denied would invent four denials the table never wrote.
            if (cell.Length == 0)
            {
                parsed[i] = spanning ?? throw new InvalidOperationException(
                    "Table 10 has an empty cell with no preceding cell to span from.");
                continue;
            }

            if (cell.StartsWith('✓'))
            {
                parsed[i] = Table10Cell.Allowed;
                var rest = cell[1..].Trim();
                if (rest.Length > 0)
                    qualifier = rest is "(delegated)"
                        ? GrantQualifier.Delegated
                        : throw new InvalidOperationException($"Table 10 cell carries an unmodelled qualifier: {rest}");
            }
            else
            {
                parsed[i] = cell switch
                {
                    "✗" => Table10Cell.Denied,
                    "rate-limited" => Table10Cell.RateLimited,
                    "owner-auth only" => Table10Cell.OwnerAuthOnly,
                    _ => throw new InvalidOperationException(
                        $"Table 10 contains a cell this parser does not model: '{cell}'. A new kind " +
                        "of cell is a specification change, not something to coerce into an existing one."),
                };
            }

            spanning = parsed[i];
        }

        return (parsed, qualifier);
    }

    private static string Unbacktick(string s) => s.Trim().Trim('`').Trim();

    /// <summary>Every published denial, as (pair, tier). R7.12's obligation, enumerated from the source.</summary>
    internal static IEnumerable<object[]> DeniedCells() =>
        from entry in Rows
        from tier in Enum.GetValues<PrincipalTier>()
        where entry.Value[tier] is Table10Cell.Denied
        select new object[] { entry.Key.Resource, entry.Key.Action, tier };

    internal static string Describe((ResourceKind Resource, ActionKind Action) pair, PrincipalTier tier) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{ResourceActionNames.Wire(pair.Resource)}:{ResourceActionNames.Wire(pair.Action)} for {tier}");
}
