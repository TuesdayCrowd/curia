using System.Security.Cryptography;
using Curia.Application.Credentials;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.OperatorTool;

/// <summary>
/// The declaration key (errata G67, R6.62): the P-256 key that signs <c>curia.key-compromise/2</c> statements and never a head. A person holds it; no
/// unattended process does (R12.18). It is a different type from <see cref="LogSigningKey"/> so that no type holds both and so that the compiler, and HSU1,
/// can say which key signs which act: this is the only <c>ILifecycleStatementSigner</c> the tool has, so a log key cannot be passed to <see cref="DeclareCompromise"/>.
/// </summary>
public sealed class DeclarationSigningKey : IDisposable, ILifecycleStatementSigner
{
    private readonly ECDsa _key;

    private DeclarationSigningKey(ECDsa key, string kid)
    {
        _key = key;
        Kid = kid;
    }

    /// <summary>RFC 7638 thumbprint of the public key, as for the issuer key: derived, never configured.</summary>
    public string Kid { get; }

    public static DeclarationSigningKey FromPem(string pem)
    {
        var (key, kid) = EcKeyMaterial.Import(pem, "The declaration signing key");
        return new DeclarationSigningKey(key, kid);
    }

    /// <summary>A fresh P-256 key as PEM -- the persistable form, never a live key; see <c>LogSigningKey.GeneratePem</c>.</summary>
    public static string GeneratePem() => EcKeyMaterial.GeneratePem();

    /// <summary>The public JWK a <c>log.declaration-key</c> entry publishes (RFC 7518 §6.2.1's EC form), as a canonicalizable value.</summary>
    public JsonValue.Object PublicJwk() => EcKeyMaterial.PublicJwk(_key, Kid);

    /// <summary>
    /// The port <see cref="DeclareCompromise"/> signs through (R6.26 rev., R6.62): ES256 in RFC 7518 section 3.4's
    /// fixed-width R||S form over the JWS signing input, as <see cref="Es256Adapter"/> signs a head.
    /// </summary>
    public Result<byte[]> SignStatement(ReadOnlySpan<byte> signingInput) =>
        Result<byte[]>.Ok(_key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose() => _key.Dispose();
}
