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
}
