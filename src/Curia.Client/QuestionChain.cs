using System.Collections.Immutable;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// What <see cref="QuestionChain.WalkAsync"/> read down a revised question's chain (errata G50, R11.67). <b>Behaviour-preserving seam of PR 3's Task 0:</b>
/// declared, filled by nothing and read by nothing; Task 4 gives it its members (the posts read, and why the walk stopped).
/// </summary>
public sealed record QuestionChainResult(ImmutableArray<ProvenancePost> Posts);

/// <summary>
/// R11.67's bounded walk down a revised question's chain by the Forum's <c>superseded_by</c>, for the answer's receipt in both clients. <b>Behaviour-preserving
/// seam of PR 3's Task 0:</b> the signature exists and the walk makes no read; no client calls it. The probes RLW23 to RLW32, RLW44 and RLW45 are red until
/// Task 4 builds it.
/// </summary>
public static class QuestionChain
{
    /// <summary>R11.67's bound: at most this many posts are read after the question (the number R11.55's refusal path uses).</summary>
    public const int WalkBound = 8;

    /// <summary>Follows <paramref name="question"/>'s <c>superseded_by</c> down through <paramref name="client"/>. The seam reads nothing.</summary>
    public static Task<QuestionChainResult> WalkAsync(ForumClient client, ProvenancePost question, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(question);
        return Task.FromResult(new QuestionChainResult([]));
    }
}
