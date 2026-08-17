using System.Collections.Concurrent;
using Curia.Application.Ports;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;

namespace Curia.Application.Authorization;

/// <summary>
/// R7.4 and R7.5 in one place, over any <see cref="IPolicyDecisionPoint"/>.
///
/// <para><b>Why a decorator and not adapter behaviour.</b> R7.4 permits a ≤ 10 second cache for
/// read actions and forbids serving writes or moderation from cache; R7.5 requires failing closed
/// for writes and open for reads from cache on unavailability, with a high-severity alert. Those
/// are three rules keyed on the action's kind, and they are properties of *how the Forum consults
/// a PDP*, not of any particular engine. Implemented per adapter, they would be re-decided every
/// time an engine was swapped -- which is precisely what R7.3's port exists to stop.</para>
///
/// <para><b>This is the only cached authorization state in the system, and that is what makes
/// R7.14 provable.</b> Suspension, quarantine and revocation must take effect within 60 seconds
/// across all PEPs. Tier is recomputed from live facts on every evaluation
/// (<see cref="TierPolicy"/> stores nothing), and credential state is likewise a projection, so
/// the only thing that can hold a stale answer is this cache. Its ceiling is 10 seconds, well
/// inside the 60 second bound -- so the bound follows from the ceiling rather than from an
/// invalidation protocol that has to be remembered and got right.</para>
///
/// <para>The stale-read fallback in R7.5 is the one exception, and it is deliberately unbounded in
/// age: an outage longer than 10 seconds means a reader either gets a stale allow or gets nothing,
/// and P6 chooses availability for reads. It is alarmed on every occurrence so the trade is
/// visible rather than silent.</para>
/// </summary>
public sealed class CachingPolicyDecisionPoint : IPolicyDecisionPoint
{
    /// <summary>R7.4's ceiling. A TTL above this is a configuration error, not a tuning choice.</summary>
    public static readonly TimeSpan MaximumTtl = TimeSpan.FromSeconds(10);

    private readonly IPolicyDecisionPoint _inner;
    private readonly IAuthorizationAlertSink _alerts;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly ConcurrentDictionary<AuthorizationRequest, Entry> _cache = new();

    private sealed record Entry(AuthorizationDecision Decision, DateTimeOffset CachedAt);

    /// <param name="ttl">
    /// How long a read decision may be reused. Clamped by <see cref="MaximumTtl"/> at construction
    /// rather than at read time, so a deployment that misconfigures it fails at startup instead of
    /// quietly serving decisions past R7.4's ceiling.
    /// </param>
    public CachingPolicyDecisionPoint(
        IPolicyDecisionPoint inner,
        IAuthorizationAlertSink alerts,
        TimeProvider clock,
        TimeSpan ttl)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(alerts);
        ArgumentNullException.ThrowIfNull(clock);

        if (ttl < TimeSpan.Zero || ttl > MaximumTtl)
            throw new ArgumentOutOfRangeException(
                nameof(ttl), ttl, $"R7.4 caps decision caching at {MaximumTtl}; a longer TTL is not configurable.");

        _inner = inner;
        _alerts = alerts;
        _clock = clock;
        _ttl = ttl;
    }

    /// <summary>
    /// R7.4: only read actions are cacheable. Writes and moderation are excluded by the action's
    /// kind rather than by an allow-list of resources, so a resource added later is uncacheable
    /// until someone deliberately makes its action a read.
    /// </summary>
    private static bool IsCacheable(AuthorizationRequest request) => ActionKinds.IsRead(request.Action);

    public async ValueTask<Result<AuthorizationDecision>> EvaluateAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Before the cache, not merely before the inner call. A cancelled request has no caller
        // left to answer, and serving it from cache would look like a cache hit in the metrics for
        // work nobody asked for -- the same reason IPolicyDecisionPoint's in-memory adapter checks
        // first thing.
        cancellationToken.ThrowIfCancellationRequested();

        var cacheable = IsCacheable(request);
        var now = _clock.GetUtcNow();

        if (cacheable
            && _cache.TryGetValue(request, out var fresh)
            && now - fresh.CachedAt < _ttl)
            return Result<AuthorizationDecision>.Ok(fresh.Decision);

        try
        {
            var result = await _inner.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);

            // Only a decision is cached. A Result failure means the model could not answer the
            // question -- an unmodelled pair, or a row decided by owner authentication -- and
            // caching that would turn a specification gap into a sticky one.
            if (cacheable && result.TryGetValue(out var decision, out _))
                _cache[request] = new Entry(decision!, now);

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable(request, cacheable, ex);
        }
    }

    /// <summary>
    /// R7.5: "fail closed for writes and open for reads from cache (principle P6), and SHALL emit
    /// a high-severity alert."
    /// </summary>
    private Result<AuthorizationDecision> Unavailable(AuthorizationRequest request, bool cacheable, Exception cause)
    {
        if (cacheable && _cache.TryGetValue(request, out var stale))
        {
            _alerts.PolicyDecisionPointUnavailable(request, PolicyUnavailabilityOutcome.ServedStaleRead, cause);
            return Result<AuthorizationDecision>.Ok(stale.Decision);
        }

        _alerts.PolicyDecisionPointUnavailable(request, PolicyUnavailabilityOutcome.FailedClosed, cause);

        // A denial rather than a Result failure: the PDP being unreachable is not a gap in the
        // model, and the caller's correct response is the same as for any denial. The reason slug
        // is distinct so R7.16's audit trail can separate "policy said no" from "policy could not
        // be asked", which are very different operational signals even though both deny.
        return Result<AuthorizationDecision>.Ok(
            new AuthorizationDecision(DecisionEffect.Deny, "r7.5/pdp-unavailable", GrantQualifier.None));
    }
}
