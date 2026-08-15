using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Tests.InMemory;

/// <summary>
/// The R11.4 in-memory adapter for <see cref="IJwsKeyResolver"/>: a fixed <c>kid</c> to
/// <see cref="PublicKeyMaterial"/> map, standing in for either the issuer's own JWKS
/// (<see cref="AccessTokenValidator"/>) or one agent's Forum-served key set
/// (<see cref="ClientAssertionValidator"/>) depending on what the test wires up -- see
/// <see cref="AccessTokenValidationContext.IssuerKeyResolver"/> and
/// <see cref="ClientAssertionValidationContext.AgentKeyResolver"/>'s remarks for which is which.
///
/// R5.10's "never fetch a key from a URL found inside the token" is not merely honored by this
/// implementation; it could not be violated by any implementation of this interface, because the
/// interface accepts nothing a caller could construct a URL from -- see
/// <see cref="R510KidResolutionTests"/> for a test that exercises exactly that shape.
/// </summary>
internal sealed class InMemoryJwsKeyResolver : IJwsKeyResolver
{
    private readonly IReadOnlyDictionary<string, PublicKeyMaterial> _keysByKid;

    public InMemoryJwsKeyResolver(IReadOnlyDictionary<string, PublicKeyMaterial> keysByKid) =>
        _keysByKid = keysByKid;

    public InMemoryJwsKeyResolver(string kid, PublicKeyMaterial key)
        : this(new Dictionary<string, PublicKeyMaterial>(StringComparer.Ordinal) { [kid] = key })
    {
    }

    public Task<Result<PublicKeyMaterial>> ResolveAsync(string kid, CancellationToken cancellationToken = default) =>
        Task.FromResult(_keysByKid.TryGetValue(kid, out var key)
            ? Result<PublicKeyMaterial>.Ok(key)
            : Result<PublicKeyMaterial>.Fail(AuthNErrors.KidNotFound(kid)));
}
