using System.Collections.Immutable;
using System.Text;

namespace Curia.Client.Cli;

/// <summary>
/// What one write to the terminal holds. Only <see cref="ScreenWriter"/> makes one, so <see cref="Output.Draw"/> takes no
/// string a caller could build from a post. <see cref="Leaves"/> marks the write that gives the terminal back: after it
/// the output refuses another draw (R10.73).
/// </summary>
internal readonly record struct ScreenBytes
{
    internal ScreenBytes(string text, bool leaves)
    {
        Text = text;
        Leaves = leaves;
    }

    internal string Text { get; }

    internal bool Leaves { get; }
}

/// <summary>One row of a frame as it is written: where, with which attribute, and its text.</summary>
internal readonly record struct ScreenWrite(int Row, string Attribute, string Text);

/// <summary>
/// R10.72: a screen's rows in the order they are written. Top to bottom first (the title, the warning, the marking row,
/// then the window, each header before its content), then <b>every row of this client's own in view again</b>, then the
/// status row last. The second pass is why a terminal that skips cells (a TAB, a wide character) cannot leave an earlier
/// pass's text standing inside a row this client meant to own: whatever the first pass left, the second erases from the
/// row's start.
/// </summary>
internal sealed record ScreenFrame(ImmutableArray<ScreenWrite> Writes)
{
    /// <summary>
    /// The writes for <paramref name="rows"/> on a terminal <paramref name="height"/> rows high. The last row is the status
    /// row and is addressed at the terminal's last row; rows between the window's end and it are written blank, so a
    /// shorter screen leaves nothing of a longer one behind.
    /// </summary>
    internal static ScreenFrame Of(ImmutableArray<ScreenRow> rows, int height)
    {
        if (rows.IsDefaultOrEmpty) throw new ArgumentException("a frame has at least its status row", nameof(rows));

        var body = rows.Length - 1;
        var room = Math.Max(0, height - 1);
        var writes = ImmutableArray.CreateBuilder<ScreenWrite>();

        for (var i = 0; i < Math.Min(body, room); i++)
            writes.Add(new ScreenWrite(i + 1, AttributeOf(i, rows[i]), rows[i].Text));
        for (var i = body; i < room; i++)
            writes.Add(new ScreenWrite(i + 1, Vt.Plain, string.Empty));

        for (var i = 0; i < Math.Min(body, room); i++)
        {
            if (rows[i] is ScreenRow.Frame && rows[i].Text.Length > 0)
                writes.Add(new ScreenWrite(i + 1, AttributeOf(i, rows[i]), rows[i].Text));
        }

        writes.Add(new ScreenWrite(Math.Max(1, height), Vt.Reverse, rows[^1].Text));
        return new ScreenFrame(writes.ToImmutable());
    }

    private static string AttributeOf(int index, ScreenRow row) => row switch
    {
        ScreenRow.Frame when index == 0 => Vt.Bold,
        ScreenRow.Frame f when f.Row.StartsWith("end of #", StringComparison.Ordinal) => Vt.Dim,
        _ => Vt.Plain,
    };
}

/// <summary>
/// R10.73: the only code that builds what is written to a terminal. Everything it writes is a <see cref="Vt"/> constant, an
/// address built from two integers, or the text of a row of a <see cref="Screen"/> (which holds nothing a terminal acts on,
/// and is checked again here, since a byte that does is the whole of the defect this type exists to prevent).
/// </summary>
internal static class ScreenWriter
{
    /// <summary>Takes the alternate screen, turns wrapping off and hides the cursor.</summary>
    internal static ScreenBytes Enter() =>
        new(Vt.AlternateScreenOn + Vt.WrapOff + Vt.CursorHidden + Vt.Clear + Vt.Home, leaves: false);

    /// <summary>
    /// Gives the terminal back: erases first, so a terminal that ignores mode 1049 keeps nothing, then wrapping, the cursor
    /// and the keypad as they were, and the alternate screen last.
    /// </summary>
    internal static ScreenBytes Leave() =>
        new(Vt.Plain + Vt.Home + Vt.Clear + Vt.WrapOn + Vt.CursorShown + Vt.KeypadOff + Vt.AlternateScreenOff, leaves: true);

    internal static ScreenBytes Serialize(Screen screen, ScreenSize size)
    {
        ArgumentNullException.ThrowIfNull(screen);
        return Serialize(ScreenFrame.Of(screen.Rows, size.Height));
    }

    internal static ScreenBytes Serialize(ScreenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var bytes = new StringBuilder();
        foreach (var write in frame.Writes)
        {
            foreach (var character in write.Text)
            {
                if (char.IsControl(character))
                    throw new InvalidOperationException($"a row holds a control character, U+{(int)character:X4}; a screen never writes one");
            }

            bytes.Append(Vt.Address(write.Row, 1)).Append(Vt.Plain).Append(write.Attribute).Append(write.Text).Append(Vt.EraseToEndOfRow).Append(Vt.Plain);
        }

        return new ScreenBytes(bytes.ToString(), leaves: false);
    }
}
