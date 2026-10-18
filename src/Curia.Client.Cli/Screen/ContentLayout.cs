using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Curia.Canon.Json;

namespace Curia.Client.Cli;

/// <summary>
/// R10.75: a post's content laid out for a screen, from the canonical form the signature covers and never from the
/// Forum's span (R10.71's last sentence).
///
/// <para><b>What a terminal acts on is escaped, in R10.67's spelling.</b> Every Cc, Cf, Zl, Zp, Co and Cn scalar value,
/// every unpaired surrogate and every combining mark with no base is written <c>\u</c> and four lowercase hexadecimal
/// digits for each UTF-16 unit. A TAB is first expanded to the next multiple of 8, because a terminal that skips cells
/// leaves earlier text standing inside the row (R10.72). Each Mn or Me after the <see cref="MarkCap"/>th on one base is
/// escaped too: a terminal, not this reader, decides what a mark draws over, and an unbounded run reaches the rows above
/// and below. A tail of marks is bounded by the cap, and Me is capped with Mn and not at zero, since U+20E3 is honest.</para>
///
/// <para><b>The cap is measured.</b> The longest run of Mn in any scalar value's NFD is 3 (U+1F82) and of Me is 1; the
/// red-team corpus's benign and known-false-positive files hold none after a base. The cap is the larger of 4 and what was
/// found (Python 3.15.0, Unicode 17.0.0), so it stays 4.</para>
///
/// <para><b>A wrap never splits a base from its marks</b> and never starts a row with a mark, so the first scalar value of
/// a row's text is never one that draws over what precedes it.</para>
/// </summary>
internal static class ContentLayout
{
    /// <summary>R10.75's number of combining marks one base keeps.</summary>
    internal const int MarkCap = 4;

    /// <summary>Tab stops are every eight columns.</summary>
    internal const int TabStop = 8;

    /// <summary>
    /// The lines of the canonical form, unescaped: members in canonical order, a string split on line feeds, an array's
    /// items as <c>- </c> lines, an object indented. An empty list for a form that does not parse (the caller has none to
    /// lay out).
    /// </summary>
    internal static ImmutableArray<string> Lines(string covered)
    {
        ArgumentNullException.ThrowIfNull(covered);

        if (!JsonReader.Parse(Encoding.UTF8.GetBytes(covered), AdmitLimits.Default).TryGetValue(out var tree, out _))
            return [];

        var lines = new List<string>();
        switch (tree)
        {
            case JsonValue.Object o:
                Members(o, string.Empty, lines);
                break;
            case JsonValue.Array a:
                Items(a, string.Empty, lines);
                break;
            default:
                Item(tree!, string.Empty, lines);
                break;
        }

        return [.. lines];
    }

    private static void Members(JsonValue.Object o, string indent, List<string> lines)
    {
        foreach (var (key, value) in o.Members)
        {
            switch (value)
            {
                case JsonValue.Object { Members.Length: > 0 } child:
                    lines.Add(indent + key + ":");
                    Members(child, indent + "  ", lines);
                    break;
                case JsonValue.Array { Items.Length: > 0 } child:
                    lines.Add(indent + key + ":");
                    Items(child, indent + "  ", lines);
                    break;
                case JsonValue.String s when s.Value.Contains('\n', StringComparison.Ordinal):
                    lines.Add(indent + key + ":");
                    foreach (var line in s.Value.Split('\n')) lines.Add(indent + "  " + line);
                    break;
                default:
                    lines.Add(indent + key + ": " + Scalar(value));
                    break;
            }
        }
    }

    private static void Items(JsonValue.Array a, string indent, List<string> lines)
    {
        foreach (var item in a.Items) Item(item, indent, lines);
    }

    private static void Item(JsonValue item, string indent, List<string> lines)
    {
        switch (item)
        {
            case JsonValue.Object { Members.Length: > 0 } child:
                lines.Add(indent + "-");
                Members(child, indent + "  ", lines);
                break;
            case JsonValue.Array { Items.Length: > 0 } child:
                lines.Add(indent + "-");
                Items(child, indent + "  ", lines);
                break;
            case JsonValue.String s when s.Value.Contains('\n', StringComparison.Ordinal):
                lines.Add(indent + "-");
                foreach (var line in s.Value.Split('\n')) lines.Add(indent + "  " + line);
                break;
            default:
                lines.Add(indent + "- " + Scalar(item));
                break;
        }
    }

    private static string Scalar(JsonValue value) => value switch
    {
        JsonValue.String s => s.Value,
        JsonValue.Number n => n.Value.ToString("R", CultureInfo.InvariantCulture),
        JsonValue.Bool b => b.Value ? "true" : "false",
        JsonValue.Object => "{}",
        JsonValue.Array => "[]",
        _ => "null",
    };

