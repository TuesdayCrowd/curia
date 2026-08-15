using Curia.Domain.Primitives;

namespace Curia.AuthN.Ports;

/// <summary>Errata B4/R5.19: a server-issued, server-chosen freshness value bound into a DPoP
/// proof's <c>nonce</c> claim, rotated on an interval &lt;= <see cref="AuthNConstants.MaxDpopNonceRotationInterval"/>.
/// Unlike <see cref="IReplayCache"/>, a nonce is not single-use -- RFC 9449 §8 has the server
/// accept any proof carrying the currently active nonce, for as long as that nonce remains
/// current, and rotate to a new one on its own schedule. What it defends against is a stockpile
/// of proofs pre-signed before the server chose today's freshness value, not repetition of one
/// proof (the <c>jti</c> replay cache already owns that).</summary>
public interface IDpopNonceStore
{
    /// <summary>Issues a fresh nonce, valid until <see cref="DpopNonce.ExpiresAt"/>. Called both to
    /// seed the first nonce a client will see and to hand back a fresh one in the RFC 9449
    /// <c>use_dpop_nonce</c> challenge when <see cref="IsCurrentAsync"/> rejects a request.</summary>
    Task<Result<DpopNonce>> IssueAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether <paramref name="nonce"/> is a value this store issued and has not yet
    /// expired. Does not consume it -- see the type remarks on why a nonce is not single-use.</summary>
    Task<Result<bool>> IsCurrentAsync(string nonce, CancellationToken cancellationToken = default);
}

/// <summary>A server-issued DPoP nonce and the instant it stops being current (R5.19's
/// "rotation intervals &lt;= 5 minutes" applied to one value).</summary>
public sealed record DpopNonce(string Value, DateTimeOffset ExpiresAt);
