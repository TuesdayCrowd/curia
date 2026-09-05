using System.Globalization;
using System.Text;

namespace Curia.Domain.Search;

/// <summary>
/// R9.7's opaque cursor for hybrid retrieval: the corpus bound the query was evaluated against,
/// and the position reached in that fixed ordering.
///
/// <para>The lexical cursor keys on a score, and its argument holds there: a lexical score is a
/// pure function of one post and the query. A fused score is not -- reciprocal rank fusion is a
/// function of every other document's rank, so a post appended between pages moves an
/// already-returned result. The log is append-only (R11.6), so "the corpus as of seq ≤ S" is a
/// stable, reproducible corpus; fixing S when the query is issued makes stability exact rather
/// than argued, and an offset into that fixed ordering is then an exact position. New posts appear
/// on the next fresh query, never mid-page.</para>
/// </summary>
public sealed record RetrievalCursor(long CorpusBound, int Offset)
{
    public string Encode() => Convert.ToBase64String(Encoding.ASCII.GetBytes(
        string.Create(CultureInfo.InvariantCulture, $"c{CorpusBound}:{Offset}")));

    /// <summary>A malformed cursor reads as "start from the beginning", for the reason the lexical cursor gives: a first page is recoverable and an exception on a read path is not.</summary>
    public static RetrievalCursor? Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;

        try
        {
            var text = Encoding.ASCII.GetString(Convert.FromBase64String(encoded));
            if (text.Length < 4 || text[0] != 'c') return null;

            var separator = text.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 1) return null;

            return long.TryParse(text.AsSpan(1, separator - 1), CultureInfo.InvariantCulture, out var bound)
                && int.TryParse(text.AsSpan(separator + 1), CultureInfo.InvariantCulture, out var offset)
                && bound >= 0 && offset >= 0
                    ? new RetrievalCursor(bound, offset)
                    : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
