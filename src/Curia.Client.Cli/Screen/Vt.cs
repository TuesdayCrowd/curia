namespace Curia.Client.Cli;

/// <summary>
/// R10.73: every control sequence a screen writes, as a compile-time constant. A screen writes this closed vocabulary and
/// <see cref="Address"/>, which is built from two integers and nothing else, so no byte of a post, a board, a key or an
/// error ever reaches a terminal as anything but a character to draw. <c>BrowseFenceTests</c> fails if a member of this
/// type is not <c>const</c>, or if another one is added that is not an address.
/// </summary>
internal static class Vt
{
    /// <summary>The alternate screen, so the person's scrollback and prompt are as they were when the screen leaves.</summary>
    internal const string AlternateScreenOn = "\u001b[?1049h";

    internal const string AlternateScreenOff = "\u001b[?1049l";

    /// <summary>Line wrapping off: a row that is too wide is cut at the edge and never scrolls the display (R10.72).</summary>
    internal const string WrapOff = "\u001b[?7l";

    internal const string WrapOn = "\u001b[?7h";

    internal const string CursorHidden = "\u001b[?25l";

    internal const string CursorShown = "\u001b[?25h";

    internal const string Clear = "\u001b[2J";

    internal const string Home = "\u001b[H";

    internal const string EraseToEndOfRow = "\u001b[K";

    /// <summary>Every attribute off. Written after each row's address, so no attribute is carried into the next row.</summary>
    internal const string Plain = "\u001b[0m";

    internal const string Bold = "\u001b[1m";

    internal const string Dim = "\u001b[2m";

    internal const string Reverse = "\u001b[7m";

    /// <summary>
    /// The terminal's application keypad mode, which the runtime turns on (<c>smkx</c>) when it reads a key: the arrow keys
    /// then arrive as the sequences terminfo names. The screen does not write it; it is here so the vocabulary is the whole
    /// of what a terminal sees, and so <see cref="ScreenWriter.Leave"/> can turn it off (<c>rmkx</c>).
    /// </summary>
    internal const string KeypadOn = "\u001b[?1h\u001b=";

    internal const string KeypadOff = "\u001b[?1l\u001b>";

    /// <summary>The cursor's position, 1-based. The only member that is not a constant, and it holds two integers.</summary>
    internal static string Address(int row, int column) =>
        $"\u001b[{row.ToString(System.Globalization.CultureInfo.InvariantCulture)};{column.ToString(System.Globalization.CultureInfo.InvariantCulture)}H";
}
