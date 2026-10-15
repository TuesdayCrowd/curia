using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using Curia.Tests.Shared;

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
        public Task<Result<PublicKeyMaterial>> ResolveAsync(AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken cancellationToken = default)
        {
            var row = rows.FirstOrDefault(r => r.AgentId == agentId.Value && r.Key.Key.Kid == kid.Value);
            return Task.FromResult(row.Key is { } held && at.Value >= held.NotBefore
                ? Result<PublicKeyMaterial>.Ok(held.Key)
                : Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NotRegisteredToAgent(agentId.Value, kid.Value)));
        }

        public Task<Result<RegisteredKey>> EnrollAsync(AgentId agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("R4.35 registers nothing");

        public Task<Result<RegisteredKey>> AddAsync(AgentId agentId, PublicKeyMaterial key, DateTimeOffset validFrom, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("R4.35 registers nothing");

        public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(AgentId agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RegisteredKey>>([.. rows.Where(r => r.AgentId == agentId.Value).Select(r => r.Key)]);
    }

    private static async Task<(LogBoundKeys Keys, InMemoryEventStore Events)> EnrolledAsync(PublicKeyMaterial enrolled, params (string AgentId, RegisteredKey Key)[] held)
    {
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        Require(await new EnrollAgent(events, clock).RecordAsync(Typed.Agent(Alice), enrolled, CancellationToken.None).ConfigureAwait(false));
        var store = new HeldKeys(held);
        return (new LogBoundKeys(store, store, events), events);
    }

    private static PublicKeyMaterial Copy(PublicKeyMaterial key) => new(key.Alg, key.Kid, key.Public.ToArray());

    [Fact]
    public async Task R4_35_AKeyTheLogBindsResolves()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)));

        Assert.True((await keys.ResolveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), At, TestContext.Current.CancellationToken)).IsOk);
    }

    /// <summary>A second key the store holds for Alice, as errata G14's hole left them: the log binds no key under its kid.</summary>
    [Fact]
    public async Task R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindIsRefusedByName()
    {
        var key = TestKeys.Es256("alice-1");
        var hole = TestKeys.Es256("hole-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)), (Alice, new RegisteredKey(hole, Start, null)));

        var refused = await keys.ResolveAsync(Typed.Agent(Alice), Typed.Kid("hole-1"), At, TestContext.Current.CancellationToken);

        Assert.Equal("curia/keys/not-bound-by-the-log agent=https://agents.example/alice kid=hole-1", refused.Match(_ => "resolved", e => $"{e.Type} {e.Detail}"));
    }

    /// <summary>Other bytes under the bound kid, as a lost row's recovery registered them before R4.31 rev.: the log carries the key, and it is not this one.</summary>
    [Fact]
    public async Task R4_35_OtherBytesUnderTheBoundKidAreRefusedByName()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(TestKeys.Es256("alice-1"), Start, null)));

        var refused = await keys.ResolveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), At, TestContext.Current.CancellationToken);

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
        var events = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
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
            $"alice-1: {(await keys.ResolveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), At, TestContext.Current.CancellationToken)).Match(_ => "resolved", e => e.Type)}; "
            + $"hole-1: {(await keys.ResolveAsync(Typed.Agent(Alice), Typed.Kid("hole-1"), At, TestContext.Current.CancellationToken)).Match(_ => "resolved", e => e.Type)}");
    }

    /// <summary>The store's own refusal is passed on unchanged, so R5.20's refusal still does not say whose a kid is.</summary>
    [Fact]
    public async Task R4_35_TheStoresRefusalIsPassedOnUnchanged()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)));

        var refused = await keys.ResolveAsync(Typed.Agent(Alice), Typed.Kid("nowhere-1"), At, TestContext.Current.CancellationToken);

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

        var set = Require(await keys.KeySetAsync(Typed.Agent(Alice), TestContext.Current.CancellationToken));
        var bindingEvent = Require(await events.ReadByAggregateAsync(Require(AggregateId.Create(Alice)), TestContext.Current.CancellationToken))
            .Single(e => e.Event.Type.Value == AgentStandingProjector.KeyBoundType).Event.Id.Value;

        Assert.Equal(
            $"stored=2 bound=[alice-1@{bindingEvent}]",
            $"stored={set.Stored} bound=[{string.Join(",", set.Bound.Select(b => $"{b.Key.Key.Kid}@{b.Binding.EventId}"))}]");
    }

    /// <summary>
    /// The key set compares the key, not the <c>kid</c> alone: the store holds other bytes under the
    /// <c>kid</c> the log bound, as a lost row's recovery registered them before R4.31 rev., and the set
    /// lists nothing, though the store holds a row and the log binds its <c>kid</c>.
    /// </summary>
    [Fact]
    public async Task R4_35_TheKeySetOmitsOtherBytesUnderTheBoundKid()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, _) = await EnrolledAsync(key, (Alice, new RegisteredKey(TestKeys.Es256("alice-1"), Start, null)));

        var set = Require(await keys.KeySetAsync(Typed.Agent(Alice), TestContext.Current.CancellationToken));

        Assert.Equal(
            "stored=1 bound=[]",
            $"stored={set.Stored} bound=[{string.Join(",", set.Bound.Select(b => $"{b.Key.Key.Kid}@{b.Binding.EventId}"))}]");
    }

    private static async Task AppendLifecycleAsync(InMemoryEventStore events, string type, string eventId, JsonValue payload)
    {
        var aggregate = Require(AggregateId.Create(Alice));
        var stream = Require(await events.ReadByAggregateAsync(aggregate, CancellationToken.None).ConfigureAwait(false));
        Require(await events.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(stream.Count)),
            [new DomainEvent(Require(EventId.Create(eventId)), Require(EventType.Create(type)), Require(ActorId.Create(Alice)), payload)],
            CancellationToken.None).ConfigureAwait(false));
    }

    /// <summary>What each reader of the lifecycle answers for <c>alice-1</c>: resolve, resolve live, and the key set.</summary>
    private static async Task<string> ReadersAnswerAsync(LogBoundKeys keys)
    {
        var ct = TestContext.Current.CancellationToken;
        return $"resolve: {(await keys.ResolveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), At, ct).ConfigureAwait(false)).Match(_ => "resolved", e => e.Type)}; "
            + $"live: {(await keys.ResolveLiveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), At, ct).ConfigureAwait(false)).Match(_ => "resolved", e => e.Type)}; "
            + $"set: {(await keys.KeySetAsync(Typed.Agent(Alice), ct).ConfigureAwait(false)).Match(set => "listed " + set.Bound.Count, e => e.Type)}";
    }

    /// <summary>
    /// R4.35 (rev.), fail closed: a retirement in the identity's own stream that names no agent is
    /// refused by the fold, never skipped, so the key it retires is neither honoured nor published. The
    /// resolvers stop earlier, where the log binds nothing while such an entry is there; the key set
    /// reaches the fold and answers its refusal.
    /// </summary>
    [Fact]
    public async Task R4_35_ARetirementThatNamesNoAgentHonoursAndPublishesNothing()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, events) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)));
        await AppendLifecycleAsync(events, "agent.key-retired", "retirement-without-agent", new JsonValue.Object([new("kid", new JsonValue.String("alice-1")), new("prev", new JsonValue.Number(1))]));

        Assert.Equal(
            "resolve: curia/keys/not-bound-by-the-log; live: curia/keys/not-bound-by-the-log; set: curia/keys/statement-mismatch",
            await ReadersAnswerAsync(keys));
    }

    /// <summary>
    /// R4.38, fail closed: a second statement-less binding is a broken link the fold refuses, so the
    /// identity's key, which the log does bind, is neither honoured nor published while it stands.
    /// </summary>
    [Fact]
    public async Task R4_38_ABrokenLinkInTheStreamHonoursAndPublishesNothing()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, events) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)));
        await AppendLifecycleAsync(events, AgentStandingProjector.KeyBoundType, "second-binding", new JsonValue.Object(
        [
            new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
            new(AgentStandingProjector.KeyIdField, new JsonValue.String("alice-2")),
            new(AgentStandingProjector.JwkField, Require(PublicJwk.Of(TestKeys.Es256("alice-2")))),
        ]));

        Assert.Equal(
            "resolve: curia/keys/statement-mismatch; live: curia/keys/statement-mismatch; set: curia/keys/statement-mismatch",
            await ReadersAnswerAsync(keys));
    }

    /// <summary>
    /// R5.22: a status read whose lifecycle will not fold is the log unreadable, a server fault, never the
    /// fold's own refusal, which the token path would serve as a 401 telling the agent to mint again. The
    /// stream is R4.38's broken link (a second statement-less binding), so the log does bind
    /// <c>alice-1</c> and it is the fold that fails; a kid the log does not bind is still
    /// <see cref="KeyStatus.Unbound"/>, which the fold never decides. Arranged through the permissive
    /// lifecycle gate the other facts here use.
    /// </summary>
    [Fact]
    public async Task R5_22_AStatusReadWhoseLifecycleWillNotFoldIsTheLogUnreadable()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, events) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)));
        await AppendLifecycleAsync(events, AgentStandingProjector.KeyBoundType, "second-binding", new JsonValue.Object(
        [
            new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
            new(AgentStandingProjector.KeyIdField, new JsonValue.String("alice-2")),
            new(AgentStandingProjector.JwkField, Require(PublicJwk.Of(TestKeys.Es256("alice-2")))),
        ]));
        var ct = TestContext.Current.CancellationToken;

        var bound = await keys.StatusAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), ct);
        var unbound = await keys.StatusAsync(Typed.Agent(Alice), Typed.Kid("never-bound"), ct);

        Assert.Equal(
            "curia/log/unreadable detail=curia/keys/statement-mismatch; unbound: Unbound",
            $"{bound.Match(s => s.ToString(), e => $"{e.Type} detail={e.Detail}")}; unbound: {unbound.Match(s => s.ToString(), e => e.Type)}");
    }
}
