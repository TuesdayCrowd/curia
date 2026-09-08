using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Xunit;

namespace Curia.Canon.Tests.Jws;

/// <summary>
/// R11.20's seam: <see cref="DetachedJws.Sign(CanonicalBytes, IAgentSigner)"/>.
///
/// <para>The property that matters is not that a handle can sign — it is that the handle path is
/// the <i>same</i> path, subject to the <i>same</i> refusals, as the key-bearing one. A second
/// signing path that composed its own protected header would agree with the first on every document
/// anybody tested and disagree on the one an attacker constructed.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AgentSignerTests
{
    private static readonly CanonicalBytes Canonical =
        CanonicalJson.Canonicalize(new JsonValue.Object([new("v", new JsonValue.Number(1))]))
            .Match(c => c, e => throw new InvalidOperationException(e.Type));

    private static DetachedJws Jws() => new(
        new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["ES256"] = new Es256Adapter() },
        new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = new Es256Adapter() });

    /// <summary>
    /// The two overloads produce byte-identical signatures over the same input with the same key.
    ///
    /// <para>ES256 is randomised, so the signatures themselves differ every time — what must be
    /// identical is the <b>protected header</b>, because that is what the signing input is built
    /// from and what a verifier re-derives. Comparing the header segment is the assertion; comparing
    /// the whole compact form would fail on nonce alone and prove nothing.</para>
    /// </summary>
    [Fact]
    public void R11_20_TheHandlePathComposesTheSameProtectedHeaderAsTheKeyPath()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bearing = new SigningKey("ES256", "alice-1", key.ExportECPrivateKey());
        var handle = new TestSigner(key, "alice-1");

        var viaKey = Jws().Sign(Canonical, bearing).Match(s => s.Compact, e => throw new InvalidOperationException(e.Type));
        var viaHandle = Jws().Sign(Canonical, handle).Match(s => s.Compact, e => throw new InvalidOperationException(e.Type));

        Assert.Equal(viaKey.Split('.')[0], viaHandle.Split('.')[0]);

        // Non-vacuity: the headers really are non-empty, so an equality between two empty strings
        // is not what passed.
        Assert.NotEmpty(viaKey.Split('.')[0]);
    }

    /// <summary>Both signatures verify under the same public key, so the seam changes nothing observable.</summary>
    [Fact]
    public void R11_20_ASignatureFromAHandleVerifiesLikeAnyOther()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var handle = new TestSigner(key, "alice-1");
        var material = new PublicKeyMaterial("ES256", "alice-1", key.ExportSubjectPublicKeyInfo());

        var signature = Jws().Sign(Canonical, handle).Match(s => s, e => throw new InvalidOperationException(e.Type));

        Assert.True(Jws().Verify(Canonical, signature, material).TryGetValue(out _, out var error), error?.Type);
    }

    /// <summary>
    /// The empty-signer guarantee survives the new overload.
    ///
    /// <para><b>This is the one that matters most.</b> Five call sites derive their meaning from
    /// passing an empty <see cref="IContentSigner"/> dictionary — above all <c>IngestPipeline</c>,
    /// which is why the Forum is <i>structurally</i> unable to sign a post envelope. A handle
    /// carries its own algorithm, so it would have been natural to accept it without consulting the
    /// allow-list; that would have handed the Forum a signing path it has never had, and nothing
    /// would have failed.</para>
    /// </summary>
    [Fact]
    public void R11_20_AnInstanceConfiguredToSignNothingRefusesAHandleToo()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var unableToSign = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal),
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["ES256"] = new Es256Adapter() });

        var result = unableToSign.Sign(Canonical, new TestSigner(key, "alice-1"));

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/jws/alg-not-allowed", error!.Type);

        // Non-vacuity: the same instance with the algorithm allowed does sign, so the refusal above
        // is about the allow-list rather than about the handle being unusable.
        Assert.True(Jws().Sign(Canonical, new TestSigner(key, "alice-1")).TryGetValue(out _, out _));
    }

    /// <summary>
    /// A signer that refuses is a value, not an exception. A delegated signer is another process or
    /// a keystore, and either can decline — CS-10 says a failed signature is a value, and the verify
    /// side already closed this crash class for algorithm confusion.
    /// </summary>
    [Fact]
    public void R11_20_ASignerThatRefusesIsReportedRatherThanThrown()
    {
        var result = Jws().Sign(Canonical, new RefusingSigner());

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/jws/signer-refused", error!.Type);
        Assert.Contains("keystore is locked", error.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The port carries no member through which private material can be obtained. Asserted over the
    /// type rather than over a call, because a test that watched one call and found no key in it
    /// would pass while copies persisted elsewhere — the plan asks for this "structurally, not by
    /// inspection", and this is what structural means.
    /// </summary>
    [Fact]
    public void R11_20_TheSignerPortExposesNoPrivateMaterial()
    {
        var members = typeof(IAgentSigner).GetMembers()
            .Select(m => m.Name)
            .Where(n => !n.StartsWith("get_", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Alg", "Kid", "PublicKey", "Sign"], members);

        // And nothing on it returns a private key by another name: the only non-scalar it yields is
        // the public half, and the only method takes bytes and returns a signature.
        Assert.Equal(typeof(byte[]), typeof(IAgentSigner).GetMethod(nameof(IAgentSigner.Sign))!.ReturnType);
    }

    private sealed class TestSigner(ECDsa key, string kid) : IAgentSigner
    {
        public string Alg => "ES256";

        public string Kid => kid;

        public ReadOnlyMemory<byte> PublicKey => key.ExportSubjectPublicKeyInfo();

        public byte[] Sign(ReadOnlySpan<byte> signingInput) => key.SignData(
            signingInput, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private sealed class RefusingSigner : IAgentSigner
    {
        public string Alg => "ES256";

        public string Kid => "locked-1";

        public ReadOnlyMemory<byte> PublicKey => ReadOnlyMemory<byte>.Empty;

        public byte[] Sign(ReadOnlySpan<byte> signingInput) =>
            throw new CryptographicException("the keystore is locked");
    }
}
