using System.Globalization;
using System.Text;

namespace Curia.Client.Tests;

/// <summary>
/// A terminal emulator for R10.72 and R10.73's facts, small and strict. It is the checking side of the screen's
/// vocabulary: it understands exactly the sequences <c>Vt</c> holds and <b>throws on any other</b>, so a sequence outside
/// the vocabulary fails the fact that fed it rather than being ignored. It also behaves the way the terminals the
/// design worries about behave, each as an option:
/// <list type="bullet">
/// <item>a TAB moves the cursor to the next tab stop and <b>erases nothing</b> (the stale-cell case);</item>
/// <item>line wrapping may be on or off, and a screen that never turns it off scrolls;</item>
/// <item>a non-ASCII character takes <c>nonAsciiCells</c> cells (1 or 3), so widths are the terminal's and not the reader's;</item>
/// <item>mode 1049 may be ignored, as on a terminal that has no alternate screen, so a reader that leaves without erasing leaves its screen behind.</item>
/// </list>
/// </summary>
internal sealed class TestTerminal
{
    [Flags]
    internal enum Attributes : byte
    {
        None = 0,
        Bold = 1,
        Dim = 2,
        Reverse = 4,
    }

    private readonly int _width;
    private readonly int _height;
    private readonly int _nonAsciiCells;
    private readonly bool _ignoresAlternateScreen;
    private Cell[][] _main;
    private Cell[][] _alternate;
    private Cell[][] _grid;
    private int _row;
    private int _column;
    private Attributes _attributes;

    internal TestTerminal(int width, int height, int nonAsciiCells = 1, bool ignoresAlternateScreen = false)
    {
        _width = width;
        _height = height;
        _nonAsciiCells = nonAsciiCells;
        _ignoresAlternateScreen = ignoresAlternateScreen;
        _main = Blank();
        _alternate = Blank();
        _grid = _main;
        WrapOn = true;
        CursorVisible = true;
    }

    /// <summary>How many times a line fed past the last row scrolled the display.</summary>
    internal int Scrolls { get; private set; }

    internal bool WrapOn { get; private set; }

    internal bool CursorVisible { get; private set; }

    internal bool AlternateScreenActive { get; private set; }

    internal int Row => _row;

    internal int Column => _column;

    /// <summary>Whether the display this terminal shows now holds nothing but blanks.</summary>
    internal bool IsBlank
    {
        get
        {
            for (var r = 0; r < _height; r++)
            {
                if (RowText(r).Length > 0) return false;
            }

            return true;
        }
    }

    /// <summary>The row's text as the terminal shows it, right-trimmed.</summary>
    internal string RowText(int row)
    {
        var text = new StringBuilder();
        for (var c = 0; c < _width; c++) text.Append(_grid[row][c].Text);
        return text.ToString().TrimEnd(' ');
    }

    /// <summary>The attributes of the cell at <paramref name="column"/> of <paramref name="row"/>.</summary>
    internal Attributes AttributesAt(int row, int column) => _grid[row][column].Attributes;

    /// <summary>Whether any non-blank cell of the row carries an attribute.</summary>
    internal bool RowTextCarriesAttributes(int row)
    {
        for (var c = 0; c < _width; c++)
        {
            var cell = _grid[row][c];
            if (cell.Text is not (" " or "") && cell.Attributes != Attributes.None) return true;
        }

        return false;
    }

    internal void Feed(string bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        var runes = bytes.EnumerateRunes().ToArray();
        for (var i = 0; i < runes.Length; i++)
        {
            var rune = runes[i];
            if (rune.Value == 0x1B)
            {
                i = Escape(runes, i);
                continue;
            }

            if (rune.Value == '\t')
            {
                _column = Math.Min(_width - 1, ((_column / 8) + 1) * 8);
                continue;
            }

            if (rune.Value < 0x20 || (rune.Value >= 0x7F && rune.Value <= 0x9F))
                throw new InvalidOperationException($"a control character the vocabulary does not hold: U+{rune.Value:X4}");

            Print(rune);
        }
    }

