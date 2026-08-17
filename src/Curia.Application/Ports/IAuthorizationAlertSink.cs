using Curia.Domain.Authorization;

namespace Curia.Application.Ports;

/// <summary>Why the PDP could not be reached, and what was done about it.</summary>
public enum PolicyUnavailabilityOutcome
{
    /// <summary>
    /// R7.5's "open for reads from cache": a read was answered from a cached decision that had
    /// already expired. Degraded, but serving.
    /// </summary>
    ServedStaleRead,

    /// <summary>
    /// R7.5's "fail closed for writes". Also covers a read with no cached decision to fall back
    /// on -- there is nothing to be open with, and inventing an allow would be worse than a
    /// denial.
    /// </summary>
    FailedClosed,
}

/// <summary>
/// R7.5: "SHALL emit a high-severity alert" when the PDP is unavailable. A port, because whether
/// that alert is a log line, a metric, or a page is an operational choice and R11.1 keeps the
/// domain free of that decision.
///
/// <para>The alert is not optional garnish on the fallback -- it is half of what R7.5 requires. A
/// system that silently served stale reads through a PDP outage would look healthy precisely
/// while its authorization decisions were least trustworthy, which is the failure mode the
/// requirement exists to prevent. So the decorator raises this on <b>every</b> unavailable call,
/// not once per outage: rate-limiting or deduplicating the alert is the sink's business, and a
/// sink that drops them is at least dropping them somewhere visible.</para>
/// </summary>
public interface IAuthorizationAlertSink
{
    /// <summary>
    /// Raised when the PDP could not be reached. Implementations SHALL NOT throw: this is called
    /// from a fallback path, and an alert sink that fails the request it was warning about would
    /// convert a degraded read into an outage.
    /// </summary>
    void PolicyDecisionPointUnavailable(
        AuthorizationRequest request,
        PolicyUnavailabilityOutcome outcome,
        Exception cause);
}
