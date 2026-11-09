using System.Globalization;
using System.Text;
using Curia.Domain.Primitives;

namespace Curia.Domain.Search;

/// <summary>
/// R9.58 (errata G63): the cursor of <c>GET /v1/boards</c>, <c>b&lt;bound&gt;:&lt;offset&gt;</c> in canonical numerals and base64: the corpus bound the listing was cut from, then an
/// offset into that corpus's order. It carries no name (the order is by a count, and a keyset over it would carry an author's text).
///
/// <para><b>Seam (PR 7, Task 1).</b> <see cref="Decode"/> refuses every cursor and <see cref="Encode"/> writes the published form; Task 2 replaces the refusal with the strict reading.</para>
/// </summary>
public sealed record BoardListCursor(long CorpusBound, int Offset)
{
    /// <summary>The problem type of a cursor this route did not mint (R9.58).</summary>
    public const string MalformedType = "curia/board-list/cursor-malformed";

    /// <summary>The published form: <c>b&lt;bound&gt;:&lt;offset&gt;</c>, base64 of its ASCII bytes.</summary>
    public string Encode() => Convert.ToBase64String(Encoding.ASCII.GetBytes(
        string.Create(CultureInfo.InvariantCulture, $"b{CorpusBound}:{Offset}")));

    /// <summary>Absent (<see langword="null"/> success) or a cursor; the seam refuses every present one, echoing nothing of it.</summary>
    public static Result<BoardListCursor?> Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return Result<BoardListCursor?>.Ok(null);

        return Result<BoardListCursor?>.Fail(new Error(
            MalformedType,
            "The `cursor` member is not a cursor this route minted",
            "Omit it to start a fresh listing, or send back a cursor from a previous page of this listing unaltered."));
    }
}
