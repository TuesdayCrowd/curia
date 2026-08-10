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

    [Fact]
    public void RejectsExcessiveNestingBeforeExhaustingTheStack()
    {
        var deep = string.Concat(Enumerable.Repeat("""{"a":""", 33)) + "1" + new string('}', 33);
        Assert.Equal("curia/admit/depth-exceeded", Parse(deep).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsOversizeInputBeforeParsing()
    {
        var big = new byte[AdmitLimits.Default.MaxBytes + 1];
        Assert.Equal("curia/admit/size-exceeded", Parse(big).Match(_ => "ok", e => e.Type));
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
