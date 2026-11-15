using Curia.Application.Credentials;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

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
    /// <summary>The facts for <paramref name="agentId"/> over <paramref name="log"/>, which this verifies once: a route that holds a <see cref="VerifiedLog"/> already calls the overload that takes it.</summary>
    public static Result<PostureFacts> Of(LifecycleVerifier verifier, IReadOnlyList<AppendedEvent> log, AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(log);

        return Of(verifier.OfLog(log), agentId);
    }

    /// <summary>
    /// The facts for <paramref name="agentId"/> from a log verified once (errata G70, R4.43, R6.67): the owner an attestation binds, the flags a moderation record upholds and the posts it withholds are those of the
    /// operator acts the Forum honours, so a skipped act moves no tier. The caller's one <see cref="VerifiedLog"/> is read, not rebuilt (ruling 76).
    /// </summary>
    public static Result<PostureFacts> Of(VerifiedLog verified, AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(agentId);

        var standings = verified.Standings();
        var posts = PostProjector.Fold(verified.Log);
        var moderation = verified.Moderation();
        bool Servable(string postId) => !moderation.TryGetValue(postId, out var state) || state.MayServe;

        var verification = VerificationProjector.Fold(posts, standings, Servable, DisputedPosts.Fold(verified, posts));

        return AgentStandingProjector.PostureOf(
            standings, agentId, VerificationProjector.VerifiedFindingsBy(verification, posts, agentId));
    }
}
