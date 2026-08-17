using Curia.Domain.Authorization;
using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>
/// R7.3: the PDP as a port. The domain expresses <i>what decision it needs</i>; the adapter knows
/// Cedar, Rego, or an embedded evaluator. R7.2 fixes the vocabulary as the OpenID AuthZEN
/// Authorization API 1.0 shape -- a request carrying <c>subject</c>, <c>action</c>,
/// <c>resource</c> and <c>context</c>, answered with a decision plus optional context -- which is
/// what makes the engine swappable rather than a permanent coupling.
///
/// <para><b>Asynchronous because the real adapter is a network call.</b> An engine reached over
/// AuthZEN is out of process, and R7.13 requires evaluation per request, so this is on the hot
/// path of every call the Forum serves. Modelling it as synchronous would be modelling the
/// in-memory adapter and hoping the real one fits.</para>
///
/// <para><b>What this port does not do.</b> It does not cache. R7.4 permits a TTL ≤ 10 seconds for
/// read actions and forbids serving writes or moderation from cache at all, and R7.5 requires
/// failing <i>closed for writes</i> and <i>open for reads from cache</i> on PDP unavailability
/// with a high-severity alert. Those are three different behaviours keyed on the action's kind,
/// and burying them in every adapter would mean re-deciding them per engine. They belong in one
/// decorator over this port, which is where Stage 2 puts them -- so an adapter implementing this
/// interface should answer the question and nothing else.</para>
///
/// <para><b>Why a <see cref="Result{T}"/> and not just a decision.</b> A denial is a well-formed
/// answer and arrives as an <see cref="AuthorizationDecision"/> whose effect is
/// <see cref="DecisionEffect.Deny"/>. A failure is the different case where the question could not
/// be answered -- Table 10 models no such row, or the row is decided by owner authentication
/// rather than by tier. Collapsing the two would let a specification gap reach the caller wearing
/// a deliberate denial's clothes, and R7.16's requirement to log denials at full fidelity would
/// then be logging the wrong thing.</para>
/// </summary>
public interface IPolicyDecisionPoint
{
    /// <summary>
    /// Evaluate one request. Implementations SHALL NOT consult a cache (see the type's remarks) and
    /// SHALL be safe to call concurrently, since R7.13 puts this on every request.
    /// </summary>
    ValueTask<Result<AuthorizationDecision>> EvaluateAsync(
        AuthorizationRequest request,
        CancellationToken cancellationToken = default);
}
