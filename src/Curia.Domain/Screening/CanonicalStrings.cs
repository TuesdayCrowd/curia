using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Curia.Domain.Screening;

/// <summary>
/// One string token of a canonical envelope — a member name or a string value — as the author
/// wrote it, with every character mapped back into the canonical text.
/// </summary>
/// <param name="Text">The decoded token: <c>\n</c> is a line break again, <c>\"</c> a quote.</param>
/// <param name="Start">
/// For each character of <see cref="Text"/>, the index in the canonical text where its
/// representation begins.
/// </param>
/// <param name="End">
/// For each character of <see cref="Text"/>, the index where its representation ends — the
/// <c>n</c> of <c>\n</c>, the last hex digit of <c>\u001f</c>. Kept separately so a finding that
/// ends on an escape covers the whole escape.
/// </param>
public sealed record CanonicalString(string Text, ImmutableArray<int> Start, ImmutableArray<int> End)
{
    /// <summary>
    /// The canonical span a finding at <paramref name="start"/> of <paramref name="length"/> in
    /// <see cref="Text"/> covers. The same shape as <see cref="DerivedView.ToOriginal"/>, one layer
    /// further out: view → token is that method, token → canonical text is this one.
    /// </summary>
    public (int Offset, int Length) ToCanonical(int start, int length)
    {
        if (Text.Length == 0 || length == 0) return (0, 0);

        var from = Start[Math.Min(start, Start.Length - 1)];
        var to = End[Math.Min(start + length - 1, End.Length - 1)];

        return (from, Math.Max(1, to - from + 1));
    }
}

/// <summary>
/// Every string token of a canonical envelope, decoded (register D19).
///
/// <para><b>Why SCREEN needs this.</b> Ingest and the client's pre-send check hold canonical text,
/// in which JCS writes a line break as the two characters <c>\n</c> and a quote as <c>\"</c>. A rule
/// anchored on a word boundary read the escape's letter instead of the separator the author typed,
/// so an AWS key, a JWT or an assigned secret on any line after the first was admitted into an
/// append-only log, while the red-team corpus — which screened bare strings — published 41/41.</para>
///
/// <para><b>Member names are tokens too.</b> An unknown member is ignored rather than rejected
/// (<c>PostEnvelope</c>), so its name is chosen by the author, signed and persisted.</para>
///
/// <para><b>Exact, not tolerant.</b> SCREEN receives the bytes VERIFY consumed, which JCS wrote, so
/// only JCS's own escapes are decoded and anything else throws: it would mean an upstream phase, or
/// a caller, handed over something that is not a canonical envelope — the same stance as the UTF-8
/// decode in <see cref="ContentScreener"/>. Messages name offsets, never content (R10.28).</para>
/// </summary>
public static class CanonicalStrings
{
    /// <summary>
    /// The decoded string tokens of <paramref name="canonical"/>, in canonical order. Throws before
    /// returning when the text is not a JSON object, so bare text handed to the envelope path fails
    /// at the call rather than yielding nothing and reading as clean.
    /// </summary>
    public static IEnumerable<CanonicalString> Of(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);

        if (canonical.Length < 2 || canonical[0] != '{' || canonical[^1] != '}')
            throw NotCanonical(0, "a canonical envelope is a JSON object");

        return Walk(canonical);
    }

    // Outside a string, JCS text holds only structure, numbers and literals, none of which contains a
    // quote; inside one, a quote is always escaped. So every unescaped quote opens or closes a token.
    private static IEnumerable<CanonicalString> Walk(string canonical)
    {
        for (var i = 0; i < canonical.Length; i++)
        {
            if (canonical[i] != '"') continue;

            var token = Read(canonical, i, out var close);
            yield return token;
            i = close;
        }
    }

    private static CanonicalString Read(string canonical, int open, out int close)
    {
        var text = new StringBuilder();
        var start = ImmutableArray.CreateBuilder<int>();
        var end = ImmutableArray.CreateBuilder<int>();

        var i = open + 1;
        while (i < canonical.Length)
        {
            var c = canonical[i];
            if (c == '"')
            {
                close = i;
                return new CanonicalString(text.ToString(), start.ToImmutable(), end.ToImmutable());
            }

            var width = 1;
            if (c == '\\') (c, width) = Unescape(canonical, i);

            text.Append(c);
            start.Add(i);
            end.Add(i + width - 1);
            i += width;
        }

        throw NotCanonical(open, "a string token is not terminated");
    }

    private static (char Decoded, int Width) Unescape(string canonical, int at) =>
        (at + 1 < canonical.Length ? canonical[at + 1] : '\0') switch
        {
            '"' => ('"', 2),
            '\\' => ('\\', 2),
            'b' => ('\b', 2),
            'f' => ('\f', 2),
            'n' => ('\n', 2),
            'r' => ('\r', 2),
            't' => ('\t', 2),
            'u' => (ControlCharacter(canonical, at), 6),
            _ => throw NotCanonical(at, "an escape JCS does not write"),
        };

    /// <summary>
    /// JCS writes <c>\u</c> only for a control character that has no short form, as four lowercase
    /// hex digits (RFC 8785 §3.2.2.2).
    /// </summary>
    private static char ControlCharacter(string canonical, int at)
    {
        if (at + 5 >= canonical.Length || !IsLowerHex(canonical.AsSpan(at + 2, 4)))
            throw NotCanonical(at, "a \\u escape JCS would not write");

        var code = int.Parse(canonical.AsSpan(at + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);

        return code < 0x20 && code is not (0x08 or 0x09 or 0x0A or 0x0C or 0x0D)
            ? (char)code
            : throw NotCanonical(at, "a \\u escape JCS would not write");
    }

    private static bool IsLowerHex(ReadOnlySpan<char> digits)
    {
        foreach (var c in digits)
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))) return false;

        return true;
    }

    private static InvalidOperationException NotCanonical(int at, string what) => new(string.Create(
        CultureInfo.InvariantCulture,
        $"SCREEN was given text that is not canonical JSON at offset {at}: {what}. ScreenEnvelope takes " +
        $"the bytes VERIFY consumed, which JCS wrote, so reaching this means a caller passed something else."));
}
