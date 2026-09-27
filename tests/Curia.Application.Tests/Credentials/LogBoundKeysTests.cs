using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// R4.35 (errata G16) at the rule: a key the store resolves is honoured only when the log binds it
/// to the same identity -- the key itself since R4.34, the <c>kid</c> alone before it -- and the
/// key set lists only such keys. The store here is a double that answers whatever it is told to
/// hold, as a store written before errata G14 can; the log is the in-memory event store, written by
/// the same <see cref="EnrollAgent"/> the Forum runs.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LogBoundKeysTests
{
    private const string Alice = "https://agents.example/alice";

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly ServerTimestamp At = ServerTimestamp.At(Start.AddHours(1));

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    /// <summary>
    /// A key store that holds exactly what it is given, for anyone, and resolves by agent and
    /// <c>kid</c> from each key's <c>NotBefore</c>; no row here has a <c>NotAfter</c>, so it closes no
    /// window. It never checks the log: that is the rule under test.
    /// </summary>
    private sealed class HeldKeys(params (string AgentId, RegisteredKey Key)[] rows) : IAuthorKeyResolver, IAuthorKeyRegistry
    {
        public Task<Result<PublicKeyMaterial>> ResolveAsync(string agentId, string kid, ServerTimestamp at, CancellationToken cancellationToken = default)
        {
            var row = rows.FirstOrDefault(r => r.AgentId == agentId && r.Key.Key.Kid == kid);
            return Task.FromResult(row.Key is { } held && at.Value >= held.NotBefore
                ? Result<PublicKeyMaterial>.Ok(held.Key)
                : Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NotRegisteredToAgent(agentId, kid)));
        }

        public Task<Result<RegisteredKey>> EnrollAsync(string agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("R4.35 registers nothing");

        public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RegisteredKey>>([.. rows.Where(r => r.AgentId == agentId).Select(r => r.Key)]);
    }

    private static async Task<(LogBoundKeys Keys, InMemoryEventStore Events)> EnrolledAsync(PublicKeyMaterial enrolled, params (string AgentId, RegisteredKey Key)[] held)
    {
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        Require(await new EnrollAgent(events, clock).RecordAsync(Alice, enrolled, CancellationToken.None).ConfigureAwait(false));
        var store = new HeldKeys(held);
        return (new LogBoundKeys(store, store, events), events);
    }

    private static PublicKeyMaterial Copy(PublicKeyMaterial key) => new(key.Alg, key.Kid, key.Public.ToArray());

    [Fact]
    public async Task R4_35_AKeyTheLogBindsResolves()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)));

        Assert.True((await keys.ResolveAsync(Alice, "alice-1", At, TestContext.Current.CancellationToken)).IsOk);
    }

    /// <summary>A second key the store holds for Alice, as errata G14's hole left them: the log binds no key under its kid.</summary>
    [Fact]
    public async Task R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindIsRefusedByName()
    {
        var key = TestKeys.Es256("alice-1");
        var hole = TestKeys.Es256("hole-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)), (Alice, new RegisteredKey(hole, Start, null)));

        var refused = await keys.ResolveAsync(Alice, "hole-1", At, TestContext.Current.CancellationToken);

        Assert.Equal("curia/keys/not-bound-by-the-log agent=https://agents.example/alice kid=hole-1", refused.Match(_ => "resolved", e => $"{e.Type} {e.Detail}"));
    }

    /// <summary>Other bytes under the bound kid, as a lost row's recovery registered them before R4.31 rev.: the log carries the key, and it is not this one.</summary>
    [Fact]
    public async Task R4_35_OtherBytesUnderTheBoundKidAreRefusedByName()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(TestKeys.Es256("alice-1"), Start, null)));

        var refused = await keys.ResolveAsync(Alice, "alice-1", At, TestContext.Current.CancellationToken);

        Assert.Equal("curia/keys/not-bound-by-the-log", refused.Match(_ => "resolved", e => e.Type));
    }

    /// <summary>
    /// An identity enrolled before R4.34: its <c>agent.enrolled</c> names the kid, no entry carries the
    /// key, and the key resolves on the kid alone; a second kid the store holds does not.
    /// </summary>
    [Fact]
    public async Task R4_35_AnIdentityEnrolledBeforeR4_34IsBoundByItsKidAlone()
    {
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        Require(await events.AppendAsync(
            Require(AggregateId.Create(Alice)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create("enrolled-before-key-binding")),
                Require(EventType.Create(AgentStandingProjector.EnrolledType)),
                Require(ActorId.Create(Alice)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String("alice-1")),
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted")),
                ]))],
            TestContext.Current.CancellationToken));
        var store = new HeldKeys((Alice, new RegisteredKey(TestKeys.Es256("alice-1"), Start, null)), (Alice, new RegisteredKey(TestKeys.Es256("hole-1"), Start, null)));
        var keys = new LogBoundKeys(store, store, events);

        Assert.Equal(
            "alice-1: resolved; hole-1: curia/keys/not-bound-by-the-log",
            $"alice-1: {(await keys.ResolveAsync(Alice, "alice-1", At, TestContext.Current.CancellationToken)).Match(_ => "resolved", e => e.Type)}; "
            + $"hole-1: {(await keys.ResolveAsync(Alice, "hole-1", At, TestContext.Current.CancellationToken)).Match(_ => "resolved", e => e.Type)}");
    }

    /// <summary>The store's own refusal is passed on unchanged, so R5.20's refusal still does not say whose a kid is.</summary>
    [Fact]
    public async Task R4_35_TheStoresRefusalIsPassedOnUnchanged()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)));

        var refused = await keys.ResolveAsync(Alice, "nowhere-1", At, TestContext.Current.CancellationToken);

        Assert.Equal("curia/keys/not-registered-to-agent", refused.Match(_ => "resolved", e => e.Type));
    }

    /// <summary>
    /// The key set lists what the log binds and nothing else, each with its binding's event, and
    /// still says how many rows the store holds, so an identity whose every row is unbound is told
    /// apart from one the store has never heard of.
    /// </summary>
    [Fact]
    public async Task R4_35_TheKeySetListsOnlyTheKeysTheLogBinds()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, events) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)), (Alice, new RegisteredKey(TestKeys.Es256("hole-1"), Start, null)));

        var set = Require(await keys.KeySetAsync(Alice, TestContext.Current.CancellationToken));
        var bindingEvent = Require(await events.ReadByAggregateAsync(Require(AggregateId.Create(Alice)), TestContext.Current.CancellationToken))
            .Single(e => e.Event.Type.Value == AgentStandingProjector.KeyBoundType).Event.Id.Value;

        Assert.Equal(
            $"stored=2 bound=[alice-1@{bindingEvent}]",
            $"stored={set.Stored} bound=[{string.Join(",", set.Bound.Select(b => $"{b.Key.Key.Kid}@{b.Binding.EventId}"))}]");
    }
}
