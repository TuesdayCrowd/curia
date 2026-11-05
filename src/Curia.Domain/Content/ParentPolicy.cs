using Curia.Domain.Primitives;

namespace Curia.Domain.Content;

/// <summary>
/// What the caller read of a parent: its post id, the kind its <c>post.accepted</c> event stored and the board that event stored. The board is the empty
/// string until errata G61 (R8.87) is built: the lookup does not read it yet.
/// </summary>
public sealed record ParentFacts(string PostId, PostKind Kind, string Board = "");

/// <summary>
/// R8.70 (errata G38) as a function: why a submission's <c>parent</c> may not be what it names, or <see langword="null"/> when it
/// may. The caller reads the parent; this decides. Only an answer and a comment consult it.
/// </summary>
public static class ParentPolicy
{
    /// <param name="kind">The submission's kind.</param>
    /// <param name="parentAsSubmitted">The submission's <c>parent</c> exactly as it wrote it.</param>
    /// <param name="found">What the lookup read for it, or <see langword="null"/> when no post has that id.</param>
    public static Error? Refusal(PostKind kind, string? parentAsSubmitted, ParentFacts? found)
    {
        if (kind is not (PostKind.Answer or PostKind.Comment))
            return null;

        if (found is null)
            return ParentErrors.NotFound(parentAsSubmitted);

        return kind == PostKind.Answer && found.Kind != PostKind.Question
            ? ParentErrors.NotAQuestion(parentAsSubmitted, found.Kind)
            : null;
    }

    /// <summary>
    /// R8.87 (errata G61) as a function: why an answer's, a comment's or a revision's board may not be the board of the post it follows, or
    /// <see langword="null"/> when it may. Not built: it refuses nothing, so the probes of errata G61 fail for the rule and not for a missing symbol.
    /// </summary>
    /// <param name="kind">The submission's kind.</param>
    /// <param name="submittedBoard">The submission's <c>board</c> exactly as it wrote it.</param>
    /// <param name="followedPostId">The id of the post the submission follows (its parent, or for a revision the post its <c>prev</c> names).</param>
    /// <param name="followedBoard">The board that post's <c>post.accepted</c> event stored.</param>
    public static Error? ReplyBoard(PostKind kind, string submittedBoard, string followedPostId, string followedBoard) => null;
}
