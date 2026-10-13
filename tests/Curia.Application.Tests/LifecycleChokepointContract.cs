using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// What both lifecycle-chokepoint contracts need: building a store over a gate, and the append shapes the
/// guard classifies (R4.38 (add.), spec section 4.13). Public and abstract, so <c>Curia.Infrastructure.Tests</c>
/// can run the same facts against <c>PostgresEventStore</c> and, through
/// <see cref="InMemoryLifecycleRefusalContract"/>, against the internal in-memory store.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "Public so Curia.Infrastructure.Tests can subclass it; it carries no [Fact] of its own, so the analyzer's test-class heuristic does not apply.")]
public abstract class LifecycleChokepointShapes
{
    /// <summary>A store, over a fresh and empty log, whose gate is <paramref name="gate"/>.</summary>
    protected abstract IEventStore CreateStore(ILifecycleAppendGate gate);

    /// <summary>The five lifecycle-shaped appends: GF3's refusals, and the positive control's appends.</summary>
    public static TheoryData<string> Shapes { get; } =
    [
        "key-retired",
        "key-compromised",
        "key-bound-on-an-existing-stream",
        "owner-attested-then-key-bound",
        "enrolled-key-bound-key-bound",
    ];

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static DomainEvent Event(string type, string stream) => new(
        Require(EventId.Create("evt-" + Guid.NewGuid().ToString("N"))),
        Require(EventType.Create(type)),
        Require(ActorId.Create("actor:" + stream)),
        new JsonValue.Object([new("agent_id", new JsonValue.String(stream))]));

    /// <summary>The stream every shape is appended to, and the number of events it already holds.</summary>
    protected sealed record Shaped(AggregateId Stream, AggregateVersion Expected, IReadOnlyList<DomainEvent> Events, long PriorLength);

    /// <summary>
    /// <paramref name="shape"/> as an append: for the existing-stream shape, the stream is first given one
    /// event the guard does not classify (a lone <c>agent.enrolled</c>), through <paramref name="store"/>.
    /// </summary>
    protected static async Task<Shaped> ShapeAsync(string shape, IEventStore store, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(store);
        var name = "agent://example/" + Guid.NewGuid().ToString("N");
        var stream = Require(AggregateId.Create(name));
        switch (shape)
        {
            case "key-retired":
                return new Shaped(stream, AggregateVersion.New, [Event(LifecycleAppendGuard.KeyRetiredType, name)], 0);
            case "key-compromised":
                return new Shaped(stream, AggregateVersion.New, [Event(LifecycleAppendGuard.KeyCompromisedType, name)], 0);
            case "key-bound-on-an-existing-stream":
                Require(await store.AppendAsync(stream, AggregateVersion.New, [Event(AgentStandingProjector.EnrolledType, name)], ct).ConfigureAwait(false));
                return new Shaped(stream, Require(AggregateVersion.From(1)), [Event(AgentStandingProjector.KeyBoundType, name)], 1);
            case "owner-attested-then-key-bound":
                return new Shaped(stream, AggregateVersion.New, [Event(AgentStandingProjector.OwnerAttestedType, name), Event(AgentStandingProjector.KeyBoundType, name)], 0);
            case "enrolled-key-bound-key-bound":
                return new Shaped(
                    stream,
                    AggregateVersion.New,
                    [Event(AgentStandingProjector.EnrolledType, name), Event(AgentStandingProjector.KeyBoundType, name), Event(AgentStandingProjector.KeyBoundType, name)],
                    0);
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "not a shape");
        }
    }
}

/// <summary>
/// R4.38 (add.), GF3 and GF4: under a gate that enables nothing, the store refuses each lifecycle shape and
/// still appends enrolment's pair. A gate that enables nothing is the production gate's behaviour over the
/// shipped (empty) record; <c>Curia.Infrastructure.Tests</c> runs these against the real one, in both adapters.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class LifecycleRefusalContract : LifecycleChokepointShapes
{
    /// <summary>The gate under test: the production gate over the shipped record.</summary>
    protected abstract ILifecycleAppendGate RefusingGate();

    /// <summary>
    /// GF3: each shape answers exactly <c>curia/domain/lifecycle-append-refused</c> and leaves the stream as
    /// it was. Red when the check is removed from the adapter under test.
    /// </summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task R4_38_TheChokepointRefusesEachLifecycleShape(string shape)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(RefusingGate());
        var shaped = await ShapeAsync(shape, store, ct);

        var appended = await store.AppendAsync(shaped.Stream, shaped.Expected, shaped.Events, ct);

        Assert.False(appended.IsOk, $"the {shape} append was written");
        Assert.Equal(DomainErrors.LifecycleAppendRefusedType, appended.Match(_ => string.Empty, e => e.Type));
        var stream = await store.ReadByAggregateAsync(shaped.Stream, ct);
        Assert.Equal(shaped.PriorLength, stream.Match(s => (long)s.Count, e => -1));
    }

    /// <summary>
    /// GF4: enrolment, through its use case, still appends <c>[agent.enrolled, agent.key-bound]</c>.
    /// Red when the chokepoint refuses every <c>agent.key-bound</c>.
    /// </summary>
    [Fact]
    public async Task R4_38_EnrollmentStillAppendsItsPairUnderTheGuard()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(RefusingGate());
        var agent = "https://agents.example/" + Guid.NewGuid().ToString("N")[..12];
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var key = new PublicKeyMaterial("ES256", "k-" + Guid.NewGuid().ToString("N")[..8], ecdsa.ExportSubjectPublicKeyInfo());

        Require(await new EnrollAgent(store, TimeProvider.System).RecordAsync(Typed.Agent(agent), key, ct));

        var stream = Require(await store.ReadByAggregateAsync(Require(AggregateId.Create(agent)), ct));
        Assert.Equal(
            [AgentStandingProjector.EnrolledType, AgentStandingProjector.KeyBoundType],
            stream.Select(e => e.Event.Type.Value).ToArray());
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));
}

