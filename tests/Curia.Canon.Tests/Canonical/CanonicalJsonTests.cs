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
        // R6.41: canonicalization tests parse via ParseUnrestricted, not the ADMIT-gated
        // Parse -- see JsonReader.ParseUnrestricted's remarks.
        var parsed = JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(json))
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
        var parsed = JsonReader.ParseUnrestricted(input)
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
        var parsed = JsonReader.ParseUnrestricted(input).Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
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
        // R6.41: several numbers/ vectors (large-exact-expansion's 123456789012345680000
        // among them) are outside R6.33's ADMIT-enforced safe-integer bound, and
        // exponent-switch/small-fraction-boundary/small-fraction-just-below are non-integer
        // -- exactly the documents ParseUnrestricted exists to still canonicalize (errata
        // E4's "Prerequisite, demonstrated"). Parse (ADMIT) would reject all four here.
        var parsed = JsonReader.ParseUnrestricted(input)
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
        var parsed = JsonReader.ParseUnrestricted(input)
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
        var parsed = JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes("""{"a":"café"}"""))
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
        var parsed = JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes("""{"a":"Å"}"""))
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
    /// R6.38 (errata E2): CanonicalizeWithNfc must accept and correctly canonicalize a
    /// Unicode noncharacter -- "treated the same way" as a document exceeding one of R6.39's
    /// caps, because a noncharacter is a valid Unicode scalar value ADMIT excludes as
    /// policy, not one RFC 8785 or NFC leaves undefined. This test previously asserted the
    /// opposite (Result.Fail with curia/canon/normalization-failed): before R6.38, that was
    /// the correct fix for the crash string.Normalize(NormalizationForm.FormC) throws on
    /// U+FFFE on this runtime (CS-10: fallibility must be a value, not an unhandled
    /// exception) -- but "fails gracefully instead of crashing" and "succeeds" are different
    /// guarantees, and errata E2's own robustness note calls the gap between them "a
    /// distinct defect from the accept/reject question R6.38 settles." NormalizeString now
    /// works around the platform defect (splitting the string at noncharacter boundaries,
    /// each of which is normalization-inert by Unicode's own stability guarantee -- see its
    /// remarks) so this succeeds rather than fails.
    ///
    /// Bypassing ADMIT (as no real caller in this codebase does -- EnvelopeParser.Parse
    /// always calls JsonReader.Parse first) by constructing the JsonValue tree directly is
    /// what makes this reachable at all: the real ingest pipeline never hands
    /// CanonicalizeWithNfc a document ADMIT has not already screened. See
    /// ParseUnrestrictedThenCanonicalizeWithNfcAcceptsANoncharacter below for the
    /// production-shaped way (R6.41) a noncharacter now reaches this function without a
    /// hand-built JsonValue tree.
    /// </summary>
    [Fact]
    public void CanonicalizeWithNfcAcceptsANoncharacterWhenAdmitIsBypassed()
    {
        var value = new JsonValue.Object([new("a", new JsonValue.String(char.ConvertFromUtf32(0xFFFE)))]);

        var result = CanonicalJson.CanonicalizeWithNfc(value);

        var canonical = result.Match(b => Encoding.UTF8.GetString(b.Span), e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal($$"""{"a":"{{char.ConvertFromUtf32(0xFFFE)}}"}""", canonical);
    }

    /// <summary>
    /// R6.41 and R6.38 composed end to end: ParseUnrestricted is the actual, production-shaped
    /// way a noncharacter reaches CanonicalizeWithNfc without ADMIT having run, not only a
    /// hand-built JsonValue tree (the test above). Also exercises a noncharacter embedded
    /// alongside ordinary text in the same string value ("a￾b"), not only a bare
    /// noncharacter -- the NormalizeString split path must reassemble both sides correctly.
    /// </summary>
    [Fact]
    public void ParseUnrestrictedThenCanonicalizeWithNfcAcceptsANoncharacter()
    {
        var json = $$"""{"a":"x{{char.ConvertFromUtf32(0xFFFE)}}y"}""";
        var parsed = JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(json))
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));

        var canonical = CanonicalJson.CanonicalizeWithNfc(parsed)
            .Match(b => Encoding.UTF8.GetString(b.Span), e => throw new Xunit.Sdk.XunitException(e.Type));

        Assert.Equal(json, canonical);
    }

    /// <summary>
    /// The other half of R6.38's first paragraph (the R6.39-cap exemption, not the
    /// noncharacter exemption): a document ADMIT would refuse for nesting depth must still
    /// canonicalize correctly when reached via ParseUnrestricted. Mirrors
    /// JsonReaderTests.RejectsExcessiveNestingBeforeExhaustingTheStack's boundary (33 levels)
    /// on the ADMIT side.
    /// </summary>
    [Fact]
    public void CanonicalizesADocumentExceedingTheAdmitDepthCap()
    {
        var overCap = string.Concat(Enumerable.Repeat("""{"a":""", AdmitLimits.Default.MaxDepth + 1))
            + "1" + new string('}', AdmitLimits.Default.MaxDepth + 1);
        var parsed = JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(overCap))
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));

        var canonical = CanonicalJson.Canonicalize(parsed)
            .Match(b => Encoding.UTF8.GetString(b.Span), e => throw new Xunit.Sdk.XunitException(e.Type));

        Assert.Equal(overCap, canonical); // already minimal JSON: one member per level, no whitespace
    }

    // -- curia/admit/duplicate-key from the bare Canonicalize (R6.38 ¶2, errata E10) ----

    /// <summary>
    /// R6.38's second paragraph: <c>Canonicalize</c> SHALL reject a raw duplicate object
    /// member name "independently of ADMIT and regardless of whether ADMIT already ran."
    /// RFC 8785 §3.2.3 orders members by name and says nothing about two names that are
    /// equal, so the bytes this function used to emit for such a tree
    /// (<c>{"dup":"FIRST","dup":"SECOND"}</c>, verbatim) were outside what the specification
    /// it implements defines, and do not re-parse to one unambiguous document.
    ///
    /// The tree is built directly rather than parsed, which is the only way to reach this at
    /// all -- both <see cref="JsonReader"/> paths already reject duplicates -- and is also
    /// exactly how the defect reached production: <c>Curia.Infrastructure.PostgresEventStore</c>
    /// canonicalized a caller-built <see cref="JsonValue"/> payload straight to jsonb, which
    /// resolves duplicates last-wins, so "FIRST" was silently dropped by the system of record.
    /// See PostgresEventStoreDuplicateMemberRefusalTests for that end of it.
    /// </summary>
    [Fact]
    public void CanonicalizeRejectsARawDuplicateMemberName()
    {
        var value = new JsonValue.Object(
        [
            new("dup", new JsonValue.String("FIRST")),
            new("dup", new JsonValue.String("SECOND")),
        ]);

        var result = CanonicalJson.Canonicalize(value);

        Assert.False(result.IsOk);
        Assert.Equal("curia/admit/duplicate-key", result.Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// "At any nesting depth" is not decoration: a check on the root object alone would pass
    /// the test above while still emitting undefined bytes here. Nested through an array as
    /// well as an object, since the writer recurses through both.
    /// </summary>
    [Fact]
    public void CanonicalizeRejectsARawDuplicateMemberNameNestedInsideTheDocument()
    {
        var value = new JsonValue.Object(
        [
            new("outer", new JsonValue.Array(
            [
                new JsonValue.Object(
                [
                    new("dup", new JsonValue.String("FIRST")),
                    new("dup", new JsonValue.String("SECOND")),
                ]),
            ])),
        ]);

        var result = CanonicalJson.Canonicalize(value);

        Assert.False(result.IsOk);
        Assert.Equal("curia/admit/duplicate-key", result.Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// The reported key does not depend on wire member order. Both objects below carry two
    /// separate duplicate pairs ("a" and "b"); whichever pair a member-order scan would meet
    /// first, the check runs on the RFC 8785 §3.2.3-sorted member list, so "a" is reported
    /// either way. Same order-independence property errata E1 made normative for
    /// <see cref="CanonicalJson.CanonicalizeWithNfc"/>'s two duplicate predicates, and for
    /// the same reason: a slug (or its detail) that depends on member order is a divergence
    /// waiting to happen between two implementations that are each individually correct.
    /// </summary>
    [Theory]
    [MemberData(nameof(TwoDuplicatePairsInBothOrders))]
    public void CanonicalizeReportsTheSortFirstDuplicateRegardlessOfMemberOrder(
        ImmutableArray<KeyValuePair<string, JsonValue>> members)
    {
        var result = CanonicalJson.Canonicalize(new JsonValue.Object(members));

        Assert.False(result.IsOk);
        Assert.Equal("curia/admit/duplicate-key", result.Match(_ => "ok", e => e.Type));
        Assert.Equal("a", result.Match(_ => "ok", e => e.Detail));
    }

    public static TheoryData<ImmutableArray<KeyValuePair<string, JsonValue>>> TwoDuplicatePairsInBothOrders()
    {
        KeyValuePair<string, JsonValue> A1 = new("a", new JsonValue.Number(1));
        KeyValuePair<string, JsonValue> A2 = new("a", new JsonValue.Number(2));
        KeyValuePair<string, JsonValue> B1 = new("b", new JsonValue.Number(3));
        KeyValuePair<string, JsonValue> B2 = new("b", new JsonValue.Number(4));

        return new TheoryData<ImmutableArray<KeyValuePair<string, JsonValue>>>
        {
            ImmutableArray.Create(A1, A2, B1, B2),
            ImmutableArray.Create(B1, B2, A1, A2),
        };
    }

    /// <summary>
    /// The exemption stays exempt. R6.38's first paragraph forbids these functions from
    /// re-enforcing ADMIT's policy caps, and adding a well-definedness check must not smuggle
    /// one in: an object with 1,025 members -- one past R6.39's cap, which
    /// <see cref="JsonReader.Parse"/> refuses -- still canonicalizes here, with pairwise
    /// distinct names and no duplicate to find.
    /// </summary>
    [Fact]
    public void CanonicalizeStillAcceptsAnObjectExceedingTheAdmitMemberCap()
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        for (var i = 0; i <= AdmitLimits.Default.MaxMembersPerObject; i++)
            members.Add(new KeyValuePair<string, JsonValue>($"k{i:D5}", new JsonValue.Number(i)));

        var result = CanonicalJson.Canonicalize(new JsonValue.Object(members.ToImmutable()));

        Assert.True(result.IsOk);
    }

    // -- curia/admit/unpaired-surrogate from both canonicalizers (R6.38 ¶2, errata E13) --

    /// <summary>A high surrogate. Unpaired unless a low surrogate immediately follows it.</summary>
    private const char LoneHigh = '\uD800';

    /// <summary>A low surrogate. Unpaired unless a high surrogate immediately precedes it.</summary>
    private const char LoneLow = '\uDC00';

    /// <summary>
    /// U+1F602 FACE WITH TEARS OF JOY, as the well-formed pair D83D DE02. The control the
    /// whole fix turns on: a check that rejected any surrogate code unit rather than an
    /// unpaired one would reject every astral character, and this is the exact character
    /// the RFC author's own <c>rfc8785/input-weird.json</c> uses as an object member name.
    /// </summary>
    private const string ValidPair = "😂";

    /// <summary>
    /// Builds an ill-formed UTF-16 string from an ASCII sketch: <c>H</c> = lone high surrogate
    /// (U+D800), <c>L</c> = lone low surrogate (U+DC00), <c>P</c> = the well-formed pair
    /// <see cref="ValidPair"/>, any other character = itself.
    ///
    /// The indirection is load-bearing, not decoration. <b>An unpaired surrogate cannot be
    /// carried in an <c>[InlineData]</c> argument</b>: xUnit serializes theory arguments, and the
    /// round trip replaces every unpaired surrogate with U+FFFD -- so the first version of these
    /// tests received a *well-formed* string, failed for a reason that had nothing to do with the
    /// defect, and would have gone on failing after the fix landed. The same round trip also
    /// renders two distinct lone surrogates identically, which silently collapsed the high and
    /// low cases into one test. Both are the shape this whole errata family keeps finding: the
    /// absence of a probe is indistinguishable from a passing one. The sketch is pure ASCII, so
    /// it survives serialization intact and names each case readably in the run output;
    /// <see cref="AssertIsIllFormed"/> then proves the string the test actually holds is the one
    /// it meant to hold.
    /// </summary>
    private static string Sketch(string? sketch)
    {
        ArgumentNullException.ThrowIfNull(sketch);
        var sb = new StringBuilder(sketch.Length);
        foreach (var c in sketch)
        {
            switch (c)
            {
                case 'H': sb.Append(LoneHigh); break;
                case 'L': sb.Append(LoneLow); break;
                case 'P': sb.Append(ValidPair); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Proves the test input really is ill-formed UTF-16 before asserting anything about how it
    /// is rejected, so a test can never quietly pass (or fail) against a well-formed string the
    /// harness substituted. The check is the defect itself, used as an oracle: a well-formed
    /// string survives a UTF-8 round trip unchanged, and an ill-formed one comes back with
    /// U+FFFD substituted -- which is precisely the silent substitution R6.38 forbids
    /// <see cref="CanonicalJson.Canonicalize"/> from performing.
    /// </summary>
    private static void AssertIsIllFormed(string s) =>
        Assert.NotEqual(s, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s)), StringComparer.Ordinal);

    /// <summary>
    /// R6.38's second paragraph, the half errata E12 recorded as found and not fixed:
    /// <c>Canonicalize</c> SHALL reject an unpaired UTF-16 surrogate "independently of ADMIT
    /// and regardless of whether ADMIT already ran," for the same reason it rejects a raw
    /// duplicate member name -- RFC 8785 defines no canonical output for it.
    ///
    /// The failure mode being closed is silent substitution, not a crash: a lone surrogate
    /// survived <see cref="CanonicalJson"/>'s writer untouched and became U+FFFD at the
    /// <c>Encoding.UTF8.GetBytes</c> step, so the function returned <c>Ok</c> with canonical
    /// bytes carrying a *different character* than the tree it was handed. The tree is built
    /// directly rather than parsed, which is the only way to reach this at all: both
    /// <see cref="JsonReader"/> paths already reject an unpaired surrogate escape with this
    /// same slug, which is exactly why the differential harness could never see the
    /// divergence -- it feeds bytes, and the byte path was never wrong.
    /// </summary>
    /// <remarks>See <see cref="Sketch"/>: the cases are given as ASCII sketches because the strings themselves cannot survive being theory arguments.</remarks>
    [Theory]
    [InlineData("H")]    // lone high surrogate
    [InlineData("L")]    // lone low surrogate
    [InlineData("xH")]   // high surrogate at the end of a longer string
    [InlineData("Hx")]   // high surrogate followed by a non-surrogate
    [InlineData("HH")]   // high surrogate followed by another high one
    [InlineData("LP")]   // low surrogate immediately before a well-formed pair
    [InlineData("PH")]   // well-formed pair immediately before a lone high one
    public void CanonicalizeRejectsAnUnpairedSurrogateInAStringValue(string sketch)
    {
        var ill = Sketch(sketch);
        AssertIsIllFormed(ill);

        var result = CanonicalJson.Canonicalize(new JsonValue.Object([new("a", new JsonValue.String(ill))]));

        Assert.False(result.IsOk);
        Assert.Equal("curia/admit/unpaired-surrogate", result.Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// Member names are strings too. A check that walked only string *values* would pass every
    /// case above and still emit a canonical document whose member name is not the name it was
    /// handed -- and the member name is what RFC 8785 §3.2.3 sorts on, so a substituted U+FFFD
    /// there changes where the member sorts as well as what it says.
    /// </summary>
    [Theory]
    [InlineData("H")]    // lone high surrogate
    [InlineData("L")]    // lone low surrogate
    [InlineData("kL")]   // low surrogate at the end of a longer name
    public void CanonicalizeRejectsAnUnpairedSurrogateInAMemberName(string sketch)
    {
        var ill = Sketch(sketch);
        AssertIsIllFormed(ill);

        var result = CanonicalJson.Canonicalize(new JsonValue.Object([new(ill, new JsonValue.Number(1))]));

        Assert.False(result.IsOk);
        Assert.Equal("curia/admit/unpaired-surrogate", result.Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// "At any depth" is not decoration, for the same reason it was not for duplicate members:
    /// a check on the root object's own strings would pass both tests above and still emit
    /// substituted bytes here. Nested through an array as well as an object, since the writer
    /// recurses through both.
    /// </summary>
    [Fact]
    public void CanonicalizeRejectsAnUnpairedSurrogateNestedInsideTheDocument()
    {
        var ill = Sketch("H");
        AssertIsIllFormed(ill);
        var value = new JsonValue.Object(
        [
            new("outer", new JsonValue.Array(
            [
                new JsonValue.Object([new("inner", new JsonValue.String(ill))]),
            ])),
        ]);

        var result = CanonicalJson.Canonicalize(value);

        Assert.False(result.IsOk);
        Assert.Equal("curia/admit/unpaired-surrogate", result.Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// The control that proves the fix did not overshoot. A well-formed surrogate pair is how
    /// every character outside the BMP is spelled in UTF-16, so a lazier check rejecting any
    /// surrogate code unit would reject every astral character -- emoji, historic scripts, the
    /// whole of plane 1 upward -- and would break <c>rfc8785/input-weird.json</c>, whose
    /// "Smiley" member name is this exact pair. Asserted on the bytes as well as the decoded
    /// string, because the defect being fixed was a silent U+FFFD substitution at the encode
    /// step: a string comparison alone would catch it, but the byte assertion names what
    /// "correctly" means here without a round trip in between.
    /// </summary>
    [Fact]
    public void CanonicalizeAcceptsAValidSurrogatePairInBothMemberNameAndValue()
    {
        // The mirror of AssertIsIllFormed: a well-formed pair round-trips through UTF-8 unchanged.
        Assert.Equal(ValidPair, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(ValidPair)), StringComparer.Ordinal);

        var value = new JsonValue.Object(
        [
            new(ValidPair, new JsonValue.String("a" + ValidPair + "b")),
        ]);

        var bytes = CanonicalJson.Canonicalize(value)
            .Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type));

        Assert.Equal($$"""{"{{ValidPair}}":"a{{ValidPair}}b"}""", Encoding.UTF8.GetString(bytes));
        Assert.Equal(
            Encoding.UTF8.GetBytes($$"""{"{{ValidPair}}":"a{{ValidPair}}b"}"""),
            bytes);
        Assert.DoesNotContain("�", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    /// <summary>
    /// <see cref="CanonicalJson.CanonicalizeWithNfc"/> already refused this tree before the fix,
    /// but named the wrong thing: <c>string.Normalize(NormalizationForm.FormC)</c> throws
    /// <see cref="ArgumentException"/> on ill-formed UTF-16, so <c>NormalizeRun</c>'s catch
    /// reported <c>curia/canon/normalization-failed</c> -- the layer that noticed, not the
    /// condition -- with a platform-specific ICU message as its detail. R6.40 pins that
    /// distinction ("a condition name, not a generic outcome word") and R6.42 restates it as an
    /// obligation ("SHALL name the condition rather than the layer that noticed it"); both
    /// <see cref="JsonReader"/> and <c>curia-testis</c> already answer
    /// <c>curia/admit/unpaired-surrogate</c> for the identical input. The check now sits ahead
    /// of normalization on this path too, so both profiles agree with both parse paths and with
    /// the Rust verifier.
    ///
    /// This is a deliberate slug move, not a cleanup: it is a public failure surface, and the
    /// event store admits payloads under this function (R11.24), so its refusal predicate for an
    /// unpaired surrogate moves with it.
    /// </summary>
    [Theory]
    [InlineData("H")]    // lone high surrogate
    [InlineData("L")]    // lone low surrogate
    [InlineData("PL")]   // well-formed pair immediately before a lone low one
    public void CanonicalizeWithNfcRejectsAnUnpairedSurrogateNamingTheConditionNotTheLayer(string sketch)
    {
        var ill = Sketch(sketch);
        AssertIsIllFormed(ill);

        var inValue = CanonicalJson.CanonicalizeWithNfc(
            new JsonValue.Object([new("a", new JsonValue.String(ill))]));
        var inMemberName = CanonicalJson.CanonicalizeWithNfc(
            new JsonValue.Object([new(ill, new JsonValue.Number(1))]));

        Assert.Equal("curia/admit/unpaired-surrogate", inValue.Match(_ => "ok", e => e.Type));
        Assert.Equal("curia/admit/unpaired-surrogate", inMemberName.Match(_ => "ok", e => e.Type));
    }

    /// <summary>The NFC profile's own overshoot control: a well-formed pair still normalizes and canonicalizes.</summary>
    [Fact]
    public void CanonicalizeWithNfcAcceptsAValidSurrogatePair()
    {
        var value = new JsonValue.Object([new(ValidPair, new JsonValue.String(ValidPair))]);

        var canonical = CanonicalJson.CanonicalizeWithNfc(value)
            .Match(b => Encoding.UTF8.GetString(b.Span), e => throw new Xunit.Sdk.XunitException(e.Type));

        Assert.Equal($$"""{"{{ValidPair}}":"{{ValidPair}}"}""", canonical);
    }

    /// <summary>
    /// Precedence, stated because it exists rather than because a document requires it: within
    /// one object both canonicalizers settle the duplicate member name first -- the pure writer
    /// because <c>OrderMembers</c> runs before a byte of the object is emitted, the NFC profile
    /// because <c>NormalizeObject</c>'s raw-duplicate pass runs before any member name is
    /// normalized. So the two profiles agree with each other here, which is the property worth
    /// having; no requirement pins an order between these two conditions, and across nesting
    /// levels the answer is positional in both.
    /// </summary>
    [Fact]
    public void ADuplicateMemberNameIsReportedAheadOfAnUnpairedSurrogateInTheSameObject()
    {
        var ill = Sketch("H");
        AssertIsIllFormed(ill);
        var value = new JsonValue.Object(
        [
            new("dup", new JsonValue.String(ill)),
            new("dup", new JsonValue.String("SECOND")),
        ]);

        Assert.Equal("curia/admit/duplicate-key", CanonicalJson.Canonicalize(value).Match(_ => "ok", e => e.Type));
        Assert.Equal("curia/admit/duplicate-key", CanonicalJson.CanonicalizeWithNfc(value).Match(_ => "ok", e => e.Type));
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
        var parsed = JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(json))
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
        var parsed = JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(json))
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
        var parsed = JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(json))
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

    /// <summary>
    /// <see cref="CanonicalJson.InCanonicalMemberOrder"/> puts every object's members in
    /// RFC 8785 §3.2.3 order at every depth, leaves array order alone (R6.8), and touches no
    /// scalar. The order is written out literally rather than obtained from the canonicalizer,
    /// so this pins which order that is rather than agreeing with the writer by construction.
    /// </summary>
    [Fact]
    public void InCanonicalMemberOrderSortsEveryObjectAndLeavesArraysAlone()
    {
        var value = new JsonValue.Object(
        [
            new("z", new JsonValue.Number(1)),
            new("longer_key_b", new JsonValue.Number(2)),
            new("arr", new JsonValue.Array(
            [
                new JsonValue.Object([new("q", new JsonValue.Bool(true)), new("b", JsonValue.Null.Instance)]),
                new JsonValue.String("first"),
                new JsonValue.String("second"),
            ])),
            new("a", new JsonValue.Number(3)),
        ]);

        var ordered = Assert.IsType<JsonValue.Object>(
            CanonicalJson.InCanonicalMemberOrder(value).Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type)));

        Assert.Equal(RootOrder, MemberKeys(ordered));

        var array = Assert.IsType<JsonValue.Array>(ordered.Members.Single(m => m.Key == "arr").Value);
        Assert.Equal(NestedOrder, MemberKeys(Assert.IsType<JsonValue.Object>(array.Items[0])));
        Assert.Equal("first", Assert.IsType<JsonValue.String>(array.Items[1]).Value);
        Assert.Equal("second", Assert.IsType<JsonValue.String>(array.Items[2]).Value);
    }

    private static IReadOnlyList<string> MemberKeys(JsonValue.Object o) => [.. o.Members.Select(m => m.Key)];

    private static readonly string[] RootOrder = ["a", "arr", "longer_key_b", "z"];
    private static readonly string[] NestedOrder = ["b", "q"];

    /// <summary>
    /// The reordering and the writer share <c>OrderMembers</c>, so they cannot disagree about
    /// what §3.2.3 order is. Stated as a property rather than assumed: canonicalizing the
    /// reordered tree must give the identical bytes canonicalizing the original does, which is
    /// what "the tree is now in the order the writer would emit" means operationally.
    /// </summary>
    [Fact]
    public void InCanonicalMemberOrderAgreesWithTheWriterItSharesTheOrderingWith()
    {
        var value = new JsonValue.Object(
        [
            new("zeta", new JsonValue.Number(-17.5)),
            new("Alpha", new JsonValue.Array([new JsonValue.String("café"), new JsonValue.Bool(false)])),
            new("nested", new JsonValue.Object([new("y", new JsonValue.Number(0)), new("x", JsonValue.Null.Instance)])),
        ]);

        var ordered = CanonicalJson.InCanonicalMemberOrder(value)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));

        Assert.Equal(
            CanonicalJson.Canonicalize(value).Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type)),
            CanonicalJson.Canonicalize(ordered).Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type)));

        // Idempotent: reordering an already-ordered tree is a no-op, which is what lets the
        // event store apply it on every read without the payload drifting.
        var twice = CanonicalJson.InCanonicalMemberOrder(ordered)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        Assert.Equal(
            CanonicalJson.Canonicalize(ordered).Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type)),
            CanonicalJson.Canonicalize(twice).Match(b => b.ToArray(), e => throw new Xunit.Sdk.XunitException(e.Type)));
    }

    /// <summary>
    /// It fails for the one reason the writer fails for, with the identical slug: §3.2.3 gives
    /// no order for two equal names, so there is no ordered tree to return. Nested, because the
    /// rule is a property of every object in the tree.
    /// </summary>
    [Fact]
    public void InCanonicalMemberOrderRejectsADuplicateMemberNameAtAnyDepth()
    {
        var value = new JsonValue.Object(
        [
            new("outer", new JsonValue.Array(
            [
                new JsonValue.Object(
                [
                    new("dup", new JsonValue.String("FIRST")),
                    new("dup", new JsonValue.String("SECOND")),
                ]),
            ])),
        ]);

        var result = CanonicalJson.InCanonicalMemberOrder(value);

        Assert.False(result.IsOk);
        Assert.Equal("curia/admit/duplicate-key", result.Match(_ => "ok", e => e.Type));
    }
}
