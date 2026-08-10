using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Tests.Vectors;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Canon.Tests.Envelope;

public sealed class EnvelopeParserTests
{
    private const string Wire = """
        {"envelope":{"v":1,"kind":"question","author":"agent://curia.example/tuesdaycrowd/scriptor",
        "board":"distributed-systems","parent":null,"prev":null,"title":"T","body":"B",
        "code_blocks":[],"refs":[],"tags":["x"],"content_type":"agent-authored/untrusted",
        "created_at":"2026-08-08T14:22:03Z","nonce":"b1b1e6f0a0c94e3a9a7d2f4c8e5a1b3d","model_hint":null},
        "signature":"eyJhbGciOiJFZERTQSJ9..c2ln"}
        """;

    private static Result<SubmissionDocument> Parse(string json) =>
        EnvelopeParser.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default);

    [Fact]
    public void ParsesTheWireFormatIntoEnvelopeAndSignature()
    {
        var doc = Parse(Wire).Match(d => d, e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal("eyJhbGciOiJFZERTQSJ9..c2ln", doc.Signature.Compact);
        Assert.Contains(doc.Envelope.Root.Members, m => m.Key == "kind");
    }

    [Fact]
    public void RejectsAWireObjectMissingTheSignature()
    {
        var slug = Parse("""{"envelope":{"v":1}}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/missing-signature", slug);
    }

    [Fact]
    public void RejectsAWireObjectMissingTheEnvelope()
    {
        var slug = Parse("""{"signature":"a..b"}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/missing-envelope", slug);
    }

    [Fact]
    public void RejectsATopLevelValueThatIsNotAnObject()
    {
        var slug = Parse("""[1,2,3]""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/malformed", slug);
    }

    [Fact]
    public void RejectsANonIntegerNumberAnywhereInTheEnvelope()
    {
        // R6.33: I-JSON-exact numerics. A float in a signed payload is where a
        // cross-language conformance break is born.
        var slug = Parse("""{"envelope":{"v":1,"x":1.5},"signature":"a..b"}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/non-integer-number", slug);
    }

    [Fact]
    public void RejectsAnIntegerOutsideTheSafeRange()
    {
        var slug = Parse("""{"envelope":{"v":1,"x":9007199254740993},"signature":"a..b"}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/unsafe-integer", slug);
    }

    [Theory]
    [MemberData(nameof(NumericRejectionVectors))]
    public void ConformanceNumericRejectionVectors(string name, byte[] input, string slug)
    {
        _ = name;
        var wrapped = Encoding.UTF8.GetBytes(
            $$"""{"envelope":{{Encoding.UTF8.GetString(input)}},"signature":"a..b"}""");
        Assert.Equal(slug, EnvelopeParser.Parse(wrapped, AdmitLimits.Default).Match(_ => "ok", e => e.Type));
    }

    public static TheoryData<string, byte[], string> NumericRejectionVectors()
    {
        var data = new TheoryData<string, byte[], string>();
        foreach (var v in VectorLoader.Load("admit-reject").Where(v => v.Requirement == "R6.33"))
            data.Add(v.Name, v.Input, v.ExpectRejectSlug!);
        return data;
    }

    [Fact]
    public void DigestOfTheCanonicalFormIsStableAndPrefixed()
    {
        var doc = Parse(Wire).Match(d => d, e => throw new Xunit.Sdk.XunitException(e.Type));
        var canonical = CanonicalJson.CanonicalizeEnvelope(doc.Envelope).Match(b => b, e => throw new Xunit.Sdk.XunitException(e.Type));
        var digest = Digests.Sha256(canonical);
        Assert.Equal(32, digest.Sha256.Length);
        Assert.StartsWith("sha256:", digest.ToPrefixed(), StringComparison.Ordinal);
        Assert.Equal(digest.ToHex(), Digests.Sha256(canonical).ToHex());
    }

    /// <summary>
    /// Pins the amendment to the brief: CanonicalJson.CanonicalizeEnvelope must delegate
    /// to CanonicalizeWithNfc (R6.9), not the bare RFC 8785 Canonicalize -- an envelope is
    /// exactly the kind of signed content R6.9 governs. "A" + COMBINING RING ABOVE (NFD)
    /// must compose to "Å" (U+00C5, NFC) in the envelope digest's canonical bytes; the bare
    /// Canonicalize(JsonValue) would leave the NFD form untouched (see
    /// CanonicalJsonTests.PureAndNfcProfileDisagreeOnNonNfcInput for the same contract on
    /// the underlying JsonValue overload).
    /// </summary>
    [Fact]
    public void EnvelopeCanonicalizationAppliesNfcNormalization()
    {
        var wire = """{"envelope":{"title":"Å"},"signature":"a..b"}""";
        var doc = Parse(wire).Match(d => d, e => throw new Xunit.Sdk.XunitException(e.Type));

        var withNfc = CanonicalJson.CanonicalizeEnvelope(doc.Envelope)
            .Match(b => Encoding.UTF8.GetString(b.Span), e => throw new Xunit.Sdk.XunitException(e.Type));
        var bareRfc8785 = CanonicalJson.Canonicalize(doc.Envelope.Root)
            .Match(b => Encoding.UTF8.GetString(b.Span), e => throw new Xunit.Sdk.XunitException(e.Type));

        Assert.Equal("""{"title":"Å"}""", withNfc);
        Assert.NotEqual(withNfc, bareRfc8785);
    }
}
