using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// The <see cref="IPolicyDecisionPoint"/> contract, exercised through the in-memory adapter. These
/// are the assertions any adapter must satisfy -- a Cedar or Rego adapter arriving later is
/// expected to be run against this same set, which is what makes R7.3's "swappable" claim
/// checkable rather than aspirational.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class InMemoryPolicyDecisionPointTests
{
    private static AuthorizationRequest Request(
        PrincipalTier tier,
        ResourceKind resource,
        ActionKind action,
        CredentialState state = CredentialState.Active) =>
        new(TierFixture.As(tier), state, resource, action);

    /// <summary>
    /// The adapter really is an adapter for the port. Asserted directly because every other test
    /// here uses the concrete type (CA1859), which would otherwise let the class drift away from
    /// the interface without any of them noticing.
    /// </summary>
    [Fact]
    public void The_adapter_implements_the_port() =>
        Assert.IsAssignableFrom<IPolicyDecisionPoint>(new InMemoryPolicyDecisionPoint());

    [Fact]
    public async Task A_permitted_request_is_allowed()
    {
        var pdp = new InMemoryPolicyDecisionPoint();
        var ct = TestContext.Current.CancellationToken;

        var result = await pdp.EvaluateAsync(
            Request(PrincipalTier.T2, ResourceKind.Finding, ActionKind.Create), ct);

        Assert.True(result.TryGetValue(out var decision, out _));
        Assert.True(decision!.IsAllowed);
    }

    /// <summary>
    /// R7.16: a denial must be as informative as an allow. Asserted as "the decision names the rule
    /// that produced it", since a bare false is what makes denial logging useless.
    /// </summary>
    [Fact]
    public async Task R7_16_ADenialCarriesTheRuleThatProducedIt()
    {
        var pdp = new InMemoryPolicyDecisionPoint();
        var ct = TestContext.Current.CancellationToken;

        var byTier = await pdp.EvaluateAsync(
            Request(PrincipalTier.T1, ResourceKind.Finding, ActionKind.Create), ct);
        var byQuarantine = await pdp.EvaluateAsync(
            Request(PrincipalTier.T2, ResourceKind.Finding, ActionKind.Create, CredentialState.Quarantined), ct);

        Assert.True(byTier.TryGetValue(out var tierDecision, out _));
        Assert.True(byQuarantine.TryGetValue(out var quarantineDecision, out _));

        Assert.False(tierDecision!.IsAllowed);
        Assert.False(quarantineDecision!.IsAllowed);

        // Both are denials, and the audit trail can still tell them apart -- one means "not yet",
        // the other means "not while quarantined", and they want different operational responses.
        Assert.NotEqual(tierDecision.Reason, quarantineDecision.Reason);
    }

    /// <summary>
    /// A question the model cannot answer is a failure, not a denial. An adapter that flattened it
    /// to "deny" would report a specification gap as a policy decision.
    /// </summary>
    [Fact]
    public async Task An_unanswerable_question_is_a_failure_not_a_denial()
    {
        var pdp = new InMemoryPolicyDecisionPoint();
        var ct = TestContext.Current.CancellationToken;

        var result = await pdp.EvaluateAsync(Request(PrincipalTier.T3, ResourceKind.Board, ActionKind.Create), ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authz/unmodelled-resource-action", error!.Type);
    }

    /// <summary>
    /// R7.13: authorization is evaluated per request. The port must therefore actually be consulted
    /// every time -- an adapter that answered the second identical call from a remembered answer
    /// would be the session-scoped authorization ZT tenet 6 forbids, and R7.4 permits caching only
    /// in a decorator with a bounded TTL, never inside the adapter.
    /// </summary>
    [Fact]
    public async Task R7_13_EveryCallIsEvaluated()
    {
        var pdp = new InMemoryPolicyDecisionPoint();
        var ct = TestContext.Current.CancellationToken;
        var request = Request(PrincipalTier.T1, ResourceKind.Answer, ActionKind.Create);

        await pdp.EvaluateAsync(request, ct);
        await pdp.EvaluateAsync(request, ct);
        await pdp.EvaluateAsync(request, ct);

        Assert.Equal(3, pdp.Evaluated.Count);
    }

    [Fact]
    public async Task A_cancelled_call_does_not_answer()
    {
        var pdp = new InMemoryPolicyDecisionPoint();
        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            pdp.EvaluateAsync(Request(PrincipalTier.T1, ResourceKind.Vote, ActionKind.Cast), cancelled).AsTask());

        Assert.Empty(pdp.Evaluated);
    }

    [Fact]
    public async Task A_null_request_is_rejected()
    {
        var pdp = new InMemoryPolicyDecisionPoint();
        var ct = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentNullException>(() => pdp.EvaluateAsync(null!, ct).AsTask());
    }

    /// <summary>
    /// The adapter agrees with the domain model on every modelled pair and tier. This is the
    /// statement a swapped-in engine has to satisfy, so it is written as a sweep rather than as a
    /// sample.
    ///
    /// <para><b>TRW44 (R7.26, errata G46): the sweep compares decisions, not two failures.</b> It used to build
    /// every request with no counts, so a pair whose decision needs one failed on both sides and "agreed":
    /// the <c>flag</c>/<c>raise</c> pairs since R7.22, and the <c>answer</c>/<c>accept</c> pairs once the
    /// acceptance budget needs its count. The sweep now supplies every count, runs twice (all counts zero;
    /// then every count at <see cref="int.MaxValue"/> with nothing marked a replay) and asserts that the only
    /// failure on either side is the owner-authenticated <c>agent</c>/<c>enroll</c> row.</para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    public async Task The_adapter_agrees_with_the_domain_model_everywhere(int count)
    {
        var pdp = new InMemoryPolicyDecisionPoint();
        var ct = TestContext.Current.CancellationToken;
        var compared = 0;
        var failedOtherThanEnroll = new List<string>();

        foreach (var pair in ResourceActionModel.ModelledPairs)
        foreach (var tier in Enum.GetValues<PrincipalTier>())
        foreach (var state in new[] { CredentialState.Active, CredentialState.Quarantined })
        {
            var request = new AuthorizationRequest(
                TierFixture.As(tier), state, pair.Resource, pair.Action,
                PostsToday: 0, FlagsToday: count, AcceptsToday: count, AcceptRecordsNothing: false);

            var viaPort = await pdp.EvaluateAsync(request, ct);
            var viaDomain = AccessPolicy.Decide(request);

            var portOk = viaPort.TryGetValue(out var portDecision, out var portError);
            var domainOk = viaDomain.TryGetValue(out var domainDecision, out var domainError);

            Assert.Equal(domainOk, portOk);
            Assert.Equal(domainDecision, portDecision);
            Assert.Equal(domainError, portError);

            var isEnroll = pair.Resource is ResourceKind.Agent && pair.Action is ActionKind.Enroll;
            if (!domainOk && !isEnroll)
                failedOtherThanEnroll.Add($"{pair.Resource}/{pair.Action} {tier} {state}: {domainError!.Type}");

            compared++;
        }

        // 20 pairs x 5 tiers x 2 states -- 16 pairs until errata G3 added `flag`/`list` and
        // `moderation`/`list` to Table 10, and 18 until errata G21 added `key`/`rotate` and `key`/`retire`.
        // Asserted so that a sweep which silently iterated nothing -- the failure this project keeps
        // rediscovering -- cannot pass as agreement.
        Assert.Equal(200, compared);

        // TRW44: with every count supplied, a failure on both sides is not agreement but a pair the sweep did not reach.
        Assert.Empty(failedOtherThanEnroll);
    }

    /// <summary>
    /// TRW44, second half: with a count missing, the port and the domain both fail, and fail alike, for each action that needs one. R7.22 for
    /// <c>flag</c>/<c>raise</c> (green first) and R7.26 for <c>answer</c>/<c>accept</c> (red until the policy needs its count).
    /// </summary>
    [Fact]
    public async Task A_missing_count_is_a_failure_on_both_sides_for_every_action_that_needs_one()
    {
        var pdp = new InMemoryPolicyDecisionPoint();
        var ct = TestContext.Current.CancellationToken;
        var outcomes = new List<string>();
        var expected = new List<string>();

        foreach (var (resource, action) in new[] { (ResourceKind.Flag, ActionKind.Raise), (ResourceKind.Answer, ActionKind.Accept) })
        foreach (var tier in new[] { PrincipalTier.T0, PrincipalTier.T1, PrincipalTier.T2, PrincipalTier.T3 })
        {
            var request = new AuthorizationRequest(
                TierFixture.As(tier), CredentialState.Active, resource, action,
                PostsToday: 0, FlagsToday: resource is ResourceKind.Flag ? null : 0, AcceptsToday: null);

            var viaPort = await pdp.EvaluateAsync(request, ct);
            var viaDomain = AccessPolicy.Decide(request);

            var port = viaPort.TryGetValue(out var portDecision, out var portError) ? portDecision!.Effect.ToString() : "fail " + portError!.Type;
            var domain = viaDomain.TryGetValue(out var domainDecision, out var domainError) ? domainDecision!.Effect.ToString() : "fail " + domainError!.Type;
            outcomes.Add($"{resource}/{action} {tier}: port {port}; domain {domain}");

            var slug = resource is ResourceKind.Flag ? "flag-count-missing" : "accept-count-missing";
            expected.Add($"{resource}/{action} {tier}: port fail curia/authz/{slug}; domain fail curia/authz/{slug}");
        }

        Assert.Equal(expected, outcomes);
    }
}
