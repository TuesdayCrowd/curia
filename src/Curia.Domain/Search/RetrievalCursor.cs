using System.Globalization;
using System.Text;
using Curia.Domain.Primitives;

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

    /// <summary>
    /// Absent (<see langword="null"/> success) or a cursor; a malformed one is a refusal.
    ///
    /// <para>This reverses the decision this type shipped with — "a malformed cursor reads as start
    /// from the beginning… a first page is recoverable and an exception on a read path is not"
    /// (R9.25). Two objections. The alternative was never an exception: it is the same
    /// <c>400</c> three sibling members of the same request already return, so the read path stays
    /// as safe as it was. And the cost of the silent reading is specific rather than theoretical —
    /// this cursor carries R9.22's corpus bound, so dropping it re-evaluates a continuation against
    /// a <i>different</i> corpus than R9.22 requires, while the response reports the new bound as
    /// though it had always been the bound. The caller cannot see it: a correctly continued page and
    /// a first page it believes was continued are the same document.</para>
    ///
    /// <para>Nor does the recoverable-first-page argument protect anyone who exists. A caller only
    /// ever echoes a cursor the Forum minted, so a malformed one arrives by corruption or by
    /// forgery, and neither is served better by silently starting over.</para>
    /// </summary>
    public static Result<RetrievalCursor?> Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return Result<RetrievalCursor?>.Ok(null);

        try
        {
            var text = Encoding.ASCII.GetString(Convert.FromBase64String(encoded));
            if (text.Length < 4 || text[0] != 'c') return Malformed();

            var separator = text.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 1) return Malformed();

            return long.TryParse(text.AsSpan(1, separator - 1), CultureInfo.InvariantCulture, out var bound)
                && int.TryParse(text.AsSpan(separator + 1), CultureInfo.InvariantCulture, out var offset)
                && bound >= 0 && offset >= 0
                    ? Result<RetrievalCursor?>.Ok(new RetrievalCursor(bound, offset))
                    : Malformed();
        }
        catch (FormatException)
        {
            return Malformed();
        }
    }

    /// <summary>
    /// The refusal, echoing no value. A cursor the caller did not mint is evidence of corruption or
    /// forgery, and quoting it back into a response quotes an attacker — the same reasoning R8.61
    /// applies to a duplicate refusal and R10.28 to a flag rationale.
    /// </summary>
    private static Result<RetrievalCursor?> Malformed() => Result<RetrievalCursor?>.Fail(new Error(
        "curia/search/cursor-malformed",
        "The `cursor` member is not a cursor this Forum minted",
        "Omit it to start a fresh query, or send back a cursor from a previous page of this search " +
        "unaltered. It is refused rather than read as a first page because it carries the corpus " +
        "bound the continuation must be evaluated against (R9.22, R9.25)."));
}