/// <summary>
/// R4.38 (add.): the positive control. The same five appends land under the permissive gate, so a refusal
/// above is the guard's and not the shape's.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class LifecyclePermissiveContract : LifecycleChokepointShapes
{
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task R4_38_TheChokepointAdmitsEachShapeUnderThePermissiveGate(string shape)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(PermissiveGate());
        var shaped = await ShapeAsync(shape, store, ct);

        var appended = await store.AppendAsync(shaped.Stream, shaped.Expected, shaped.Events, ct);

        Assert.True(appended.IsOk, appended.Match(_ => string.Empty, e => $"{e.Type}: {e.Detail}"));
    }

    /// <summary>The permissive gate for the store under test. Overridable so a fixture that checks its database can hand it out.</summary>
    protected virtual ILifecycleAppendGate PermissiveGate() => new PermissiveLifecycleAppendGate();
}

/// <summary>
/// The in-memory store's runs of <see cref="LifecycleRefusalContract"/>. <c>InMemoryEventStore</c> is
/// internal to this assembly and no assembly is granted <c>InternalsVisibleTo</c>, so the store is built
/// here and the gate is taken from the subclass.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "Public so Curia.Infrastructure.Tests can subclass it; it carries no [Fact] of its own, so the analyzer's test-class heuristic does not apply.")]
public abstract class InMemoryLifecycleRefusalContract : LifecycleRefusalContract
{
    protected override IEventStore CreateStore(ILifecycleAppendGate gate) => new InMemoryEventStore(TimeProvider.System, gate);
}

/// <summary>
/// R4.38 (add.) for R6.56's overload: an append conditioned on another stream's version passes the same
/// chokepoint as the plain one. Red when the overload writes outside the gated core. Public and abstract so
/// <c>Curia.Infrastructure.Tests</c> subclasses it for the Postgres store; the in-memory subclass builds the
/// internal store here.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "Public so Curia.Infrastructure.Tests can subclass it; it carries no [Fact] of its own, so the analyzer's test-class heuristic does not apply.")]
public abstract class ConditionedAppendChokepointContract : LifecycleChokepointShapes
{
    /// <summary>The gate under test: the production gate over the shipped record.</summary>
    protected abstract ILifecycleAppendGate RefusingGate();

    [Fact]
    public async Task R4_38_TheConditionedOverloadPassesTheChokepoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(RefusingGate());
        var shaped = await ShapeAsync("key-retired", store, ct);
        var condition = new StreamCondition(Require(AggregateId.Create("agg-conditioned-chokepoint")), AggregateVersion.New);

        var appended = await store.AppendAsync(shaped.Stream, shaped.Expected, shaped.Events, condition, ct);

        Assert.False(appended.IsOk, "the conditioned key-retired append was written");
        Assert.Equal(DomainErrors.LifecycleAppendRefusedType, appended.Match(_ => string.Empty, e => e.Type));
        var stream = await store.ReadByAggregateAsync(shaped.Stream, ct);
        Assert.Equal(shaped.PriorLength, stream.Match(s => (long)s.Count, e => -1));
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));
}

/// <summary>The in-memory store's run of <see cref="ConditionedAppendChokepointContract"/>.</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "Public so Curia.Infrastructure.Tests can subclass it; it carries no [Fact] of its own, so the analyzer's test-class heuristic does not apply.")]
public abstract class InMemoryConditionedAppendChokepointContract : ConditionedAppendChokepointContract
{
    protected override IEventStore CreateStore(ILifecycleAppendGate gate) => new InMemoryEventStore(TimeProvider.System, gate);
}

/// <summary>The in-memory store's runs of <see cref="LifecyclePermissiveContract"/>.</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "Public so Curia.Infrastructure.Tests can subclass it; it carries no [Fact] of its own, so the analyzer's test-class heuristic does not apply.")]
public abstract class InMemoryLifecyclePermissiveContract : LifecyclePermissiveContract
{
    protected override IEventStore CreateStore(ILifecycleAppendGate gate) => new InMemoryEventStore(TimeProvider.System, gate);
}

/// <summary>
/// <c>Curia.Application.Tests</c> keeps only its in-memory permissive run: the production gate lives in
/// <c>Curia.Infrastructure</c>, which this assembly cannot reach.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovers concrete test classes only when public; the facts are inherited.")]
public sealed class InMemoryLifecyclePermissiveTests : InMemoryLifecyclePermissiveContract
{
}
