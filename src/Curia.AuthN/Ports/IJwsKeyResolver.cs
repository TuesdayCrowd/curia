using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Ports;

/// <summary>
/// R5.10: "resolve <c>kid</c> only within the configured issuer JWKS. Never fetch a key from a
/// URL found inside the token." The signature is the enforcement mechanism, not a comment above
/// one: the only input is a <c>kid</c> string, so there is no parameter an implementation could
/// even be tempted to treat as a fetchable location -- unlike errata A16's now-closed gap
/// (agent-hosted JWKS URLs fetched at runtime), a port shaped this way cannot regress into that
/// SSRF/availability surface no matter what a future adapter does with it.
///
/// Scoped to the issuer's own signing keys, used when validating access tokens at a resource
/// server (<see cref="AccessTokenValidator"/>). The issuer's JWKS has no per-key validity window
/// -- a resolved key is simply the issuer's current signing key -- which is precisely why this
/// port carries no instant. That is also why it is <em>not</em> the port
/// <see cref="ClientAssertionValidator"/> uses to resolve an asserting agent's own registered
/// keys: those keys are exactly about overlapping validity windows (R4.17) evaluated at
/// <c>server_ts</c> (errata A12/R6.31), a question this shape cannot express and should not be
/// asked to. See <see cref="IAgentKeyResolver"/> for that half.
/// </summary>
public interface IJwsKeyResolver
{
    Task<Result<PublicKeyMaterial>> ResolveAsync(string kid, CancellationToken cancellationToken = default);
}
