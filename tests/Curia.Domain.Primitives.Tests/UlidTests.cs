using CsCheck;
using Xunit;

namespace Curia.Domain.Primitives.Tests;

/// <summary>
/// <see cref="Ulid"/>'s own contract: encode/decode round-trips exactly, rejects everything the
/// ULID spec's 26-character Crockford Base32 encoding cannot represent, and never throws doing
/// it (CS-10). <see cref="UlidGeneratorTests"/> covers generation, monotonicity, and sort order.
/// </summary>
public sealed class UlidTests
{
    /// <summary>The maximum valid ULID (spec + task brief): leading character 7, everything else Z.</summary>
    private const string MaxUlidText = "7ZZZZZZZZZZZZZZZZZZZZZZZZZ";

    /// <summary>A known-valid 26-character ULID (the ULID spec README's own example), used as a base for single-character substitution tests.</summary>
    private const string ValidUlidText = "01ARZ3NDEKTSV4RRFFQ69G5FAV";

    private static Ulid Require(Result<Ulid> result) =>
        result.Match(v => v, e => throw new Xunit.Sdk.XunitException(e.Type));

    [Fact]
    public void ParseThenToStringRoundTripsAKnownValue()
    {
        const string text = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
        var ulid = Require(Ulid.Parse(text));
        Assert.Equal(text, ulid.ToString());
    }

    [Fact]
    public void MaximumValidUlidParsesAndRoundTrips()
    {
        var ulid = Require(Ulid.Parse(MaxUlidText));
        Assert.Equal(UInt128.MaxValue, ulid.Value);
        Assert.Equal(MaxUlidText, ulid.ToString());
    }

    [Fact]
    public void MinimumUlidParsesAndRoundTrips()
    {
        const string text = "00000000000000000000000000";
        var ulid = Require(Ulid.Parse(text));
        Assert.Equal(UInt128.Zero, ulid.Value);
        Assert.Equal(text, ulid.ToString());
    }

    [Fact]
    public void ParseIsCaseInsensitiveOverTheValidAlphabet()
    {
        var upper = Require(Ulid.Parse("01ARZ3NDEKTSV4RRFFQ69G5FAV"));
        var lower = Require(Ulid.Parse("01arz3ndektsv4rrffq69g5fav"));
        Assert.Equal(upper, lower);
    }

    [Theory]
    [InlineData("")]
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FA")] // 25 chars: one short
    [InlineData("01ARZ3NDEKTSV4RRFFQ69G5FAVX")] // 27 chars: one long
    public void ParseRejectsTheWrongLength(string text) =>
        Assert.False(Ulid.Parse(text).IsOk);

    [Theory]
    [InlineData('!')]
    [InlineData(' ')]
    [InlineData('_')]
    [InlineData('$')]
    public void ParseRejectsCharactersOutsideTheAlphabet(char offender)
    {
        var text = offender + ValidUlidText[1..];
        Assert.Equal(26, text.Length);
        Assert.False(Ulid.Parse(text).IsOk);
    }

    /// <summary>
    /// I, L, O, and U are valid Base32 symbols in the general sense but are deliberately excluded
    /// from Crockford's alphabet (visual confusion with 1/1/0, obscenity avoidance) -- distinct
    /// from an arbitrary non-alphabet character because a naive decoder might be tempted to
    /// tolerantly map them (I/L -> 1, O -> 0) instead of rejecting them outright.
    /// </summary>
    [Theory]
    [InlineData('I')]
    [InlineData('i')]
    [InlineData('L')]
    [InlineData('l')]
    [InlineData('O')]
    [InlineData('o')]
    [InlineData('U')]
    [InlineData('u')]
    public void ParseRejectsTheExcludedAmbiguousCharacters(char excluded)
    {
        var text = excluded + ValidUlidText[1..];
        Assert.Equal(26, text.Length);
        Assert.False(Ulid.Parse(text).IsOk);
    }

    [Theory]
    [InlineData("8ZZZZZZZZZZZZZZZZZZZZZZZZZ")] // leading digit 8: one past the maximum
    [InlineData("9ZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("AZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZ")] // the textually-largest 26-char string
    public void ParseRejectsAnythingAboveTheMaximumUlid(string text) =>
        Assert.False(Ulid.Parse(text).IsOk);

    [Fact]
    public void ParseFailureNamesTheOverflowSlug()
    {
        var result = Ulid.Parse("8ZZZZZZZZZZZZZZZZZZZZZZZZZ");
        var error = result.Match(_ => throw new Xunit.Sdk.XunitException("expected failure"), e => e);
        Assert.Equal("curia/domain-primitives/ulid/overflow", error.Type);
    }

    /// <summary>
    /// CsCheck property: every (timestamp, randomness) pair a real <see cref="UlidGenerator"/>
    /// could ever produce -- the full legal ranges, not just generator output -- round-trips
    /// through <see cref="Ulid.ToString"/> then <see cref="Ulid.Parse"/> to an equal value.
    /// </summary>
    [Fact]
    public void RoundTripHoldsForEveryValidTimestampAndRandomnessPair() =>
        Gen.Select(
            Gen.Long[0, Ulid.MaxTimestampMilliseconds],
            Gen.Byte.Array[10])
           .Select(t => (Timestamp: t.Item1, Randomness: BytesToUInt128(t.Item2)))
           .Sample(t =>
           {
               var original = Ulid.FromParts(t.Timestamp, t.Randomness);
               var reparsed = Require(Ulid.Parse(original.ToString()));
               return reparsed == original && reparsed.Value == original.Value;
           }, iter: 2000);

    /// <summary>Big-endian combine, matching <see cref="Ulid"/>'s bit layout and <c>UlidGenerator.DrawRandomness</c>.</summary>
    private static UInt128 BytesToUInt128(byte[] bytes)
    {
        UInt128 value = 0;
        foreach (var b in bytes)
            value = (value << 8) | b;
        return value;
    }
}
