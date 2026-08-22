using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Moderation;

/// <summary>What the log recorded when an answer was accepted.</summary>
/// <param name="ThreadRoot">The thread whose accepted answer this is.</param>
/// <param name="AnswerId">The answer that now stands.</param>
/// <param name="AcceptedAt">The store's <c>server_ts</c> — R6.5's Forum observation.</param>
public sealed record Acceptance(string ThreadRoot, string AnswerId, DateTimeOffset AcceptedAt);

/// <summary>
/// Table 10's <c>answer</c>/<c>accept</c>, as a use case.
///
/// <para><b>The event lands on the thread root's aggregate, not the answer's.</b> Accepting is a
/// statement about the thread — a thread has one accepted answer — so the root's stream is the
/// consistency boundary, and two askers racing to resolve the same thread contend through the same
/// optimistic-concurrency check that makes every other append safe. Landing it on the answer's
/// stream would let two concurrent acceptances of two different answers both succeed, and the
/// projection would then be resolving a race the store had already declined to.</para>
///
/// <para><b>There is no un-accept, and none is needed.</b> Re-accepting a different answer appends,
/// and <see cref="AcceptanceProjector"/> takes the latest — the history is the state, as it is for
/// servability (R10.36) and for credential lifecycle.</para>
/// </summary>
public sealed class AcceptAnswer
{
    private readonly IEventStore _events;
    private readonly UlidGenerator _ids;

    public AcceptAnswer(IEventStore events, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>
    /// Records that <paramref name="answerId"/> is the accepted answer of
    /// <paramref name="threadRoot"/>.
    ///
    /// <para>Authorization — including Table 10's "(own thread)" — is the caller's, and is settled
    /// before this is reached. This records a decision; it does not make one.</para>
    /// </summary>
    public async Task<Result<Acceptance>> RecordAsync(
        string threadRoot,
        string answerId,
        string acceptedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(threadRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(answerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptedBy);

        if (!AggregateId.Create(threadRoot).TryGetValue(out var aggregate, out var aggregateError))
            return Result<Acceptance>.Fail(aggregateError!);

        if (!ActorId.Create(acceptedBy).TryGetValue(out var actor, out var actorError))
            return Result<Acceptance>.Fail(actorError!);

        var history = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        if (!history.TryGetValue(out var events, out var readError))
            return Result<Acceptance>.Fail(readError!);

        if (!AggregateVersion.From(events!.Count).TryGetValue(out var version, out var versionError))
            return Result<Acceptance>.Fail(versionError!);

        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<Acceptance>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<Acceptance>.Fail(eventIdError!);

        if (!EventType.Create(AcceptanceProjector.AnswerAcceptedType).TryGetValue(out var type, out var typeError))
            return Result<Acceptance>.Fail(typeError!);

        var payload = new JsonValue.Object(
        [
            new(AcceptanceProjector.ThreadRootField, new JsonValue.String(threadRoot)),
            new(AcceptanceProjector.AnswerIdField, new JsonValue.String(answerId)),
            new(AcceptanceProjector.AcceptedByField, new JsonValue.String(acceptedBy)),
        ]);

        var appended = await _events
            .AppendAsync(aggregate, version, [new DomainEvent(eventId, type, actor, payload)], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(recorded => new Acceptance(
            threadRoot, answerId, recorded[0].ServerTimestamp.Value));
    }
}
