using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// R4.35 (rev.), R4.31 (rev. 2) and R4.38 at the rule (errata G21): a key the log retired is honoured for
/// nothing, the lifecycle is folded from the identity's stream on the merits, and a statement is read from
/// its exact bytes. Entries are appended by hand, behind the permissive gate, until Task 1.4 writes them.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyLifecycleTests
{
    private const string Alice = "https://agents.example/alice";

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly ServerTimestamp At = ServerTimestamp.At(Start.AddDays(2));

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} {e.Detail}"));

    private static Error Refusal<T>(Result<T> result) =>
        result.Match(v => throw new InvalidOperationException($"expected a refusal, got {v}"), e => e);

    private static JsonValue.Object Obj(params (string Name, JsonValue Value)[] members) =>
        new([.. members.Select(m => new KeyValuePair<string, JsonValue>(m.Name, m.Value))]);

    private static JsonValue.String Str(string value) => new(value);

    private static JsonValue.Number Num(long value) => new(value);

    private static string Canonical(JsonValue.Object statement) =>
        Encoding.UTF8.GetString(Require(CanonicalJson.CanonicalizeWithNfc(statement)).Span);

    private static string RetirementStatement(string agent, string kid, long prev) =>
        Canonical(Obj(("agent_id", Str(agent)), ("kid", Str(kid)), ("prev", Num(prev)), ("typ", Str(KeyStatements.RetirementStatementTyp))));

    private static string RotationStatement(string agent, string authorizedBy, PublicKeyMaterial key, long prev) =>
        Canonical(Obj(
            ("agent_id", Str(agent)),
            ("authorized_by", Str(authorizedBy)),
            ("jwk", Require(PublicJwk.Of(key))),
            ("kid", Str(key.Kid)),
            ("prev", Num(prev)),
            ("typ", Str(KeyStatements.RotationStatementTyp))));

    private static JsonValue.Object RetirementPayload(string agent, string kid, long prev) =>
        Obj(
            ("agent_id", Str(agent)),
            ("authorization", Str("signature")),
            ("kid", Str(kid)),
            ("prev", Num(prev)),
            ("statement", Str(RetirementStatement(agent, kid, prev))));

    internal static JsonValue.Object RotationPayload(string agent, string authorizedBy, PublicKeyMaterial key, long prev) =>
        Obj(
            ("agent_id", Str(agent)),
            ("authorization", Str("signature")),
            ("authorized_by", Str(authorizedBy)),
            ("jwk", Require(PublicJwk.Of(key))),
            ("kid", Str(key.Kid)),
            ("possession", Str("signature")),
            ("prev", Num(prev)),
            ("statement", Str(RotationStatement(agent, authorizedBy, key, prev))));

    private static PublicKeyMaterial Copy(PublicKeyMaterial key) => new(key.Alg, key.Kid, key.Public.ToArray());

    private static async Task<IReadOnlyList<AppendedEvent>> StreamAsync(InMemoryEventStore events, string agent) =>
        Require(await events.ReadByAggregateAsync(Require(AggregateId.Create(agent)), TestContext.Current.CancellationToken).ConfigureAwait(false));

    /// <summary>Appends one lifecycle entry to <paramref name="agent"/>'s stream, behind the permissive gate, as nothing writes one yet.</summary>
    private static async Task AppendAsync(InMemoryEventStore events, string agent, string type, string eventId, JsonValue payload)
    {
        var stream = await StreamAsync(events, agent).ConfigureAwait(false);
        Require(await events.AppendAsync(
            Require(AggregateId.Create(agent)),
            Require(AggregateVersion.From(stream.Count)),
            [new DomainEvent(Require(EventId.Create(eventId)), Require(EventType.Create(type)), Require(ActorId.Create(agent)), payload)],
            TestContext.Current.CancellationToken).ConfigureAwait(false));
    }

    private static async Task<(InMemoryEventStore Events, ManualTimeProvider Clock)> EnrolledAsync(string agent, PublicKeyMaterial first)
    {
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        Require(await new EnrollAgent(events, clock).RecordAsync(Typed.Agent(agent), first, TestContext.Current.CancellationToken).ConfigureAwait(false));
        return (events, clock);
    }

    /// <summary>The store's resolving half over the in-memory registry: the key under the agent and kid, from its <c>NotBefore</c>.</summary>
    private sealed class RegistryResolver(IAuthorKeyRegistry registry) : IAuthorKeyResolver
    {
        public async Task<Result<PublicKeyMaterial>> ResolveAsync(AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken cancellationToken = default)
        {
            var held = (await registry.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(k => k.Key.Kid == kid.Value);
            return held is not null && at.Value >= held.NotBefore
                ? Result<PublicKeyMaterial>.Ok(held.Key)
                : Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NotRegisteredToAgent(agentId.Value, kid.Value));
        }
    }

    private static LogBoundKeys KeysOver(InMemoryEventStore events, InMemoryAuthorKeyRegistry registry) => new(new RegistryResolver(registry), registry, events);

    private static async Task<InMemoryAuthorKeyRegistry> RegistryHoldingAsync(string agent, params PublicKeyMaterial[] keys)
    {
        var registry = new InMemoryAuthorKeyRegistry();
        foreach (var key in keys)
            Require(await registry.AddAsync(Typed.Agent(agent), Copy(key), Start, TestContext.Current.CancellationToken).ConfigureAwait(false));
        return registry;
    }

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return Path.Combine([dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above the test binary"), .. parts]);
    }

    /// <summary>The rotation vector's entry payload, its statement text and its agent.</summary>
    private static (JsonValue.Object Payload, string Statement, string Agent) RotationVector()
    {
        var input = Require(JsonReader.ParseUnrestricted(File.ReadAllBytes(RepoFile("conformance", "acta", "key-rotated-entry", "input.json"))));
        var payload = (JsonValue.Object)input.Member("payload")!;
        return (payload, ((JsonValue.String)payload.Member("statement")!).Value, ((JsonValue.String)payload.Member("agent_id")!).Value);
    }

    private static async Task<InMemoryEventStore> EnrolledWithVectorRotationAsync(JsonValue payload)
    {
        var (_, _, agent) = RotationVector();
        var (events, _) = await EnrolledAsync(agent, TestKeys.Es256("conformance-ed25519-minimal")).ConfigureAwait(false);
        await AppendAsync(events, agent, AgentStandingProjector.KeyBoundType, "01K4CQ1V9P3R5T7W9Y1A3C5E8H", payload).ConfigureAwait(false);
        return events;
    }

    private static JsonValue.Object EnrolledPayload(string agent, string? kid) =>
        Obj([("agent_id", Str(agent)), .. kid is null ? Array.Empty<(string, JsonValue)>() : [("kid", Str(kid))], ("reason", Str("Enrollment accepted"))]);

    /// <summary>The binding an enrolment appends: <c>{agent_id, jwk, kid}</c> and no statement.</summary>
    private static JsonValue.Object BindingPayload(string agent, PublicKeyMaterial key) =>
        Obj(("agent_id", Str(agent)), ("jwk", Require(PublicJwk.Of(key))), ("kid", Str(key.Kid)));

    private static async Task<InMemoryEventStore> StreamOfAsync(string agent, params (string Type, JsonValue Payload)[] entries)
    {
        var events = new InMemoryEventStore(new ManualTimeProvider(Start), new PermissiveLifecycleAppendGate());
        for (var i = 0; i < entries.Length; i++)
            await AppendAsync(events, agent, entries[i].Type, $"entry-{i}", entries[i].Payload).ConfigureAwait(false);
        return events;
    }

    private static async Task<string> FoldAsync(InMemoryEventStore events, string agent)
    {
        var folded = KeyLifecycle.Of(await StreamAsync(events, agent).ConfigureAwait(false), Typed.Agent(agent));
        return folded.Match(
            l => "ok: " + string.Join(", ", l.Entries.Select(e => $"{e.Type}@{e.Kid}")),
            e => $"{e.Type} {e.Detail}");
    }

    private const string EntryMismatch = "curia/keys/statement-mismatch field=entry";

    private const string Enrolled = AgentStandingProjector.EnrolledType;

    private const string KeyBound = AgentStandingProjector.KeyBoundType;

    [Fact]
    public async Task R4_38_AStatementLessBindingAfterARotationIsABrokenLink()
    {
        var (first, second) = (TestKeys.Es256("alice-1"), TestKeys.Es256("alice-2"));
        var events = await StreamOfAsync(Alice,
            (Enrolled, EnrolledPayload(Alice, "alice-1")), (KeyBound, BindingPayload(Alice, first)),
            (KeyBound, RotationPayload(Alice, "alice-1", second, 1)), (KeyBound, BindingPayload(Alice, first)));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_AStatementLessBindingAfterARetirementIsABrokenLink()
    {
        var first = TestKeys.Es256("alice-1");
        var events = await StreamOfAsync(Alice,
            (Enrolled, EnrolledPayload(Alice, "alice-1")),
            (LifecycleAppendGuard.KeyRetiredType, RetirementPayload(Alice, "alice-1", 1)),
            (KeyBound, BindingPayload(Alice, first)));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_AStatementLessBindingAfterOnlyARetirementIsABrokenLink()
    {
        // One entry before the binding, so only the type test on that entry (an enrolment) can refuse it.
        var first = TestKeys.Es256("alice-1");
        var events = await StreamOfAsync(Alice,
            (LifecycleAppendGuard.KeyRetiredType, RetirementPayload(Alice, "alice-1", 1)),
            (KeyBound, BindingPayload(Alice, first)));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_ASecondStatementLessBindingUnderTheSameKidIsABrokenLink()
    {
        var first = TestKeys.Es256("alice-1");
        var events = await StreamOfAsync(Alice,
            (Enrolled, EnrolledPayload(Alice, "alice-1")), (KeyBound, BindingPayload(Alice, first)), (KeyBound, BindingPayload(Alice, first)));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_ASecondStatementLessBindingUnderAnotherKidIsABrokenLink()
    {
        var (first, second) = (TestKeys.Es256("alice-1"), TestKeys.Es256("alice-2"));
        var events = await StreamOfAsync(Alice,
            (Enrolled, EnrolledPayload(Alice, "alice-1")), (KeyBound, BindingPayload(Alice, first)), (KeyBound, BindingPayload(Alice, second)));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_AStatementLessBindingUnderAKidTheEnrolmentDoesNotNameIsABrokenLink()
    {
        var second = TestKeys.Es256("alice-2");
        var events = await StreamOfAsync(Alice, (Enrolled, EnrolledPayload(Alice, "alice-1")), (KeyBound, BindingPayload(Alice, second)));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_AStatementLessBindingBeforeTheEnrolmentIsABrokenLink()
    {
        var first = TestKeys.Es256("alice-1");
        var events = await StreamOfAsync(Alice, (KeyBound, BindingPayload(Alice, first)), (Enrolled, EnrolledPayload(Alice, "alice-1")));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_AStatementLessBindingFollowingAnEnrolmentThatNamesNoKidIsABrokenLink()
    {
        var first = TestKeys.Es256("alice-1");
        var events = await StreamOfAsync(Alice, (Enrolled, EnrolledPayload(Alice, null)), (KeyBound, BindingPayload(Alice, first)));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_AStatementLessBindingWithoutAJwkIsABrokenLink()
    {
        var events = await StreamOfAsync(Alice,
            (Enrolled, EnrolledPayload(Alice, "alice-1")), (KeyBound, Obj(("agent_id", Str(Alice)), ("kid", Str("alice-1")))));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_AStatementLessBindingWithAnExtraMemberIsABrokenLink()
    {
        var first = TestKeys.Es256("alice-1");
        var binding = BindingPayload(Alice, first);
        var events = await StreamOfAsync(Alice,
            (Enrolled, EnrolledPayload(Alice, "alice-1")),
            (KeyBound, new JsonValue.Object([.. binding.Members, new KeyValuePair<string, JsonValue>("extra", Num(1))])));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_TheEnrolmentsOwnBindingFolds()
    {
        var first = TestKeys.Es256("alice-1");
        var events = await StreamOfAsync(Alice, (Enrolled, EnrolledPayload(Alice, "alice-1")), (KeyBound, BindingPayload(Alice, first)));

        Assert.Equal($"ok: {Enrolled}@alice-1, {KeyBound}@alice-1", await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_AnEnrolmentsBindingThatComesAfterPostsStillFolds()
    {
        // Pre-G16: the first binding follows the identity's posts, which are no lifecycle entries.
        var first = TestKeys.Es256("alice-1");
        var events = await StreamOfAsync(Alice,
            (Enrolled, EnrolledPayload(Alice, "alice-1")),
            ("post.created", Obj(("title", Str("a post")))),
            (KeyBound, BindingPayload(Alice, first)));

        Assert.Equal($"ok: {Enrolled}@alice-1, {KeyBound}@alice-1", await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_TheSecondEnrolmentIsSkippedAsTheFirstOneAlwaysWas()
    {
        // FindIn and Of both use the first enrolment; a second counts for nothing, so the binding after it is the first's.
        var first = TestKeys.Es256("alice-1");
        var events = await StreamOfAsync(Alice,
            (Enrolled, EnrolledPayload(Alice, "alice-1")), (Enrolled, EnrolledPayload(Alice, "alice-2")), (KeyBound, BindingPayload(Alice, first)));

        Assert.Equal($"ok: {Enrolled}@alice-1, {KeyBound}@alice-1", await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_38_AnEnrolmentTheProducerWritesFolds()
    {
        // The rule is no tighter than what EnrollIdentity writes. The RotateKey half is owed to Task 1.4.
        var first = TestKeys.Es256("alice-1");
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        var registry = new InMemoryAuthorKeyRegistry();
        Require(await new EnrollIdentity(events, registry, new EnrollAgent(events, clock), clock).EnrollAsync(Typed.Agent(Alice), first, TestContext.Current.CancellationToken));

        Assert.Equal($"ok: {Enrolled}@alice-1, {KeyBound}@alice-1", await FoldAsync(events, Alice));
    }

    [Theory]
    [InlineData(KeyBound, false)]
    [InlineData(LifecycleAppendGuard.KeyRetiredType, false)]
    [InlineData(LifecycleAppendGuard.KeyRetiredType, true)]
    [InlineData(LifecycleAppendGuard.KeyCompromisedType, false)]
    public async Task R4_35_ALifecycleEntryOfThisAggregateThatNamesNoAgentIsRefusedNotSkipped(string type, bool namesAnotherAgent)
    {
        var first = TestKeys.Es256("alice-1");
        JsonValue payload = namesAnotherAgent
            ? Obj(("agent_id", Str("https://agents.example/bob")), ("kid", Str("alice-1")), ("prev", Num(1)))
            : Obj(("kid", Str("alice-1")), ("prev", Num(1)));
        var events = await StreamOfAsync(Alice, (Enrolled, EnrolledPayload(Alice, "alice-1")), (KeyBound, BindingPayload(Alice, first)), (type, payload));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_35_ALifecycleEntryWhosePayloadIsNotAnObjectIsRefusedNotSkipped()
    {
        var first = TestKeys.Es256("alice-1");
        var events = await StreamOfAsync(Alice,
            (Enrolled, EnrolledPayload(Alice, "alice-1")), (KeyBound, BindingPayload(Alice, first)), (LifecycleAppendGuard.KeyRetiredType, Str("retired")));

        Assert.Equal(EntryMismatch, await FoldAsync(events, Alice));
    }

    [Fact]
    public async Task R4_35_ARetiredKeyResolvesForNothing()
    {
        var first = TestKeys.Es256("alice-1");
        var second = TestKeys.Es256("alice-2");
        var (events, _) = await EnrolledAsync(Alice, first);
        await AppendAsync(events, Alice, AgentStandingProjector.KeyBoundType, "rotation-1", RotationPayload(Alice, "alice-1", second, 1));
        var keys = KeysOver(events, await RegistryHoldingAsync(Alice, first, second));
        var ct = TestContext.Current.CancellationToken;

        Assert.True((await keys.ResolveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), At, ct)).IsOk);

        await AppendAsync(events, Alice, LifecycleAppendGuard.KeyRetiredType, "retirement-1", RetirementPayload(Alice, "alice-1", 2));

        Assert.Equal(
            "resolve: curia/keys/retired; live: curia/keys/retired; second: resolved",
            $"resolve: {(await keys.ResolveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), At, ct)).Match(_ => "resolved", e => e.Type)}; "
            + $"live: {(await keys.ResolveLiveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), At, ct)).Match(_ => "resolved", e => e.Type)}; "
            + $"second: {(await keys.ResolveAsync(Typed.Agent(Alice), Typed.Kid("alice-2"), At, ct)).Match(_ => "resolved", e => e.Type)}");
    }

    [Fact]
    public async Task R6_56_AResolvedKeyCarriesTheVersionOfTheStreamItWasReadAt()
    {
        var first = TestKeys.Es256("alice-1");
        var (events, _) = await EnrolledAsync(Alice, first);
        var keys = KeysOver(events, await RegistryHoldingAsync(Alice, first));

        var live = Require(await keys.ResolveLiveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), At, TestContext.Current.CancellationToken));

        Assert.Equal(Require(AggregateVersion.From(2)), live.AuthorStream);
    }

    [Fact]
    public async Task R4_35_TheKeySetPublishesARetiredKeyWithItsStatus()
    {
        var first = TestKeys.Es256("alice-1");
        var second = TestKeys.Es256("alice-2");
        var (events, _) = await EnrolledAsync(Alice, first);
        await AppendAsync(events, Alice, AgentStandingProjector.KeyBoundType, "rotation-1", RotationPayload(Alice, "alice-1", second, 1));
        await AppendAsync(events, Alice, LifecycleAppendGuard.KeyRetiredType, "retirement-1", RetirementPayload(Alice, "alice-1", 2));
        var keys = KeysOver(events, await RegistryHoldingAsync(Alice, first, second));

        var set = Require(await keys.KeySetAsync(Typed.Agent(Alice), TestContext.Current.CancellationToken));

        Assert.Equal(
            "alice-1: Retired by retirement-1; alice-2: Active by -",
            string.Join("; ", set.Bound.OrderBy(b => b.Key.Key.Kid, StringComparer.Ordinal).Select(b => $"{b.Key.Key.Kid}: {b.Status} by {b.EndedByEventId ?? "-"}")));
    }

    [Fact]
    public async Task R4_31_ARotatedKeysLostRowIsRestoredWhileTheStoreHoldsTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = TestKeys.Es256("alice-1");
        var second = TestKeys.Es256("alice-2");
        var (events, clock) = await EnrolledAsync(Alice, first);
        clock.Advance(TimeSpan.FromHours(1));
        await AppendAsync(events, Alice, AgentStandingProjector.KeyBoundType, "rotation-1", RotationPayload(Alice, "alice-1", second, 1));
        var boundAt = (await StreamAsync(events, Alice))[^1].ServerTimestamp.Value;
        var registry = await RegistryHoldingAsync(Alice, first);
        var enroll = new EnrollIdentity(events, registry, new EnrollAgent(events, clock), clock);

        var restored = Require(await enroll.EnrollAsync(Typed.Agent(Alice), Copy(second), ct));

        Assert.Equal(
            "already-enrolled; alice-1@" + Start.ToString("O") + ", alice-2@" + boundAt.ToString("O"),
            $"{(restored.WasAlreadyEnrolled ? "already-enrolled" : "new")}; "
            + string.Join(", ", (await registry.KeysForAsync(Typed.Agent(Alice), ct)).OrderBy(k => k.Key.Kid, StringComparer.Ordinal).Select(k => $"{k.Key.Kid}@{k.NotBefore:O}")));
    }

    [Fact]
    public async Task R4_31_ARetiredKeysLostRowIsRestoredAndHonouredForNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = TestKeys.Es256("alice-1");
        var second = TestKeys.Es256("alice-2");
        var (events, clock) = await EnrolledAsync(Alice, first);
        await AppendAsync(events, Alice, AgentStandingProjector.KeyBoundType, "rotation-1", RotationPayload(Alice, "alice-1", second, 1));
        await AppendAsync(events, Alice, LifecycleAppendGuard.KeyRetiredType, "retirement-1", RetirementPayload(Alice, "alice-1", 2));
        var registry = await RegistryHoldingAsync(Alice, second);
        var enroll = new EnrollIdentity(events, registry, new EnrollAgent(events, clock), clock);

        Require(await enroll.EnrollAsync(Typed.Agent(Alice), Copy(first), ct));

        Assert.Equal(
            "stored: alice-1, alice-2; alice-1 resolves: curia/keys/retired",
            "stored: " + string.Join(", ", (await registry.KeysForAsync(Typed.Agent(Alice), ct)).Select(k => k.Key.Kid).Order(StringComparer.Ordinal))
            + "; alice-1 resolves: " + (await KeysOver(events, registry).ResolveAsync(Typed.Agent(Alice), Typed.Kid("alice-1"), At, ct)).Match(_ => "resolved", e => e.Type));
    }

    [Fact]
    public async Task R4_38_TheFoldCountsLiveKeysStatusAndRotationsFromTheStream()
    {
        var first = TestKeys.Es256("alice-1");
        var second = TestKeys.Es256("alice-2");
        var (events, clock) = await EnrolledAsync(Alice, first);
        clock.Advance(TimeSpan.FromHours(30));
        await AppendAsync(events, Alice, AgentStandingProjector.KeyBoundType, "rotation-1", RotationPayload(Alice, "alice-1", second, 1));
        var lifecycle = Require(KeyLifecycle.Of(await StreamAsync(events, Alice), Typed.Agent(Alice)));
        var rotatedAt = lifecycle.Entries.Single(e => e.IsRotation).At;

        var before = $"{lifecycle.StatusNow("alice-1")} {lifecycle.StatusNow("alice-2")} {lifecycle.StatusNow("nobody")} live={lifecycle.LiveCount()}";
        await AppendAsync(events, Alice, LifecycleAppendGuard.KeyRetiredType, "retirement-1", RetirementPayload(Alice, "alice-1", 2));
        var after = Require(KeyLifecycle.Of(await StreamAsync(events, Alice), Typed.Agent(Alice)));

        Assert.Equal(
            "Active Active Unbound live=2 | Retired Active Unbound live=1 | rotations since 1h: 1, since after: 0",
            $"{before} | {after.StatusNow("alice-1")} {after.StatusNow("alice-2")} {after.StatusNow("nobody")} live={after.LiveCount()} | "
            + $"rotations since 1h: {after.RotationsSince(rotatedAt.AddHours(-1))}, since after: {after.RotationsSince(rotatedAt.AddTicks(1))}");
    }

    [Fact]
    public async Task R4_38_AnotherIdentitysEntryInAWiderReadIsNotThisLifecycles()
    {
        const string Bob = "https://agents.example/bob";
        var (events, _) = await EnrolledAsync(Alice, TestKeys.Es256("alice-1"));
        Require(await new EnrollAgent(events, new ManualTimeProvider(Start)).RecordAsync(Typed.Agent(Bob), TestKeys.Es256("bob-1"), TestContext.Current.CancellationToken));

        var wide = Require(await events.ReadAllAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            ["alice-1", "alice-1"],
            Require(KeyLifecycle.Of(wide, Typed.Agent(Alice))).Entries.Select(e => e.Kid));
    }

    [Fact]
    public void R4_38_AStatementNotInItsOwnCanonicalFormIsRefused()
    {
        var (_, statement, _) = RotationVector();
        var spaced = statement.Replace("\":", "\": ", StringComparison.Ordinal);

        Assert.Equal("curia/keys/statement-not-canonical", Refusal(KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(spaced))).Type);
    }

    [Fact]
    public async Task R4_38_EveryRotationVectorMemberEqualsItsStatement()
    {
        var (payload, statementText, agent) = RotationVector();

        var statement = Require(KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(statementText)));
        var folded = Require(KeyLifecycle.Of(
            await StreamAsync(await EnrolledWithVectorRotationAsync(payload), agent),
            Typed.Agent(agent)));

        Assert.Equal(
            $"{payload.Member("agent_id") is JsonValue.String a && a.Value == statement.AgentId}; "
            + $"{((JsonValue.String)payload.Member("authorized_by")!).Value == statement.AuthorizedBy}; "
            + $"{((JsonValue.String)payload.Member("kid")!).Value == statement.Kid}; "
            + $"{((JsonValue.Number)payload.Member("prev")!).Value == statement.Prev}; "
            + $"{PublicJwk.SameKey((JsonValue.Object)payload.Member("jwk")!, statement.Jwk)}; "
            + "folded: conformance-ed25519-second@1",
            "True; True; True; True; True; "
            + $"folded: {folded.Entries.Last().Kid}@{folded.Entries.Last().Prev}");
    }

    [Fact]
    public void R4_38_AStatementWithAnExtraMemberIsRefused()
    {
        var (_, statement, _) = RotationVector();
        var tree = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(statement)));
        var extra = Canonical(new JsonValue.Object([.. tree.Members, new KeyValuePair<string, JsonValue>("extra", Num(1))]));

        Assert.Equal("curia/keys/statement-mismatch", Refusal(KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(extra))).Type);
    }

    [Fact]
    public async Task R4_38_AnEntryWithAnExtraMemberIsRefused()
    {
        var (payload, _, agent) = RotationVector();
        var extra = new JsonValue.Object([.. payload.Members, new KeyValuePair<string, JsonValue>("extra", Num(1))]);
        var events = await EnrolledWithVectorRotationAsync(extra);

        var refused = KeyLifecycle.Of(await StreamAsync(events, agent), Typed.Agent(agent));

        Assert.Equal("curia/keys/statement-mismatch", Refusal(refused).Type);
    }

    [Fact]
    public async Task R4_38_AnEntryWhoseMemberDisagreesWithItsStatementIsRefused()
    {
        var (payload, _, agent) = RotationVector();
        var moved = new JsonValue.Object([.. payload.Members.Select(m => m.Key == "prev" ? new KeyValuePair<string, JsonValue>("prev", Num(2)) : m)]);
        var events = await EnrolledWithVectorRotationAsync(moved);

        Assert.Equal("curia/keys/statement-mismatch", Refusal(KeyLifecycle.Of(await StreamAsync(events, agent), Typed.Agent(agent))).Type);
    }

    [Fact]
    public void R4_38_AnUnknownStatementTypIsRefused()
    {
        var (_, statement, _) = RotationVector();
        var tree = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(statement)));
        var v2 = Canonical(new JsonValue.Object([.. tree.Members.Select(m => m.Key == "typ" ? new KeyValuePair<string, JsonValue>("typ", Str("curia.key-rotation/2")) : m)]));

        Assert.Equal("curia/keys/statement-unreadable", Refusal(KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(v2))).Type);
    }

    [Fact]
    public void R4_38_AnUnknownTypWithAnExtraMemberIsReadAsUnreadableBeforeItsMembers()
    {
        var (_, statement, _) = RotationVector();
        var tree = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(statement)));
        var v2 = Canonical(new JsonValue.Object([.. tree.Members.Select(m => m.Key == "typ" ? new KeyValuePair<string, JsonValue>("typ", Str("curia.key-rotation/2")) : m), new KeyValuePair<string, JsonValue>("extra", Num(1))]));

        Assert.Equal("curia/keys/statement-unreadable", Refusal(KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(v2))).Type);
    }

    [Fact]
    public void R4_38_ARetirementStatementIsReadFromItsBytes()
    {
        var read = Require(KeyStatements.ReadRetirement(Encoding.UTF8.GetBytes(RetirementStatement(Alice, "alice-1", 2))));

        Assert.Equal($"{Alice} alice-1 2", $"{read.AgentId} {read.Kid} {read.Prev}");
    }

    [Fact]
    public void R4_38_ARotationStatementIsNotARetirementAndTheReverse()
    {
        var (_, rotation, _) = RotationVector();

        Assert.Equal(
            "curia/keys/statement-unreadable curia/keys/statement-unreadable",
            $"{Refusal(KeyStatements.ReadRetirement(Encoding.UTF8.GetBytes(rotation))).Type} "
            + $"{Refusal(KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(RetirementStatement(Alice, "alice-1", 2)))).Type}");
    }

    [Fact]
    public void R4_38_AJwkWhoseAlgDisagreesWithItsKeyTypeIsRefused()
    {
        var key = TestKeys.Es256("alice-2");
        var jwk = Require(PublicJwk.Of(key));
        var wrong = new JsonValue.Object([.. jwk.Members.Select(m => m.Key == "alg" ? new KeyValuePair<string, JsonValue>("alg", Str("EdDSA")) : m)]);
        var statement = Canonical(Obj(("agent_id", Str(Alice)), ("authorized_by", Str("alice-1")), ("jwk", wrong), ("kid", Str("alice-2")), ("prev", Num(1)), ("typ", Str(KeyStatements.RotationStatementTyp))));

        Assert.Equal("curia/keys/jwk-alg-mismatch", Refusal(KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(statement))).Type);
    }

    [Fact]
    public void R4_38_ARotationAuthorizedByItsOwnKeyIsRefused()
    {
        var statement = RotationStatement(Alice, "alice-2", TestKeys.Es256("alice-2"), 1);

        Assert.Equal("curia/keys/self-authorized", Refusal(KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(statement))).Type);
    }

    [Fact]
    public void R4_38_AHeaderTypAndKidAreHeldToTheirKindAndRole()
    {
        var input = Require(JsonReader.ParseUnrestricted(File.ReadAllBytes(RepoFile("conformance", "acta", "key-rotated-entry", "input.json"))));
        var payload = (JsonValue.Object)input.Member("payload")!;
        var authorization = new JwsSignature(((JsonValue.String)payload.Member("authorization")!).Value);

        Assert.Equal(
            "ok; signature-invalid authorization; signature-invalid authorization",
            $"{(KeyStatements.CheckHeader(authorization, "authorization", KeyStatements.RotationHeaderTyp, "conformance-ed25519-minimal").IsOk ? "ok" : "refused")}; "
            + $"{Describe(KeyStatements.CheckHeader(authorization, "authorization", DetachedJws.ExpectedTyp, "conformance-ed25519-minimal"))}; "
            + $"{Describe(KeyStatements.CheckHeader(authorization, "authorization", KeyStatements.RotationHeaderTyp, "conformance-ed25519-second"))}");

        static string Describe(Result<JwsProtectedHeader> r) => r.Match(_ => "ok", e => $"{e.Type.Replace("curia/keys/", string.Empty, StringComparison.Ordinal)} {e.Detail?.Replace("field=", string.Empty, StringComparison.Ordinal)}");
    }
}

internal static class JsonObjectExtensions
{
    /// <summary>The value of <paramref name="name"/> in <paramref name="value"/> when it is an object holding it, otherwise null.</summary>
    internal static JsonValue? Member(this JsonValue value, string name) =>
        value is JsonValue.Object o ? o.Members.Where(m => m.Key == name).Select(m => m.Value).FirstOrDefault() : null;
}