    private int Escape(Rune[] runes, int at)
    {
        if (at + 1 >= runes.Length) throw new InvalidOperationException("an escape at the end of the stream");

        var next = runes[at + 1].Value;
        if (next is '=' or '>') return at + 1; // the keypad's application and numeric modes

        if (next != '[') throw new InvalidOperationException($"an escape not followed by '[': U+{next:X4}");

        var j = at + 2;
        var parameters = new StringBuilder();
        while (j < runes.Length && runes[j].Value is >= 0x30 and <= 0x3F)
        {
            parameters.Append((char)runes[j].Value);
            j++;
        }

        if (j >= runes.Length) throw new InvalidOperationException("an unterminated control sequence");

        Control(parameters.ToString(), (char)runes[j].Value);
        return j;
    }

    private void Control(string parameters, char final)
    {
        switch (parameters, final)
        {
            case ("?1049", 'h'):
                if (_ignoresAlternateScreen) return;
                AlternateScreenActive = true;
                _alternate = Blank();
                _grid = _alternate;
                break;
            case ("?1049", 'l'):
                if (_ignoresAlternateScreen) return;
                AlternateScreenActive = false;
                _grid = _main;
                break;
            case ("?7", 'h'):
                WrapOn = true;
                break;
            case ("?7", 'l'):
                WrapOn = false;
                break;
            case ("?25", 'h'):
                CursorVisible = true;
                break;
            case ("?25", 'l'):
                CursorVisible = false;
                break;
            case ("?1", 'h' or 'l'):
                break;
            case ("2", 'J'):
                for (var r = 0; r < _height; r++)
                {
                    for (var c = 0; c < _width; c++) _grid[r][c] = Cell.Empty;
                }

                break;
            case ("", 'H'):
                (_row, _column) = (0, 0);
                break;
            case ("", 'K') or ("0", 'K'):
                for (var c = _column; c < _width; c++) _grid[_row][c] = new Cell(" ", _attributes);
                break;
            case (_, 'H'):
                var pair = parameters.Split(';');
                if (pair.Length != 2) throw new InvalidOperationException($"an address that is not row;column: {parameters}");
                _row = Math.Clamp(int.Parse(pair[0], CultureInfo.InvariantCulture) - 1, 0, _height - 1);
                _column = Math.Clamp(int.Parse(pair[1], CultureInfo.InvariantCulture) - 1, 0, _width - 1);
                break;
            case (_, 'm'):
                foreach (var part in parameters.Length == 0 ? ["0"] : parameters.Split(';'))
                {
                    _attributes = part switch
                    {
                        "0" => Attributes.None,
                        "1" => _attributes | Attributes.Bold,
                        "2" => _attributes | Attributes.Dim,
                        "7" => _attributes | Attributes.Reverse,
                        _ => throw new InvalidOperationException($"an attribute the vocabulary does not hold: {part}"),
                    };
                }

                break;
            default:
                throw new InvalidOperationException($"a control sequence outside the vocabulary: ESC [ {parameters}{final}");
        }
    }

    private void Print(Rune rune)
    {
        var cells = rune.Value is >= 0x20 and <= 0x7E ? 1 : _nonAsciiCells;
        if (_column + cells > _width)
        {
            if (WrapOn)
            {
                _column = 0;
                NewLine();
            }
            else
            {
                _column = Math.Max(0, _width - cells);
            }
        }

        _grid[_row][_column] = new Cell(rune.ToString(), _attributes);
        for (var k = 1; k < cells; k++) _grid[_row][_column + k] = new Cell(string.Empty, _attributes);
        _column += cells;
    }

    private void NewLine()
    {
        if (_row + 1 < _height)
        {
            _row++;
            return;
        }

        Scrolls++;
        for (var r = 1; r < _height; r++)
        {
            for (var c = 0; c < _width; c++) _grid[r - 1][c] = _grid[r][c];
        }

        for (var c = 0; c < _width; c++) _grid[_height - 1][c] = Cell.Empty;
    }

    private Cell[][] Blank()
    {
        var grid = new Cell[_height][];
        for (var r = 0; r < _height; r++)
        {
            grid[r] = new Cell[_width];
            for (var c = 0; c < _width; c++) grid[r][c] = Cell.Empty;
        }

        return grid;
    }

    private readonly record struct Cell(string Text, Attributes Attributes)
    {
        internal static Cell Empty => new(" ", Attributes.None);
    }
}
