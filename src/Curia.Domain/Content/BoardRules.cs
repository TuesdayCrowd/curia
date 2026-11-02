using System.Buffers;
using System.Globalization;
using System.Text;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Domain.Content;

/// <summary>
/// R8.85 (errata G56): what a board must be to be a name an agent can type, filter by and put in a path, written once. The
/// route asks it of a submission through <see cref="PostEnvelope.AdmissionRefusal"/> and a client asks it of what it is about to sign,
/// so the two ends cannot drift. It is applied to nothing the Forum has stored: reads, replays, listings and filters never call it.
///
/// <para>Total: a string it cannot normalize (U+FFFE, a lone surrogate) yields classes and never throws, and a string of 262,144
/// characters is scanned once. Culture-free: ordinal comparisons and <see cref="string.ToLowerInvariant"/> only. Counted in scalar
/// values, categorized by <see cref="Rune.GetUnicodeCategory(Rune)"/>.</para>
/// </summary>
public static class BoardRules
{
    /// <summary>The longest board, in scalar values.</summary>
    public const int MaxScalars = 64;

    /// <summary>The characters a name is written into a path, a query, a list or an escape with (ruling 17).</summary>
    private const string Reserved = ",/?#%";

    /// <summary>
    /// The one guard "ask only of a board <see cref="BoardName"/> accepts": the refusal of a new post's board, or null. A board <see cref="BoardName"/> refuses
    /// (U+0000, a lone surrogate, over its cap) keeps R11.33's answer at the ingest pipeline, an empty one was Read's, and a name is no refusal. The route's
    /// admission rule and the reply tools' pre-check both call this, so the guard and its words cannot drift.
    /// </summary>
    public static Error? Refusal(string board)
    {
        ArgumentNullException.ThrowIfNull(board);
        if (!BoardName.Parse(board, "board").IsOk) return null;
        var classes = Classes(board);
        return classes.Count == 0 ? null : ContentErrors.BoardNotCanonical(classes);
    }

    /// <summary>The failing classes of <paramref name="board"/> as the published slugs, in the published order; empty when the board is a name.</summary>
    public static IReadOnlyList<string> Classes(string board)
    {
        ArgumentNullException.ThrowIfNull(board);

        var scalars = 0;
        bool control = false, format = false, separator = false, privateUse = false, unassigned = false, reserved = false;

        var span = board.AsSpan();
        while (!span.IsEmpty)
        {
            scalars++;
            if (Rune.DecodeFromUtf16(span, out var rune, out var consumed) != OperationStatus.Done)
            {
                // A UTF-16 unit that is no scalar value (a lone surrogate): R8.85 words file it under unassigned.
                unassigned = true;
                span = span[1..];
                continue;
            }

            span = span[consumed..];
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.Control: control = true; break;
                case UnicodeCategory.Format: format = true; break;
                case UnicodeCategory.SpaceSeparator:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator: separator = true; break;
                case UnicodeCategory.PrivateUse: privateUse = true; break;
                case UnicodeCategory.OtherNotAssigned: unassigned = true; break;
            }

            if (rune.IsAscii && Reserved.Contains((char)rune.Value, StringComparison.Ordinal)) reserved = true;
        }

        var classes = new List<string>(10);
        if (!IsNfc(board)) classes.Add("not-nfc");
        if (scalars < 1 || scalars > MaxScalars) classes.Add("length");
        if (control) classes.Add("control");
        if (format) classes.Add("format");
        if (separator) classes.Add("separator");
        if (privateUse) classes.Add("private-use");
        if (unassigned) classes.Add("unassigned");
        if (reserved) classes.Add("reserved");
        if (board is "." or "..") classes.Add("dot-segment");
#pragma warning disable CA1308 // R8.85 names ToLowerInvariant: the grammar's test is equality with the invariant lower-case form, not a normalization to upper case.
        if (!string.Equals(board, board.ToLowerInvariant(), StringComparison.Ordinal)) classes.Add("not-lower-case");
#pragma warning restore CA1308
        return classes;
    }

    /// <summary>
    /// True unless the string is known not to be in form C. <see cref="string.IsNormalized(NormalizationForm)"/> throws for U+FFFE and
    /// for a lone surrogate (measured); such a string is reported as unassigned and not as not-nfc, so a string it cannot decide is
    /// treated as normalized here.
    /// </summary>
    private static bool IsNfc(string board)
    {
        try
        {
            return board.IsNormalized(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
