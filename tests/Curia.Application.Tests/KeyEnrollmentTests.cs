using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// R4.31's decision and R4.32's comparison, as the pure rule both key-store adapters apply (errata
/// G14). Every held key and every request is built from its own array, so a comparison by
/// reference -- which is what <see cref="PublicKeyMaterial"/>'s generated equality does -- cannot
/// pass a test that expects two copies of one key to be the same key.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyEnrollmentTests
{
    private const string Agent = "https://agents.example/alice";
    private static readonly DateTimeOffset LastMonth = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    private static byte[] Bytes(byte seed) => [.. Enumerable.Range(0, 91).Select(i => (byte)(seed + i))];

    private static PublicKeyMaterial Key(string kid, byte seed, string alg = "ES256") => new(alg, kid, Bytes(seed));

    private static RegisteredKey Held(string kid, byte seed) => new(Key(kid, seed), LastMonth, null);

    private static Error Refusal(Result<RegisteredKey?> result) =>
        result.Match(v => throw new InvalidOperationException($"expected a refusal, got {v?.Key.Kid ?? "register"}"), e => e);

    [Fact]
    public void R4_31_AnIdentifierHoldingNoKeyRegistersIt()
    {
        var decided = KeyEnrollment.Decide(Agent, Key("alice-1", 1), []);

        Assert.True(decided.TryGetValue(out var held, out _));
        Assert.Null(held);
    }

    /// <summary>
    /// A re-announcement: the key the identifier holds, sent again from a fresh array. It is held,
    /// not registered, and it comes back with the window it was first given.
    /// </summary>
    [Fact]
    public void R4_31_TheSameKeyAgainIsHeldNotRegistered()
    {
        var existing = Held("alice-1", 1);

        var decided = KeyEnrollment.Decide(Agent, Key("alice-1", 1), [existing]);

        Assert.True(decided.TryGetValue(out var held, out _));
        Assert.Same(existing, held);
        Assert.Equal(LastMonth, held!.NotBefore);
    }

    /// <summary>
    /// The attack errata G14 records, as the rule sees it: an identifier holding one key is sent
    /// another under a new kid. Refused, naming the identifier.
    /// </summary>
    [Fact]
    public void R4_31_ASecondKidForAnEnrolledIdentifierIsRefused()
    {
        var refusal = Refusal(KeyEnrollment.Decide(Agent, Key("mallory-1", 7), [Held("alice-1", 1)]));

        Assert.Equal("curia/enroll/already-enrolled", refusal.Type);
        Assert.Contains(Agent, refusal.Detail, StringComparison.Ordinal);
    }

    /// <summary>The second attack: the identifier's own kid, with other bytes. One byte differs.</summary>
    [Fact]
    public void R4_32_TheSameKidWithOtherBytesIsRefused()
    {
        var other = Bytes(1);
        other[^1] ^= 0x01;

        var refusal = Refusal(KeyEnrollment.Decide(Agent, new PublicKeyMaterial("ES256", "alice-1", other), [Held("alice-1", 1)]));

        Assert.Equal("curia/keys/material-immutable", refusal.Type);
        Assert.Contains("alice-1", refusal.Detail, StringComparison.Ordinal);
    }

    /// <summary>The algorithm is part of the material: the same bytes declared under another algorithm are another key.</summary>
    [Fact]
    public void R4_32_TheSameKidAndBytesUnderAnotherAlgorithmIsRefused()
    {
        var refusal = Refusal(KeyEnrollment.Decide(Agent, Key("alice-1", 1, alg: "EdDSA"), [Held("alice-1", 1)]));

        Assert.Equal("curia/keys/material-immutable", refusal.Type);
    }

    /// <summary>
    /// An identifier holding two keys -- which a store written before this entry can -- is matched by
    /// kid, not by position. The match is the second key, so a rule that looked only at the first
    /// comes back wrong.
    /// </summary>
    [Fact]
    public void R4_31_AHeldKeyIsFoundByItsKidNotItsPosition()
    {
        var second = Held("alice-2", 2);

        var decided = KeyEnrollment.Decide(Agent, Key("alice-2", 2), [Held("alice-1", 1), second]);

        Assert.True(decided.TryGetValue(out var held, out _));
        Assert.Same(second, held);
    }

    /// <summary>The comparison, directly: equal content in two arrays is the same material; one bit apart is not.</summary>
    [Fact]
    public void R4_32_MaterialIsComparedByContent()
    {
        Assert.True(KeyEnrollment.SameMaterial(Key("k", 3), Key("k", 3)));
        Assert.False(KeyEnrollment.SameMaterial(Key("k", 3), Key("k", 4)));
    }
}
