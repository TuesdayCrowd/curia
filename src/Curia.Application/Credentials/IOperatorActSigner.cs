using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// What the operator's act key offers the use cases that publish it and sign with it (errata G70, R6.66): its <c>kid</c>, its public JWK so a use case can publish it to the log, and an ES256
/// signature over a JWS signing input. The members are exactly <see cref="ILifecycleStatementSigner"/>'s, and so is the key's algorithm; what differs is the key's class and what it signs. A declaration
/// key signs <c>curia.key-compromise/2</c> statements under header <c>typ</c> <c>curia-key-compromise+jws</c>; an operator key signs the statements of <c>curia.owner-attestation/1</c> and
/// <c>curia.moderation/1</c> under header <c>typ</c> <c>curia-operator-act+jws</c>, and never a head, a declaration or a lifecycle statement. The key itself lives in <c>Curia.Operator</c>, which
/// references this assembly and not the reverse (CS-7).
/// </summary>
public interface IOperatorActSigner
{
    /// <summary>The act's header <c>kid</c>: the RFC 7638 thumbprint of the public key.</summary>
    string Kid { get; }

    /// <summary>The public JWK the log's <c>log.operator-key</c> entry for this key carries.</summary>
    JsonValue.Object PublicJwk();

    /// <summary>ES256 (RFC 7518 section 3.4, fixed-width R||S) over <paramref name="signingInput"/>.</summary>
    Result<byte[]> SignStatement(ReadOnlySpan<byte> signingInput);
}
