using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// What every <see cref="IAuthorKeyRegistry"/> promises about enrollment (R4.31, R4.32; errata G14),
/// run against the in-memory adapter here and against Postgres in <c>Curia.Infrastructure.Tests</c>
/// (R11.4). Each fact reads the store back after a refusal, because a refusal that had already
/// written something is the defect this contract exists for.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class AuthorKeyRegistryPortContractTests
{
    private const string Alice = "https://agents.example/alice";
    private const string Bob = "https://agents.example/bob";

    private static readonly DateTimeOffset LastMonth = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Today = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A fresh, empty store: no test sees another's keys.</summary>
    protected abstract IAuthorKeyRegistry CreateStore();

    /// <summary>A real P-256 key, so what is stored is the byte layout the ES256 verifier consumes.</summary>
    private static PublicKeyMaterial NewKey(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The same key from a fresh array, as a second request would carry it.</summary>
    private static PublicKeyMaterial Copy(PublicKeyMaterial key) => new(key.Alg, key.Kid, key.Public.ToArray());

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static Error Refusal<T>(Result<T> result) =>
        result.Match(v => throw new InvalidOperationException($"expected a refusal, got {v}"), e => e);

    private static void AssertHolds(IReadOnlyList<RegisteredKey> held, PublicKeyMaterial expected, DateTimeOffset notBefore)
    {
        var only = Assert.Single(held);
        Assert.Equal(expected.Kid, only.Key.Kid);
        Assert.Equal(expected.Alg, only.Key.Alg);
        Assert.Equal(expected.Public.ToArray(), only.Key.Public.ToArray());
        Assert.Equal(notBefore, only.NotBefore);
        Assert.Null(only.NotAfter);
    }

    [Fact]
    public async Task R4_31_AnIdentifierHoldingNoKeyIsEnrolledWithIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var key = NewKey("alice-1");

        var enrolled = Require(await store.EnrollAsync(Alice, key, LastMonth, ct));

        Assert.Equal(key.Kid, enrolled.Key.Kid);
        AssertHolds(await store.KeysForAsync(Alice, ct), key, LastMonth);
    }

    /// <summary>
    /// A re-announcement, a month later, from a fresh array: accepted, nothing written, and the
    /// window is the first one -- a later <c>NotBefore</c> would unverify a month of posts (R6.31).
    /// </summary>
    [Fact]
    public async Task R4_31_ReEnrollingTheSameKeyWritesNothingAndKeepsItsWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var key = NewKey("alice-1");

        Require(await store.EnrollAsync(Alice, key, LastMonth, ct));
        var again = Require(await store.EnrollAsync(Alice, Copy(key), Today, ct));

        Assert.Equal(LastMonth, again.NotBefore);
        AssertHolds(await store.KeysForAsync(Alice, ct), key, LastMonth);
    }

    /// <summary>
    /// The attack errata G14 records: an enrolled identifier sent another key under a new
    /// <c>kid</c>. Refused, and nothing is written -- the proof being that the refused <c>kid</c> is
    /// still free for an identifier of its own.
    /// </summary>
    [Fact]
    public async Task R4_31_ASecondKidForAnEnrolledIdentifierIsRefusedAndRegistersNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var alices = NewKey("alice-1");
        var mallorys = NewKey("mallory-1");

        Require(await store.EnrollAsync(Alice, alices, LastMonth, ct));

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await store.EnrollAsync(Alice, mallorys, Today, ct)).Type);
        AssertHolds(await store.KeysForAsync(Alice, ct), alices, LastMonth);

        Require(await store.EnrollAsync(Bob, mallorys, Today, ct));
        AssertHolds(await store.KeysForAsync(Bob, ct), mallorys, Today);
    }

    /// <summary>The second attack: the identifier's own <c>kid</c> with other bytes. Refused; the original bytes stand.</summary>
    [Fact]
    public async Task R4_32_ReEnrollingAKidWithOtherBytesIsRefusedAndTheOriginalStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var original = NewKey("alice-1");

        Require(await store.EnrollAsync(Alice, original, LastMonth, ct));

        Assert.Equal("curia/keys/material-immutable", Refusal(await store.EnrollAsync(Alice, NewKey("alice-1"), Today, ct)).Type);
        AssertHolds(await store.KeysForAsync(Alice, ct), original, LastMonth);
    }

    /// <summary>
    /// A <c>kid</c> another identifier holds is refused to a fresh one -- and the refusal writes
    /// nothing for either: the owner keeps its key, and the refused identifier holds none.
    /// </summary>
    [Fact]
    public async Task R4_31_AKidAnotherIdentifierHoldsIsRefusedAndNeitherIdentifierChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var alices = NewKey("shared-kid");

        Require(await store.EnrollAsync(Alice, alices, LastMonth, ct));

        Assert.Equal("curia/enroll/kid-already-registered", Refusal(await store.EnrollAsync(Bob, NewKey("shared-kid"), Today, ct)).Type);
        AssertHolds(await store.KeysForAsync(Alice, ct), alices, LastMonth);
        Assert.Empty(await store.KeysForAsync(Bob, ct));
    }

    /// <summary>
    /// The rule is per identifier: a store that refused every enrollment after its first would pass
    /// every refusal above, and fails here.
    /// </summary>
    [Fact]
    public async Task R4_31_EachIdentifierIsDecidedOnItsOwnKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var alices = NewKey("alice-1");
        var bobs = NewKey("bob-1");

        Require(await store.EnrollAsync(Alice, alices, LastMonth, ct));
        Require(await store.EnrollAsync(Bob, bobs, Today, ct));

        AssertHolds(await store.KeysForAsync(Alice, ct), alices, LastMonth);
        AssertHolds(await store.KeysForAsync(Bob, ct), bobs, Today);
    }

    /// <summary>
    /// R4.32 against the caller: bytes changed in the caller's array after enrollment do not change
    /// what is held. It can fail only in memory: on Postgres the bytes have crossed the wire before
    /// the caller can touch them, so this fact holds the in-memory adapter to what a database row has
    /// by construction.
    /// </summary>
    [Fact]
    public async Task R4_32_ACallerReusingItsArrayCannotChangeWhatIsHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var bytes = NewKey("alice-1").Public.ToArray();
        var original = bytes.ToArray();

        Require(await store.EnrollAsync(Alice, new PublicKeyMaterial("ES256", "alice-1", bytes), LastMonth, ct));
        bytes[^1] ^= 0xFF;

        Assert.Equal(original, Assert.Single(await store.KeysForAsync(Alice, ct)).Key.Public.ToArray());
    }
}

/// <summary>R11.4's in-memory adapter, held to the contract.</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovery needs the concrete class public; every [Fact] is inherited, so the analyzer's test-class heuristic does not see it.")]
public sealed class InMemoryAuthorKeyRegistryContractTests : AuthorKeyRegistryPortContractTests
{
    protected override IAuthorKeyRegistry CreateStore() => new InMemory.InMemoryAuthorKeyRegistry();
}
