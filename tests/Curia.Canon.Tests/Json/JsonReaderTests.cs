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
        Assert.Equal("curia/admit/malformed", slug);
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

    [Theory]
    [MemberData(nameof(RejectionVectors))]
    public void ConformanceRejectionVectorsAreRejectedWithTheDeclaredSlug(string name, byte[] input, string slug)
    {
        _ = name;
        // Vectors citing R6.33 are envelope-level numeric rules, enforced in Task 6, not here.
        Assert.Equal(slug, Parse(input).Match(_ => "ok", e => e.Type));
    }

    public static TheoryData<string, byte[], string> RejectionVectors()
    {
        var data = new TheoryData<string, byte[], string>();
        foreach (var v in VectorLoader.Load("admit-reject").Where(v => v.Requirement == "R6.15"))
            data.Add(v.Name, v.Input, v.ExpectRejectSlug!);
        return data;
    }
}
