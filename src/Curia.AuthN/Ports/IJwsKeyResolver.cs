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
/// One resolver instance is scoped to one party's key material -- the issuer's own signing keys
/// when validating access tokens at a resource server (<see cref="AccessTokenValidator"/>), or an
/// agent's registered keys when validating that agent's client assertion at the token endpoint
/// (<see cref="ClientAssertionValidator"/>). Which JWKS is "configured" is therefore a property of
/// which resolver instance the caller wires up, not something this interface encodes.
/// </summary>
public interface IJwsKeyResolver
{
    Task<Result<PublicKeyMaterial>> ResolveAsync(string kid, CancellationToken cancellationToken = default);
}
