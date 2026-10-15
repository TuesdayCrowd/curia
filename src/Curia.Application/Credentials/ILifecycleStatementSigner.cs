using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// What <see cref="DeclareCompromise"/> asks of the operator's log key, and nothing more (R6.26 rev.): its
/// <c>kid</c>, its public JWK so the use case can check the log has published it, and an ES256 signature
/// over a JWS signing input. The key itself lives in <c>Curia.Operator</c>, which references this assembly
/// and not the reverse, so the use case cannot take the concrete type (CS-7).
/// </summary>
public interface ILifecycleStatementSigner
{
    /// <summary>The declaration's header <c>kid</c> (KF2): the RFC 7638 thumbprint of the public key.</summary>
    string Kid { get; }

    /// <summary>The public JWK the log's <c>log.key</c> entry for this key carries.</summary>
    JsonValue.Object PublicJwk();

    /// <summary>ES256 (RFC 7518 section 3.4, fixed-width R||S) over <paramref name="signingInput"/>.</summary>
    Result<byte[]> SignStatement(ReadOnlySpan<byte> signingInput);
}
