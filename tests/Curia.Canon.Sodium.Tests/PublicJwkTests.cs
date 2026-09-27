using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Jws;
using Curia.Tests.Shared;
using NSec.Cryptography;
using Xunit;

namespace Curia.Canon.Sodium.Tests;

/// <summary>
/// R4.28 and R4.34 (errata G16): the public JWK a key is published as, and bound in the log as, is
/// RFC 8037's octet key pair for Ed25519 and RFC 7518's <c>EC</c> form for P-256, derived from the
/// RFCs' own example keys rather than from the renderer; and the renderer refuses exactly the
/// material the verifying adapter refuses, so a key the key set would omit is never bound either.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PublicJwkTests
{
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("public jwk");

    private static string Canonical(PublicKeyMaterial key) =>
        PublicJwk.Of(key).Match(
            jwk => Encoding.UTF8.GetString(CanonicalJson.Canonicalize(jwk).Match(b => b.ToArray(), e => throw new InvalidOperationException(e.Type))),
            e => "refused " + e.Type);

    /// <summary>
    /// RFC 8037 Appendix A.1's private key, whose public half A.2 prints as
    /// <c>x = 11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo</c>. The public key is derived here from the
    /// private scalar by NSec, so the expectation is the RFC's and not a round trip through the
    /// renderer.
    /// </summary>
    [Fact]
    public void R4_28_AnEd25519KeyIsRenderedAsRfc8037sOctetKeyPair()
    {
        var d = Base64Url.DecodeFromChars("nWGxne_9WmC6hEr0kuwsxERJxWl7MmkZcDusAxyuf2A");
        using var key = Key.Import(SignatureAlgorithm.Ed25519, d, KeyBlobFormat.RawPrivateKey);
        var raw = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        Assert.Equal(
            """{"alg":"EdDSA","crv":"Ed25519","kid":"rfc8037-a","kty":"OKP","x":"11qYAYKxCrfVS_7TyWQHOg7hcvPapiMlrwIaaPcHURo"}""",
            Canonical(new PublicKeyMaterial("EdDSA", "rfc8037-a", raw)));
    }

    /// <summary>
    /// RFC 7515 Appendix A.3.1's P-256 key, stored as R4.28 stores an <c>ES256</c> key: its DER
    /// SubjectPublicKeyInfo. The coordinates come back out of the DER exactly as the RFC prints them.
    /// </summary>
    [Fact]
    public void R4_28_AP256KeyIsRenderedAsRfc7518sEcForm()
    {
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Base64Url.DecodeFromChars("f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU"),
                Y = Base64Url.DecodeFromChars("x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"),
            },
        });

        Assert.Equal(
            """{"alg":"ES256","crv":"P-256","kid":"rfc7515-a3","kty":"EC","x":"f83OJ3D2xF1Bg8vub9tLe1gHMzV76e8Tus9uPHvRVEU","y":"x_FEzRu9m36HLN_tue659LNpXW6pCyStikYjKIWI5a0"}""",
            Canonical(new PublicKeyMaterial("ES256", "rfc7515-a3", ecdsa.ExportSubjectPublicKeyInfo())));
    }

    /// <summary>
    /// The renderer and the rule the verifying adapter owns give one answer on every material
    /// <c>KeyMaterials</c> names, under each algorithm it is asked about. The rows include both
    /// answers for both algorithms, so an agreement that held only because both sides refused
    /// everything would show as a row reading <c>False/False</c> where the positive rows read
    /// <c>True/True</c>.
    /// </summary>
    [Theory]
    [InlineData("ES256", "p256-spki", true)]
    [InlineData("ES256", "empty", false)]
    [InlineData("ES256", "three-zero-bytes", false)]
    [InlineData("ES256", "32-raw-bytes", false)]
    [InlineData("ES256", "rsa-2048-spki", false)]
    [InlineData("ES256", "p384-spki", false)]
    [InlineData("ES256", "p256-spki-and-a-trailing-byte", false)]
    [InlineData("ES256", "brainpoolP256r1-spki", false)]
    [InlineData("EdDSA", "32-raw-bytes", true)]
    [InlineData("EdDSA", "empty", false)]
    [InlineData("EdDSA", "31-bytes", false)]
    [InlineData("EdDSA", "33-bytes", false)]
    [InlineData("EdDSA", "p256-spki", false)]
    public void R4_34_TheRendererRefusesExactlyWhatTheVerifierRefuses(string alg, string material, bool isKey)
    {
        var bytes = KeyMaterials.Build(material, Message).Material;
        var adapter = alg == "ES256" ? Es256Adapter.IsPublicKey(bytes) : Ed25519Adapter.IsPublicKey(bytes);
        var rendered = PublicJwk.Of(new PublicKeyMaterial(alg, "k", bytes)).IsOk;

        Assert.Equal($"adapter={isKey} rendered={isKey}", $"adapter={adapter} rendered={rendered}");
    }

    /// <summary>Two renderings of one key are the same key, and a different key under the same name is not.</summary>
    [Fact]
    public void R4_34_SameKeyComparesTheKeyNotTheReference()
    {
        var (p256, _) = KeyMaterials.Build("p256-spki", Message);
        var (other, _) = KeyMaterials.Build("p256-spki", Message);

        var first = PublicJwk.Of(new PublicKeyMaterial("ES256", "k", p256)).Match(j => j, e => throw new InvalidOperationException(e.Type));
        var again = PublicJwk.Of(new PublicKeyMaterial("ES256", "k", p256.ToArray())).Match(j => j, e => throw new InvalidOperationException(e.Type));
        var stranger = PublicJwk.Of(new PublicKeyMaterial("ES256", "k", other)).Match(j => j, e => throw new InvalidOperationException(e.Type));

        Assert.Equal("same=True different=False", $"same={PublicJwk.SameKey(first, again)} different={PublicJwk.SameKey(first, stranger)}");
    }
}
