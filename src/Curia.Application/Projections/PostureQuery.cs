using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>
/// Table 11's facts for one agent, from the whole log: the standing fold, plus the verification
/// fold that Table 11's "≥ 1 verified finding" needs (R7.19).
///
/// <para>One function so every PEP evaluates the same facts. Until Stage 3 each route folded the
/// standing itself and passed no verified-finding count, so T2's disjunction ran on one arm
/// everywhere at once and nothing disagreed; the failure was consistent, which is why it read as
/// design. A second route computing the count differently would be the failure that presents as an
/// agent oscillating between tiers.</para>
/// </summary>
public static class PostureQuery
{
    public static Result<PostureFacts> Of(IReadOnlyList<AppendedEvent> log, string agentId)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        var standings = AgentStandingProjector.Fold(log);
        var posts = PostProjector.Fold(log);
        var moderation = FlagProjector.Fold(log);
        bool Servable(string postId) => !moderation.TryGetValue(postId, out var state) || state.MayServe;

        var verification = VerificationProjector.Fold(posts, standings, Servable);

        return AgentStandingProjector.PostureOf(
            standings, agentId, VerificationProjector.VerifiedFindingsBy(verification, posts, agentId));
    }
}
