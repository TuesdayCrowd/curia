using System.Security.Cryptography;
using Curia.Application.Credentials;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.OperatorTool;

/// <summary>
/// The operator-acts key (errata G70, R4.43, R6.66): the P-256 key that signs <c>curia.owner-attestation/1</c> and <c>curia.moderation/1</c> statements and never a head, a declaration or a lifecycle
/// statement. A person holds it in the environment the operator's acts are made from (<c>CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM</c>); no Forum process holds it, and R12.18 refuses an environment that
/// holds it with the log key or the declaration key. It is a type of its own that implements <see cref="IOperatorActSigner"/> and nothing else, so that neither of the others can be passed where
/// an act is signed, and this one cannot be passed where a head or a declaration is.
/// </summary>
public sealed class OperatorActsSigningKey : IDisposable, IOperatorActSigner
{
    private readonly ECDsa _key;

    private OperatorActsSigningKey(ECDsa key, string kid)
    {
        _key = key;
        Kid = kid;
    }

    /// <summary>RFC 7638 thumbprint of the public key: derived, never configured.</summary>
    public string Kid { get; }

    public static OperatorActsSigningKey FromPem(string pem)
    {
        var (key, kid) = EcKeyMaterial.Import(pem, "The operator-acts signing key");
        return new OperatorActsSigningKey(key, kid);
    }

    /// <summary>A fresh P-256 key as PEM: the persistable form, never a live key.</summary>
    public static string GeneratePem() => EcKeyMaterial.GeneratePem();

    /// <summary>The public JWK a <c>log.operator-key</c> entry publishes, as a canonicalizable value.</summary>
    public JsonValue.Object PublicJwk() => EcKeyMaterial.PublicJwk(_key, Kid);

    /// <summary>ES256 in RFC 7518 section 3.4's fixed-width R||S form over the JWS signing input.</summary>
    public Result<byte[]> SignStatement(ReadOnlySpan<byte> signingInput) =>
        Result<byte[]>.Ok(_key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose() => _key.Dispose();
}
