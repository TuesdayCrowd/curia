using System.Globalization;
using System.Text;
using Curia.Domain.Primitives;

namespace Curia.Domain.Search;

/// <summary>
/// R9.58 (errata G63): the cursor of <c>GET /v1/boards</c>, <c>b&lt;bound&gt;:&lt;offset&gt;</c> in canonical numerals and base64: the corpus bound the listing was cut from, then an
/// offset into that corpus's order. It carries no name (the order is by a count, and a keyset over it would carry an author's text).
/// </summary>
public sealed record BoardListCursor(long CorpusBound, int Offset)
{
    /// <summary>The problem type of a cursor this route did not mint (R9.58).</summary>
    public const string MalformedType = "curia/board-list/cursor-malformed";

    /// <summary>The published form: <c>b&lt;bound&gt;:&lt;offset&gt;</c>, base64 of its ASCII bytes.</summary>
    public string Encode() => Convert.ToBase64String(Encoding.ASCII.GetBytes(
        string.Create(CultureInfo.InvariantCulture, $"b{CorpusBound}:{Offset}")));

    /// <summary>
    /// Absent (<see langword="null"/> success) or a cursor. Exactly <c>b&lt;bound&gt;:&lt;offset&gt;</c> in canonical numerals (digits only, no leading zero unless a lone zero) under base64;
    /// anything else, another route's cursor included, is malformed, echoing nothing of it. A bound beyond the log's newest event is the route's to refuse: it knows the log.
    /// </summary>
    public static Result<BoardListCursor?> Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return Result<BoardListCursor?>.Ok(null);

        try
        {
            var text = Encoding.ASCII.GetString(Convert.FromBase64String(encoded));

            var fields = text.Split(':');
            if (fields.Length != 2 || fields[0].Length < 2 || fields[0][0] != 'b' || !IsCanonicalNumeral(fields[0][1..]) || !IsCanonicalNumeral(fields[1]))
                return Malformed();

            return long.TryParse(fields[0].AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var bound)
                && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                    ? Result<BoardListCursor?>.Ok(new BoardListCursor(bound, offset))
                    : Malformed();
        }
        catch (FormatException)
        {
            return Malformed();
        }
    }

    /// <summary>The refusal a cursor this route did not mint gets, and the route's own for a bound beyond the log: the same words, nothing echoed.</summary>
    public static Error MalformedError() => new(
        MalformedType,
        "The `cursor` member is not a cursor this route minted",
        "Omit it to start a fresh listing, or send back a cursor from a previous page of this listing unaltered.");

    /// <summary>Digits only, and no leading zero unless the numeral is a lone zero: the one spelling <see cref="Encode"/> writes.</summary>
    private static bool IsCanonicalNumeral(string text) =>
        text.Length > 0 && text.All(char.IsAsciiDigit) && (text.Length == 1 || text[0] != '0');

    private static Result<BoardListCursor?> Malformed() => Result<BoardListCursor?>.Fail(MalformedError());
}
