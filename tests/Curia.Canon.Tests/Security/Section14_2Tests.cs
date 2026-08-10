using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Xunit;

namespace Curia.Canon.Tests.Security;

/// <summary>
/// §14.2 (R14.3) security test suite, one test per bullet in scope for Increment 1. Test
/// names match the spec bullet text so a reviewer can check coverage by reading test
/// names against the whitepaper directly, rather than tracing through assertions.
///
/// Most §14.2 bullets govern a token layer, PDP, event store, or content-screening
/// pipeline that does not exist yet -- this increment is Canon only: ADMIT, RFC 8785
/// canonicalization, and detached JWS with an algorithm allow-list. The bullets below are
/// exactly the ones Canon's own surface area can exercise. See the task report for the
/// full bullet-by-bullet accounting of what is covered here, covered elsewhere in this
/// suite (P1-P5 in <see cref="Curia.Canon.Tests.Properties.CanonProperties"/>), and
/// deferred to a later increment with the reason why.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names (and this type's own name) are the exact §14.2 bullet text, snake-cased " +
        "so a reviewer can check spec coverage by reading names alone -- the underscores are the point, " +
        "not a naming lapse (mirrors CanonProperties' P1-P5 precedent).")]
public sealed class Section14_2Tests
{
    private static string Reject(string wireJson) =>
        EnvelopeParser.Parse(Encoding.UTF8.GetBytes(wireJson), AdmitLimits.Default).Match(_ => "accepted", e => e.Type);

    private static JsonValue Parse(string json) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));

    /// <summary>
    /// Canonicalizes bare JSON content as if it were an envelope's content: always through
    /// <see cref="CanonicalJson.CanonicalizeEnvelope"/> (the R6.9 NFC profile), because that
    /// is the function signing and verification actually use, never the bare RFC 8785
    /// <see cref="CanonicalJson.Canonicalize"/> a caller might reach for by accident.
    /// </summary>
    private static byte[] CanonicalizeAsEnvelopeContent(string json) =>
        CanonicalJson.CanonicalizeEnvelope(new EnvelopeDocument((JsonValue.Object)Parse(json)))
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));

    [Fact]
    public void Post_with_a_mutated_field_fails_verification()
    {
        // §14.2: "Envelope mutated after signing (each field, systematically) →
        // rejected." Exhaustive coverage (every field, many mutations) is
        // CanonProperties.P2_AnySingleFieldMutationBreaksVerification, which signs and
        // verifies for real; this is the named regression case the spec bullet asks for,
        // pinning the precondition P2's property depends on: a mutated field must change
        // the canonical bytes, or no downstream signature check could ever catch it.
        var original = CanonicalizeAsEnvelopeContent("""{"body":"original"}""");
        var tampered = CanonicalizeAsEnvelopeContent("""{"body":"tampered"}""");
        Assert.False(original.AsSpan().SequenceEqual(tampered));
    }

    [Fact]
    public void Equivalent_serializations_canonicalize_identically()
    {
        // §14.2: "Envelope with equivalent-but-different JSON serialization → accepted
        // (this is the false-negative test that catches over-strict raw-byte
        // verification)." Different member order and whitespace must canonicalize to the
        // same bytes, or a verifier comparing raw wire bytes instead of re-canonicalizing
        // would reject a legitimately re-serialized (but semantically identical) envelope.
        var compact = CanonicalizeAsEnvelopeContent("""{"b":1,"a":2}""");
        var spaced = CanonicalizeAsEnvelopeContent("""{ "a" : 2 , "b" : 1 }""");
        Assert.Equal(compact, spaced);
    }

    [Fact]
    public void Oversize_payload_is_rejected_before_canonicalization() =>
        // §14.2: "Oversize or deeply nested envelope → rejected before parsing" (R6.15).
        Assert.Equal("curia/admit/size-exceeded",
            EnvelopeParser.Parse(new byte[AdmitLimits.Default.MaxBytes + 1], AdmitLimits.Default)
                .Match(_ => "accepted", e => e.Type));

    [Fact]
    public void Excessively_nested_payload_is_rejected() =>
        // Same bullet, the nesting half. 40 levels comfortably exceeds the 32-level cap
        // (R15.1); JsonReaderTests pins the exact boundary (32 accepted, 33 rejected).
        Assert.Equal("curia/admit/depth-exceeded",
            Reject(string.Concat(Enumerable.Repeat("""{"a":""", 40)) + "1" + new string('}', 40)));

    [Fact]
    public void Invalid_utf8_is_rejected_never_repaired() =>
        // §14.2: "Invalid UTF-8 or an unpaired surrogate in a string field → rejected
        // before canonicalization, never repaired" (R6.15) -- the "never repaired" half:
        // ADMIT must fail closed, not substitute U+FFFD and continue.
        Assert.Equal("curia/admit/invalid-utf8",
            EnvelopeParser.Parse([.. "{\"envelope\":{\"a\":\""u8, 0xFF, .. "\"},\"signature\":\"a..b\"}"u8], AdmitLimits.Default)
                .Match(_ => "accepted", e => e.Type));

    [Fact]
    public void Unpaired_surrogate_is_rejected() =>
        // Same bullet, the surrogate half.
        Assert.Equal("curia/admit/unpaired-surrogate", Reject("""{"envelope":{"a":"\uD800"},"signature":"a..b"}"""));

    [Fact]
    public void Embedded_nul_byte_is_rejected() =>
        // Not a literal §14.2 bullet by name, but the same R6.15 admit-reject family the
        // design doc's test-architecture table calls out by name alongside invalid UTF-8
        // and unpaired surrogates (conformance/admit-reject/raw-nul-byte); included here
        // because a raw NUL in signed content is exactly the kind of ADMIT-time hazard
        // this suite exists to name and regression-test.
        Assert.Equal("curia/admit/nul-byte",
            EnvelopeParser.Parse([.. "{\"envelope\":{\"a\":\""u8, (byte)0, .. "\"},\"signature\":\"a..b\"}"u8], AdmitLimits.Default)
                .Match(_ => "accepted", e => e.Type));

    [Fact]
    public void Algorithm_confusion_is_rejected_alg_none_and_hmac()
    {
        // §14.2: "alg: none token → rejected" and "Algorithm confusion: RS256 token
        // verified with the public key as an HMAC secret → rejected." The verifier's
        // allow-list is keyed by alg; "EdDSA" is the only entry, so none of these four
        // can resolve to a verifier regardless of what the forged header claims.
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(),
            new Dictionary<string, IContentVerifier> { ["EdDSA"] = new StubVerifier() });
        var canonical = CanonicalJson.CanonicalizeEnvelope(new EnvelopeDocument((JsonValue.Object)Parse("""{"a":1}""")))
            .Match(b => b, e => throw new Xunit.Sdk.XunitException(e.Type));

        foreach (var alg in new[] { "none", "HS256", "HS512", "RS256" })
        {
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $$"""{"alg":"{{alg}}","kid":"k","typ":"curia-post+jws","b64":false,"crit":["b64"]}"""))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var sig = new JwsSignature($"{header}..AAAA");
            Assert.Equal("curia/jws/alg-not-allowed",
                jws.Verify(canonical, sig, new PublicKeyMaterial("EdDSA", "k", new byte[32]))
                   .Match(_ => "accepted", e => e.Type));
        }
    }

    /// <summary>Always returns true: proves rejection happens at the allow-list, never by asking the adapter.</summary>
    private sealed class StubVerifier : IContentVerifier
    {
        public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key) => true;
    }
}
