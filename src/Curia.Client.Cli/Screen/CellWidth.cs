using System.Text;

namespace Curia.Client.Cli;

/// <summary>
/// R10.75: the width a screen assumes for a character, never less than a terminal's. A terminal, not the reader, decides
/// how many cells a scalar value takes, and a reader that undercounts lets a row run past the edge it laid out for. So
/// printable ASCII is one cell and everything else is two, which is what a wide East Asian character takes and more
/// than a narrow one does; the status row says when the estimate is in use (<c>wide characters on screen</c>).
/// </summary>
internal static class CellWidth
{
    /// <summary>The cells <paramref name="rune"/> is assumed to take: 1 for printable ASCII, otherwise 2.</summary>
    internal static int Of(Rune rune) => rune.Value is >= 0x20 and <= 0x7E ? 1 : 2;

    /// <summary>The cells <paramref name="text"/> is assumed to take, scalar value by scalar value.</summary>
    internal static int Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var width = 0;
        foreach (var rune in text.EnumerateRunes()) width += Of(rune);
        return width;
    }

    /// <summary>Whether <paramref name="text"/> holds a scalar value the estimate takes as wide.</summary>
    internal static bool HasWide(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var rune in text.EnumerateRunes())
        {
            if (Of(rune) != 1) return true;
        }

        return false;
    }
}
