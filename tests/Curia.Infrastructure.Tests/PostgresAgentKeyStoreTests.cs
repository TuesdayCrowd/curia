using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// <see cref="PostgresAgentKeyStore"/> against a real Postgres.
///
/// <para>Two properties dominate. The first is durability: a key registered through one adapter
/// resolves through another over the same database, which is what makes an archive's authorship
/// claims survive a restart. The second is R6.31 (errata A12) -- validity evaluated at
/// <c>server_ts</c>, never at "now" -- because a key revoked today must still verify a post the
/// Forum received last month, and a store that answered "is this valid now" would quietly
/// invalidate the archive one rotation at a time.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresAgentKeyStoreTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresAgentKeyStoreTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static readonly DateTimeOffset LastMonth = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Today = new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private PostgresAgentKeyStore StoreOn(string schema) =>
        new(_fixture.AppRoleDataSource, schema);

    /// <summary>A real P-256 key, so what is stored and read back is the byte layout the ES256
    /// verifier actually consumes rather than a placeholder that would round-trip anything.</summary>
    private static PublicKeyMaterial NewKey(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static Error Refusal<T>(Result<T> result) =>
        result.Match(v => throw new InvalidOperationException($"Expected a failure, got {v}"), e => e);

    [Fact]
    public async Task ARegisteredKeyResolvesForItsAgentAtAnInstantInsideItsWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var key = NewKey("kid-inside-window");

        Require(await store.RegisterAsync("agent://forum/alice", key, LastMonth, cancellationToken: ct));

        var resolved = Require(await store.ResolveAsync(
            "agent://forum/alice", "kid-inside-window", ServerTimestamp.At(Today), ct));

        Assert.Equal(key.Alg, resolved.Alg);
        Assert.Equal(key.Kid, resolved.Kid);
        Assert.Equal(key.Public.ToArray(), resolved.Public.ToArray());
    }

    /// <summary>
    /// The durability claim, stated as the test the in-memory store could not pass: a second
    /// adapter instance -- a restarted process, or a second pod -- resolves what the first
    /// registered. <c>InMemoryAuthorKeyResolver</c> lost every enrollment on restart, which made
    /// every post ever made unverifiable.
    /// </summary>
    [Fact]
    public async Task AKeyRegisteredByOneInstanceResolvesThroughAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);
        var key = NewKey("kid-across-instances");

        Require(await StoreOn(schema).RegisterAsync("agent://forum/alice", key, LastMonth, cancellationToken: ct));

        var resolved = Require(await StoreOn(schema).ResolveAsync(
            "agent://forum/alice", "kid-across-instances", ServerTimestamp.At(Today), ct));

        Assert.Equal(key.Public.ToArray(), resolved.Public.ToArray());
    }

    /// <summary>
    /// R6.31 / errata A12, in the form that matters: <b>a key revoked today still verifies a post
    /// received last month.</b> The instant comes from the caller, and the same store answers
    /// differently for two different instants -- which is only possible because validity is
    /// evaluated at <c>server_ts</c> rather than at the resolver's own idea of now. There is no
    /// <c>TimeProvider</c> in the adapter at all, so there is nowhere for a "now" to come from.
    /// </summary>
    [Fact]
    public async Task AKeyRevokedTodayStillResolvesForAPostReceivedLastMonth()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var key = NewKey("kid-revoked-today");

        // Registered last month, revoked today: NotAfter is exclusive, so `Today` itself is
        // already outside the window.
        Require(await store.RegisterAsync("agent://forum/alice", key, LastMonth, Today, ct));

        var duringTheWindow = await store.ResolveAsync(
            "agent://forum/alice", "kid-revoked-today", ServerTimestamp.At(LastMonth.AddDays(3)), ct);
        Assert.Equal(key.Kid, Require(duringTheWindow).Kid);

        var afterRevocation = await store.ResolveAsync(
            "agent://forum/alice", "kid-revoked-today", ServerTimestamp.At(Today), ct);
        Assert.Equal("curia/keys/no-longer-valid", Refusal(afterRevocation).Type);
    }

    /// <summary>The other edge of the window, with its own distinct failure slug: a signature made
    /// before the key was registered does not verify, and an operator can tell that outcome apart
    /// from a revocation.</summary>
    [Fact]
    public async Task AKeyDoesNotResolveAtAnInstantBeforeItWasRegistered()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));

        Require(await store.RegisterAsync(
            "agent://forum/alice", NewKey("kid-not-yet-valid"), Today, cancellationToken: ct));

        var tooEarly = await store.ResolveAsync(
            "agent://forum/alice", "kid-not-yet-valid", ServerTimestamp.At(LastMonth), ct);

        Assert.Equal("curia/keys/not-yet-valid", Refusal(tooEarly).Type);
    }

    /// <summary>
    /// The constraint the task brief singles out, now enforced by a UNIQUE (PRIMARY KEY) index
    /// rather than by an application-side scan two concurrent enrollments could both pass: the
    /// store holds a <c>kid</c> for one identifier (R4.31, R4.32).
    /// </summary>
    [Fact]
    public async Task AKidAlreadyRegisteredToADifferentAgentIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));

        Require(await store.RegisterAsync("agent://forum/alice", NewKey("kid-contested"), LastMonth, cancellationToken: ct));

        var stolen = await store.RegisterAsync("agent://forum/mallory", NewKey("kid-contested"), Today, cancellationToken: ct);

        Assert.Equal("curia/enroll/kid-already-registered", Refusal(stolen).Type);

        // And the refusal left the original registration alone: the resolver still answers with
        // Alice's key, which is the property the refusal exists to protect.
        var stillAlices = Require(await store.ResolveAsync("agent://forum/alice", "kid-contested", ServerTimestamp.At(Today), ct));
        Assert.Equal("kid-contested", stillAlices.Kid);
    }

    /// <summary>
    /// Re-enrolling is permitted -- it is a repeat enrollment, not a collision -- and it does not
    /// move <c>valid_from</c> forward. The in-memory predecessor overwrote it, which retroactively
    /// invalidated every signature the key had already made: R6.31 evaluates validity at each
    /// post's <c>server_ts</c>, so a <c>valid_from</c> dragged to today asserts that last month's
    /// posts were signed by a key that did not yet exist.
    /// </summary>
    [Fact]
    public async Task ARepeatRegistrationDoesNotMoveTheValidityWindowForward()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var key = NewKey("kid-re-enrolled");

        Require(await store.RegisterAsync("agent://forum/alice", key, LastMonth, cancellationToken: ct));
        var again = Require(await store.RegisterAsync("agent://forum/alice", key, Today, cancellationToken: ct));

        Assert.Equal(LastMonth, again.NotBefore);

        var lastMonthsPost = await store.ResolveAsync(
            "agent://forum/alice", "kid-re-enrolled", ServerTimestamp.At(LastMonth.AddDays(3)), ct);
        Assert.Equal(key.Kid, Require(lastMonthsPost).Kid);
    }

    /// <summary>
    /// R4.32 (errata G14) at the history primitive: the same <c>kid</c> registered again with other
    /// bytes is refused by name, and the key that resolves is still the original. Before G14 this
    /// call replaced the bytes and returned success -- which is how an enrollment request could make
    /// every post a key had signed stop verifying.
    /// </summary>
    [Fact]
    public async Task AKidRegisteredAgainWithOtherBytesIsRefusedAndTheOriginalStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var original = NewKey("kid-material");

        Require(await store.RegisterAsync("agent://forum/alice", original, LastMonth, cancellationToken: ct));
        var replaced = await store.RegisterAsync("agent://forum/alice", NewKey("kid-material"), Today, cancellationToken: ct);

        Assert.Equal("curia/keys/material-immutable", Refusal(replaced).Type);
        var resolved = Require(await store.ResolveAsync("agent://forum/alice", "kid-material", ServerTimestamp.At(Today), ct));
        Assert.Equal(original.Public.ToArray(), resolved.Public.ToArray());
    }

    /// <summary>
    /// R4.32 counts the algorithm as material: the same <c>kid</c> and bytes registered again under
    /// another algorithm are refused by name, and the key that resolves still declares the original.
    /// A verifier dispatches on the declared algorithm, so relabelling a key changes what its
    /// signatures mean as surely as replacing its bytes. Nothing else fences the statement's
    /// algorithm clause: the grant cannot, since the statement no longer sets the column.
    /// </summary>
    [Fact]
    public async Task AKidRegisteredAgainUnderAnotherAlgorithmIsRefusedAndTheOriginalStands()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var original = NewKey("kid-algorithm");

        Require(await store.RegisterAsync("agent://forum/alice", original, LastMonth, cancellationToken: ct));
        var relabelled = await store.RegisterAsync(
            "agent://forum/alice", new PublicKeyMaterial("EdDSA", original.Kid, original.Public.ToArray()), Today, cancellationToken: ct);

        Assert.Equal("curia/keys/material-immutable", Refusal(relabelled).Type);
        var resolved = Require(await store.ResolveAsync("agent://forum/alice", "kid-algorithm", ServerTimestamp.At(Today), ct));
        Assert.Equal("ES256", resolved.Alg);
    }

    /// <summary>
    /// R4.31 at the adapter's read: a key whose window has closed is still a key its identifier
    /// holds, so enrolling that identifier under a new <c>kid</c> is refused by name. The rule says so
    /// already (<c>KeyEnrollmentTests.R4_31_AKeyWhoseWindowHasClosedIsStillHeld</c>); this fences the
    /// SELECT that feeds it, where an added <c>valid_until IS NULL</c> would hand every retired
    /// identity to whoever enrolled it next -- G14's attack, reopened the day revocation writes
    /// <c>valid_until</c>.
    /// </summary>
    [Fact]
    public async Task AKeyWhoseWindowHasClosedStillCountsAsHeldWhenEnrollingANewKid()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));

        Require(await store.RegisterAsync("agent://forum/alice", NewKey("kid-closed"), LastMonth, Today, ct));
        var enrolled = await store.EnrollAsync("agent://forum/alice", NewKey("kid-after-closing"), Today.AddDays(1), ct);

        Assert.Equal("curia/enroll/already-enrolled", Refusal(enrolled).Type);
    }

    /// <summary>
    /// The ownership clause, fenced on its own: another identity presenting alice's exact key -- which
    /// is public, since the JWKS serves it -- is refused by name and moves nothing. The material checks
    /// cannot refuse it, because the material matches; without the ownership clause the statement
    /// would hand back alice's row as mallory's success, and <c>LEAST</c> would let mallory close
    /// alice's window (a revocation) or open it earlier (a backdating).
    /// </summary>
    [Fact]
    public async Task AnotherAgentPresentingTheExactKeyIsRefusedAndMovesNoWindow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var alices = NewKey("kid-exact-copy");

        Require(await store.RegisterAsync("agent://forum/alice", alices, LastMonth, cancellationToken: ct));
        var copied = await store.RegisterAsync("agent://forum/mallory", alices, LastMonth, Today, ct);

        Assert.Equal("curia/enroll/kid-already-registered", Refusal(copied).Type);
        var stillOpen = Require(await store.ResolveAsync("agent://forum/alice", "kid-exact-copy", ServerTimestamp.At(Today.AddDays(1)), ct));
        Assert.Equal(alices.Kid, stillOpen.Kid);
    }

    /// <summary>
    /// A repeat registration cannot un-revoke a key. The in-memory predecessor assigned
    /// <c>NotAfter</c> outright, so calling the enrollment endpoint again -- which needs no owner
    /// authentication yet -- quietly restored a revoked key to service. R4.19 requires revocation
    /// to take effect within 60 seconds, not to hold until the next enrollment.
    /// </summary>
    [Fact]
    public async Task ARepeatRegistrationCannotUndoARevocation()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var key = NewKey("kid-revoked-then-re-enrolled");

        Require(await store.RegisterAsync("agent://forum/alice", key, LastMonth, Today, ct));

        // Re-registered with an open-ended window, which is what an enrollment request carries.
        var again = Require(await store.RegisterAsync("agent://forum/alice", key, LastMonth, null, ct));

        Assert.Equal(Today, again.NotAfter);
        Assert.Equal(
            "curia/keys/no-longer-valid",
            Refusal(await store.ResolveAsync("agent://forum/alice", key.Kid, ServerTimestamp.At(Today.AddDays(1)), ct)).Type);
    }

    /// <summary>
    /// The other direction still works: a registration <i>can</i> record a revocation on a key
    /// whose window was open, and an earlier revocation beats a later one. Without this the test
    /// above would be satisfied by a store that ignored <c>NotAfter</c> entirely.
    /// </summary>
    [Fact]
    public async Task ARegistrationCanRecordARevocationAndAnEarlierOneWins()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        var key = NewKey("kid-revoked-later");

        Require(await store.RegisterAsync("agent://forum/alice", key, LastMonth, null, ct));
        Assert.Equal(Today, Require(await store.RegisterAsync("agent://forum/alice", key, LastMonth, Today, ct)).NotAfter);

        var later = Require(await store.RegisterAsync("agent://forum/alice", key, LastMonth, Today.AddDays(30), ct));
        Assert.Equal(Today, later.NotAfter);
    }

    /// <summary>
    /// R5.20 (errata G15), at the adapter: <c>Curia.AuthN</c>'s port asks for a key by agent and
    /// <c>kid</c> together, and the store answers only for the agent the key is registered to, and
    /// only inside its window. The fact this replaces held the store to resolving by <c>kid</c>
    /// alone, the lookup through which any enrolled key obtained any enrolled identity's token.
    /// </summary>
    [Fact]
    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "The fact is about Curia.AuthN's port, so the store is held as that port: the " +
            "calls go through the interface R5.20 gave an agent parameter, and would not compile if it lost one.")]
    public async Task R5_20_TheAssertionPortResolvesAKeyOnlyForTheAgentItIsRegisteredTo()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));
        Curia.AuthN.Ports.IAgentKeyResolver assertionPort = store;
        var key = NewKey("kid-alices-alone");
        var insideWindow = ServerTimestamp.At(LastMonth.AddDays(1));

        Require(await store.RegisterAsync("agent://forum/alice", key, LastMonth, Today, ct));

        Assert.Equal(key.Kid, Require(await assertionPort.ResolveAsync("agent://forum/alice", key.Kid, insideWindow, ct)).Kid);
        Assert.Equal(
            "curia/keys/not-registered-to-agent",
            Refusal(await assertionPort.ResolveAsync("agent://forum/bob", key.Kid, insideWindow, ct)).Type);
        Assert.Equal(
            "curia/keys/no-longer-valid",
            Refusal(await assertionPort.ResolveAsync("agent://forum/alice", key.Kid, ServerTimestamp.At(Today), ct)).Type);
    }

    /// <summary>
    /// A <c>kid</c> that belongs to someone else is a resolution failure for the agent who asked,
    /// with its own slug -- not a signature failure that looks identical to a corrupted body.
    /// Those are different incidents and an operator should be able to tell them apart.
    /// </summary>
    [Fact]
    public async Task AKidRegisteredToAnotherAgentDoesNotResolveForThisOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));

        Require(await store.RegisterAsync("agent://forum/alice", NewKey("kid-alices"), LastMonth, cancellationToken: ct));

        var asBob = await store.ResolveAsync("agent://forum/bob", "kid-alices", ServerTimestamp.At(Today), ct);

        Assert.Equal("curia/keys/not-registered-to-agent", Refusal(asBob).Type);
    }

    /// <summary>
    /// R4.19 / R4.16 rev.: the served JWKS carries the full history with validity intervals,
    /// including keys that are expired or revoked. A JWKS listing only currently-valid keys would
    /// make every older post unverifiable by anyone but the Forum, which is Phase 1's exit
    /// criterion -- an independent verifier confirming authorship offline -- quietly lost.
    /// </summary>
    [Fact]
    public async Task KeysForAnAgentIncludeExpiredAndRevokedOnes()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));

        Require(await store.RegisterAsync("agent://forum/alice", NewKey("kid-retired"), LastMonth, Today, ct));
        Require(await store.RegisterAsync("agent://forum/alice", NewKey("kid-current"), Today, cancellationToken: ct));
        Require(await store.RegisterAsync("agent://forum/bob", NewKey("kid-bobs"), Today, cancellationToken: ct));

        var alices = await store.KeysForAsync("agent://forum/alice", ct);

        Assert.Equal(2, alices.Count);
        Assert.Contains(alices, k => k.Key.Kid == "kid-retired" && k.NotAfter == Today);
        Assert.Contains(alices, k => k.Key.Kid == "kid-current" && k.NotAfter is null);
        Assert.DoesNotContain(alices, k => k.Key.Kid == "kid-bobs");
    }

    [Fact]
    public async Task AnAgentWithNoRegisteredKeysHasAnEmptyKeySet()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = StoreOn(await _fixture.CreateIsolatedOperationalSchemaAsync(ct));

        Assert.Empty(await store.KeysForAsync("agent://forum/nobody", ct));
    }
}
