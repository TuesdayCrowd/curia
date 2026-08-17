using Curia.Application.Ports;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// R11.4's in-memory adapter for <see cref="IPolicyDecisionPoint"/>: the published model, evaluated
/// in process.
///
/// <para>It delegates to <see cref="AccessPolicy"/> and adds nothing, which is the whole point.
/// R7.3 makes the engine swappable, and "swappable" is only true if there is a statement of what
/// every engine must agree with. That statement is <see cref="AccessPolicy"/>; this adapter is it
/// wearing the port's shape, so a Cedar or Rego adapter has something concrete to be checked
/// against rather than being checked against a prose reading of §7.</para>
///
/// <para>Lives in the test project by the same convention as <c>InMemoryEventStore</c>. A
/// production embedded evaluator -- R7.3 permits one -- would be an
/// <c>Infrastructure</c> adapter, because at that point it is a real engine with real
/// configuration rather than a fixture.</para>
///
/// <para>Deliberately does <b>not</b> cache: R7.4's read-only TTL and R7.5's fail-closed-for-writes
/// rule belong in one decorator over the port, not re-implemented per adapter. See the port's
/// remarks.</para>
/// </summary>
internal sealed class InMemoryPolicyDecisionPoint : IPolicyDecisionPoint
{
    /// <summary>Every request this adapter has answered, in order, for tests asserting on call shape.</summary>
    private readonly List<AuthorizationRequest> _evaluated = [];

    internal IReadOnlyList<AuthorizationRequest> Evaluated => _evaluated;

    public ValueTask<Result<AuthorizationDecision>> EvaluateAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        _evaluated.Add(request);
        return ValueTask.FromResult(AccessPolicy.Decide(request));
    }
}
