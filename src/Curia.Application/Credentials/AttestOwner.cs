using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>What the log says after an attestation has been recorded.</summary>
/// <param name="AttestedAt">The store's <c>server_ts</c> for the attestation event.</param>
/// <param name="Owner">The owner the agent is bound to (R4.1).</param>
/// <param name="Verified">Table 11's "owner verified", as it now stands.</param>
/// <param name="Method">Which of R4.24's proofs the attestation records.</param>
public sealed record OwnerAttestation(
    DateTimeOffset AttestedAt,
    OwnerId Owner,
    bool Verified,
    OwnerVerificationMethod Method);

/// <summary>
/// R4.30 (errata G5): records that an operator, or the owner, attests an agent's owner as verified
/// -- or no longer verified -- as an append-only event naming the owner, the R4.24 proof, and the
/// attesting actor.
///
/// <para><b>This is the only way owner verification enters the log.</b> The enrollment request
/// used to carry the flag, which put the one control §4.6 leans on in the hands of the party it
/// constrains. It no longer does; <see cref="EnrollAgent"/> records nothing about the owner, and
/// nothing an agent presents can. What an agent can do is ask, out of band, and be refused
/// <c>answer:create</c> until someone with standing to say so has said so.</para>
///
/// <para><b>What the use case can check, and what it cannot.</b> It refuses an attestation whose
/// actor is the agent it would promote, because that is the exact shape of the defect and the one
/// identity the domain can recognise. It cannot tell an operator from anything else: no agent
/// identifier form is enforced (plan D4), so <c>operator:</c> is a convention the composition root
/// keeps, not a rule the domain holds. It refuses a second attestation that names a different owner
/// -- R4.1 makes the binding immutable, and a transfer is retirement plus re-enrollment -- and it
/// refuses to attest for an agent the log has never enrolled, because a per-agent stream that began
/// with an attestation would make the enrollment's <see cref="AggregateVersion.New"/> guard refuse
/// the enrollment itself.</para>
///
/// <para><b>Every attestation is appended, including a repeated one.</b> <see cref="EnrollAgent"/>
/// declines to append a fact the log already holds because a client re-announces its enrollment on
/// every token refresh. An attestation is an operator's act, rare and deliberate, and the log is
/// the record of acts: two attestations are two facts, even when they agree.</para>
/// </summary>
public sealed class AttestOwner
{
    /// <summary>One retry, for the reason <see cref="EnrollAgent"/> gives: a conflict means someone else appended to this agent's stream, and the re-read observes it.</summary>
    private const int Attempts = 2;

    private readonly IEventStore _events;
    private readonly UlidGenerator _ids;

    public AttestOwner(IEventStore events, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _ids = new UlidGenerator(clock);
    }

    /// <param name="agentId">The agent whose owner is being attested; also the aggregate the event lands in.</param>
    /// <param name="owner">R4.1's owner. Must match any owner an earlier attestation named.</param>
    /// <param name="verified">Whether the owner is verified as of this attestation. <see langword="false"/> records a lapse.</param>
    /// <param name="method">Which of R4.24's proofs was satisfied, or lapsed.</param>
    /// <param name="reason">R4.21's free-text reason, carried on the event.</param>
    /// <param name="attestedBy">The attesting actor -- an operator, or the owner. Never the agent.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<OwnerAttestation>> RecordAsync(
        string agentId,
        OwnerId owner,
        bool verified,
        OwnerVerificationMethod method,
        string reason,
        ActorId attestedBy,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        if (string.Equals(attestedBy.Value, agentId, StringComparison.Ordinal))
            return Result<OwnerAttestation>.Fail(AttestationErrors.SelfAttestation(agentId));

        if (!TransitionReason.Create(reason).TryGetValue(out _, out var reasonError))
            return Result<OwnerAttestation>.Fail(reasonError!);

