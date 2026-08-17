using System.Diagnostics.CodeAnalysis;
using Curia.Application.Authorization;
using Curia.Application.Ports;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>R7.4's caching rules and R7.5's unavailability behaviour.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CachingPolicyDecisionPointTests
{
    /// <summary>An inner PDP whose answers and availability the test controls.</summary>
    private sealed class ScriptedPolicyDecisionPoint : IPolicyDecisionPoint
    {
        internal int Calls { get; private set; }

        internal bool Unavailable { get; set; }

        public ValueTask<Result<AuthorizationDecision>> EvaluateAsync(
            AuthorizationRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Unavailable)
                throw new InvalidOperationException("policy engine unreachable");

            return ValueTask.FromResult(AccessPolicy.Decide(request));
        }
    }

    private sealed class RecordingAlertSink : IAuthorizationAlertSink
    {
        internal List<PolicyUnavailabilityOutcome> Raised { get; } = [];

        public void PolicyDecisionPointUnavailable(
            AuthorizationRequest request, PolicyUnavailabilityOutcome outcome, Exception cause) =>
            Raised.Add(outcome);
    }

    private static readonly DateTimeOffset Start = new(2026, 8, 16, 0, 0, 0, TimeSpan.Zero);

    private static AuthorizationRequest Read() => new(
        TierFixture.As(PrincipalTier.T1), CredentialState.Active, ResourceKind.Thread, ActionKind.Read);

    private static AuthorizationRequest Write() => new(
        TierFixture.As(PrincipalTier.T1), CredentialState.Active, ResourceKind.Answer, ActionKind.Create);

    private static AuthorizationRequest Moderation() => new(
        TierFixture.As(PrincipalTier.T3), CredentialState.Active, ResourceKind.Moderation, ActionKind.Apply);

    private static (CachingPolicyDecisionPoint Pdp, ScriptedPolicyDecisionPoint Inner, RecordingAlertSink Alerts, ManualTimeProvider Clock)
        Build(TimeSpan? ttl = null)
    {
        var inner = new ScriptedPolicyDecisionPoint();
        var alerts = new RecordingAlertSink();
        var clock = new ManualTimeProvider(Start);
        return (new CachingPolicyDecisionPoint(inner, alerts, clock, ttl ?? TimeSpan.FromSeconds(10)), inner, alerts, clock);
    }

    // ---- R7.4 ------------------------------------------------------------------------------

    [Fact]
    public async Task R7_4_AReadIsServedFromCacheWithinTheTtl()
    {
        var (pdp, inner, _, clock) = Build();
        var ct = TestContext.Current.CancellationToken;

        await pdp.EvaluateAsync(Read(), ct);
        clock.Advance(TimeSpan.FromSeconds(9));
        await pdp.EvaluateAsync(Read(), ct);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task R7_4_AReadIsReevaluatedOnceTheTtlExpires()
    {
        var (pdp, inner, _, clock) = Build();
        var ct = TestContext.Current.CancellationToken;

        await pdp.EvaluateAsync(Read(), ct);
        clock.Advance(TimeSpan.FromSeconds(10));
        await pdp.EvaluateAsync(Read(), ct);

        Assert.Equal(2, inner.Calls);
    }

    /// <summary>
    /// R7.4: "Write and moderation actions SHALL NOT be served from cache." Asserted by calling
    /// twice with no time passing at all -- the most favourable possible conditions for a cache
    /// hit, so a pass here cannot be an accident of timing.
    /// </summary>
    [Fact]
    public async Task R7_4_WritesAndModerationAreNeverCached()
    {
        var (pdp, inner, _, _) = Build();
        var ct = TestContext.Current.CancellationToken;

        await pdp.EvaluateAsync(Write(), ct);
        await pdp.EvaluateAsync(Write(), ct);
        Assert.Equal(2, inner.Calls);

        await pdp.EvaluateAsync(Moderation(), ct);
        await pdp.EvaluateAsync(Moderation(), ct);
        Assert.Equal(4, inner.Calls);
    }

    /// <summary>
    /// R7.4 caps caching at 10 seconds. A longer TTL is rejected at construction, so a
    /// misconfigured deployment fails at startup rather than serving past the ceiling.
    /// </summary>
    [Fact]
    public void R7_4_ATtlAboveTheCeilingIsRejected()
    {
        var inner = new ScriptedPolicyDecisionPoint();
        var alerts = new RecordingAlertSink();
        var clock = new ManualTimeProvider(Start);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CachingPolicyDecisionPoint(inner, alerts, clock, TimeSpan.FromSeconds(11)));

        Assert.Equal(TimeSpan.FromSeconds(10), CachingPolicyDecisionPoint.MaximumTtl);
    }

    /// <summary>
    /// Different principals must not share a cached decision. The cache key is the whole request,
    /// so a tier change is a different key -- but that is exactly the kind of thing that is
    /// obviously true until someone keys on the resource for speed.
    /// </summary>
    [Fact]
    public async Task Different_principals_do_not_share_a_cached_decision()
    {
        var (pdp, inner, _, _) = Build();
        var ct = TestContext.Current.CancellationToken;

        var t1 = Read();
        var anonymous = t1 with { Tier = EvaluatedTier.Anonymous(Start) };

        await pdp.EvaluateAsync(t1, ct);
        await pdp.EvaluateAsync(anonymous, ct);

        Assert.Equal(2, inner.Calls);
    }

    /// <summary>
    /// A failure is not cached. An unmodelled pair is a gap in §7.2, and caching it would turn a
    /// gap that someone might fix into one that keeps answering from memory.
    /// </summary>
    [Fact]
    public async Task A_result_failure_is_not_cached()
    {
        var (pdp, inner, _, _) = Build();
        var ct = TestContext.Current.CancellationToken;
        var unmodelled = new AuthorizationRequest(
            TierFixture.As(PrincipalTier.T1), CredentialState.Active, ResourceKind.Board, ActionKind.Search);

        var first = await pdp.EvaluateAsync(unmodelled, ct);
        var second = await pdp.EvaluateAsync(unmodelled, ct);

        Assert.False(first.TryGetValue(out _, out _));
        Assert.False(second.TryGetValue(out _, out _));
        Assert.Equal(2, inner.Calls);
    }

    // ---- R7.5 ------------------------------------------------------------------------------

    /// <summary>R7.5: "open for reads from cache", with the high-severity alert that makes it visible.</summary>
    [Fact]
    public async Task R7_5_AReadIsServedStaleWhenThePdpIsUnavailable()
    {
        var (pdp, inner, alerts, clock) = Build();
        var ct = TestContext.Current.CancellationToken;

        var warm = await pdp.EvaluateAsync(Read(), ct);
        Assert.True(warm.TryGetValue(out var warmDecision, out _));

        clock.Advance(TimeSpan.FromMinutes(5));
        inner.Unavailable = true;

        var stale = await pdp.EvaluateAsync(Read(), ct);

        Assert.True(stale.TryGetValue(out var staleDecision, out _));
        Assert.Equal(warmDecision, staleDecision);
        Assert.Equal([PolicyUnavailabilityOutcome.ServedStaleRead], alerts.Raised);
    }

    /// <summary>R7.5: "fail closed for writes".</summary>
    [Fact]
    public async Task R7_5_AWriteFailsClosedWhenThePdpIsUnavailable()
    {
        var (pdp, inner, alerts, _) = Build();
        var ct = TestContext.Current.CancellationToken;
        inner.Unavailable = true;

        var result = await pdp.EvaluateAsync(Write(), ct);

        Assert.True(result.TryGetValue(out var decision, out _));
        Assert.False(decision!.IsAllowed);
        Assert.Equal("r7.5/pdp-unavailable", decision.Reason);
        Assert.Equal([PolicyUnavailabilityOutcome.FailedClosed], alerts.Raised);
    }

    /// <summary>
    /// A read with nothing cached has nothing to be open with, so it fails closed too. "Open for
    /// reads from cache" is conditional on there being a cache entry; inventing an allow would be
    /// strictly worse than a denial.
    /// </summary>
    [Fact]
    public async Task R7_5_AColdReadFailsClosed()
    {
        var (pdp, inner, alerts, _) = Build();
        var ct = TestContext.Current.CancellationToken;
        inner.Unavailable = true;

        var result = await pdp.EvaluateAsync(Read(), ct);

        Assert.True(result.TryGetValue(out var decision, out _));
        Assert.False(decision!.IsAllowed);
        Assert.Equal([PolicyUnavailabilityOutcome.FailedClosed], alerts.Raised);
    }

    /// <summary>
    /// The alert fires on every unavailable call, not once per outage. Deduplication is the sink's
    /// business; a decorator that decided when an outage was "still the same one" would be making
    /// an operational judgement in the wrong layer.
    /// </summary>
    [Fact]
    public async Task R7_5_EveryUnavailableCallAlerts()
    {
        var (pdp, inner, alerts, _) = Build();
        var ct = TestContext.Current.CancellationToken;
        inner.Unavailable = true;

        await pdp.EvaluateAsync(Write(), ct);
        await pdp.EvaluateAsync(Write(), ct);
        await pdp.EvaluateAsync(Write(), ct);

        Assert.Equal(3, alerts.Raised.Count);
    }

    /// <summary>
    /// R7.14: suspension, quarantine and revocation take effect within 60 seconds across all PEPs.
    /// This decorator holds the only cached authorization state in the system -- tier and
    /// credential state are recomputed from live facts every time -- so the bound follows from the
    /// TTL ceiling. Asserted as: a decision that changes underneath a warm cache is visible within
    /// 60 seconds without any invalidation step.
    /// </summary>
    [Fact]
    public async Task R7_14_AChangedDecisionIsVisibleWellInsideSixtySeconds()
    {
        var (pdp, inner, _, clock) = Build();
        var ct = TestContext.Current.CancellationToken;

        var active = Read();
        await pdp.EvaluateAsync(active, ct);

        // The agent is quarantined. Nothing invalidates anything; the cache key simply changes,
        // and even an identical key would expire within the ceiling.
        var quarantined = active with { CredentialState = CredentialState.Quarantined };
        clock.Advance(CachingPolicyDecisionPoint.MaximumTtl);

        var after = await pdp.EvaluateAsync(quarantined, ct);

        Assert.True(after.TryGetValue(out var decision, out _));
        Assert.Equal("table-11/quarantined-read-only", decision!.Reason);
        Assert.True(CachingPolicyDecisionPoint.MaximumTtl < TimeSpan.FromSeconds(60));
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task A_cancelled_call_is_not_treated_as_unavailability()
    {
        var (pdp, _, alerts, _) = Build();
        var cancelled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            pdp.EvaluateAsync(Read(), cancelled).AsTask());

        // A cancelled request is the caller going away, not the policy engine falling over.
        // Alerting on it would bury real outages in noise from ordinary client disconnects.
        Assert.Empty(alerts.Raised);
    }
}
