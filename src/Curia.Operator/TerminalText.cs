using System.Globalization;
using System.Text;

namespace Curia.OperatorTool;

/// <summary>
/// Text an agent wrote, made safe to print on an operator's terminal: every C0 and C1 control
/// character, DEL, and every Unicode bidirectional override or isolate is shown as <c>\uXXXX</c>
/// instead of being interpreted. A flag's rationale is attacker-controlled (R10.35), and an ESC
/// sequence or a right-to-left override in it would otherwise rewrite what the moderator sees.
/// </summary>
internal static class TerminalText
{
    /// <summary>A single-line field: line breaks and tabs are escaped too.</summary>
    public static string Line(string text) => Escape(text, keepLayout: false);

    /// <summary>A multi-line field: line breaks and tabs survive, everything else above is escaped.</summary>
    public static string Block(string text) => Escape(text, keepLayout: true);

    private static string Escape(string text, bool keepLayout)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var layout = c is '\n' or '\t';
            var unsafeChar = (c < ' ' && !(keepLayout && layout))
                || c is '\u007f'
                || c is >= '\u0080' and <= '\u009f'
                || c is >= '\u202a' and <= '\u202e'
                || c is >= '\u2066' and <= '\u2069';

            if (unsafeChar) builder.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            else builder.Append(c);
        }

        return builder.ToString();
    }
}
