using System.Buffers.Text;
using System.Security.Cryptography;
using Curia.Application.Credentials;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// R11.4: the in-memory adapter of <see cref="IOperatorActSigner"/>, the twin of <see cref="InMemoryLifecycleStatementSigner"/>. Unlike that twin, which holds one fixed key, each instance holds a fresh
/// P-256 key (a test key only, that guards nothing), so that an operator key and a declaration key are different keys unless a test says otherwise; <see cref="WithKid"/> builds a second signer over
/// the same key material under another <c>kid</c>, which is what "the same public key, another kid" needs (R6.66 compares by both).
/// </summary>
internal sealed class InMemoryOperatorActSigner : IOperatorActSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    internal InMemoryOperatorActSigner(string kid = "test-operator-key") => Kid = kid;

    public string Kid { get; }

    /// <summary>A signer over the same key material under <paramref name="kid"/>.</summary>
    internal InMemoryOperatorActSigner WithKid(string kid)
    {
        var other = new InMemoryOperatorActSigner(kid);
        other._key.ImportPkcs8PrivateKey(_key.ExportPkcs8PrivateKey(), out _);
        return other;
    }

    public JsonValue.Object PublicJwk()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);
        return new JsonValue.Object(
        [
            new("alg", new JsonValue.String("ES256")),
            new("crv", new JsonValue.String("P-256")),
            new("kid", new JsonValue.String(Kid)),
            new("kty", new JsonValue.String("EC")),
            new("use", new JsonValue.String("sig")),
            new("x", new JsonValue.String(Base64Url.EncodeToString(parameters.Q.X!))),
            new("y", new JsonValue.String(Base64Url.EncodeToString(parameters.Q.Y!))),
        ]);
    }

    public Result<byte[]> SignStatement(ReadOnlySpan<byte> signingInput) =>
        Result<byte[]>.Ok(_key.SignData(signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public void Dispose() => _key.Dispose();
}
