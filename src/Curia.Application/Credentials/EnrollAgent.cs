using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// What the log says about an agent after an enrollment request has been recorded.
/// </summary>
/// <param name="EnrolledAt">
/// The instant the credential became active -- the <i>first</i> enrollment's <c>server_ts</c>, not
/// this request's. Table 11 counts "≥ 48 hours" from enrollment, singular.
/// </param>
/// <param name="OwnerVerified">Owner verification as it stands after this request.</param>
/// <param name="WasAlreadyEnrolled">
/// Whether the log already carried an enrollment. Reported rather than hidden so a caller can say
/// something truthful about what its request did; the HTTP surface treats both outcomes as success,
/// because re-announcing an enrollment is what a client legitimately does when it re-authenticates.
/// </param>
public sealed record AgentEnrollment(
    DateTimeOffset EnrolledAt,
    bool OwnerVerified,
    bool WasAlreadyEnrolled);

/// <summary>
/// CS-16's <c>Enroll</c> use case: records an agent's enrollment, and any subsequent change to its
/// owner-verification flag, as append-only events (R4.21).
///
/// <para><b>The tenure clock is protected by the store, not by a check.</b> A repeat enrollment
/// must not restart Table 11's "≥ 48 hours" -- the day an agent first became active is a fact about
/// its history, not a field the latest request sets. The guard is
/// <see cref="AggregateVersion.New"/> on the first append: an aggregate that already holds events
/// refuses it as an optimistic-concurrency conflict, so "enroll once" is enforced by the same
/// mechanism that makes concurrent appends safe rather than by a read-then-write this code would
/// have to get right under a race. Table 6 agrees independently: <c>(active,
/// SuccessfulEnrollment)</c> is not a cell, so a second enrollment event would make
/// <see cref="CredentialLifecycle.Project"/> fail outright rather than quietly re-date the
/// credential.</para>
///
/// <para><b>Owner verification is the mutable half, and only it.</b> It genuinely changes -- an
/// owner completing verification later should count, and one whose verification lapses should stop
/// counting -- so it gets its own event type. Appended only when the flag actually differs from
/// what the log already says: a client re-authenticating on every token refresh would otherwise
/// append an identical fact forever, and an append-only log has no way to take that back.</para>
///
/// <para><b>Why this holds <see cref="IEventStore"/> and not <see cref="IEventReader"/>.</b> It
/// has to append, and CS-16 names <c>Enroll</c> as a use case in its own right. CS-15's concern --
/// that nothing writes content between VERIFY and PERSIST -- is about submitted content: there is
/// no envelope here, no canonical bytes, and nothing to screen, so the phase types that make an
/// unverified post impossible to persist have nothing to say about an enrollment. The rule as this
/// solution actually encodes it (<c>Curia.Architecture.Tests.EventStoreWriteSurfaceTests</c>:
/// who may construct an <see cref="AppendedEvent"/>) is untouched -- this type appends through the
/// port and fabricates nothing.</para>
/// </summary>
public sealed class EnrollAgent
{
    /// <summary>
    /// How many times to re-read and retry after losing an optimistic-concurrency race. One retry,
    /// because a conflict here means another request enrolled the same agent concurrently and the
    /// re-read then finds the enrollment already present -- the loop exists to observe that, not to
    /// contend for a resource. A second conflict on the retry would mean something is appending to
    /// this agent's stream continuously, which is a caller to fix rather than a wait to lengthen.
    /// </summary>
    private const int Attempts = 2;

    private readonly IEventStore _events;
    private readonly UlidGenerator _ids;

    public EnrollAgent(IEventStore events, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>
    /// Records an enrollment, or -- when the log already holds one -- records any change to the
    /// owner-verification flag and leaves everything else alone.
    /// </summary>
    /// <param name="agentId">The enrolling agent; also the aggregate its credential events land in.</param>
    /// <param name="keyId">The <c>kid</c> this enrollment registered, recorded on the event.</param>
    /// <param name="ownerVerified">Table 11's "owner verified", as the Registrar reports it.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<AgentEnrollment>> RecordAsync(
        string agentId,
        string keyId,
        bool ownerVerified,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);

        // One aggregate per agent, for the reason IngestPipeline gives for one aggregate per post:
        // a single shared "agents" stream would make every concurrent enrollment an
        // optimistic-concurrency conflict against every other, and agents are not a consistency
        // boundary with each other. Here the per-agent stream does a second job the post streams do
        // not need -- it is what lets AggregateVersion.New mean "this agent has never enrolled".
        if (!AggregateId.Create(agentId).TryGetValue(out var aggregate, out var aggregateError))
            return Result<AgentEnrollment>.Fail(aggregateError!);

