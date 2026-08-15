using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Domain.Tests;

/// <summary>R4.15 (only Ed25519/P-256) and errata D4/R4.28 (their RFC 8037/RFC 7518 wire shapes).</summary>
public sealed class AgentPublicKeyTests
{
    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException(e.Type));

    private static KeyId Kid(string value) => Require(KeyId.Create(value));

    private static byte[] Bytes(int length, byte fill = 0x01)
    {
        var buffer = new byte[length];
        Array.Fill(buffer, fill);
        return buffer;
    }

    [Fact]
    public void Ed25519AcceptsExactly32ByteX()
    {
        var key = Require(AgentPublicKey.CreateEd25519(Kid("k1"), Bytes(32)));
        Assert.Equal("k1", key.Kid.Value);
        Assert.IsType<AgentPublicKey.Ed25519Key>(key);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void Ed25519RejectsAnyOtherXLength(int length) =>
        Assert.False(AgentPublicKey.CreateEd25519(Kid("k1"), Bytes(length)).IsOk);

    [Fact]
    public void P256AcceptsExactly32ByteXAndY()
    {
        var key = Require(AgentPublicKey.CreateP256(Kid("k1"), Bytes(32), Bytes(32, 0x02)));
        Assert.IsType<AgentPublicKey.P256Key>(key);
    }

    [Fact]
    public void P256RejectsAWrongLengthX() =>
        Assert.False(AgentPublicKey.CreateP256(Kid("k1"), Bytes(31), Bytes(32)).IsOk);

    [Fact]
    public void P256RejectsAWrongLengthY() =>
        Assert.False(AgentPublicKey.CreateP256(Kid("k1"), Bytes(32), Bytes(31)).IsOk);

    [Fact]
    public void FromJwkShapeBuildsEd25519ForOkpEd25519() =>
        Assert.True(AgentPublicKey.FromJwkShape(Kid("k1"), "OKP", "Ed25519", Bytes(32), null).IsOk);

    [Fact]
    public void FromJwkShapeBuildsP256ForEcP256() =>
        Assert.True(AgentPublicKey.FromJwkShape(Kid("k1"), "EC", "P-256", Bytes(32), Bytes(32)).IsOk);

    [Fact]
    public void FromJwkShapeRejectsEcP256MissingY() =>
        Assert.False(AgentPublicKey.FromJwkShape(Kid("k1"), "EC", "P-256", Bytes(32), null).IsOk);

    [Theory]
    [InlineData("RSA", "")]
    [InlineData("oct", "")]
    [InlineData("OKP", "X25519")]
    [InlineData("EC", "P-384")]
    [InlineData("EC", "Ed25519")]
    [InlineData("OKP", "P-256")]
    public void FromJwkShapeRejectsEveryUnsupportedKtyCrvCombination(string kty, string crv) =>
        Assert.False(AgentPublicKey.FromJwkShape(Kid("k1"), kty, crv, Bytes(32), Bytes(32)).IsOk);

    [Fact]
    public void MatchDispatchesToTheEd25519Branch()
    {
        var key = Require(AgentPublicKey.CreateEd25519(Kid("k1"), Bytes(32)));

        var branch = key.Match(_ => "ed25519", _ => "p256");

        Assert.Equal("ed25519", branch);
    }

    [Fact]
    public void MatchDispatchesToTheP256Branch()
    {
        var key = Require(AgentPublicKey.CreateP256(Kid("k1"), Bytes(32), Bytes(32)));

        var branch = key.Match(_ => "ed25519", _ => "p256");

        Assert.Equal("p256", branch);
    }
}
