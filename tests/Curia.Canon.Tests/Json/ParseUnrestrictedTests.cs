using System.Text;
using Curia.Canon.Json;
using Curia.Canon.Tests.Vectors;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Canon.Tests.Json;

/// <summary>
/// R6.41: <see cref="JsonReader.ParseUnrestricted"/> is a parse path from bytes to a
/// <see cref="JsonValue"/> that applies none of ADMIT's R6.39 policy caps (nesting depth,
/// member count, submission size, string length), does not reject a Unicode noncharacter
/// (R6.38's policy exemption), and does not enforce R6.33 (rev. 2)'s numeric bound -- while
/// still rejecting every well-definedness violation <see cref="JsonReader.Parse"/> rejects
/// (invalid UTF-8, a raw NUL byte, an unpaired UTF-16 surrogate, a raw duplicate object
/// member name, a number literal that overflows a double to a non-finite value), with the
/// identical slug either way.
///
/// Each "accepts" test below pairs a ParseUnrestricted assertion with the corresponding
/// <see cref="JsonReader.Parse"/> (ADMIT) rejection on the exact same bytes, so the split
/// itself -- not just one side of it -- is what is pinned: JsonReaderTests already covers
/// Parse's own rejections in isolation, and this file exists to show ParseUnrestricted
/// disagrees with Parse in precisely the cases R6.41/R6.38/R6.33 (rev. 2) say it should,
/// and agrees with it everywhere else.
/// </summary>
public sealed class ParseUnrestrictedTests
{
    private static Result<JsonValue> Unrestricted(string json) =>
        JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(json));

    private static Result<JsonValue> Admit(string json) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(json), AdmitLimits.Default);

    // -- R6.39's four size-shaped caps: accepted here, rejected under Parse --------------

    [Fact]
    public void AcceptsNestingOneContainerBeyondTheAdmitDepthCap()
    {
        var overCap = string.Concat(Enumerable.Repeat("""{"a":""", AdmitLimits.Default.MaxDepth + 1))
            + "1" + new string('}', AdmitLimits.Default.MaxDepth + 1);

        Assert.True(Unrestricted(overCap).IsOk);
        Assert.Equal("curia/admit/depth-exceeded", Admit(overCap).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void AcceptsMoreObjectMembersThanTheAdmitCap()
    {
        var json = "{" + string.Join(",",
            Enumerable.Range(0, AdmitLimits.Default.MaxMembersPerObject + 1).Select(i => $"\"k{i}\":0")) + "}";

        Assert.True(Unrestricted(json).IsOk);
        Assert.Equal("curia/admit/members-exceeded", Admit(json).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void AcceptsAStringLongerThanTheAdmitCap()
    {
        var json = "{\"a\":\"" + new string('x', AdmitLimits.Default.MaxStringBytes + 1) + "\"}";

        Assert.True(Unrestricted(json).IsOk);
        Assert.Equal("curia/admit/string-too-long", Admit(json).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void AcceptsASubmissionLargerThanTheAdmitByteCap()
    {
        // A wide array of zeros, not a long string or a wide object: arrays carry no
        // member-count cap at all (see JsonReader.ReadArray), so this isolates the byte-size
        // cap from the other three rather than tripping several caps at once.
        var json = "[" + string.Join(",", Enumerable.Repeat("0", 600_000)) + "]";
        Assert.True(Encoding.UTF8.GetByteCount(json) > AdmitLimits.Default.MaxBytes);

        Assert.True(Unrestricted(json).IsOk);
        Assert.Equal("curia/admit/size-exceeded", Admit(json).Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void AcceptsAUnicodeNoncharacter()
    {
        var json = $$"""{"a":"{{char.ConvertFromUtf32(0xFFFE)}}"}""";

        Assert.True(Unrestricted(json).IsOk);
        Assert.Equal("curia/admit/noncharacter", Admit(json).Match(_ => "ok", e => e.Type));
    }

    // -- R6.33 (rev. 2)'s numeric bound: accepted here, rejected under Parse -------------

    [Fact]
    public void AcceptsANonIntegerNumber()
    {
        // {"n":1.5} -- the published admit-reject/non-integer-number vector, fed unwrapped.
        Assert.True(Unrestricted("""{"n":1.5}""").IsOk);
        Assert.Equal("curia/admit/non-integer-number", Admit("""{"n":1.5}""").Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void AcceptsAnIntegerAboveTheSafeRange()
    {
        // {"n":9007199254740993} (2^53 + 1) -- the published admit-reject/unsafe-integer
        // vector, fed unwrapped.
        Assert.True(Unrestricted("""{"n":9007199254740993}""").IsOk);
        Assert.Equal("curia/admit/unsafe-integer", Admit("""{"n":9007199254740993}""").Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void RejectsTheFirstIntegerJustPastTheSafeRange()
    {
        // 2^53 itself: R6.33 (rev.)'s bound is the *symmetric* -(2^53-1)..2^53-1, not
        // -2^53..2^53 -- 2^53 is one past the boundary, not the boundary, and errata D5's
        // own consolidated-index entry says so explicitly ("2^53 rejected"). Admit must
        // reject it; ParseUnrestricted, carrying no R6.33 bound at all, must still accept
        // it. This is the third of the three numbers the task report compares before/after
        // across both paths (alongside 1.5 and 9007199254740993 above).
        Assert.True(Unrestricted("""{"n":9007199254740992}""").IsOk);
        Assert.Equal("curia/admit/unsafe-integer", Admit("""{"n":9007199254740992}""").Match(_ => "ok", e => e.Type));
    }

    [Fact]
    public void BothPathsAcceptTheSafeIntegerBoundaryItself()
    {
        // 2^53 - 1: R6.33's actual boundary (the largest value both paths must accept).
        // Both paths agree here -- not a divergence, so this pins that the split did not
        // accidentally widen ParseUnrestricted's agreement with Parse into disagreement on
        // a value R6.33 always accepted.
        Assert.True(Unrestricted("""{"n":9007199254740991}""").IsOk);
        Assert.True(Admit("""{"n":9007199254740991}""").IsOk);
    }

    [Fact]
    public void AcceptsAnOutOfRangeNumberNestedInsideAnEnvelopeShapedDocument()
    {
        // R6.33 (rev. 2) applies "in any document, at any depth" -- including a document
        // that happens to be envelope-shaped, since ParseUnrestricted has no notion of
        // envelope shape at all. Confirms the exemption is not accidentally narrower here
        // than the ADMIT-generic rule it mirrors the absence of.
        var json = """{"envelope":{"v":1,"x":1.5},"signature":"a..b"}""";
        Assert.True(Unrestricted(json).IsOk);
        Assert.Equal("curia/admit/non-integer-number", Admit(json).Match(_ => "ok", e => e.Type));
    }

    // -- Well-definedness: both paths reject, with the identical slug --------------------

    [Fact]
    public void RejectsInvalidUtf8() =>
        Assert.Equal("curia/admit/invalid-utf8",
            JsonReader.ParseUnrestricted([.. "{\"a\":\""u8, 0xFF, 0xFE, .. "\"}"u8]).Match(_ => "ok", e => e.Type));

    [Fact]
    public void RejectsARawNulByte() =>
        Assert.Equal("curia/admit/nul-byte",
            JsonReader.ParseUnrestricted([.. "{\"a\":\""u8, (byte)0, .. "\"}"u8]).Match(_ => "ok", e => e.Type));

    [Fact]
    public void RejectsAnUnpairedSurrogate() =>
        Assert.Equal("curia/admit/unpaired-surrogate", Unrestricted("""{"a":"\uD800"}""").Match(_ => "ok", e => e.Type));

    [Fact]
    public void RejectsARawDuplicateMemberName() =>
        Assert.Equal("curia/admit/duplicate-key", Unrestricted("""{"a":1,"a":2}""").Match(_ => "ok", e => e.Type));

    [Fact]
    public void RejectsANumberLiteralThatOverflowsToNonFinite() =>
        Assert.Equal("curia/admit/non-finite-number", Unrestricted("""{"a":1e400}""").Match(_ => "ok", e => e.Type));

    [Fact]
    public void AcceptsANumberLiteralThatUnderflowsToZero()
    {
        // Not a well-definedness violation on either path: 1e-400 rounds to positive zero,
        // an entirely ordinary finite double. Mirrors
        // JsonReaderTests.AcceptsNumberLiteralThatUnderflowsToZero for Parse.
        var value = Unrestricted("""{"a":1e-400}""").Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));
        var obj = Assert.IsType<JsonValue.Object>(value);
        Assert.Equal(0.0, Assert.IsType<JsonValue.Number>(obj.Members[0].Value).Value);
    }

    [Theory]
    [MemberData(nameof(WellDefinednessVectors))]
    public void RejectsEveryAdmitWellDefinednessVectorWithTheDeclaredSlug(string name, byte[] input, string slug)
    {
        _ = name;
        // The five admit-reject/ vectors that are well-definedness rules, not R6.39 policy
        // caps or the noncharacter exemption, fed unwrapped: ParseUnrestricted must
        // reject them exactly as Parse does. over-nested is deliberately excluded -- that
        // vector pins a policy cap (R6.39's depth cap), which ParseUnrestricted must accept
        // (see AcceptsNestingOneContainerBeyondTheAdmitDepthCap above), so it belongs on the
        // "accepts" side of this file, not this table.
        Assert.Equal(slug, JsonReader.ParseUnrestricted(input).Match(_ => "ok", e => e.Type));
    }

    public static TheoryData<string, byte[], string> WellDefinednessVectors()
    {
        string[] wellDefinedness =
        [
            "duplicate-keys", "invalid-utf8", "non-finite-number", "raw-nul-byte", "unpaired-surrogate",
        ];
        var data = new TheoryData<string, byte[], string>();
        foreach (var v in VectorLoader.Load("admit-reject").Where(v => wellDefinedness.Contains(v.Name)))
            data.Add(v.Name, v.Input, v.ExpectRejectSlug!);
        return data;
    }
}
