using System.Text;
using Curia.Canon.Json;
using Curia.Canon.Tests.Vectors;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Canon.Tests.Json;

public sealed class JsonReaderTests
{
    private static Result<JsonValue> Parse(string json) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default);

    private static Result<JsonValue> Parse(byte[] utf8) =>
        JsonReader.Parse(utf8, AdmitLimits.Default);

    [Fact]
    public void ParsesAnObjectPreservingMemberOrderAsWritten()
    {
        var root = Assert.IsType<JsonValue.Object>(Parse("""{"b":1,"a":2}""").Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type)));
        Assert.Equal(["b", "a"], root.Members.Select(m => m.Key));
    }

    [Fact]
    public void RejectsDuplicateKeys()
    {
        // System.Text.Json tolerates duplicates silently; JCS and I-JSON do not.
        var slug = Parse("""{"a":1,"a":2}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/duplicate-key", slug);
    }

    [Fact]
    public void RejectsRawNulByteInAString()
    {
        var slug = Parse([.. "{\"a\":\""u8, (byte)0, .. "\"}"u8]).Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/nul-byte", slug);
    }

    [Fact]
    public void AcceptsEscapedNulBecauseItIsLegalJson()
    {
        // c4/vector-09: the six-character escape is legal input and must survive.
        var value = Parse("""{"a":"\u0000"}""").Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        var obj = Assert.IsType<JsonValue.Object>(value);
        Assert.Equal("\u0000", Assert.IsType<JsonValue.String>(obj.Members[0].Value).Value);
    }

    [Fact]
    public void RejectsInvalidUtf8()
    {
        var slug = Parse([.. "{\"a\":\""u8, 0xFF, 0xFE, .. "\"}"u8]).Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/invalid-utf8", slug);
    }

    [Fact]
    public void RejectsUnpairedSurrogate()
    {
        var slug = Parse("""{"a":"\uD800"}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/unpaired-surrogate", slug);
    }

    /// <summary>
    /// Unicode §23.7: 66 code points permanently reserved and "not for interchange" --
    /// U+FDD0-U+FDEF, plus the last two code points of every plane. Built via
    /// char.ConvertFromUtf32 at test-run time rather than embedding the raw character in
    /// this source file, since this repo has previously lost non-BMP/noncharacter code
    /// points between authoring and disk (see VectorLoaderTests' EveryR6NineTransformation-
    /// VectorActuallyTransforms comment and the ordering/non-bmp-vs-e000 fix).
    /// </summary>
    [Theory]
    [InlineData(0xFDD0)]   // start of the FDD0-FDEF reserved block
    [InlineData(0xFDEF)]   // end of that block
    [InlineData(0xFFFE)]   // BMP plane-0 noncharacter (the one that also throws in Normalize)
    [InlineData(0xFFFF)]   // BMP plane-0 noncharacter
    [InlineData(0x1FFFE)]  // plane-1 noncharacter, requires a UTF-16 surrogate pair
    [InlineData(0x10FFFF)] // plane-16 noncharacter, the last code point in Unicode at all
    public void RejectsUnicodeNoncharacterInAStringValue(int codePoint)
    {
        var ch = char.ConvertFromUtf32(codePoint);
        var slug = Parse($$"""{"a":"{{ch}}"}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/noncharacter", slug);
    }

    [Fact]
    public void RejectsUnicodeNoncharacterInAnObjectKey()
    {
        // Same rule, same slug, for the key position -- object keys and string values both
        // go through JsonReader's shared ReadStringValue, so this pins that the check
        // actually runs for both call sites rather than only the value one.
        var ch = char.ConvertFromUtf32(0xFFFE);
        var slug = Parse($$"""{"{{ch}}":1}""").Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/noncharacter", slug);
    }

    [Fact]
    public void RejectsExcessiveNestingBeforeExhaustingTheStack()
    {
        var deep = string.Concat(Enumerable.Repeat("""{"a":""", 33)) + "1" + new string('}', 33);
        Assert.Equal("curia/admit/depth-exceeded", Parse(deep).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void AcceptsNestingExactlyAtTheDepthCap()
    {
        // R6.15 boundary, pinned against a real earlier off-by-one: MaxDepth (32) containers
        // wrapping a leaf value must be ACCEPTED. The cap governs container nesting, not the
        // leaf value found inside the innermost container -- a leaf one level past the last
        // legal container is not itself an extra level of nesting.
        var atCap = string.Concat(Enumerable.Repeat("""{"a":""", AdmitLimits.Default.MaxDepth))
            + "1" + new string('}', AdmitLimits.Default.MaxDepth);
        Assert.True(Parse(atCap).IsOk);
    }

    [Fact]
    public void RejectsNestingOneContainerBeyondTheDepthCap()
    {
        // The other half of the same boundary: MaxDepth + 1 (33) containers must be REJECTED.
        // Mirrors conformance/admit-reject/over-nested, whose meta.json says exactly this:
        // "33 levels exceeds the depth cap of 32".
        var overCap = string.Concat(Enumerable.Repeat("""{"a":""", AdmitLimits.Default.MaxDepth + 1))
            + "1" + new string('}', AdmitLimits.Default.MaxDepth + 1);
        Assert.Equal("curia/admit/depth-exceeded", Parse(overCap).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsOversizeInputBeforeParsing()
    {
        var big = new byte[AdmitLimits.Default.MaxBytes + 1];
        Assert.Equal("curia/admit/size-exceeded", Parse(big).Match(_ => "ok", e => e.Type));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"a":""")]
    [InlineData("[1,")]
    public void RejectsTruncatedInputAsMalformedNotDepthExceeded(string json)
    {
        // Utf8JsonReader signals a truncated-but-shallow document with the same word
        // ("Expected depth to be zero at the end of the JSON payload...") it uses for its
        // own MaxDepth violations. None of these inputs come close to the depth cap, so
        // classifying by message substring would misdiagnose truncation as nesting.
        var slug = Parse(json).Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/malformed-json", slug);
    }

    [Fact]
    public void RejectsObjectsWithMoreMembersThanTheCap()
    {
        var json = "{" + string.Join(",", Enumerable.Range(0, AdmitLimits.Default.MaxMembersPerObject + 1).Select(i => $"\"k{i}\":0")) + "}";
        Assert.Equal("curia/admit/members-exceeded", Parse(json).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsStringsLongerThanTheCap()
    {
        var json = "{\"a\":\"" + new string('x', AdmitLimits.Default.MaxStringBytes + 1) + "\"}";
        Assert.Equal("curia/admit/string-too-long", Parse(json).Match(_ => "ok", e => e.Type));
    }

    // R6.39's second sentence -- "Published vectors SHALL exercise both sides of each of the
    // four boundaries -- the value at the limit (accepted) and one past it (rejected)" -- was
    // discharged for depth alone. The three tests above reject one past three caps; nothing
    // anywhere accepted the value AT those caps, so an off-by-one narrowing any of them by a
    // single unit was invisible in both implementations.
    //
    // Expectations still derive from AdmitLimits.Default, which is only legitimate because
    // PublishedAdmitLimitsTests now pins those constants against R6.39's own sentence.

    [Fact]
    public void AcceptsAnObjectWithExactlyTheMemberCap()
    {
        var json = "{" + string.Join(",", Enumerable.Range(0, AdmitLimits.Default.MaxMembersPerObject).Select(i => $"\"k{i}\":0")) + "}";
        Assert.True(Parse(json).IsOk);
    }

    [Fact]
    public void AcceptsAStringOfExactlyTheStringCap()
    {
        var json = "{\"a\":\"" + new string('x', AdmitLimits.Default.MaxStringBytes) + "\"}";
        Assert.True(Parse(json).IsOk);
    }

    /// <summary>
    /// R6.39 measures the string cap "in UTF-8 bytes", and an all-ASCII suite cannot tell that
    /// from "in characters" -- every fixture above agrees under both readings. U+00E9 is two
    /// UTF-8 bytes, so half the cap's worth of them lands exactly on the boundary and one more
    /// crosses it by two; an implementation counting characters would accept both, having seen
    /// 131,073 of a permitted 262,144.
    ///
    /// <para>Written as literal, unescaped UTF-8, where both readings of the cap agree. The
    /// escaped form used to be deliberately untested here, because R6.39 did not say whether
    /// the cap measured the decoded value or the raw source span and pinning either would have
    /// frozen a reading by accident. Errata G1 settled it -- R6.39 (addendum), decoded --
    /// so the escaped form is now pinned directly, immediately below.</para>
    /// </summary>
    [Fact]
    public void StringCapCountsUtf8BytesNotCharacters()
    {
        var atCap = string.Concat(Enumerable.Repeat("\u00e9", AdmitLimits.Default.MaxStringBytes / 2));
        Assert.Equal(AdmitLimits.Default.MaxStringBytes, Encoding.UTF8.GetByteCount(atCap));
        Assert.True(Parse("{\"a\":\"" + atCap + "\"}").IsOk);

        var pastCap = atCap + "\u00e9";
        Assert.Equal(AdmitLimits.Default.MaxStringBytes + 2, Encoding.UTF8.GetByteCount(pastCap));
        Assert.Equal("curia/admit/string-too-long", Parse("{\"a\":\"" + pastCap + "\"}").Match(_ => "ok", e => e.Type));
    }

    // ---- R6.39 (addendum) / errata G1 -------------------------------------------------
    //
    // Two questions R6.39's own sentence left open, each of which was a live divergence
    // against curia-testis in the opposite direction from the other:
    //
    //   (a) is the cap measured over the DECODED value, or over the raw JSON source span
    //       with escapes uncollapsed?   -- decoded.
    //   (b) is an object member NAME a string for this purpose?   -- yes.
    //
    // Both were reproduced by feeding identical bytes to both implementations' differential
    // endpoints before the erratum was written, and both are pinned here now that G1 has
    // decided them. Before this change (a) rejected and (b) admitted; curia-testis was
    // already correct on both, so this file is where the divergence closes.

    /// <summary>
    /// 131,072 escaped U+00E9 decode to exactly the cap -- 262,144 bytes -- while occupying
    /// 786,432 bytes of JSON source. Under the source-span reading this is rejected; under
    /// R6.39 (addendum) it is admitted, because the cap bounds the string, not its spelling.
    ///
    /// <para>The assertion on the source length is the point of the test: without it, a
    /// regression to span-measurement would still be caught, but nothing would record that
    /// the document deliberately spans 3x the cap in source bytes.</para>
    /// </summary>
    [Fact]
    public void StringCapIsMeasuredOverTheDecodedValueNotTheSourceSpan()
    {
        var escaped = string.Concat(Enumerable.Repeat("\\u00e9", AdmitLimits.Default.MaxStringBytes / 2));
        var json = "{\"a\":\"" + escaped + "\"}";

        Assert.Equal(AdmitLimits.Default.MaxStringBytes * 3 + 8, Encoding.UTF8.GetByteCount(json));
        Assert.True(Parse(json).IsOk);
    }

    [Fact]
    public void StringCapRejectsOnePastTheDecodedLengthHoweverItIsSpelled()
    {
        var escaped = string.Concat(Enumerable.Repeat("\\u00e9", AdmitLimits.Default.MaxStringBytes / 2 + 1));
        Assert.Equal(
            "curia/admit/string-too-long",
            Parse("{\"a\":\"" + escaped + "\"}").Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void AcceptsAMemberNameOfExactlyTheStringCap()
    {
        var json = "{\"" + new string('a', AdmitLimits.Default.MaxStringBytes) + "\":0}";
        Assert.True(Parse(json).IsOk);
    }

    [Fact]
    public void RejectsAMemberNameOnePastTheStringCap()
    {
        var json = "{\"" + new string('a', AdmitLimits.Default.MaxStringBytes + 1) + "\":0}";
        Assert.Equal("curia/admit/string-too-long", Parse(json).Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// The direction that mattered operationally. Before G1 a member name was bounded only by
    /// the 1 MiB submission cap -- measured, not inferred: a name of 1,048,570 bytes makes the
    /// document exactly 1 MiB and was admitted, four times past the Forum's published string
    /// cap, while curia-testis refused it. Such a post would be stored, served and attributed,
    /// and then fail to verify offline, which is Phase 1's exit criterion failing quietly on
    /// one post rather than loudly on all of them.
    /// </summary>
    [Fact]
    public void AMemberNameIsNotBoundedOnlyBySubmissionSize()
    {
        var name = AdmitLimits.Default.MaxBytes - 6;   // {"<name>":0} -- a 6-byte wrapper
        var json = "{\"" + new string('a', name) + "\":0}";

        Assert.Equal(AdmitLimits.Default.MaxBytes, Encoding.UTF8.GetByteCount(json));
        Assert.Equal(AdmitLimits.Default.MaxStringBytes * 4, name + 6);
        Assert.Equal("curia/admit/string-too-long", Parse(json).Match(_ => "ok", e => e.Type));
    }

    // ---- Precedence, R6.39 (addendum, cont.) ------------------------------------------
    //
    // Both the length cap and the noncharacter rule are policy ADMIT alone enforces (R6.38),
    // so no well-definedness-before-policy principle orders them. G1 breaks the tie toward
    // the cap, which is the answer both implementations already agreed on for a string value.
    // Getting this wrong does not fail loudly: it produces two implementations that reject
    // the same document for different reasons, which R14.8 makes a divergence and which an
    // accept/reject comparison cannot see.

    [Fact]
    public void TheStringCapIsReportedAheadOfANoncharacterInTheSameValue()
    {
        var value = new string('x', AdmitLimits.Default.MaxStringBytes + 1) + char.ConvertFromUtf32(0xFFFE);
        Assert.Equal(
            "curia/admit/string-too-long",
            Parse("{\"a\":\"" + value + "\"}").Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void TheStringCapIsReportedAheadOfANoncharacterInTheSameMemberName()
    {
        var name = new string('a', AdmitLimits.Default.MaxStringBytes + 1) + char.ConvertFromUtf32(0xFFFE);
        Assert.Equal(
            "curia/admit/string-too-long",
            Parse("{\"" + name + "\":0}").Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// The converse, and the case that fails under span-measurement: escaping puts the raw
    /// source span past the cap while the decoded value stays within it, so the cap must not
    /// fire and the noncharacter must be what is reported. This is the pair of the test above
    /// -- together they pin that precedence is decided on the decoded length, not on whichever
    /// check happens to run first.
    /// </summary>
    [Fact]
    public void ANoncharacterIsReportedWhenTheDecodedValueIsWithinTheCap()
    {
        var filler = new string('x', AdmitLimits.Default.MaxStringBytes - 4);
        var json = "{\"a\":\"" + filler + "\\uFFFE\"}";

        Assert.True(Encoding.UTF8.GetByteCount(json) > AdmitLimits.Default.MaxStringBytes);
        Assert.Equal("curia/admit/noncharacter", Parse(json).Match(_ => "ok", e => e.Type));
    }

    /// <summary>
    /// R6.38 and R6.41: the ADMIT-free parse path applies no policy cap, so the member-name
    /// cap must not leak into it. Without this, extending the cap to member names would
    /// silently narrow the path canonicalization depends on -- and R6.41 requires any document
    /// RFC 8785 defines a canonical form for to remain canonicalizable whether ADMIT would
    /// admit it or not.
    /// </summary>
    [Fact]
    public void TheMemberNameCapIsNotAppliedOnTheAdmitFreePath()
    {
        var json = "{\"" + new string('a', AdmitLimits.Default.MaxStringBytes + 1) + "\":0}";
        Assert.True(JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(json)).IsOk);
    }

    [Fact]
    public void AcceptsASubmissionOfExactlyTheSizeCap()
    {
        var doc = DocumentOfExactSize(AdmitLimits.Default.MaxBytes);
        Assert.Equal(AdmitLimits.Default.MaxBytes, doc.Length);
        Assert.True(Parse(Encoding.UTF8.GetBytes(doc)).IsOk);
    }

    /// <summary>
    /// A syntactically valid document of exactly <paramref name="total"/> bytes that no cap
    /// other than the submission-size cap can decide: sixteen members (far under the
    /// 1,024-member cap), each holding a string far under the 256 KiB string cap.
    ///
    /// <para>The naive fixture -- one giant string in a one-member object -- cannot reach the
    /// size cap at all, because the string cap fires four times sooner. curia-testis's
    /// admit_fuzz.rs had exactly that fixture under a "submission-size-boundary" label, and
    /// three of its four cases were being decided by the string cap.</para>
    /// </summary>
    private static string DocumentOfExactSize(int total)
    {
        const int slots = 16;
        var skeleton = 2 + (slots * 6) + (slots - 1);   // {"a":"", ... ,"p":""}
        var payload = total - skeleton;
        var (basis, extra) = (payload / slots, payload % slots);

        var members = Enumerable.Range(0, slots).Select(i =>
            $"\"{(char)('a' + i)}\":\"" + new string('a', basis + (i == 0 ? extra : 0)) + "\"");

        return "{" + string.Join(",", members) + "}";
    }

    /// <summary>
    /// Utf8JsonReader.GetDouble() returns +/-Infinity for a syntactically valid literal
    /// whose magnitude overflows a double (e.g. 1e400), rather than throwing -- and
    /// nothing downstream checked finiteness, so JsonReader.Parse and
    /// CanonicalJson.Canonicalize (both independently frozen, independently
    /// conformance-tested entry points) would admit the value and then emit the invalid
    /// JSON literal "Infinity". Rejecting the whole non-finite class at ADMIT, the same
    /// place the noncharacter rule lives, means such a value can never become a JsonValue
    /// at all -- matching serde_json's default behavior, which rejects the literal at
    /// parse time with "number out of range". See conformance/admit-reject/non-finite-number/.
    /// </summary>
    [Theory]
    [InlineData("""{"a":1e400}""")]
    [InlineData("""{"a":-1e400}""")]
    public void RejectsNumberLiteralsThatOverflowToNonFinite(string json)
    {
        var slug = Parse(json).Match(_ => "ok", e => e.Type);
        Assert.Equal("curia/admit/non-finite-number", slug);
    }

    /// <summary>
    /// The other half of the same boundary, and the regression this test exists to guard:
    /// underflow to zero is IEEE 754 doing exactly what it should -- 1e-400 rounds to
    /// positive zero, an entirely ordinary finite double -- so it must still be ACCEPTED.
    /// Only overflow to +/-Infinity is a defect; conflating the two directions and
    /// rejecting both would itself be a bug.
    /// </summary>
    [Fact]
    public void AcceptsNumberLiteralThatUnderflowsToZero()
    {
        var value = Parse("""{"a":1e-400}""").Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        var obj = Assert.IsType<JsonValue.Object>(value);
        Assert.Equal(0.0, Assert.IsType<JsonValue.Number>(obj.Members[0].Value).Value);
    }

    [Theory]
    [MemberData(nameof(RejectionVectors))]
    public void ConformanceRejectionVectorsAreRejectedWithTheDeclaredSlug(string name, byte[] input, string slug)
    {
        _ = name;
        // Every admit-reject/ vector, R6.33's two (non-integer-number, unsafe-integer)
        // included, fed byte-for-byte unwrapped to the real ADMIT entry point (R6.11
        // addendum 2 / errata E6). R6.33 (rev. 2) makes the numeric bound ADMIT-generic --
        // "every number ADMIT parses, in any document, at any depth" -- so JsonReader.Parse
        // is exactly the entry point these two vectors' own "profile": "admit" designates,
        // and no envelope wrapper is needed or permitted to exercise them. Previously this
        // filter excluded R6.33 vectors with the comment "envelope-level numeric rules,
        // enforced in Task 6, not here", and EnvelopeParserTests satisfied them instead by
        // splicing the vector bytes into a synthetic {"envelope":...,"signature":"a..b"}
        // shell -- a different document than the one published, exercising a different code
        // path (errata E6, E4). That test is gone; this is the single place both vectors are
        // now exercised, as published.
        Assert.Equal(slug, Parse(input).Match(_ => "ok", e => e.Type));
    }

    public static TheoryData<string, byte[], string> RejectionVectors()
    {
        var data = new TheoryData<string, byte[], string>();
        foreach (var v in VectorLoader.Load("admit-reject"))
        {
            // R6.44: route by the declared profile, never by the directory a vector occupies.
            // Without this, a vector filed here under some other profile -- admit-accept most
            // obviously -- would be run as a rejection case and fail with a slug comparison
            // that says nothing about why, and a vector whose expect-reject was never
            // committed would surface as a NullReferenceException from the "!" that used to
            // stand here rather than as the corpus defect it is.
            if (v.Profile is not VectorProfile.Admit)
                throw new InvalidOperationException(
                    $"admit-reject/{v.Name} declares profile \"{VectorLoader.ProfileName(v.Profile)}\", not \"admit\"");

            data.Add(v.Name, v.Input, v.ExpectRejectSlug
                ?? throw new InvalidOperationException($"admit-reject/{v.Name} declares profile \"admit\" but has no expect-reject file"));
        }
        return data;
    }
}