    /// <summary>
    /// One logical line as text a terminal can be handed: TAB expanded, every scalar value a terminal acts on written as an
    /// escape, a mark with no base or beyond <see cref="MarkCap"/> written as an escape.
    /// </summary>
    internal static string Escape(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var written = new StringBuilder(line.Length);
        var column = 0;
        var hasBase = false;
        var marks = 0;

        for (var i = 0; i < line.Length;)
        {
            if (Rune.DecodeFromUtf16(line.AsSpan(i), out var rune, out var consumed) != OperationStatus.Done)
            {
                // A surrogate without its pair: one code unit, which no terminal can show as itself.
                column += EscapeUnits(written, line.AsSpan(i, 1));
                hasBase = false;
                marks = 0;
                i++;
                continue;
            }

            var unit = line.AsSpan(i, consumed);
            i += consumed;

            if (rune.Value == 0x09)
            {
                var spaces = TabStop - (column % TabStop);
                written.Append(' ', spaces);
                column += spaces;
                hasBase = false;
                marks = 0;
                continue;
            }

            var category = Rune.GetUnicodeCategory(rune);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned
                or UnicodeCategory.Surrogate)
            {
                column += EscapeUnits(written, unit);
                hasBase = false;
                marks = 0;
            }
            else if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                if (!hasBase || marks >= MarkCap)
                {
                    column += EscapeUnits(written, unit);
                }
                else
                {
                    written.Append(unit);
                    marks++;
                }
            }
            else
            {
                written.Append(unit);
                column += CellWidth.Of(rune);
                hasBase = true;
                marks = 0;
            }
        }

        return written.ToString();
    }

    /// <summary>R10.67's spelling of each UTF-16 unit of <paramref name="units"/>; the columns written.</summary>
    private static int EscapeUnits(StringBuilder written, ReadOnlySpan<char> units)
    {
        var columns = 0;
        foreach (var unit in units)
        {
            written.Append("\\u").Append(((int)unit).ToString("x4", CultureInfo.InvariantCulture));
            columns += 6;
        }

        return columns;
    }

    /// <summary>
    /// <paramref name="escaped"/> as rows no wider than <paramref name="width"/> cells (by <see cref="CellWidth"/>), at
    /// least one: each scalar value with the marks that follow it kept together, so no row begins with a mark. A cluster
    /// wider than a row stands alone on one.
    /// </summary>
    internal static ImmutableArray<string> Wrap(string escaped, int width)
    {
        ArgumentNullException.ThrowIfNull(escaped);

        var rows = ImmutableArray.CreateBuilder<string>();
        var current = new StringBuilder();
        var used = 0;

        foreach (var cluster in Clusters(escaped))
        {
            var clusterWidth = CellWidth.Of(cluster);
            if (current.Length > 0 && used + clusterWidth > width)
            {
                rows.Add(current.ToString());
                current.Clear();
                used = 0;
            }

            current.Append(cluster);
            used += clusterWidth;
        }

        rows.Add(current.ToString());
        return rows.ToImmutable();
    }

    private static IEnumerable<string> Clusters(string escaped)
    {
        var cluster = new StringBuilder();
        foreach (var rune in escaped.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var isMark = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark;
            if (!isMark && cluster.Length > 0)
            {
                yield return cluster.ToString();
                cluster.Clear();
            }

            cluster.Append(rune.ToString());
        }

        if (cluster.Length > 0) yield return cluster.ToString();
    }

    /// <summary>
    /// The content rows of a form laid out at <paramref name="width"/> columns: each logical line escaped and wrapped, its
    /// first row <c>N|</c> and the rest <c>N+</c>. The gutter's number column is as wide as the last line number.
    /// </summary>
    internal static ImmutableArray<ScreenRow> Rows(string covered, int width)
    {
        var lines = Lines(covered);
        return Rows(lines, width);
    }

    /// <summary>The rows of already-split <paramref name="lines"/> (a title is one line), numbered from one.</summary>
    internal static ImmutableArray<ScreenRow> Rows(ImmutableArray<string> lines, int width)
    {
        var digits = lines.Length.ToString(CultureInfo.InvariantCulture).Length;
        var bodyWidth = Math.Max(1, width - ScreenRow.Content.GutterWidth(digits));
        var rows = ImmutableArray.CreateBuilder<ScreenRow>();

        for (var n = 0; n < lines.Length; n++)
        {
            var wrapped = Wrap(Escape(lines[n]), bodyWidth);
            for (var r = 0; r < wrapped.Length; r++)
                rows.Add(new ScreenRow.Content(n + 1, Continuation: r > 0, digits, wrapped[r]));
        }

        return rows.ToImmutable();
    }
}
