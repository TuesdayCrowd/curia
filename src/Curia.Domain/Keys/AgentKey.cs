namespace Curia.Domain;

/// <summary>
/// One entry in an agent's key history: the material (<see cref="AgentPublicKey"/>, itself only
/// constructible already-validated per R4.15/R4.28) paired with the interval it was authoritative
/// for (<see cref="KeyValidityWindow"/>, itself only constructible well-formed). Composing two
/// already-validated value types has nothing further to validate, so -- like
/// <c>Curia.Domain.DomainEvent</c> -- this is a plain positional record with no separate
/// <c>Result</c>-returning factory: there is no invalid <see cref="AgentKey"/> that both
/// arguments being individually valid could still produce.
/// </summary>
public sealed record AgentKey(AgentPublicKey Material, KeyValidityWindow Validity)
{
    public KeyId Kid => Material.Kid;

    /// <summary>R6.31: the only question this type answers, and always against a <see cref="ServerTimestamp"/>.</summary>
    public bool IsValidAt(ServerTimestamp at) => Validity.Contains(at);
}
