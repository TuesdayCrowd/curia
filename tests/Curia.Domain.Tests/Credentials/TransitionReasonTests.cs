using Curia.Domain.Credentials;
using Xunit;

namespace Curia.Domain.Tests.Credentials;

public sealed class TransitionReasonTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsEmptyOrWhitespace(string? value)
    {
        var result = TransitionReason.Create(value!);
        Assert.False(result.IsOk);
        result.Match(_ => throw new InvalidOperationException("expected failure"), e =>
        {
            Assert.Equal("curia/domain/empty-identifier", e.Type);
            return 0;
        });
    }

    [Fact]
    public void AcceptsNonEmptyText() =>
        Assert.Equal(
            "moderator flagged repeated policy violations",
            TestSupport.Require(TransitionReason.Create("moderator flagged repeated policy violations")).Value);

    [Fact]
    public void EqualValuesAreEqual()
    {
        var a = TestSupport.Require(TransitionReason.Create("same"));
        var b = TestSupport.Require(TransitionReason.Create("same"));
        Assert.Equal(a, b);
    }
}
