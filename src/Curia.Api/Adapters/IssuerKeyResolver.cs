using Curia.Application.Ports;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Api.Adapters;

/// <summary>
/// The issuer's own signing key, as an <see cref="IJwsKeyResolver"/> for the resource server.
///
/// <para>Deliberately resolves exactly one <c>kid</c> and fails on every other. An issuer key
/// resolver that accepted any key it was shown would let anything that could sign a JWT mint
/// Forum credentials, which is the algorithm-confusion family of failure in a different costume.</para>
///
/// <para>The last in-memory adapter left in this host, and the only one that should be: it holds
/// no state that outlives a request, it forgets nothing across a restart, and every instance
/// behind a load balancer derives the identical answer from the identical configured key. The
/// replay cache, the DPoP nonce store and the Registrar's key store had none of those properties
/// in memory and are now Postgres-backed in <c>Curia.Infrastructure</c>; this one was never
/// a store at all.</para>
/// </summary>
public sealed class IssuerKeyResolver(PublicKeyMaterial key) : IJwsKeyResolver
{
    public Task<Result<PublicKeyMaterial>> ResolveAsync(
        string kid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(string.Equals(kid, key.Kid, StringComparison.Ordinal)
            ? Result<PublicKeyMaterial>.Ok(key)
            : Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NotRegisteredToAgent("issuer", kid)));
    }
}
