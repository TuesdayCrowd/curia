using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Canon.Tests.Jws;

/// <summary>
/// R6.49: a tree head is signed under <c>typ: curia-head+jws</c> and a post under
/// <c>curia-post+jws</c>. One key may sign both, so the header is what keeps a head from
/// passing as a post or a post as a head -- checked before any cryptography runs.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DetachedJwsTypTests
{
    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    [Fact]
    public void R6_49_AHeadSignatureIsNotAPostSignatureAndViceVersa()
    {
        using var key = NSec.Cryptography.Key.Create(
            NSec.Cryptography.SignatureAlgorithm.Ed25519,
            new NSec.Cryptography.KeyCreationParameters { ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport });
        var signing = new SigningKey("EdDSA", "log-k", key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey));
        var material = new PublicKeyMaterial("EdDSA", "log-k", key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey));

        var ed25519 = new Ed25519Adapter();
        var signers = new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["EdDSA"] = ed25519 };
        var verifiers = new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["EdDSA"] = ed25519 };
        var posts = new DetachedJws(signers, verifiers);
        var heads = new DetachedJws(signers, verifiers, DetachedJws.HeadTyp);

        var canonical = Require(CanonicalJson.Canonicalize(new JsonValue.Object([new("tree_size", new JsonValue.Number(8))])));
        var headSignature = Require(heads.Sign(canonical, signing));
        var postSignature = Require(posts.Sign(canonical, signing));

        Assert.Equal(DetachedJws.HeadTyp, Require(DetachedJws.ReadProtectedHeader(headSignature)).Typ);
        Assert.Equal(DetachedJws.ExpectedTyp, Require(DetachedJws.ReadProtectedHeader(postSignature)).Typ);

        Assert.True(heads.Verify(canonical, headSignature, material).TryGetValue(out _, out _));
        Assert.True(posts.Verify(canonical, postSignature, material).TryGetValue(out _, out _));

        Assert.False(posts.Verify(canonical, headSignature, material).TryGetValue(out _, out var headAsPost));
        Assert.Equal("curia/jws/typ-mismatch", headAsPost!.Type);
        Assert.False(heads.Verify(canonical, postSignature, material).TryGetValue(out _, out var postAsHead));
        Assert.Equal("curia/jws/typ-mismatch", postAsHead!.Type);
    }
}
