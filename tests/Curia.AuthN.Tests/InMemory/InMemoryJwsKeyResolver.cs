using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Tests.InMemory;

/// <summary>
/// The R11.4 in-memory adapter for <see cref="IJwsKeyResolver"/>: a fixed <c>kid</c> to
/// <see cref="PublicKeyMaterial"/> map standing in for the issuer's own JWKS
/// (<see cref="AccessTokenValidator"/>; see <see cref="AccessTokenValidationContext.IssuerKeyResolver"/>'s
/// remarks). Issuer-scoped only -- the agent-key half, which additionally carries a
/// <c>server_ts</c> validity window (errata A12/R6.31), has its own adapter,
/// <see cref="InMemoryAgentKeyResolver"/>, for <see cref="ClientAssertionValidationContext.AgentKeyResolver"/>.
///
/// R5.10's "never fetch a key from a URL found inside the token" is not merely honored by this
/// implementation; it could not be violated by any implementation of this interface, because the
/// interface accepts nothing a caller could construct a URL from -- see
/// <see cref="AccessTokenValidatorOrderingTests"/>'s R5.10 tests for the shape that exercises this.
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