        if (!AggregateId.Create(agentId).TryGetValue(out var aggregate, out var aggregateError))
            return Result<OwnerAttestation>.Fail(aggregateError!);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var read = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var history, out var readError))
                return Result<OwnerAttestation>.Fail(readError!);

            if (!AggregateVersion.From(history!.Count).TryGetValue(out var version, out var versionError))
                return Result<OwnerAttestation>.Fail(versionError!);

            var standing = AgentStandingProjector.Fold(history).GetValueOrDefault(agentId);

            if (standing?.EnrolledAt is null)
                return Result<OwnerAttestation>.Fail(AttestationErrors.NotEnrolled(agentId));

            if (standing.OwnerId is { } bound && !string.Equals(bound, owner.Value, StringComparison.Ordinal))
                return Result<OwnerAttestation>.Fail(AttestationErrors.OwnerBindingImmutable(agentId, bound, owner.Value));

            var attempted = await AppendAsync(
                aggregate, version, agentId, owner, verified, method, reason, attestedBy, cancellationToken)
                .ConfigureAwait(false);

            if (attempted.TryGetValue(out var attestation, out var attemptError))
                return Result<OwnerAttestation>.Ok(attestation!);

            if (!string.Equals(attemptError!.Type, DomainErrors.ConcurrencyConflictType, StringComparison.Ordinal))
                return Result<OwnerAttestation>.Fail(attemptError);
        }

        return Result<OwnerAttestation>.Fail(AttestationErrors.ContendedAggregate(agentId, Attempts));
    }

    private async Task<Result<OwnerAttestation>> AppendAsync(
        AggregateId aggregate,
        AggregateVersion expectedVersion,
        string agentId,
        OwnerId owner,
        bool verified,
        OwnerVerificationMethod method,
        string reason,
        ActorId attestedBy,
        CancellationToken cancellationToken)
    {
        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<OwnerAttestation>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<OwnerAttestation>.Fail(eventIdError!);

        if (!EventType.Create(AgentStandingProjector.OwnerAttestedType).TryGetValue(out var type, out var typeError))
            return Result<OwnerAttestation>.Fail(typeError!);

        // No server_ts in the payload, for the reason EnrollAgent gives: the store stamps the event,
        // and a second instant here would be a claim that could disagree with it.
        var payload = new JsonValue.Object(
        [
            new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
            new(AgentStandingProjector.OwnerIdField, new JsonValue.String(owner.Value)),
            new(AgentStandingProjector.OwnerVerifiedField, new JsonValue.Bool(verified)),
            new(AgentStandingProjector.MethodField, new JsonValue.String(OwnerVerificationMethods.Wire(method))),
            new(AgentStandingProjector.ReasonField, new JsonValue.String(reason)),
        ]);

        var appended = await _events
            .AppendAsync(aggregate, expectedVersion, [new DomainEvent(eventId, type, attestedBy, payload)], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(events => new OwnerAttestation(events[0].ServerTimestamp.Value, owner, verified, method));
    }
}

/// <summary>RFC 9457 problem-type slugs the attestation use case emits.</summary>
public static class AttestationErrors
{
    /// <summary>The attesting actor is the agent being attested for -- the exact shape of the defect R4.30 closes.</summary>
    public static Error SelfAttestation(string agentId) => new(
        "curia/attest/self-attestation",
        "An agent cannot attest its own owner's verification; R4.30 requires an operator or the owner",
        $"agent={agentId}");

    /// <summary>No enrollment in the log, so there is no credential to attest an owner for.</summary>
    public static Error NotEnrolled(string agentId) => new(
        "curia/attest/not-enrolled",
        "The agent has not enrolled, so there is no credential to attest an owner for",
        $"agent={agentId}");

    /// <summary>R4.1: the agent-to-owner binding is immutable; a transfer is retirement plus re-enrollment.</summary>
    public static Error OwnerBindingImmutable(string agentId, string bound, string requested) => new(
        "curia/attest/owner-binding-immutable",
        "The agent is already bound to a different owner; R4.1 makes that binding immutable, and a transfer is retirement plus re-enrollment",
        $"agent={agentId} bound={bound} requested={requested}");

    /// <summary>Lost the optimistic-concurrency race on every attempt.</summary>
    public static Error ContendedAggregate(string agentId, int attempts) => new(
        "curia/attest/contended",
        "The agent's credential stream is being appended to concurrently; the attestation was not recorded",
        $"agent={agentId} attempts={attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
}
