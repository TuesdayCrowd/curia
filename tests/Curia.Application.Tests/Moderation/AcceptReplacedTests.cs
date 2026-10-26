using System.Diagnostics.CodeAnalysis;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Moderation;

/// <summary>
/// R8.71 (errata G40): an acceptance that replaced another says which. The use case names the answer whose standing acceptance the call
/// replaced, computed from the stream it read in the same pass that appends. Every id here is the test's own constant, and the count of
/// <c>answer.accepted</c> events is read from the store, so neither the oracle nor the setup is the use case's report of itself.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptReplacedTests
{
    private const string Root = "01JP0ST0000000000000000001";
    private const string AnswerA = "01JP0ST000000000000000000A";
    private const string AnswerB = "01JP0ST000000000000000000B";
    private const string Interloper = "01JP0ST000000000000000000C";
    private const string Asker = "https://agents.example/asker";

    private static readonly DateTimeOffset Start = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static Task<Result<Acceptance>> AcceptAsync(AcceptAnswer use, string answer, CancellationToken ct) =>
        use.RecordAsync(Typed.Post(Root), Typed.Post(answer), Typed.Agent(Asker), ct);

    /// <summary>The <c>answer.accepted</c> events the store holds, in order, as the answer id each payload carries.</summary>
    private static async Task<string[]> AcceptedAnswersInOrderAsync(InMemoryEventStore store, CancellationToken ct) =>
        [.. Require(await store.ReadByAggregateAsync(Require(AggregateId.Create(Root)), ct).ConfigureAwait(false))
            .Where(e => e.Event.Type.Value == AcceptanceProjector.AnswerAcceptedType)
            .Select(e => ((Curia.Canon.Json.JsonValue.String)((Curia.Canon.Json.JsonValue.Object)e.Event.Payload).Members
                .Single(m => m.Key == AcceptanceProjector.AnswerIdField).Value).Value)];

    /// <summary>A store that lets another writer land one event between the use case's read and its append, once.</summary>
    private sealed class RacedStore(IEventStore inner, Func<Task> rival) : IEventStore
    {
        private bool _raced;

        public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
            AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken = default)
        {
            await RaceOnceAsync().ConfigureAwait(false);
            return await inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
            AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition condition, CancellationToken cancellationToken = default)
        {
            await RaceOnceAsync().ConfigureAwait(false);
            return await inner.AppendAsync(aggregateId, expectedVersion, events, condition, cancellationToken).ConfigureAwait(false);
        }

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
            inner.ReadByAggregateAsync(aggregateId, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) =>
            inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);

        private async Task RaceOnceAsync()
        {
            if (_raced) return;
            _raced = true;
            await rival().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task R8_71_ASwitchNamesTheAnswerItReplacedAndAFirstAcceptanceAndAReplayNameNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        var use = new AcceptAnswer(store, clock);

        var first = Require(await AcceptAsync(use, AnswerA, ct));
        clock.Set(Start + TimeSpan.FromMinutes(1));
        var switched = Require(await AcceptAsync(use, AnswerB, ct));
        clock.Set(Start + TimeSpan.FromMinutes(2));
        var replay = Require(await AcceptAsync(use, AnswerB, ct));
        clock.Set(Start + TimeSpan.FromMinutes(3));
        var back = Require(await AcceptAsync(use, AnswerA, ct));

        Assert.Equal([AnswerA, AnswerB, AnswerA], await AcceptedAnswersInOrderAsync(store, ct));
        Assert.Null(first.ReplacedAnswer);
        Assert.Equal(AnswerA, switched.ReplacedAnswer);
        Assert.True(replay.AlreadyAccepted);
        Assert.Null(replay.ReplacedAnswer);
        Assert.Equal(AnswerB, back.ReplacedAnswer);
    }

    [Fact]
    public async Task R8_71_ALostRaceNamesWhatStoodAtTheSecondPass()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        var rival = new AcceptAnswer(store, clock);

        // A stands when the loser reads; the rival then switches to the interloper before the loser appends B.
        Require(await AcceptAsync(rival, AnswerA, ct));
        clock.Set(Start + TimeSpan.FromMinutes(1));
        var loser = new AcceptAnswer(new RacedStore(store, async () => Require(await AcceptAsync(rival, Interloper, ct).ConfigureAwait(false))), clock);

        var result = Require(await AcceptAsync(loser, AnswerB, ct));

        Assert.Equal([AnswerA, Interloper, AnswerB], await AcceptedAnswersInOrderAsync(store, ct));
        Assert.False(result.AlreadyAccepted);
        Assert.Equal(Interloper, result.ReplacedAnswer);
    }
}
