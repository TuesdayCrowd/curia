using Curia.Domain.Primitives;

namespace Curia.Domain.Content;

/// <summary>What the caller read of a parent: its post id and the kind its <c>post.accepted</c> event stored.</summary>
public sealed record ParentFacts(string PostId, PostKind Kind);

/// <summary>
/// R8.70 (errata G38) as a function: why a submission's <c>parent</c> may not be what it names, or <see langword="null"/> when it
/// may. The caller reads the parent; this decides. Only an answer and a comment consult it.
/// </summary>
public static class ParentPolicy
{
    /// <param name="kind">The submission's kind.</param>
    /// <param name="parentAsSubmitted">The submission's <c>parent</c> exactly as it wrote it.</param>
    /// <param name="found">What the lookup read for it, or <see langword="null"/> when no post has that id.</param>
    public static Error? Refusal(PostKind kind, string? parentAsSubmitted, ParentFacts? found) => null;
}
