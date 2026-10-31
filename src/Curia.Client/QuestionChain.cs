using System.Collections.Immutable;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>Why <see cref="QuestionChain.WalkAsync"/> stopped (errata G50, R11.67). The words of each are <see cref="Supersession.QuestionChainStanding"/>'s.</summary>
public enum QuestionChainEnd
{
    /// <summary>The question's <c>superseded_by</c> is empty or was not served: no walk, no read, and nothing to say beyond <see cref="Supersession.QuestionStanding"/>.</summary>
    NotSuperseded = 0,

    /// <summary>The last post read has an empty list: it is the latest revision the Forum reports.</summary>
    Latest,

    /// <summary>A list of two or more successors: stated as FORKED, none followed.</summary>
    Forked,

    /// <summary>A single successor the Forum lists by digest alone (no post id): R10.91's words, and no read.</summary>
    NotServed,

    /// <summary>The last post read served no <c>superseded_by</c>: no head is claimed past it.</summary>
    ListNotStated,

    /// <summary>The post served for the next revision does not hash to the digest the list named.</summary>
    DigestDiffers,

    /// <summary>The post served for the next revision is not signed as a revision.</summary>
    KindNotRevision,

    /// <summary>The next revision's signed <c>prev</c> is not the recomputed digest of the post before it.</summary>
    PrevNotPrevious,

    /// <summary>The next revision named is a post this walk had already read.</summary>
    AlreadyRead,

    /// <summary>The read of the next revision failed.</summary>
    ReadFailed,

    /// <summary>The bound was spent with a successor still listed.</summary>
    BoundSpent,
}

/// <summary>A post the walk read and confirmed (its recomputed digest is the one the previous list named, it is signed as a revision, its <c>prev</c> is the previous post's digest), with its own list.</summary>
public sealed record QuestionChainHop(string PostId, ImmutableArray<Successor> SupersededBy);

/// <summary>
/// What <see cref="QuestionChain.WalkAsync"/> read down a revised question's chain (errata G50, R11.67): the question's own list, each confirmed hop with its own
/// list, and why the walk stopped. Holds no value an author wrote; the posts' ids and digests are the Forum's word.
/// </summary>
public sealed record QuestionChainResult(ImmutableArray<Successor> QuestionList, ImmutableArray<QuestionChainHop> Hops, QuestionChainEnd End);

/// <summary>
/// R11.67's bounded walk down a revised question's chain by the Forum's <c>superseded_by</c>, for the answer's receipt in both clients. It is advisory (the
/// caller states it and never stops the answer on it), it is the Forum's word (no signature is checked and no key is fetched), and it recomputes what it can:
/// each post's digest from the canonical form served, and reads each post's signed kind and <c>prev</c> from that form. At most <see cref="WalkBound"/> posts
/// are read after the question, and a question that is not superseded costs no read.
/// </summary>
public static class QuestionChain
{
    /// <summary>R11.67's bound: at most this many posts are read after the question (the number R11.55's refusal path uses).</summary>
    public const int WalkBound = 8;

    /// <summary><see cref="WalkBound"/> as the walk's words say it, derived from it so the two cannot drift.</summary>
    internal static string WalkBoundWords => WalkBound switch
    {
        1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten",
        _ => WalkBound.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Follows <paramref name="question"/>'s <c>superseded_by</c> down through <paramref name="client"/>: nothing for an empty or absent list; for one successor
    /// with a post id, a read of that post (marking none) and the three keyless checks, then its own list; stopping at an empty list, a list of two or more, a
    /// successor with no post id, a read that fails, a check that does not hold, a post already read, or the bound. Never throws except to honour
    /// <paramref name="ct"/>.
    /// </summary>
    public static async Task<QuestionChainResult> WalkAsync(ForumClient client, ProvenancePost question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(question);

        var current = question.Provenance.SupersededBy;
        if (current.IsDefaultOrEmpty) return new QuestionChainResult(current, [], QuestionChainEnd.NotSuperseded);

        var hops = ImmutableArray.CreateBuilder<QuestionChainHop>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { question.PostId };
        var previousDigest = SignatureCheck.Verify(question, []).PrefixedDigest;

        QuestionChainResult Stop(QuestionChainEnd end) => new(question.Provenance.SupersededBy, hops.ToImmutable(), end);

        while (true)
        {
            if (current.Length > 1) return Stop(QuestionChainEnd.Forked);

            var next = current[0];
            if (next.PostId is not { } nextId) return Stop(QuestionChainEnd.NotServed);
            if (!visited.Add(nextId)) return Stop(QuestionChainEnd.AlreadyRead);
            if (hops.Count == WalkBound) return Stop(QuestionChainEnd.BoundSpent);

            var read = await client.GetPostAsync(nextId, MarkingMode.None, ct).ConfigureAwait(false);
            if (!read.TryGetValue(out var post, out _)) return Stop(QuestionChainEnd.ReadFailed);

            var verdict = SignatureCheck.Verify(post!, []);
            if (!string.Equals(verdict.PrefixedDigest, next.Digest, StringComparison.Ordinal)) return Stop(QuestionChainEnd.DigestDiffers);

            var envelope = new Passage(post!, verdict).Envelope;
            if (envelope is not { Kind: Curia.Domain.Content.PostKind.Revision }) return Stop(QuestionChainEnd.KindNotRevision);
            if (previousDigest is null || !string.Equals(envelope.Prev, previousDigest, StringComparison.Ordinal)) return Stop(QuestionChainEnd.PrevNotPrevious);

            hops.Add(new QuestionChainHop(post!.PostId, post.Provenance.SupersededBy));
            previousDigest = verdict.PrefixedDigest;
            current = post.Provenance.SupersededBy;
            if (current.IsDefault) return Stop(QuestionChainEnd.ListNotStated);
            if (current.IsEmpty) return Stop(QuestionChainEnd.Latest);
        }
    }
}
