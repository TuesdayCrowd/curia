using System.Collections.Immutable;
using System.Text;

namespace Curia.Client.Cli;

/// <summary>
/// R10.72, R10.75: wrapping one of this client's own rows. A row is cut between tokens, never inside a display literal
/// (R10.64): a quoted literal is one token, so two values that share a long prefix are not cut to look alike. A token
/// longer than a whole row is the only thing cut, and then across rows with all of it kept, because dropping the end of
/// a literal would be the same defect. A continuation row begins with this client's own marker.
/// </summary>
internal static class RowWrap
{
    /// <summary>What a wrapped frame row's continuation begins with: this client's text, and not a gutter.</summary>
    internal const string ContinuationPrefix = "  > ";

    /// <summary>
    /// <paramref name="text"/> as rows no wider than <paramref name="width"/> cells (by <see cref="CellWidth"/>), at least
    /// one. The first row begins as <paramref name="text"/> does.
    /// </summary>
    internal static ImmutableArray<string> Frame(string text, int width)
    {
        ArgumentNullException.ThrowIfNull(text);

        var rows = ImmutableArray.CreateBuilder<string>();
        var current = new StringBuilder();
        var used = 0;
        var fresh = true;

        void Flush()
        {
            rows.Add(current.ToString());
            current.Clear().Append(ContinuationPrefix);
            used = CellWidth.Of(ContinuationPrefix);
            fresh = true;
        }

        foreach (var (gap, token) in Tokens(text))
        {
            var tokenWidth = CellWidth.Of(token);

            // The first token of the text keeps the gap it began with; a token that begins a continuation drops it.
            var spaces = fresh && rows.Count > 0 ? string.Empty : gap;
            if (!fresh && used + CellWidth.Of(spaces) + tokenWidth > width)
            {
                Flush();
                spaces = string.Empty;
            }

            if (used + CellWidth.Of(spaces) + tokenWidth <= width)
            {
                current.Append(spaces).Append(token);
                used += CellWidth.Of(spaces) + tokenWidth;
                fresh = false;
                continue;
            }

            // Wider than a whole row: the only thing cut, across rows, with all of it kept.
            current.Append(spaces);
            used += CellWidth.Of(spaces);
            var placed = false;
            foreach (var rune in token.EnumerateRunes())
            {
                var runeWidth = CellWidth.Of(rune);
                if (placed && used + runeWidth > width) Flush();
                current.Append(rune.ToString());
                used += runeWidth;
                placed = true;
            }

            fresh = false;
        }

        rows.Add(current.ToString());
        return rows.ToImmutable();
    }

    /// <summary>
    /// The text as (gap, token) pairs: a token is a run of non-space characters, except that a double-quoted literal, with
    /// its <c>\"</c> and <c>\\</c> escapes, is one token whatever it holds. The gap is the spaces before the token.
    /// </summary>
    private static List<(string Gap, string Token)> Tokens(string text)
    {
        var tokens = new List<(string, string)>();
        var i = 0;
        while (i < text.Length)
        {
            var gapStart = i;
            while (i < text.Length && text[i] == ' ') i++;
            var gap = text[gapStart..i];
            var start = i;

            while (i < text.Length && text[i] != ' ')
            {
                if (text[i] == '"')
                {
                    i++;
                    while (i < text.Length && text[i] != '"') i += text[i] == '\\' && i + 1 < text.Length ? 2 : 1;
                    if (i < text.Length) i++;
                }
                else
                {
                    i++;
                }
            }

            if (i > start || gap.Length > 0) tokens.Add((gap, text[start..i]));
        }

        return tokens;
    }
}
