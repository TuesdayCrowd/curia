using System.Buffers;
using System.Globalization;
using System.Text;

namespace Curia.Canon.Json;

/// <summary>
/// R10.67 (errata G17): how a reader writes text it did not compose and does not quote: the Forum's
/// delimited span, and in <c>curia-operator</c> a flag's rationale and the identifiers it names. Every
/// character of Unicode general category Cc, Cf, Zl or Zp, and every surrogate without its pair, is
/// written as <see cref="DisplayLiteral"/> writes it inside a literal: a backslash, <c>u</c> and four
/// lowercase hexadecimal digits for each UTF-16 code unit. Every other character is written as it is.
///
/// <para><b>Why a span needs this when its delimiters were checked.</b> The delimiters are a boundary to
/// whatever parses the text. A terminal does not parse it: it acts on a control wherever one sits, so an
/// eight-bit CSI in a body moves the cursor up and erases the verdict the reader wrote above the span,
/// and an OSC writes the user's clipboard. A reader cannot tell whether a terminal is behind its output,
/// so it writes these as escapes whatever its output reaches.</para>
///
/// <para><b>Why these categories.</b> They are R4.37's: characters that lay out the text around them
/// instead of showing as themselves, so one set governs what an enrollment refuses and what a reader
/// shows as an escape. Unlike <see cref="DisplayLiteral"/> this keeps every letter of every script,
/// because a span is a post's content and is read; the cost is that the categories come from the
/// runtime's Unicode tables. Scalar values are walked, not code units: a tag character is two
/// surrogates to a walk of code units, and is of category Cf only as one scalar value.</para>
///
/// <para><b>The ambiguity is accepted.</b> Content that spells an escape itself prints the same as
/// content that held the character. The display is not evidence: a reader verifies the canonical form
/// it was served, never what it displayed.</para>
/// </summary>
public static class SpanText
{
    /// <summary>Text of several lines: line feeds and tabs are written as they are.</summary>
    public static string Block(string text) => Escape(text, keepLayout: true);

    /// <summary>Text of one line: line feeds and tabs are written as escapes too.</summary>
    public static string Line(string text) => Escape(text, keepLayout: false);

    private static string Escape(string text, bool keepLayout)
    {
        ArgumentNullException.ThrowIfNull(text);

        var written = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            if (Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var consumed) != OperationStatus.Done)
            {
                // A surrogate without its pair: one code unit, which no terminal can show as itself.
                Append(written, text[i]);
                i++;
                continue;
            }

            var layout = keepLayout && (rune.Value is 0x0A or 0x09);
            if (!layout && IsLayoutControl(rune))
            {
                for (var j = 0; j < consumed; j++)
                    Append(written, text[i + j]);
            }
            else
            {
                written.Append(text, i, consumed);
            }

            i += consumed;
        }

        return written.ToString();
    }

    /// <summary>Whether <paramref name="rune"/> is of general category Cc, Cf, Zl or Zp: R4.37's set.</summary>
    private static bool IsLayoutControl(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control
            or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;

    private static void Append(StringBuilder written, char unit) =>
        written.Append("\\u").Append(((int)unit).ToString("x4", CultureInfo.InvariantCulture));
}
