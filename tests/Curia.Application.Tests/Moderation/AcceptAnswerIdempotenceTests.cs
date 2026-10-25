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
/// R8.68 (errata G33, closing D69): an acceptance is recorded once. Accepting the answer that already
/// stands appends nothing and answers the standing acceptance; accepting another answer replaces it, as
/// before. The count of <c>answer.accepted</c> events is read from the store, never from the use case's
/// own result, because the result is the thing a bug would make read well.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptAnswerIdempotenceTests
{
    private const string Root = "01JP0ST0000000000000000001";
    private const string AnswerA = "01JP0ST000000000000000000A";
    private const string AnswerB = "01JP0ST000000000000000000B";
    private const string Asker = "https://agents.example/asker";

    private static readonly DateTimeOffset Start = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> AcceptedEventsAsync(InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadByAggregateAsync(Require(AggregateId.Create(Root)), ct).ConfigureAwait(false))
            .Where(e => e.Event.Type.Value == AcceptanceProjector.AnswerAcceptedType)
            .ToList();

    private static Task<Result<Acceptance>> AcceptAsync(AcceptAnswer use, string answer, CancellationToken ct) =>
        use.RecordAsync(Typed.Post(Root), Typed.Post(answer), Typed.Agent(Asker), ct);

    /// <summary>
    /// A store that lets another writer land one event on the aggregate between the use case's read and its
    /// append, once: the use case's first append then carries a stale version and loses the race.
    /// </summary>
    private sealed class RacedStore(IEventStore inner, Func<Task> rival) : IEventStore
    {
        private bool _raced;

        public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
            AggregateId aggregateId,
            AggregateVersion expectedVersion,
            IReadOnlyList<DomainEvent> events,
            CancellationToken cancellationToken = default)
        {
            if (!_raced)
            {
                _raced = true;
                await rival().ConfigureAwait(false);
            }

            return await inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken).ConfigureAwait(false);
        }

        public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
            AggregateId aggregateId,
            AggregateVersion expectedVersion,
            IReadOnlyList<DomainEvent> events,
            StreamCondition condition,
            CancellationToken cancellationToken = default)
        {
            if (!_raced)
            {
                _raced = true;
                await rival().ConfigureAwait(false);
            }

            return await inner.AppendAsync(aggregateId, expectedVersion, events, condition, cancellationToken).ConfigureAwait(false);
        }

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(
            AggregateId aggregateId,
            CancellationToken cancellationToken = default) =>
            inner.ReadByAggregateAsync(aggregateId, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(
            EventSequence afterSeq,
            int? maxCount = null,
            CancellationToken cancellationToken = default) =>
            inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }

    [Fact]
    public async Task R8_68_AcceptingTheStandingAnswerAppendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        var use = new AcceptAnswer(store, clock);

        var first = Require(await AcceptAsync(use, AnswerA, ct));
        clock.Set(Start + TimeSpan.FromMinutes(5));
        var second = Require(await AcceptAsync(use, AnswerA, ct));

        Assert.Single(await AcceptedEventsAsync(store, ct));
        Assert.Equal(first.AcceptedAt, second.AcceptedAt);
        Assert.False(first.AlreadyAccepted);
        Assert.True(second.AlreadyAccepted);
    }

    /// <summary>The control: latest wins is untouched, and a return to an earlier answer is a new acceptance.</summary>
    [Fact]
    public async Task R8_68_AcceptingAnotherAnswerReplacesAndAcceptingTheFirstAgainAppendsAThird()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        var use = new AcceptAnswer(store, clock);

        var results = new List<Acceptance>();
        foreach (var answer in new[] { AnswerA, AnswerB, AnswerA })
        {
            clock.Set(clock.GetUtcNow() + TimeSpan.FromMinutes(1));
            results.Add(Require(await AcceptAsync(use, answer, ct)));
        }

        Assert.Equal(3, (await AcceptedEventsAsync(store, ct)).Count);
        Assert.All(results, r => Assert.False(r.AlreadyAccepted));
        Assert.Equal(AnswerA, AcceptanceProjector.Fold(Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct)))[Root]);
    }

    [Fact]
    public async Task R8_68_TwoRacingAcceptancesOfOneAnswerEndAsOneEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        var winner = new AcceptAnswer(store, clock);
        var loser = new AcceptAnswer(new RacedStore(store, async () => Require(await AcceptAsync(winner, AnswerA, ct).ConfigureAwait(false))), clock);

        var result = Require(await AcceptAsync(loser, AnswerA, ct));

        Assert.Single(await AcceptedEventsAsync(store, ct));
        Assert.True(result.AlreadyAccepted);
    }

    [Fact]
    public async Task R8_68_ARaceBetweenTwoDifferentAnswersEndsOnTheSecondsAppend()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        var rival = new AcceptAnswer(store, clock);
        var loser = new AcceptAnswer(new RacedStore(store, async () => Require(await AcceptAsync(rival, AnswerB, ct).ConfigureAwait(false))), clock);

        var result = Require(await AcceptAsync(loser, AnswerA, ct));

        Assert.Equal(2, (await AcceptedEventsAsync(store, ct)).Count);
        Assert.False(result.AlreadyAccepted);
        Assert.Equal(AnswerA, AcceptanceProjector.Fold(Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct)))[Root]);
    }
}
