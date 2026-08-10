using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Domain.Primitives.Tests;

public sealed class ResultTests
{
    private static readonly Error Boom = new("curia/test/boom", "Boom");

    [Fact]
    public void OkCarriesItsValue() =>
        Assert.Equal(42, Result<int>.Ok(42).Match(v => v, _ => -1));

    [Fact]
    public void FailCarriesItsError() =>
        Assert.Equal("curia/test/boom", Result<int>.Fail(Boom).Match(_ => "", e => e.Type));

    [Fact]
    public void MapTransformsOkAndSkipsFail()
    {
        Assert.Equal(84, Result<int>.Ok(42).Map(v => v * 2).Match(v => v, _ => -1));
        Assert.Equal(-1, Result<int>.Fail(Boom).Map(v => v * 2).Match(v => v, _ => -1));
    }

    [Fact]
    public void BindChainsOkAndShortCircuitsFail()
    {
        Assert.Equal(43, Result<int>.Ok(42).Bind(v => Result<int>.Ok(v + 1)).Match(v => v, _ => -1));
        Assert.Equal("curia/test/boom",
            Result<int>.Ok(42).Bind(_ => Result<int>.Fail(Boom)).Match(_ => "", e => e.Type));
    }

    [Fact]
    public void ToFailureRetypesAFailureAndRejectsASuccess()
    {
        Assert.Equal("curia/test/boom", Result<int>.Fail(Boom).ToFailure<string>().Match(_ => "", e => e.Type));
        Assert.Throws<InvalidOperationException>(() => Result<int>.Ok(1).ToFailure<string>());
    }

    [Fact]
    public void DigestRendersLowercaseHexAndPrefixedForm()
    {
        var digest = new EnvelopeDigest(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        Assert.Equal("deadbeef", digest.ToHex());
        Assert.Equal("sha256:deadbeef", digest.ToPrefixed());
    }

    [Fact]
    public void DigestEqualityComparesContentNotArrayIdentity()
    {
        var a = new EnvelopeDigest(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        var b = new EnvelopeDigest(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });
        var c = new EnvelopeDigest(new byte[] { 0x00, 0x01, 0x02, 0x03 });

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        Assert.NotEqual(a, c);
        Assert.False(a == c);

        Assert.Single(new HashSet<EnvelopeDigest> { a, b });
    }

    [Fact]
    public void UninitializedResultRejectsEveryOperation()
    {
        var uninitialized = default(Result<int>);

        Assert.Throws<InvalidOperationException>(() => _ = uninitialized.IsOk);
        Assert.Throws<InvalidOperationException>(() => uninitialized.Match(v => v, _ => -1));
        Assert.Throws<InvalidOperationException>(() => uninitialized.Map(v => v));
        Assert.Throws<InvalidOperationException>(() => uninitialized.Bind(v => Result<int>.Ok(v)));
        Assert.Throws<InvalidOperationException>(() => uninitialized.ToFailure<string>());
        Assert.Throws<InvalidOperationException>(() => uninitialized.TryGetValue(out _, out _));
    }
}
