using Curia.Domain.Primitives;

namespace Curia.Domain.Credentials;

/// <summary>
/// RFC 9457 problem-type slugs for the credential lifecycle, mirroring
/// <see cref="Curia.Domain.DomainErrors"/>'s one-factory-per-condition shape so every rejection
/// here names the Table 6 rule it enforces, the same way the event-store errors name theirs.
/// </summary>
public static class CredentialErrors
{
    /// <summary>
    /// Table 6 defines no cell for (<paramref name="from"/>, <paramref name="trigger"/>). A
    /// <see cref="Result{T}"/> failure (CS-10) -- never an exception, never a silent no-op --
    /// exactly as <see cref="Curia.Domain.DomainErrors.ConcurrencyConflict"/> is for the event
    /// store's own illegal-write case.
    /// </summary>
    public static Error IllegalTransition(CredentialState from, CredentialTrigger trigger) => new(
        "curia/domain/credential/illegal-transition",
        "Table 6 defines no exit for this state under this trigger",
        $"from={from} trigger={trigger}");
}