        if (!ActorId.Create(agentId).TryGetValue(out var actor, out var actorError))
            return Result<AgentEnrollment>.Fail(actorError!);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var read = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var history, out var readError))
                return Result<AgentEnrollment>.Fail(readError!);

            if (!AggregateVersion.From(history!.Count).TryGetValue(out var version, out var versionError))
                return Result<AgentEnrollment>.Fail(versionError!);

            // This aggregate's slice carries credential events only -- posts live in their own
            // streams -- so the standing folded here has no question count and no reached-T1
            // instant, and neither is read below. What is read is what this stream is the whole
            // record of: whether an enrollment exists, when it was, and what the flag says now.
            var standing = AgentStandingProjector.Fold(history)
                .GetValueOrDefault(agentId);

            var attempted = standing?.EnrolledAt is { } enrolledAt
                ? await UpdateOwnerVerificationAsync(
                    aggregate, actor, version, agentId, enrolledAt, standing.OwnerVerified, ownerVerified, cancellationToken)
                    .ConfigureAwait(false)
                : await AppendEnrollmentAsync(
                    aggregate, actor, agentId, keyId, ownerVerified, cancellationToken).ConfigureAwait(false);

            if (attempted.TryGetValue(out var enrollment, out var attemptError))
                return Result<AgentEnrollment>.Ok(enrollment!);

            if (!IsConcurrencyConflict(attemptError!))
                return Result<AgentEnrollment>.Fail(attemptError!);
        }

        return Result<AgentEnrollment>.Fail(EnrollmentErrors.ContendedAggregate(agentId, Attempts));
    }

    private async Task<Result<AgentEnrollment>> AppendEnrollmentAsync(
        AggregateId aggregate,
        ActorId actor,
        string agentId,
        string keyId,
        bool ownerVerified,
        CancellationToken cancellationToken)
    {
        var payload = new JsonValue.Object(
        [
            new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
            new(AgentStandingProjector.KeyIdField, new JsonValue.String(keyId)),
            new(AgentStandingProjector.OwnerVerifiedField, new JsonValue.Bool(ownerVerified)),

            // R4.21's "reason", carried on the event rather than supplied by whatever reads it
            // back. The trigger is Table 6's SuccessfulEnrollment and is implied by the event type;
            // this is the free-text elaboration TransitionReason exists for.
            new(AgentStandingProjector.ReasonField, new JsonValue.String(EnrollmentReason)),
        ]);

        // No server_ts in the payload. The store stamps the event, and R6.5 makes that the Forum's
        // observation; a second instant in the payload would be a claim that could disagree with it.
        var appended = await AppendAsync(
            aggregate, actor, AgentStandingProjector.EnrolledType, payload, AggregateVersion.New, cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(events => new AgentEnrollment(
            events[0].ServerTimestamp.Value, ownerVerified, WasAlreadyEnrolled: false));
    }

    private async Task<Result<AgentEnrollment>> UpdateOwnerVerificationAsync(
        AggregateId aggregate,
        ActorId actor,
        AggregateVersion version,
        string agentId,
        DateTimeOffset enrolledAt,
        bool current,
        bool requested,
        CancellationToken cancellationToken)
    {
        if (current == requested)
            return Result<AgentEnrollment>.Ok(new AgentEnrollment(enrolledAt, current, WasAlreadyEnrolled: true));

        var payload = new JsonValue.Object(
        [
            new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
            new(AgentStandingProjector.OwnerVerifiedField, new JsonValue.Bool(requested)),
        ]);

        var appended = await AppendAsync(
            aggregate, actor, AgentStandingProjector.OwnerVerificationRecordedType, payload, version, cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(_ => new AgentEnrollment(enrolledAt, requested, WasAlreadyEnrolled: true));
    }

    private async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregate,
        ActorId actor,
        string eventType,
        JsonValue payload,
        AggregateVersion expectedVersion,
        CancellationToken cancellationToken)
    {
        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<IReadOnlyList<AppendedEvent>>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<IReadOnlyList<AppendedEvent>>.Fail(eventIdError!);

        if (!EventType.Create(eventType).TryGetValue(out var type, out var typeError))
            return Result<IReadOnlyList<AppendedEvent>>.Fail(typeError!);

        return await _events
            .AppendAsync(aggregate, expectedVersion, [new DomainEvent(eventId, type, actor, payload)], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Matched on the slug the domain publishes rather than on a locally written string, so a
    /// rename of the condition is a compile error here instead of a retry loop that silently stops
    /// retrying.
    /// </summary>
    private static bool IsConcurrencyConflict(Error error) =>
        string.Equals(error.Type, DomainErrors.ConcurrencyConflictType, StringComparison.Ordinal);

    private const string EnrollmentReason = "Enrollment accepted: agent key registered with the Registrar";
}

/// <summary>RFC 9457 problem-type slugs the enrollment use case emits.</summary>
public static class EnrollmentErrors
{
    /// <summary>
    /// The enrollment lost its optimistic-concurrency race on every attempt. Distinct from
    /// <see cref="DomainErrors.ConcurrencyConflict"/>, which the caller never sees here: a single
    /// conflict is expected and retried, and only exhausting the retries is a condition worth
    /// reporting.
    /// </summary>
    public static Error ContendedAggregate(string agentId, int attempts) => new(
        "curia/enroll/contended",
        "The agent's credential stream is being appended to concurrently; the enrollment was not recorded",
        $"agent={agentId} attempts={attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
}
