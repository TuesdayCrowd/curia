using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Moderation;

/// <summary>What the log recorded when an answer was accepted.</summary>
/// <param name="ThreadRoot">The thread whose accepted answer this is.</param>
/// <param name="AnswerId">The answer that now stands.</param>
/// <param name="AcceptedAt">The store's <c>server_ts</c> — R6.5's Forum observation.</param>
/// <param name="AlreadyAccepted">R8.68: the answer was already the accepted one, nothing was appended, and <paramref name="AcceptedAt"/> is the first acceptance's time.</param>
/// <param name="ReplacedAnswer">R8.71 (errata G40): the answer whose standing acceptance this call replaced; <c>null</c> for a first acceptance and for a replay. Computed in the pass that appends, from the standing acceptance that pass read.</param>
public sealed record Acceptance(string ThreadRoot, string AnswerId, DateTimeOffset AcceptedAt, bool AlreadyAccepted = false, string? ReplacedAnswer = null);

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
    /// <para>Idempotent for the answer that stands (R8.68, closing D69): a replay appends nothing and returns the standing
    /// acceptance with <see cref="Acceptance.AlreadyAccepted"/> set; a different answer appends, latest wins.</para>
    ///
    /// <para>Authorization — including Table 10's "(own thread)" — is the caller's, and is settled
    /// before this is reached. This records a decision; it does not make one.</para>
    /// </summary>
    public async Task<Result<Acceptance>> RecordAsync(
        PostId threadRoot,
        PostId answerId,
        AgentId acceptedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(threadRoot);
        ArgumentNullException.ThrowIfNull(answerId);
        ArgumentNullException.ThrowIfNull(acceptedBy);

        if (!AggregateId.Create(threadRoot.Value).TryGetValue(out var aggregate, out var aggregateError))
            return Result<Acceptance>.Fail(aggregateError!);

        if (!ActorId.Create(acceptedBy.Value).TryGetValue(out var actor, out var actorError))
            return Result<Acceptance>.Fail(actorError!);

        // R8.68: one decision, and on a lost race one re-read and a second decision, never a loop. Two callers
        // accepting one answer end as one event (the loser's second pass finds it standing); two callers
        // accepting different answers end latest-wins, as before.
        var first = await DecideAsync(aggregate, threadRoot, answerId, acceptedBy, actor, cancellationToken).ConfigureAwait(false);
        if (first.TryGetValue(out _, out var firstError)
            || !string.Equals(firstError!.Type, DomainErrors.ConcurrencyConflictType, StringComparison.Ordinal))
            return first;

        return await DecideAsync(aggregate, threadRoot, answerId, acceptedBy, actor, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<Acceptance>> DecideAsync(
        AggregateId aggregate,
        PostId threadRoot,
        PostId answerId,
        AgentId acceptedBy,
        ActorId actor,
        CancellationToken cancellationToken)
    {
        var history = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        if (!history.TryGetValue(out var events, out var readError))
            return Result<Acceptance>.Fail(readError!);

        // R8.68: accepting the answer that already stands records nothing and answers the first time.
        if (AcceptanceProjector.Standing(events!) is { } standing
            && string.Equals(standing.AnswerId, answerId.Value, StringComparison.Ordinal))
            return Result<Acceptance>.Ok(new Acceptance(threadRoot.Value, answerId.Value, standing.AcceptedAt, AlreadyAccepted: true));

        // R8.71: the answer this pass replaces is the one that stood when this pass read, and the append is conditioned
        // on that read, so on a lost race the second pass names what stood then.
        var replaced = AcceptanceProjector.Standing(events!)?.AnswerId;

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
            new(AcceptanceProjector.ThreadRootField, new JsonValue.String(threadRoot.Value)),
            new(AcceptanceProjector.AnswerIdField, new JsonValue.String(answerId.Value)),
            new(AcceptanceProjector.AcceptedByField, new JsonValue.String(acceptedBy.Value)),
        ]);

        var appended = await _events
            .AppendAsync(aggregate, version, [new DomainEvent(eventId, type, actor, payload)], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(recorded => new Acceptance(
            threadRoot.Value, answerId.Value, recorded[0].ServerTimestamp.Value, AlreadyAccepted: false, ReplacedAnswer: replaced));
    }
}
