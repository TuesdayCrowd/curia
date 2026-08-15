using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Ports;

/// <summary>
/// The agent-key half of what used to be one <see cref="IJwsKeyResolver"/> pretending to serve
/// two different questions. The issuer's own JWKS (<see cref="IJwsKeyResolver"/>) has no per-key
/// validity window -- a resolved issuer key is simply the issuer's current signing key. An
/// agent's registered keys are the opposite: R4.17 requires overlapping validity windows so
/// rotation is overlap-then-retire, and errata A12/R6.31 requires that validity be evaluated at
/// <c>server_ts</c> -- never at submission time, never at an envelope's self-reported
/// <c>created_at</c>. A resolver for agent keys that returned material without an instant to
/// check it against would need a second, separate call to be correct, and that second call is
/// exactly the kind of thing an adapter can omit while still looking correct -- which is what A12
/// is about. Requiring the instant in the resolve call itself makes the omission unwritable: there
/// is no way to ask "does this agent have this kid" without simultaneously answering "as of when."
///
/// <paramref name="at"/> is a <see cref="ServerTimestamp"/>, never a bare
/// <see cref="DateTimeOffset"/>, for the same reason <c>Curia.Domain.AgentKeySet.ValidateAt</c>
/// takes one -- see that type's remarks (in <c>src/Curia.Domain/Keys/AgentKeySet.cs</c>) for why
/// the distinction is enforced in the signature rather than left to caller discipline. This
/// interface names the type, not the concrete <c>Curia.Domain</c> model behind it, because
/// <c>Curia.AuthN</c> deliberately does not reference <c>Curia.Domain</c> (CS-5): a future
/// Infrastructure adapter is free to implement this port by calling
/// <c>AgentKeySet.ValidateAt</c>/<c>ValidKeysAt</c> under the hood, but that is an implementation
/// detail on the other side of this port, not something this interface can or should name.
///
/// One resolver instance is scoped by its caller to one agent's registered keys -- see
/// <see cref="ClientAssertionValidationContext.AgentKeyResolver"/>'s remarks -- mirroring
/// <see cref="IJwsKeyResolver"/>'s own "resolve <c>kid</c> only within the configured [...] JWKS,
/// never fetch a key from a URL found inside the token" shape: the only inputs are a
/// <c>kid</c> string and an instant, so there is nothing here an implementation could be tempted
/// to treat as a fetchable location.
/// </summary>
public interface IAgentKeyResolver
{
    Task<Result<PublicKeyMaterial>> ResolveAsync(
        string kid, ServerTimestamp at, CancellationToken cancellationToken = default);
}
