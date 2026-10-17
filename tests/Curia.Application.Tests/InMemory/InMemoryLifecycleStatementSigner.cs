using System.Buffers.Text;
using System.Security.Cryptography;
using Curia.Application.Credentials;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// R11.4: the in-memory adapter of <see cref="ILifecycleStatementSigner"/>. It holds a fixed P-256 key, a
/// test key only that guards nothing, so a use-case fact can sign a declaration without the operator tool.
/// </summary>
internal sealed class InMemoryLifecycleStatementSigner : ILifecycleStatementSigner, IDisposable
{
    // Generated for these tests; it signs nothing outside them.
    private const string TestKeyPem =
        """
        -----BEGIN PRIVATE KEY-----
        MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgt1hn2zFnSBNdhoCD
        mLQFasMiNZUS+DeOzkwO/917sgahRANCAAQ+E3p+v8e2Ek69cc7dmPYVVqpbzX/p
        P1I2hj+3WgN2dID5IJNCfg7hL017sAObind3pi5KNsnt0g8wkM/hptcp
        -----END PRIVATE KEY-----
        """;

    private readonly ECDsa _key = ECDsa.Create();

    internal InMemoryLifecycleStatementSigner(string kid = "test-log-key")
    {
        _key.ImportFromPem(TestKeyPem);
        Kid = kid;
    }

    public string Kid { get; }

    /// <summary>The public key as an SPKI, for a test that verifies the declaration.</summary>
    internal PublicKeyMaterial Material => new("ES256", Kid, _key.ExportSubjectPublicKeyInfo());

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
