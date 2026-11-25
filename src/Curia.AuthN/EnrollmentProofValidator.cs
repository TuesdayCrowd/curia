using Curia.AuthN.Dpop;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.AuthN;

/// <summary>What an enrollment request carries that R4.46's proof is judged against: the request's members, the key's stored form (R4.28) and the proof as sent (null when the member was absent).</summary>
public sealed record EnrollmentProofRequest(string AgentId, string Alg, string Kid, ReadOnlyMemory<byte> PublicKey, string? Proof);

/// <summary>
/// STUB (Task 2 of the "enrolment proves possession" stage, errata G79). It exists so that the probes written before the validator compile and run: it accepts every request,
/// as the enrollment route does today, so every probe whose verdict is a refusal is red by its own assertion. Task 3 replaces this file with the validator R4.46 describes;
/// nothing else may call it.
/// </summary>
public sealed class EnrollmentProofValidator
{
    private readonly IReadOnlyDictionary<string, IContentVerifier> _verifiers;
    private readonly IDpopNonceStore _nonces;
    private readonly IReplayCache _replay;

    public EnrollmentProofValidator(IReadOnlyDictionary<string, IContentVerifier> verifiers, IDpopNonceStore nonces, IReplayCache replay)
    {
        ArgumentNullException.ThrowIfNull(verifiers);
        ArgumentNullException.ThrowIfNull(nonces);
        ArgumentNullException.ThrowIfNull(replay);
        _verifiers = verifiers;
        _nonces = nonces;
        _replay = replay;
    }

    /// <summary>R4.46's seven steps in order, all of them; today's stub decides none of them.</summary>
    public Task<Result<Unit>> ValidateAsync(EnrollmentProofRequest request, string audience, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(audience);
        _ = (_verifiers, _nonces, _replay, now, cancellationToken);
        return Task.FromResult(Result<Unit>.Ok(Unit.Value));
    }
}
