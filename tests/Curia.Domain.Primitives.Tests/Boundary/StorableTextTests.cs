using System.Diagnostics.CodeAnalysis;
using CsCheck;
using Curia.Domain.Primitives.Boundary;
using Xunit;

namespace Curia.Domain.Primitives.Tests.Boundary;

/// <summary>R11.34 (errata G18): the one rule a caller's string meets before a port takes it, and the types built on it.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class StorableTextTests
{
    private const int Cap = 8;

    /// <summary>A three-byte character in UTF-8 (U+4E2D).</summary>
    private static readonly string ThreeByte = ((char)0x4E2D).ToString();

    private static string? TypeOf<T>(Result<T> result) => result.TryGetValue(out _, out var error) ? null : error!.Type;

    private static Error Refusal<T>(Result<T> result) =>
        result.Match<Error>(_ => throw new Xunit.Sdk.XunitException("expected a refusal"), e => e);

    // Indexed rather than passed as values: a theory argument holding a lone surrogate does not survive the
    // runner's serialization intact.
    private static readonly (string? Value, string Expected)[] Cases =
    [
        (null, StorableText.AbsentType),
        ("", StorableText.BlankType),
        (" ", StorableText.BlankType),
        ("a\0b", StorableText.NulType),
        ((char)0xD800 + "", StorableText.UnpairedSurrogateType),
        ((char)0xDC00 + "x", StorableText.UnpairedSurrogateType),
        (new string('a', Cap + 1), StorableText.TooLongType),
        // The first check that fails wins: a blank value over the cap is blank, a NUL over the cap is a NUL,
        // and an unpaired surrogate over the cap is an unpaired surrogate.
        (new string(' ', Cap + 1), StorableText.BlankType),
        ("\0" + new string('a', Cap + 1), StorableText.NulType),
        ("\0" + (char)0xD800, StorableText.NulType),
        ((char)0xD800 + new string('a', Cap + 1), StorableText.UnpairedSurrogateType),
    ];

    public static TheoryData<int> InOrder => [.. Enumerable.Range(0, Cases.Length)];

    [Theory]
    [MemberData(nameof(InOrder))]
    public void R11_34_TheRuleRefusesInItsOrder(int index) =>
        Assert.Equal(Cases[index].Expected, TypeOf(StorableText.Check(Cases[index].Value, "f", Cap, names: true)));

    [Fact]
    public void R11_34_APairedSurrogateIsWellFormed()
    {
        var grin = char.ConvertFromUtf32(0x1F600);

        Assert.Equal(grin, StorableText.Check(grin, "f", 4, names: true).Match(v => v, e => e.Type));
        var refusal = Refusal(StorableText.Check(grin, "f", 3, names: true));
        Assert.Equal(StorableText.TooLongType, refusal.Type);
        Assert.Equal("field=f bytes=4: at most 3 UTF-8 bytes", refusal.Detail);
    }

    [Fact]
    public void R11_34_TheCapIsUtf8BytesAndInclusive()
    {
        Assert.True(StorableText.Check(new string('a', Cap), "f", Cap, names: true).IsOk);
        Assert.Equal("field=f bytes=9: at most 8 UTF-8 bytes", Refusal(StorableText.Check(new string('a', Cap + 1), "f", Cap, names: true)).Detail);

        var nine = ThreeByte + ThreeByte + ThreeByte;
        Assert.True(StorableText.Check(nine, "f", 9, names: true).IsOk);
        var refusal = Refusal(StorableText.Check(nine + "a", "f", 9, names: true));
        Assert.Equal(StorableText.TooLongType, refusal.Type);
        Assert.Equal("field=f bytes=10: at most 9 UTF-8 bytes", refusal.Detail);
    }

    [Fact]
    public void R11_34_ANonBlankRuleAcceptsWhiteSpace()
    {
        Assert.Equal(" ", StorableText.Check(" ", "f", Cap, names: false).Match(v => v, e => e.Type));
        Assert.Equal("", StorableText.Check("", "f", Cap, names: false).Match(v => v, e => e.Type));
        Assert.Equal(StorableText.AbsentType, TypeOf(StorableText.Check(null, "f", Cap, names: false)));
    }

    [Fact]
    public void R11_34_ARefusalNeverEchoesTheValue() =>
        Gen.Select(Gen.Char[char.MinValue, char.MaxValue].Array[4, 64], Gen.Bool).Sample((chars, names) =>
        {
            var value = new string(chars);
            // A cap of zero refuses every value: each reaches the check it fails, too-long at the latest.
            var refusal = Refusal(StorableText.Check(value, "f", 0, names));
            Assert.DoesNotContain(value, refusal.Detail ?? "", StringComparison.Ordinal);
            Assert.DoesNotContain(value, refusal.Title, StringComparison.Ordinal);
        });

    [Fact]
    public void R11_34_EachTypeTakesItsCapAndItsField()
    {
        AssertCap(AgentId.Parse, 1_024, "agent_id", names: true);
        AssertCap(Kid.Parse, 1_024, "kid", names: true);
        AssertCap(Jti.Parse, 256, "jti", names: true);
        AssertCap(DpopNonce.Parse, 4_096, "nonce", names: true);
        AssertCap(BoardName.Parse, 262_144, "board", names: false);
        AssertCap(ParentReference.Parse, 262_144, "parent", names: false);
    }

    [Fact]
    public void R11_34_ATypeKeepsTheValueItWasGiven()
    {
        var at = "https://agents.example/" + ThreeByte;
        Assert.Equal(at, AgentId.Parse(at).Match(v => v.Value, e => e.Type));
        Assert.Equal(at, AgentId.Parse(at).Match(v => v.ToString(), e => e.Type));
        Assert.Equal("field=author", Refusal(AgentId.Parse(null, "author")).Detail);
    }

    [Fact]
    public void R11_34_EachTypeNamesItsDefaultField()
    {
        Assert.Equal("field=agent_id", Refusal(AgentId.Parse(null)).Detail);
        Assert.Equal("field=kid", Refusal(Kid.Parse(null)).Detail);
        Assert.Equal("field=jti", Refusal(Jti.Parse(null)).Detail);
        Assert.Equal("field=nonce", Refusal(DpopNonce.Parse(null)).Detail);
        Assert.Equal("field=board", Refusal(BoardName.Parse(null)).Detail);
        Assert.Equal("field=parent", Refusal(ParentReference.Parse(null)).Detail);
        Assert.Equal("field=post_id", Refusal(PostId.Parse(null)).Detail);
    }

    private const string ValidPostId = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    [Fact]
    public void R11_34_APostIdIsTheCanonicalUlid()
    {
        Assert.Equal(ValidPostId, PostId.Parse(ValidPostId).Match(v => v.Value, e => e.Type));
        Assert.Equal(26, PostId.MaxUtf8Bytes);
        Assert.Equal(StorableText.BlankType, TypeOf(PostId.Parse(" ")));
        Assert.Equal(StorableText.AbsentType, TypeOf(PostId.Parse(null)));
        Assert.Equal("field=post_id", Refusal(PostId.Parse(null)).Detail);
    }

    [Fact]
    public void R11_34_APostIdRefusesLowercase()
    {
        var refusal = Refusal(PostId.Parse("01arz3ndektsv4rrffq69g5fav", "postId"));
        Assert.Equal("curia/input/not-a-post-id", refusal.Type);
        Assert.Equal("field=postId", refusal.Detail);
    }

    [Fact]
    public void R11_34_APostIdRefusesTwentyFiveOrTwentySevenCharacters()
    {
        Assert.Equal("curia/input/not-a-post-id", TypeOf(PostId.Parse(ValidPostId[..25])));
        Assert.Equal(StorableText.TooLongType, TypeOf(PostId.Parse(ValidPostId + "A")));
    }

    [Theory]
    [InlineData('I')]
    [InlineData('L')]
    [InlineData('O')]
    [InlineData('U')]
    public void R11_34_APostIdRefusesALetterCrockfordExcludes(char letter) =>
        Assert.Equal("curia/input/not-a-post-id", TypeOf(PostId.Parse(ValidPostId[..25] + letter)));

    private static void AssertCap<T>(Func<string?, string, Result<T>> parse, int cap, string field, bool names)
    {
        Assert.True(parse(new string('a', cap), field).IsOk, $"{typeof(T).Name}: at the cap");
        Assert.True(parse(new string('a', cap - 2) + (char)0xE9, field).IsOk, $"{typeof(T).Name}: two-byte character at the cap");
        var refusal = Refusal(parse(new string('a', cap + 1), field));
        Assert.Equal(StorableText.TooLongType, refusal.Type);
        Assert.Equal($"field={field} bytes={cap + 1}: at most {cap} UTF-8 bytes", refusal.Detail);
        Assert.Equal(StorableText.TooLongType, TypeOf(parse(new string('a', cap - 1) + (char)0xE9, field)));
        Assert.Equal(StorableText.NulType, TypeOf(parse("a\0", field)));
        Assert.Equal(names ? StorableText.BlankType : null, TypeOf(parse(" ", field)));

        Assert.Equal("field=" + field, Refusal(parse(null, field)).Detail);
    }
}
