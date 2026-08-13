using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Tests.Vectors;
using Xunit;

namespace Curia.Canon.Tests.Canonical;

/// <summary>
/// Cūria's own canonicalization profile (RFC 8785 + R6.9 NFC). Vectors here exercise
/// <see cref="CanonicalJson.CanonicalizeWithNfc"/> — the function signing and
/// verification actually use. Pure RFC 8785 conformance against the vendored
/// RFC-author vectors is <see cref="Vectors.Rfc8785VectorTests"/>, which targets the
/// bare <see cref="CanonicalJson.Canonicalize"/> instead.
/// </summary>
public sealed class CanonicalJsonTests
{
    private static string CanonicalizeWithNfc(string json)
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException($"parse failed: {e.Type}"));
        var bytes = CanonicalJson.CanonicalizeWithNfc(parsed)
            .Match(b => b, e => throw new Xunit.Sdk.XunitException($"canonicalize failed: {e.Type}"));
        return Encoding.UTF8.GetString(bytes.Span);
    }

    [Theory]
    [MemberData(nameof(C4Vectors))]
    public void AppendixC4Vector(string name, byte[] input, byte[] expected)
    {
        _ = name;
        var parsed = JsonReader.Parse(input, AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        var actual = CanonicalJson.CanonicalizeWithNfc(parsed)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal(expected, actual);
    }

    public static TheoryData<string, byte[], byte[]> C4Vectors()
    {
        var data = new TheoryData<string, byte[], byte[]>();
        foreach (var v in VectorLoader.Load("c4"))
            data.Add(v.Name, v.Input, v.ExpectedCanonical!);
        return data;
    }

    [Theory]
    [MemberData(nameof(OrderingVectors))]
    public void Utf16CodeUnitOrdering(string name, byte[] input, byte[] expected)
    {
        _ = name;
        var parsed = JsonReader.Parse(input, AdmitLimits.Default).Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal(expected, CanonicalJson.CanonicalizeWithNfc(parsed).Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type)));
    }

    public static TheoryData<string, byte[], byte[]> OrderingVectors()
    {
        var data = new TheoryData<string, byte[], byte[]>();
        foreach (var v in VectorLoader.Load("ordering"))
            data.Add(v.Name, v.Input, v.ExpectedCanonical!);
        return data;
    }

    /// <summary>
    /// Closes the coverage gap that let JsonNumber.Serialize's shortest-round-trip and
    /// exponent-threshold bugs ship undetected: numbers/ vectors existed on disk, but
    /// nothing in the committed suite actually canonicalized and compared them.
    /// VectorLoaderTests only checks that each vector cites a requirement and declares
    /// an expected form -- it never calls CanonicalJson at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(NumbersVectors))]
    public void NumbersVector(string name, byte[] input, byte[] expected)
    {
        _ = name;
        var parsed = JsonReader.Parse(input, AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        var actual = CanonicalJson.CanonicalizeWithNfc(parsed)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal(expected, actual);
    }

    public static TheoryData<string, byte[], byte[]> NumbersVectors()
    {
        var data = new TheoryData<string, byte[], byte[]>();
        foreach (var v in VectorLoader.Load("numbers"))
            data.Add(v.Name, v.Input, v.ExpectedCanonical!);
        return data;
    }

    /// <summary>
    /// Same gap, for unicode/ -- including the key-normalization vector below.
    /// </summary>
    /// <remarks>
    /// A unicode/ vector may carry <c>expect-reject</c> instead of
    /// <c>expected.canonical</c>. That combination did not originally exist:
    /// <c>expect-reject</c> was defined only for the <c>admit</c> profile, on the
    /// assumption that canonicalization either succeeds or is never reached. The
    /// NFC-collision finding disproved it -- normalizing two distinct member names
    /// can make them equal, and ADMIT cannot catch that, because the input
    /// genuinely has no duplicate. So <see cref="CanonicalJson.CanonicalizeWithNfc"/>
    /// itself must reject, and the corpus needs a way to say so.
    /// </remarks>
    [Theory]
    [MemberData(nameof(UnicodeVectors))]
    public void UnicodeVector(string name, byte[] input, byte[]? expected, string? expectRejectSlug)
    {
        _ = name;
        var parsed = JsonReader.Parse(input, AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        var result = CanonicalJson.CanonicalizeWithNfc(parsed);

        if (expectRejectSlug is not null)
        {
            Assert.Equal(expectRejectSlug, result.Match(_ => "unexpectedly succeeded", e => e.Type));
            return;
        }

        Assert.Equal(expected, result.Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type)));
    }

    public static TheoryData<string, byte[], byte[]?, string?> UnicodeVectors()
    {
        var data = new TheoryData<string, byte[], byte[]?, string?>();
        foreach (var v in VectorLoader.Load("unicode"))
            data.Add(v.Name, v.Input, v.ExpectedCanonical, v.ExpectRejectSlug);
        return data;
    }

    [Fact]
    [SuppressMessage(
        "Naming",
        "CA1707:Identifiers should not contain underscores",
        Justification = "Test name spells out the exact code points under test (U+FFFD) for readability " +
            "in failure output; the underscores are load-bearing documentation, not a naming lapse.")]
    public void NonBmpKeySortsBeforeU_FFFD_BecauseSurrogatesAreLowInUtf16()
    {
        // The single most likely cross-implementation divergence. U+10000 encodes in
        // UTF-16 as the surrogate pair D800 DC00, so it sorts BELOW U+FFFD. In UTF-8
        // it starts 0xF0 against U+FFFD's 0xEF, giving the opposite answer — and
        // Rust's native String Ord is UTF-8 order.
        var nonBmp = char.ConvertFromUtf32(0x10000);   // "\U00010000"
        var replacement = "�";

        var input = $$"""{"{{replacement}}":1,"{{nonBmp}}":2}""";
        var expected = $$"""{"{{nonBmp}}":2,"{{replacement}}":1}""";

        Assert.Equal(expected, CanonicalizeWithNfc(input));
        Assert.True(Utf16Ordinal.Compare(nonBmp, replacement) < 0, "UTF-16 order must place the surrogate pair first");
    }

    [Fact]
    public void ControlCharactersRemainEscaped() =>
        Assert.Equal("""{"a":"\u0000"}""", CanonicalizeWithNfc("""{"a":"\u0000"}"""));

    [Fact]
    public void NfdInputNormalizesToNfc() =>
        Assert.Equal(CanonicalizeWithNfc("""{"a":"café"}"""), CanonicalizeWithNfc("""{"a":"café"}"""));

    [Fact]
    public void CanonicalizationIsIdempotent()
    {
        var once = CanonicalizeWithNfc("""{"b":1,"a":{"d":4,"c":3}}""");
        Assert.Equal(once, CanonicalizeWithNfc(once));
    }

    [Fact]
    public void UnicodeVersionIsPinnedToSixteenZero() =>
        Assert.Equal("16.0", CanonicalJson.UnicodeVersion);

    /// <summary>
    /// Companion to JsonReaderTests.AcceptsNumberLiteralThatUnderflowsToZero: a literal
    /// too small to represent underflows to positive zero at ADMIT, and must canonicalize
    /// exactly like any other zero. Distinct from the overflow case (1e400), which ADMIT
    /// rejects outright with curia/admit/non-finite-number rather than letting it reach
    /// this function at all -- see conformance/admit-reject/non-finite-number/.
    /// </summary>
    [Fact]
    public void UnderflowToZeroCanonicalizesAsZero() =>
        Assert.Equal("""{"a":0}""", CanonicalizeWithNfc("""{"a":1e-400}"""));

    /// <summary>
    /// The split's contract: on already-NFC input, the pure and NFC-profile functions
    /// must agree byte-for-byte, because CanonicalizeWithNfc delegates to Canonicalize
    /// after normalizing, and normalization is a no-op on content that is already NFC.
    /// </summary>
    [Fact]
    public void PureAndNfcProfileAgreeOnAlreadyNfcInput()
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes("""{"a":"café"}"""), AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        var pure = CanonicalJson.Canonicalize(parsed)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));
        var nfcProfile = CanonicalJson.CanonicalizeWithNfc(parsed)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal(pure, nfcProfile);
    }

    /// <summary>
    /// The other half of the contract: on non-NFC input, the two functions MUST diverge
    /// — this is R6.9's entire point, and the reason the split exists instead of a single
    /// function. If this ever passes with pure == nfcProfile, CanonicalizeWithNfc has
    /// stopped normalizing and R6.9 is silently broken.
    /// </summary>
    [Fact]
    public void PureAndNfcProfileDisagreeOnNonNfcInput()
    {
        // "A" + COMBINING RING ABOVE (NFD) — the official rfc8785 "unicode" vector's own
        // case. Pure JCS must preserve it; the NFC profile must compose it to "Å" (U+00C5).
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes("""{"a":"Å"}"""), AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        var pure = CanonicalJson.Canonicalize(parsed)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));
        var nfcProfile = CanonicalJson.CanonicalizeWithNfc(parsed)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.NotEqual(pure, nfcProfile);
    }

    /// <summary>
    /// U+FFFE is a Unicode noncharacter that string.Normalize(NormalizationForm.FormC)
    /// throws ArgumentException on (verified directly against this runtime: ICU reads it as
    /// a reversed byte-order mark rather than text). Before JsonReader rejected
    /// noncharacters, an admitted document carrying U+FFFE reached CanonicalizeWithNfc —
    /// the function CanonicalizeEnvelope always uses for signing/verification — and crashed
    /// there instead of returning Result.Fail, violating CS-10 (fallibility is a value).
    /// This pins the primary fix: ADMIT rejects the document before any JsonValue exists to
    /// hand to CanonicalizeWithNfc, so that failure is unreachable from wire input, not merely
    /// rare. See JsonReaderTests.RejectsUnicodeNoncharacterInAStringValue for the ADMIT-level
    /// rejection pinned directly, and conformance/admit-reject/noncharacter/ for the vector.
    /// </summary>
    [Fact]
    public void AdmitRejectsNoncharacterBeforeCanonicalizationCanEverSeeIt()
    {
        var json = $$"""{"a":"{{char.ConvertFromUtf32(0xFFFE)}}"}""";

        var result = JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default);

        Assert.False(result.IsOk);
        Assert.Equal("curia/admit/noncharacter", result.Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// The defense-in-depth half of the same evidence: CanonicalizeWithNfc's contract is
    /// "the caller already admitted this," but CS-10 forbids an unhandled exception
    /// regardless of whether a real caller could ever violate that contract. Bypassing
    /// ADMIT (as no real caller in this codebase does — EnvelopeParser.Parse always calls
    /// JsonReader.Parse first) and constructing the JsonValue tree directly used to
    /// reproduce the original crash; NormalizeString now catches the ArgumentException
    /// string.Normalize throws for this input and returns Result.Fail instead, so the
    /// primary fix (ADMIT rejecting the input before it becomes a JsonValue) is backed by
    /// a second, independent guarantee that this function itself never crashes.
    /// </summary>
    [Fact]
    public void CanonicalizeWithNfcFailsRatherThanThrowsOnANoncharacterIfAdmitIsBypassed()
    {
        var value = new JsonValue.Object([new("a", new JsonValue.String(char.ConvertFromUtf32(0xFFFE)))]);

        var result = CanonicalJson.CanonicalizeWithNfc(value);

        Assert.False(result.IsOk);
        Assert.Equal("curia/canon/normalization-failed", result.Match(_ => "ok", e => e.Type));
    }

    // -- curia/canon/duplicate-normalized-key (P22 non-repudiation defect) --------------

    /// <summary>
    /// The reproducer: hex 7b22636166c3a9223a312c2263616665cc81223a327d, i.e.
    /// {"café":1,"café":2} where the first key is precomposed U+00E9 and the second
    /// is "cafe" + COMBINING ACUTE ACCENT (U+0301) -- distinct on the wire, so ADMIT's
    /// duplicate-key check (raw bytes only) cannot see the collision. R6.9 then mandates
    /// the NFC step that makes them equal. Before this fix, CanonicalizeWithNfc emitted
    /// {"café":1,"café":2} -- a duplicate member in the signed canonical form, meaning two
    /// distinct wire documents could share one signature.
    /// </summary>
    [Fact]
    public void RejectsDuplicateNormalizedKeyFromDistinctWireCombiningForms()
    {
        var json = "{\"café\":1,\"café\":2}";
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException($"parse failed: {e.Type}"));

        var result = CanonicalJson.CanonicalizeWithNfc(parsed);

        Assert.False(result.IsOk);
        Assert.Equal("curia/canon/duplicate-normalized-key", result.Match(_ => "ok", e => e.Type));
    }

    /// <summary>Same collision, members reversed: the check must not depend on which of the two colliding names was admitted first.</summary>
    [Fact]
    public void RejectsDuplicateNormalizedKeyRegardlessOfWhichCombiningFormComesFirst()
    {
        var json = "{\"café\":2,\"café\":1}";
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException($"parse failed: {e.Type}"));

        var result = CanonicalJson.CanonicalizeWithNfc(parsed);

        Assert.False(result.IsOk);
        Assert.Equal("curia/canon/duplicate-normalized-key", result.Match(_ => "ok", e => e.Type));
    }

    /// <summary>The check is not limited to the document root: a collision inside a nested object must be caught too.</summary>
    [Fact]
    public void RejectsDuplicateNormalizedKeyInsideANestedObject()
    {
        var json = "{\"outer\":{\"café\":1,\"café\":2}}";
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException($"parse failed: {e.Type}"));

        var result = CanonicalJson.CanonicalizeWithNfc(parsed);

        Assert.False(result.IsOk);
        Assert.Equal("curia/canon/duplicate-normalized-key", result.Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// An object with both a raw duplicate ("a" twice) and a separate NFC collision
    /// ("café" precomposed vs. decomposed) must report the raw duplicate, using ADMIT's
    /// own curia/admit/duplicate-key predicate -- never curia/canon/duplicate-normalized-key,
    /// which would misdescribe a raw duplicate as something normalization created. Built by
    /// direct JsonValue construction (bypassing JsonReader/ADMIT, which would otherwise
    /// reject the raw "a" duplicate itself before this function's own check ever ran) so
    /// this function's internal precedence rule is exercised directly, mirroring
    /// curia-testis's nfc.rs three ordering permutations
    /// (raw_duplicate_always_wins_when_raw_duplicate_is_first /
    /// _when_nfc_collision_is_first / _with_an_unrelated_key_between_them).
    /// </summary>
    [Theory]
    [MemberData(nameof(BothDefectsMemberOrderings))]
    public void RawDuplicateAlwaysWinsOverAnNfcCollisionRegardlessOfMemberOrder(
        ImmutableArray<KeyValuePair<string, JsonValue>> members)
    {
        var value = new JsonValue.Object(members);

        var result = CanonicalJson.CanonicalizeWithNfc(value);

        Assert.False(result.IsOk);
        Assert.Equal("curia/admit/duplicate-key", result.Match(_ => "ok", e => e.Type));
    }

    public static TheoryData<ImmutableArray<KeyValuePair<string, JsonValue>>> BothDefectsMemberOrderings()
    {
        KeyValuePair<string, JsonValue> RawA1 = new("a", new JsonValue.Number(1));
        KeyValuePair<string, JsonValue> RawA2 = new("a", new JsonValue.Number(2));
        KeyValuePair<string, JsonValue> CafePrecomposed = new("café", new JsonValue.Number(3));
        KeyValuePair<string, JsonValue> CafeDecomposed = new("café", new JsonValue.Number(4));
        KeyValuePair<string, JsonValue> Unrelated = new("z", new JsonValue.Number(5));

        var data = new TheoryData<ImmutableArray<KeyValuePair<string, JsonValue>>>
        {
            // Raw duplicate pair appears before the NFC-collision pair.
            ImmutableArray.Create(RawA1, RawA2, CafePrecomposed, CafeDecomposed),
            // NFC-collision pair appears before the raw duplicate pair.
            ImmutableArray.Create(CafePrecomposed, CafeDecomposed, RawA1, RawA2),
            // An unrelated key sits between the two members of each colliding pair.
            ImmutableArray.Create(RawA1, Unrelated, CafePrecomposed, RawA2, CafeDecomposed),
        };
        return data;
    }

    // -- Controls: inputs that must still succeed --------------------------------------

    [Fact]
    public void AcceptsGenuinelyDistinctKeys() =>
        Assert.Equal("""{"a":1,"b":2}""", CanonicalizeWithNfc("""{"a":1,"b":2}"""));

    /// <summary>Two normalized names colliding in *different* objects is not a defect -- the check is scoped to one object's own member list (RFC 8785 §3.2.3).</summary>
    [Fact]
    public void AcceptsTheSameNormalizedNameInDifferentObjects()
    {
        var json = "{\"one\":{\"café\":1},\"two\":{\"café\":2}}";
        var expected = "{\"one\":{\"café\":1},\"two\":{\"café\":2}}";

        Assert.Equal(expected, CanonicalizeWithNfc(json));
    }

    [Fact]
    public void AcceptsKeysAlreadyInNfc() =>
        Assert.Equal("{\"café\":1}", CanonicalizeWithNfc("{\"café\":1}"));

    /// <summary>Case differs but NFC does not fold case; "Cafe" and "cafe" remain distinct keys.</summary>
    [Fact]
    public void AcceptsKeysDifferingOnlyByCase() =>
        Assert.Equal("""{"Cafe":1,"cafe":2}""", CanonicalizeWithNfc("""{"Cafe":1,"cafe":2}"""));
}
