using System.Globalization;

namespace Curia.Client.Cli;

/// <summary>
/// One row of a screen, with a side (R10.72). A <see cref="Frame"/> row is this client's: it begins with its own text and
/// every value in it that it did not compose is a display literal. A <see cref="Content"/> row is a post's content and
/// begins with a gutter, a line number then <c>|</c> (<c>+</c> on a wrapped continuation) then a space, in a form no frame
/// row can match. A closed hierarchy with an explicit <see cref="Match{T}"/> (CS-11), so a third kind of row breaks every
/// call site.
/// </summary>
internal abstract record ScreenRow
{
    private protected ScreenRow()
    {
    }

    /// <summary>R10.72's gutter: optional spaces, digits, <c>|</c> or <c>+</c>, a space. No frame row matches it.</summary>
    internal static bool BeginsWithGutter(string row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var i = 0;
        while (i < row.Length && row[i] == ' ') i++;
        var digits = i;
        while (i < row.Length && row[i] is >= '0' and <= '9') i++;
        return i > digits && i + 1 < row.Length && row[i] is '|' or '+' && row[i + 1] == ' ';
    }

    /// <summary>The whole row as drawn, gutter included.</summary>
    internal abstract string Text { get; }

    internal T Match<T>(Func<Frame, T> frame, Func<Content, T> content)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(content);

        return this switch
        {
            Frame f => frame(f),
            Content c => content(c),
            _ => throw new InvalidOperationException("a screen row is a frame row or a content row"),
        };
    }

    /// <summary>A row of this client's own, already wrapped to the screen's width.</summary>
    internal sealed record Frame(string Row) : ScreenRow
    {
        internal override string Text => Row;
    }

    /// <summary>
    /// A row of a post's content: line <paramref name="Number"/> of the passage (or of the index), its text already escaped
    /// and wrapped. <paramref name="Digits"/> is the gutter's number column, so every row of one passage aligns.
    /// </summary>
    internal sealed record Content(int Number, bool Continuation, int Digits, string Body) : ScreenRow
    {
        internal override string Text =>
            Number.ToString(CultureInfo.InvariantCulture).PadLeft(Digits) + (Continuation ? "+" : "|") + " " + Body;

        /// <summary>The columns the gutter takes: the number column, the separator and the space.</summary>
        internal static int GutterWidth(int digits) => digits + 2;
    }
}
