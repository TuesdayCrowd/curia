using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Curia.Canon.Canonical;

namespace Curia.Canon.Json;

/// <summary>
/// R10.64 (errata G17): the one form in which a reader writes a value it did not compose into its
/// own output. A JSON string literal (RFC 8259 §7) in which <c>"</c> and <c>\</c> are escaped with a
/// backslash, every other character from U+0020 to U+007E stands for itself, and every other UTF-16
/// code unit is written <c>\u</c> and four lowercase hex digits: a character outside the Basic
/// Multilingual Plane as its surrogate pair, and a surrogate without its pair as itself.
///
/// <para><b>Why printable ASCII and nothing else.</b> A reader's output is read by a model, and a
/// value a stranger named reaches it beside words the reader wrote. Printed as it came, a value
/// holding a line break begins a line that reads as the reader's own; one holding U+202E reorders
/// the text after it; one holding a tag character from U+E0000's block carries words a model reads
/// and a person does not see; and one holding U+0430 reads as <c>a</c>. Any rule written as a list
/// of dangerous characters is a list Unicode will outgrow, and would have to be kept in step in two
/// languages. This one needs no Unicode data at all: two values that differ print differently, and a
/// value can end neither the literal nor the line.</para>
///
/// <para><b>Absent is not empty.</b> A null value is written <see cref="Absent"/>, unquoted, which no
/// literal can be mistaken for; an empty string is <c>""</c>.</para>
///
/// <para><c>curia-testis</c> implements the same function in <c>src/display.rs</c>, and
/// <c>conformance/display/</c> holds both to the same bytes.</para>
/// </summary>
public static class DisplayLiteral
{
    /// <summary>How an absent value is written: outside quotes, so no literal can read as it.</summary>
    public const string Absent = "(none)";

    /// <summary>The value as a display literal, or <see cref="Absent"/> when there is none.</summary>
    public static string Of(string? value)
    {
        if (value is null) return Absent;

        var literal = new StringBuilder(value.Length + 2).Append('"');
        foreach (var unit in value)
        {
            if (unit is '"' or '\\')
                literal.Append('\\').Append(unit);
            else if (unit is >= ' ' and <= '~')
                literal.Append(unit);
            else
                literal.Append("\\u").Append(((int)unit).ToString("x4", CultureInfo.InvariantCulture));
        }

        return literal.Append('"').ToString();
    }

    /// <summary>
    /// The value <paramref name="literal"/> spells, when it is exactly the display literal
    /// <see cref="Of"/> writes for that value and the value is well-formed UTF-16 (R10.66, errata
    /// G17); otherwise false.
    ///
    /// <para><b>Why exactly, and not any JSON string.</b> A reader takes its own output back so its
    /// caller never has to decode the escapes by hand. One spelling per value means a literal that is
    /// not the one a reader printed -- an escape in capitals, a printable character escaped, a quote
    /// left bare -- is refused rather than read as some value, so an argument that was altered on the
    /// way from the output to the command fails where it is given.</para>
    ///
    /// <para><b>Why a surrogate without its pair is refused.</b> <see cref="Of"/> writes one as its
    /// own escape, but no name on the Forum can hold one (R6.15), and the next hop would send another
    /// value: a URL's percent-encoding and a JSON writer each turn it into U+FFFD. Read, it would name
    /// a board or a post that is not the one the literal spells.</para>
    /// </summary>
    public static bool TryRead(string? literal, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (literal is null || literal.Length < 2 || literal[0] != '"' || literal[^1] != '"') return false;

        var read = new StringBuilder(literal.Length);
        for (var i = 1; i < literal.Length - 1; i++)
        {
            if (literal[i] != '\\')
            {
                read.Append(literal[i]);
                continue;
            }

            if (i + 1 >= literal.Length - 1) return false;
            var next = literal[++i];
            if (next is '"' or '\\')
            {
                read.Append(next);
                continue;
            }

            if (next != 'u' || i + 4 >= literal.Length - 1
                || !ushort.TryParse(literal.AsSpan(i + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unit))
                return false;

            read.Append((char)unit);
            i += 4;
        }

        var candidate = read.ToString();
        if (!string.Equals(Of(candidate), literal, StringComparison.Ordinal)) return false;
        if (CanonicalJson.HasUnpairedSurrogate(candidate)) return false;

        value = candidate;
        return true;
    }
}
