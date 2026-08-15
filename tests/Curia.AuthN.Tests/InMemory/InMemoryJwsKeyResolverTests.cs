using Curia.AuthN.Tests.Support;
using Xunit;

namespace Curia.AuthN.Tests.InMemory;

/// <summary>R11.4: <see cref="InMemoryJwsKeyResolver"/> exercised directly, not only through
/// <c>AccessTokenValidator</c>/<c>ClientAssertionValidator</c> -- mirrors
/// <c>Curia.Application.Tests.InMemoryEventStoreTests</c>' "the fake is a first-class
/// implementation" framing (CS-16).</summary>
public sealed class InMemoryJwsKeyResolverTests
{
    [Fact]
    public async Task ResolvingAConfiguredKidReturnsItsKey()
    {
        var key = TestKeys.Ed25519("k1");
        var resolver = new InMemoryJwsKeyResolver(key.Kid, key.PublicKey);

        var result = await resolver.ResolveAsync("k1", TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var resolved, out var error), error?.Detail);
        Assert.Equal(key.PublicKey, resolved);
    }

    [Fact]
    public async Task ResolvingAnUnconfiguredKidFails()
    {
        var key = TestKeys.Ed25519("k1");
        var resolver = new InMemoryJwsKeyResolver(key.Kid, key.PublicKey);

        var result = await resolver.ResolveAsync("k2", TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/kid-not-found", error!.Type);
        Assert.Equal("k2", error.Detail);
    }

    [Fact]
    public async Task SupportsMultipleSimultaneouslyValidKeysByKid()
    {
        var first = TestKeys.Ed25519("k1");
        var second = TestKeys.Es256("k2");
        var resolver = new InMemoryJwsKeyResolver(new Dictionary<string, Curia.Canon.Jws.PublicKeyMaterial>(StringComparer.Ordinal)
        {
            [first.Kid] = first.PublicKey,
            [second.Kid] = second.PublicKey,
        });
        var ct = TestContext.Current.CancellationToken;

        var resolvedFirst = await resolver.ResolveAsync("k1", ct);
        var resolvedSecond = await resolver.ResolveAsync("k2", ct);

        Assert.True(resolvedFirst.TryGetValue(out var firstKey, out _));
        Assert.True(resolvedSecond.TryGetValue(out var secondKey, out _));
        Assert.Equal("EdDSA", firstKey.Alg);
        Assert.Equal("ES256", secondKey.Alg);
    }
}
