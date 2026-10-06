using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.AuthN.Jwt;
using Xunit;

namespace Curia.AuthN.Tests;

/// <summary>
/// R11.33 (errata G17) at the one function that turns a JWT's <c>iat</c>, <c>exp</c> and <c>nbf</c>
/// into a time: a number <see cref="DateTimeOffset.FromUnixTimeSeconds"/> cannot represent is a
/// malformed claim, never a throw. Both JWTs that reach it are signed by keys their caller holds, and
/// are parsed after their signatures verify, so any enrolled agent chooses the number.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NumericDateTests
{
    /// <summary>The first and last second <see cref="DateTimeOffset"/> holds are read, by both readers.</summary>
    [Theory]
    [InlineData(253402300799L)]
    [InlineData(-62135596800L)]
    public void R11_33_ANumericDateAtTheEdgeOfTheRangeIsRead(long seconds)
    {
        using var json = JsonDocument.Parse("{\"iat\":" + seconds + "}");
        var expected = DateTimeOffset.FromUnixTimeSeconds(seconds);

        var required = NumericDate.ReadRequired(json.RootElement, "iat");
        var optional = NumericDate.ReadOptional(json.RootElement, "iat");

        Assert.True(required.TryGetValue(out var read, out var requiredError), requiredError?.Detail);
        Assert.Equal(expected, read);
        Assert.True(optional.TryGetValue(out var readOptional, out var optionalError), optionalError?.Detail);
        Assert.Equal(expected, readOptional);
    }

    /// <summary>One second beyond either end, and far beyond, is malformed by both readers, and nothing throws.</summary>
    [Theory]
    [InlineData(253402300800L)]
    [InlineData(-62135596801L)]
    [InlineData(10000000000000L)]
    [InlineData(-100000000000L)]
    public void R11_33_ANumericDateOneBeyondTheRangeIsMalformedNotThrown(long seconds)
    {
        using var json = JsonDocument.Parse("{\"iat\":" + seconds + "}");

        var required = NumericDate.ReadRequired(json.RootElement, "iat");
        var optional = NumericDate.ReadOptional(json.RootElement, "iat");

        Assert.False(required.TryGetValue(out _, out var requiredError));
        Assert.Equal("curia/authn/malformed", requiredError!.Type);
        Assert.False(optional.TryGetValue(out _, out var optionalError));
        Assert.Equal("curia/authn/malformed", optionalError!.Type);
    }
}
