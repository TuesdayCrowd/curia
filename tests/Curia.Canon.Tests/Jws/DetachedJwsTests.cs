using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Xunit;

namespace Curia.Canon.Tests.Jws;

/// <summary>A deterministic stand-in so JWS structure is tested without real crypto.</summary>
internal sealed class StubCrypto : IContentSigner, IContentVerifier
{
    public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key) =>
        System.Security.Cryptography.SHA256.HashData(input);

    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key) =>
        System.Security.Cryptography.SHA256.HashData(input).AsSpan().SequenceEqual(sig);
}

/// <summary>Counts invocations so a rejected algorithm can be proven to never reach an adapter.</summary>
internal sealed class CountingCrypto : IContentVerifier
{
    public int VerifyCalls { get; private set; }

    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
    {
        VerifyCalls++;
        return true;
    }
}

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DetachedJwsTests
{
    private static readonly StubCrypto Stub = new();

    private static DetachedJws Jws() => new(
        new Dictionary<string, IContentSigner> { ["EdDSA"] = Stub },
        new Dictionary<string, IContentVerifier> { ["EdDSA"] = Stub });

    // R6.41: this helper's whole job is turning bytes into canonical bytes, not testing
    // ADMIT -- it parses via ParseUnrestricted, not the ADMIT-gated Parse.
    private static CanonicalBytes Canonical(string json) =>
        CanonicalJson.Canonicalize(JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(json))
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type)))
            .Match(b => b, e => throw new Xunit.Sdk.XunitException(e.Type));

    private static readonly SigningKey Key = new("EdDSA", "agent-key-2026-08", new byte[32]);
    private static readonly PublicKeyMaterial Pub = new("EdDSA", "agent-key-2026-08", new byte[32]);

    [Fact]
    public void SignThenVerifyRoundTrips()
    {
        var canonical = Canonical("""{"a":1}""");
        var sig = Jws().Sign(canonical, Key).Match(s => s, e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.True(Jws().Verify(canonical, sig, Pub).IsOk);
    }

    [Fact]
    public void ProtectedHeaderCarriesTheRfc7797Profile()
    {
        var sig = Jws().Sign(Canonical("""{"a":1}"""), Key).Match(s => s, e => throw new Xunit.Sdk.XunitException(e.Type));
        var header = DetachedJws.ReadProtectedHeader(sig).Match(h => h, e => throw new Xunit.Sdk.XunitException(e.Type));

        Assert.Equal("EdDSA", header.Alg);
        Assert.Equal("curia-post+jws", header.Typ);
        Assert.False(header.B64);
        Assert.Equal(["b64"], header.Crit);
    }

    [Fact]
    public void SerializationHasAnEmptyPayloadSegment()
    {
        var sig = Jws().Sign(Canonical("""{"a":1}"""), Key).Match(s => s, e => throw new Xunit.Sdk.XunitException(e.Type));
        var parts = sig.Compact.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("", parts[1]);        // detached: the payload is not carried
    }

    [Fact]
    public void VerificationFailsWhenAnyByteOfTheContentChanges()
    {
        var sig = Jws().Sign(Canonical("""{"a":1}"""), Key).Match(s => s, e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal("curia/jws/signature-invalid",
            Jws().Verify(Canonical("""{"a":2}"""), sig, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsAlgNone()
    {
        var forged = Forge("""{"alg":"none","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64"]}""");
        Assert.Equal("curia/jws/alg-not-allowed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    [SuppressMessage(
        "Naming",
        "CA1707:Identifiers should not contain underscores",
        Justification = "Test name cites the exact requirement (R4.15) the rejection enforces; the " +
            "underscore is load-bearing documentation, not a naming lapse (mirrors CanonicalJsonTests' " +
            "NonBmpKeySortsBeforeU_FFFD_BecauseSurrogatesAreLowInUtf16 precedent).")]
    public void RejectsHmacAlgorithmsBecauseR4_15ForbidsThem()
    {
        var forged = Forge("""{"alg":"HS256","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64"]}""");
        Assert.Equal("curia/jws/alg-not-allowed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsUnknownCritEntries()
    {
        var forged = Forge("""{"alg":"EdDSA","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64","zip"]}""");
        Assert.Equal("curia/jws/crit-unsupported",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsB64True()
    {
        var forged = Forge("""{"alg":"EdDSA","kid":"k","typ":"curia-post+jws","b64":true,"crit":["b64"]}""");
        Assert.Equal("curia/jws/b64-must-be-false",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsWrongTyp()
    {
        var forged = Forge("""{"alg":"EdDSA","kid":"k","typ":"JWT","b64":false,"crit":["b64"]}""");
        Assert.Equal("curia/jws/typ-mismatch",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    private static JwsSignature Forge(string headerJson)
    {
        var h = Base64Url(Encoding.UTF8.GetBytes(headerJson));
        return new JwsSignature($"{h}..{Base64Url(new byte[32])}");
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // The three tests below pin a property the given fixtures above don't reach: a
    // structurally hostile protected header (bad base64url, or base64url JSON that
    // decodes to something other than an object) must come back as a Result.Fail, not
    // an unhandled exception. JsonElement.TryGetProperty/.GetString()/.GetBoolean()
    // throw on the wrong JsonValueKind, and this header is attacker-controlled input by
    // definition -- a crash here would be a denial-of-service on a malformed forgery,
    // which is its own way of failing "reject before verifying".

    [Fact]
    public void RejectsAHeaderSegmentThatIsNotValidBase64Url()
    {
        var forged = new JwsSignature($"not-valid-base64url!!..{Base64Url(new byte[32])}");
        Assert.Equal("curia/jws/malformed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsAHeaderThatDecodesToNonObjectJson()
    {
        var h = Base64Url(Encoding.UTF8.GetBytes("[1,2,3]"));
        var forged = new JwsSignature($"{h}..{Base64Url(new byte[32])}");
        Assert.Equal("curia/jws/malformed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsASignatureSegmentThatIsNotValidBase64Url()
    {
        var h = Base64Url(Encoding.UTF8.GetBytes(
            """{"alg":"EdDSA","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64"]}"""));
        var forged = new JwsSignature($"{h}..not valid base64url!!!");
        Assert.Equal("curia/jws/malformed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// The architectural property the whole task exists for: alg:none and HS256 are
    /// rejected because they are absent from the allow-list, not because the code
    /// special-cases them -- and the rejection happens before <see cref="IContentVerifier.Verify"/>
    /// is ever called. A verifier that consulted the adapter first and rejected only on a
    /// false result would still be exploitable (timing, side channels, adapters that throw
    /// instead of returning false on garbage input); this proves the adapter is never reached.
    /// </summary>
    [Fact]
    public void RejectedAlgorithmsNeverReachTheAdapter()
    {
        var counting = new CountingCrypto();
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(),
            new Dictionary<string, IContentVerifier> { ["EdDSA"] = counting });
        var canonical = Canonical("""{"a":1}""");

        var none = Forge("""{"alg":"none","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64"]}""");
        var hs256 = Forge("""{"alg":"HS256","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64"]}""");

        Assert.Equal("curia/jws/alg-not-allowed", jws.Verify(canonical, none, Pub).Match(_ => "ok", e => e.Type));
        Assert.Equal("curia/jws/alg-not-allowed", jws.Verify(canonical, hs256, Pub).Match(_ => "ok", e => e.Type));
        Assert.Equal(0, counting.VerifyCalls);
    }

    // A null Compact is reachable input: JwsSignature is built from attacker-supplied wire
    // content (EnvelopeParser hands a raw string straight into the record). Both entry
    // points must return Result.Fail rather than let ArgumentNullException.ThrowIfNull(sig)
    // pass a null sig.Compact through to .Split('.').

    [Fact]
    public void VerifyRejectsANullCompactSerializationWithoutThrowing()
    {
        var forged = new JwsSignature(null!);
        Assert.Equal("curia/jws/malformed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void ReadProtectedHeaderRejectsANullCompactSerializationWithoutThrowing()
    {
        var forged = new JwsSignature(null!);
        Assert.Equal("curia/jws/malformed",
            DetachedJws.ReadProtectedHeader(forged).Match(_ => "ok", e => e.Type));
    }

    // The following pin behavior that is already correct today, so a future refactor of
    // ReadB64/ReadCrit/the segment-count check has a test to break.

    [Fact]
    public void RejectsWhenCritIsAbsentEntirely()
    {
        var forged = Forge("""{"alg":"EdDSA","kid":"k","typ":"curia-post+jws","b64":false}""");
        Assert.Equal("curia/jws/crit-unsupported",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsWhenCritIsAnEmptyArray()
    {
        var forged = Forge("""{"alg":"EdDSA","kid":"k","typ":"curia-post+jws","b64":false,"crit":[]}""");
        Assert.Equal("curia/jws/crit-unsupported",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsWhenB64IsAbsentEntirely()
    {
        // RFC 7797: b64 defaults to true when absent, and true is exactly what's rejected here.
        var forged = Forge("""{"alg":"EdDSA","kid":"k","typ":"curia-post+jws","crit":["b64"]}""");
        Assert.Equal("curia/jws/b64-must-be-false",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsTypThatDiffersOnlyByCase()
    {
        var forged = Forge("""{"alg":"EdDSA","kid":"k","typ":"CURIA-POST+JWS","b64":false,"crit":["b64"]}""");
        Assert.Equal("curia/jws/typ-mismatch",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsATwoSegmentToken()
    {
        var forged = new JwsSignature("headerpart.sigpart");
        Assert.Equal("curia/jws/malformed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsAFourSegmentToken()
    {
        var forged = new JwsSignature("a.b.c.d");
        Assert.Equal("curia/jws/malformed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsANonEmptyPayloadSegmentReachingVerify()
    {
        var h = Base64Url(Encoding.UTF8.GetBytes(
            """{"alg":"EdDSA","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64"]}"""));
        var forged = new JwsSignature($"{h}.not-empty.{Base64Url(new byte[32])}");
        Assert.Equal("curia/jws/malformed",
            Jws().Verify(Canonical("""{"a":1}"""), forged, Pub).Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// The JSON escape of a lone high surrogate, built rather than written, so that no tool or editor
    /// between this file and the compiler can decode it.
    /// </summary>
    private const string LoneSurrogate = "\\" + "ud800";

    /// <summary>
    /// The finding's four protected headers (the strangers stage's final gate, third round): a lone
    /// surrogate escape in <c>kid</c>, in <c>typ</c> and as a <c>crit</c> element, and a <c>kid</c> whose
    /// raw bytes are not UTF-8. <see cref="System.Text.Json.JsonDocument"/> parses each, and
    /// <c>GetString</c> throws on the string.
    /// </summary>
    internal static byte[] UndecodableHeader(string row) => row switch
    {
        "kid" => Encoding.UTF8.GetBytes(
            "{\"alg\":\"EdDSA\",\"kid\":\"" + LoneSurrogate + "\",\"typ\":\"curia-post+jws\",\"b64\":false,\"crit\":[\"b64\"]}"),
        "typ" => Encoding.UTF8.GetBytes(
            "{\"alg\":\"EdDSA\",\"kid\":\"agent-key-2026-08\",\"typ\":\"" + LoneSurrogate + "\",\"b64\":false,\"crit\":[\"b64\"]}"),
        "crit" => Encoding.UTF8.GetBytes(
            "{\"alg\":\"EdDSA\",\"kid\":\"agent-key-2026-08\",\"typ\":\"curia-post+jws\",\"b64\":false,\"crit\":[\"" + LoneSurrogate + "\"]}"),
        "raw-ff" => [.. Encoding.ASCII.GetBytes("{\"kid\":\""), 0xFF, .. Encoding.ASCII.GetBytes("\"}")],
        _ => throw new ArgumentOutOfRangeException(nameof(row), row, "no such row"),
    };

    /// <summary>
    /// R11.33 (the strangers stage's final gate, third round): a protected header holding a string that
    /// does not decode -- an escaped unpaired surrogate, or bytes that are not UTF-8 -- is malformed, as
    /// <c>CompactJws</c> has refused such a segment since Task 11's fix review. <c>DetachedJws</c> read
    /// <c>alg</c>, <c>kid</c>, <c>typ</c> and <c>crit</c> through <c>GetString</c>, which threw, so any
    /// enrolled agent's post carrying one answered 500, and a Forum serving one would have thrown in the
    /// reference readers' <c>SignatureCheck</c> and <c>ActaCheck</c>.
    /// </summary>
    [Theory]
    [InlineData("kid")]
    [InlineData("typ")]
    [InlineData("crit")]
    [InlineData("raw-ff")]
    public void R11_33_AProtectedHeaderHoldingAStringThatDoesNotDecodeIsMalformedNotThrown(string row)
    {
        var forged = new JwsSignature($"{Base64Url(UndecodableHeader(row))}..{Base64Url(new byte[64])}");

        var read = DetachedJws.ReadProtectedHeader(forged);
        Assert.Equal("curia/jws/malformed", read.Match(_ => "read", e => e.Type));

        var verified = Jws().Verify(Canonical("""{"a":1}"""), forged, Pub);
        Assert.Equal("curia/jws/malformed", verified.Match(_ => "verified", e => e.Type));
    }
}
