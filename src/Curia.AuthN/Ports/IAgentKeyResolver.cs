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
/// The agent is a parameter, for the reason the instant is: there is no way to ask for a key
/// without saying whose (R5.20, errata G15). A signature shows that its signer holds <i>some</i>
/// registered key and says nothing about whose; only the Registrar's store knows, and a lookup by
/// <c>kid</c> alone discards the answer. This port once took a <c>kid</c> and an instant, and its
/// remarks said each instance was scoped by its caller to one agent's keys. The token endpoint
/// passed the store unscoped, so a key enrolled under its holder's own identifier obtained every
/// enrolled identity's token. The client-assertion validator now passes the agent the request names
/// as its client.
///
/// The agent is an identifier the store is asked about, never a location, mirroring
/// <see cref="IJwsKeyResolver"/>'s own "resolve <c>kid</c> only within the configured [...] JWKS,
/// never fetch a key from a URL found inside the token" shape: the inputs are two strings and an
/// instant, and an agent identifier that happens to look like a URL is still only a key in the
/// store's table (R4.16 rev., errata A16).
/// </summary>
public interface IAgentKeyResolver
{
    Task<Result<PublicKeyMaterial>> ResolveAsync(
        string agentId, string kid, ServerTimestamp at, CancellationToken cancellationToken = default);
}
