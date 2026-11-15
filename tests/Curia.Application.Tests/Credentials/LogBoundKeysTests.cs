using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain.Acta;
using Curia.Domain.Keys;
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
        Require(await new EnrollAgent(events, TestVerifier.Es256, clock).RecordAsync(Typed.Agent(Alice), enrolled, CancellationToken.None).ConfigureAwait(false));
        var store = new HeldKeys(held);
        return (new LogBoundKeys(store, store, events, TestVerifier.Es256), events);
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
        var keys = new LogBoundKeys(store, store, events, TestVerifier.Es256);

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
    /// R4.40 and R6.64 (errata G68; this fact was R4.35 (rev.)'s "fail closed": a retirement that names no agent made the fold refuse, and so the identity's key neither honoured nor published):
    /// a retirement in the identity's own stream that names no agent is skipped (<c>statement-mismatch</c>) and inert, so the key it names stays honoured and published, and no request is refused for it.
    /// </summary>
    [Fact]
    public async Task R4_40_ARetirementThatNamesNoAgentIsSkippedAndTheKeyStaysHonoured()
    {
        var key = TestKeys.Es256("alice-1");
        var (keys, events) = await EnrolledAsync(key, (Alice, new RegisteredKey(Copy(key), Start, null)));
        await AppendLifecycleAsync(events, "agent.key-retired", "retirement-without-agent", new JsonValue.Object([new("kid", new JsonValue.String("alice-1")), new("prev", new JsonValue.Number(1))]));

        Assert.Equal(
            "resolve: resolved; live: resolved; set: listed 1",
            await ReadersAnswerAsync(keys));
    }

    /// <summary>
    /// R4.38 and R6.64 (errata G68; this fact was "fail closed": a second statement-less binding made the fold refuse, so the identity's key was neither honoured nor published while it stood):
    /// a second statement-less binding is a <c>broken-link</c> row the verifier skips, so the identity's key stays honoured and published and the second binding binds nothing.
    /// </summary>
    [Fact]
    public async Task R4_38_ABrokenLinkInTheStreamIsSkippedAndTheKeyStaysHonoured()
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
            "resolve: resolved; live: resolved; set: listed 1",
            await ReadersAnswerAsync(keys));
    }

    /// <summary>
    /// R5.22 and R4.40 (errata G68; this fact was "a status read whose lifecycle will not fold is the log unreadable", <c>curia/log/unreadable detail=curia/keys/statement-mismatch</c>): a row the
    /// verifier skips never makes a status read unreadable. The stream holds R4.38's broken link (a second statement-less binding); <c>alice-1</c> is still <see cref="KeyStatus.Active"/>, and a kid the
    /// log does not bind is still <see cref="KeyStatus.Unbound"/>. Arranged through the permissive lifecycle gate the other facts here use.
    /// </summary>
    [Fact]
    public async Task R5_22_AStatusReadIsNotRefusedByARowTheVerifierSkips()
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
            "Active; unbound: Unbound",
            $"{bound.Match(s => s.ToString(), e => $"{e.Type} detail={e.Detail}")}; unbound: {unbound.Match(s => s.ToString(), e => e.Type)}");
    }
    /// <summary>
    /// LRV46 (errata G68, ruling 83, F-M1): the parity probe. For each algorithm the Forum accepts, an enrolment through <c>EnrollAgent</c> yields a pair the fold honours (no row skipped) and a binding that carries the
    /// key (<c>Jwk</c> not null). If it were red, the key the enrolment publishes (<c>PublicJwk.Of</c>) and the key the fold reads (<c>ClosedJwk</c>) would disagree, and an honest enrolment's pair would be skipped.
    /// </summary>
    [Theory]
    [InlineData("ES256")]
    [InlineData("EdDSA")]
    public async Task LRV46_AnEnrolmentThroughEnrollAgentIsHonouredWithItsKeyForEachAlgorithm(string alg)
    {
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        var key = alg == "ES256"
            ? TestKeys.Es256("alice-lrv46")
            : new PublicKeyMaterial("EdDSA", "alice-lrv46", Convert.FromBase64String("HRzJlnTufZYYTZyCDBpyP5ldQ38JlbCeDOQHgIozgg8="));
        var verifier = ConformanceLog.Verifier();
        Require(await new EnrollAgent(events, verifier, clock).RecordAsync(Typed.Agent(Alice), key, CancellationToken.None).ConfigureAwait(true));

        var verified = verifier.OfLog(Require(await events.ReadAllAsync(CancellationToken.None).ConfigureAwait(true))).For(Typed.Agent(Alice));

        var skipped = string.Join(", ", verified.Skipped.Select(r => $"{r.LogIndex} {r.Predicate}"));
        var held = verified.Binding?.For(Typed.Kid("alice-lrv46"));
        Assert.Equal(
            $"{alg}: skipped [none]; binding carries the key: True",
            $"{alg}: skipped [{(skipped.Length == 0 ? "none" : skipped)}]; binding carries the key: {held?.Jwk is not null}");
    }

    /// <summary>The reads a resolver makes of the log: every <c>ReadForwardAsync</c> and <c>ReadByAggregateAsync</c>, counted (VBR6).</summary>
    private sealed class CountingReader(IEventReader inner) : IEventReader
    {
        internal int Reads { get; private set; }

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.ReadByAggregateAsync(aggregateId, cancellationToken);
        }

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
        {
            Reads++;
            return inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
        }
    }

    /// <summary>Alice enrolled with alice-1, alice-2 rotated in and alice-3 rotated in, the log key published; alice-2 is retired and alice-3 declared compromised, so each status is one kid's.</summary>
    private static async Task<(KeyActHarness Harness, LogBoundKeys Keys, CountingReader Reader, TestIdentity[] Identities)> StatusesAsync()
    {
        var harness = new KeyActHarness();
        var identities = Enumerable.Range(1, 3).Select(i => TestIdentity.New($"alice-{i}")).ToArray();
        await harness.EnrollAsync(KeyActHarness.Alice, identities[0]).ConfigureAwait(false);
        Require(await harness.RotateAsync(KeyActHarness.Alice, identities[0], identities[1]).ConfigureAwait(false));
        var thirdBoundAt = Require(await harness.RotateAsync(KeyActHarness.Alice, identities[0], identities[2]).ConfigureAwait(false)).LogIndex;
        Require(await harness.RetireAsync(KeyActHarness.Alice, identities[1]).ConfigureAwait(false));

        using var signer = new InMemoryLifecycleStatementSigner();
        var acta = Require(ActaLog.Fold(Require(await harness.Events.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))));
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(LogEntries.DeclarationKeysAggregate)),
            Require(AggregateVersion.From(acta.DeclarationKeys.Length)),
            [new DomainEvent(
                Require(EventId.Create("01K4CQ1V9P3R5T7W9Y1A3C5EKY")),
                Require(EventType.Create(LogEntries.DeclarationKeyType)),
                Require(ActorId.Create("operator:ops")),
                new JsonValue.Object(
                [
                    new(LogEntries.KidMember, new JsonValue.String(signer.Kid)),
                    new(LogEntries.JwkMember, signer.PublicJwk()),
                    new(LogEntries.ValidFromMember, new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(harness.Clock.GetUtcNow())))),
                ]))],
            CancellationToken.None).ConfigureAwait(false));
        Require(await new DeclareCompromise(harness.Events, TestVerifier.Es256, harness.Clock).DeclareAsync(
            Typed.Agent(KeyActHarness.Alice), Typed.Kid("alice-3"), thirdBoundAt, CompromiseReason.OwnerReported, signer, Require(ActorId.Create("operator:ops")), CancellationToken.None).ConfigureAwait(false));

        var reader = new CountingReader(harness.Events);
        var keys = new LogBoundKeys(new KeyActHarness.RegistryResolver(harness.Registry), harness.Registry, reader, new LifecycleVerifier(harness.Verifiers));
        return (harness, keys, reader, identities);
    }

    private static string Outcome<T>(Result<T> result) => result.Match(_ => "ok", e => e.Type);

    /// <summary>
    /// VBR6 (register item 282, errata G72, R5.25), the half that makes no read: <see cref="LogBoundKeys.ResolveStoredAsync"/> is the store's lookup and nothing else, so it reads the log <b>0</b> times,
    /// whether the key it returns is bound, retired, compromised or not held by the log at all. Implemented and quiet until the token endpoint calls it (the fix, FVBR6 and FVBR7).
    /// </summary>
    [Fact]
    public async Task R5_25_VBR6_ResolveStoredMakesNoReadOfTheLog()
    {
        var (_, keys, reader, _) = await StatusesAsync().ConfigureAwait(true);
        var outcomes = new List<string>();
        foreach (var kid in new[] { "alice-1", "alice-2", "alice-3" })
        {
            outcomes.Add($"{kid}: {Outcome(await keys.ResolveStoredAsync(Typed.Agent(KeyActHarness.Alice), Typed.Kid(kid), ServerTimestamp.At(KeyActHarness.Start.AddDays(1)), TestContext.Current.CancellationToken).ConfigureAwait(true))}");
        }

        Assert.Equal(
            "alice-1: ok; alice-2: ok; alice-3: ok; reads 0",
            $"{string.Join("; ", outcomes)}; reads {reader.Reads}");
    }

    /// <summary>
    /// VBR6, the half that reads: <see cref="LogBoundKeys.RequireBoundAsync"/> makes exactly <b>one</b> whole-log read and gives the binding, retired and compromised refusals their types and the live key its
    /// value; a key whose bytes are not the log's is <c>not-bound-by-the-log</c>. <see cref="LogBoundKeys.ResolveLiveAsync"/>, which composes the two, makes one read (R5.24).
    /// </summary>
    [Fact]
    public async Task R5_25_VBR6_RequireBoundMakesOneReadAndRefusesWhatTheLogDoesNotHold()
    {
        var (_, keys, reader, identities) = await StatusesAsync().ConfigureAwait(true);
        var ct = TestContext.Current.CancellationToken;
        var alice = Typed.Agent(KeyActHarness.Alice);
        var seen = new List<string>();
        async Task Record(string label, Func<Task<Result<LiveKey>>> call)
        {
            var before = reader.Reads;
            seen.Add($"{label}: {Outcome(await call().ConfigureAwait(true))} reads {reader.Reads - before}");
        }

        await Record("bound", () => keys.RequireBoundAsync(alice, Typed.Kid("alice-1"), KeyActHarness.Copy(identities[0].Material), ct)).ConfigureAwait(true);
        await Record("not bound (other bytes)", () => keys.RequireBoundAsync(alice, Typed.Kid("alice-1"), TestKeys.Es256("alice-1"), ct)).ConfigureAwait(true);
        await Record("retired", () => keys.RequireBoundAsync(alice, Typed.Kid("alice-2"), KeyActHarness.Copy(identities[1].Material), ct)).ConfigureAwait(true);
        await Record("compromised", () => keys.RequireBoundAsync(alice, Typed.Kid("alice-3"), KeyActHarness.Copy(identities[2].Material), ct)).ConfigureAwait(true);
        await Record("resolve live", () => keys.ResolveLiveAsync(alice, Typed.Kid("alice-1"), ServerTimestamp.At(KeyActHarness.Start.AddDays(1)), ct)).ConfigureAwait(true);

        Assert.Equal(
            "bound: ok reads 1; not bound (other bytes): curia/keys/not-bound-by-the-log reads 1; retired: curia/keys/retired reads 1; compromised: curia/keys/compromised reads 1; resolve live: ok reads 1",
            string.Join("; ", seen));
    }
}
