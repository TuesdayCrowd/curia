using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R7.26 (errata G46), TRW10: the acceptance budget's count, <see cref="AcceptanceProjector.CountBy"/>, is a pure function of the events: the
/// <c>answer.accepted</c> events whose <c>accepted_by</c> is the agent and whose <c>server_ts</c> is strictly after the instant given. Every event here is
/// appended through the in-memory store at an instant the test sets, so <c>server_ts</c> is the store's and not the test's claim about it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptBudgetCountTests
{
    private const string AgentA = "https://agents.example/asker-a";
    private const string AgentB = "https://agents.example/asker-b";
    private const string AgentC = "https://agents.example/asker-c";

    private static readonly DateTimeOffset Since = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static AgentId Id(string value) => Require(AgentId.Parse(value));

    private static async Task AppendAsync(
        InMemoryEventStore store, ManualTimeProvider clock, DateTimeOffset at, string aggregate, string type, string actor, JsonValue payload, CancellationToken ct)
    {
        clock.Set(at);
        Require(await store.AppendAsync(
            Require(AggregateId.Create(aggregate)),
            AggregateVersion.New,
            [new DomainEvent(Require(EventId.Create(aggregate + "-e")), Require(EventType.Create(type)), Require(ActorId.Create(actor)), payload)],
            ct).ConfigureAwait(false));
    }

    private static JsonValue.Object Accepted(string root, string answer, string? by)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new(AcceptanceProjector.ThreadRootField, new JsonValue.String(root)),
            new(AcceptanceProjector.AnswerIdField, new JsonValue.String(answer)),
        };

        if (by is not null) members.Add(new(AcceptanceProjector.AcceptedByField, new JsonValue.String(by)));
        return new JsonValue.Object([.. members]);
    }

    /// <summary>
    /// TRW10. An acceptance exactly at the instant is not counted and one tick later is; another agent's acceptance, a <c>post.accepted</c> that names the
    /// agent in an <c>accepted_by</c> member, an <c>answer.accepted</c> with no <c>accepted_by</c> and one whose payload is not an object are counted for
    /// nobody.
    /// </summary>
    [Fact]
    public async Task R7_26_TRW10_TheCountIsTheAgentsAcceptancesStrictlyAfterTheInstant()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Since);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());

        await AppendAsync(store, clock, Since, "t1", AcceptanceProjector.AnswerAcceptedType, AgentA, Accepted("t1", "a1", AgentA), ct);
        await AppendAsync(store, clock, Since + TimeSpan.FromTicks(1), "t2", AcceptanceProjector.AnswerAcceptedType, AgentA, Accepted("t2", "a2", AgentA), ct);
        await AppendAsync(store, clock, Since + TimeSpan.FromHours(1), "t3", AcceptanceProjector.AnswerAcceptedType, AgentB, Accepted("t3", "a3", AgentB), ct);
        await AppendAsync(
            store, clock, Since + TimeSpan.FromHours(1), "c1", PostProjector.PostAcceptedType, AgentA,
            new JsonValue.Object([new("post_id", new JsonValue.String("c1")), new(AcceptanceProjector.AcceptedByField, new JsonValue.String(AgentA))]), ct);
        await AppendAsync(store, clock, Since + TimeSpan.FromHours(1), "t4", AcceptanceProjector.AnswerAcceptedType, AgentA, Accepted("t4", "a4", null), ct);
        await AppendAsync(store, clock, Since + TimeSpan.FromHours(1), "t5", AcceptanceProjector.AnswerAcceptedType, AgentA, new JsonValue.String("not an object"), ct);
        await AppendAsync(store, clock, Since + TimeSpan.FromHours(2), "t6", AcceptanceProjector.AnswerAcceptedType, AgentA, Accepted("t6", "a6", AgentA), ct);

        var log = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));

        Assert.Equal(
            (2, 1, 0),
            (AcceptanceProjector.CountBy(log, Id(AgentA), Since), AcceptanceProjector.CountBy(log, Id(AgentB), Since), AcceptanceProjector.CountBy(log, Id(AgentC), Since)));
    }
}
