using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

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

    /// <summary>The operator namespace the composition root applies (plan D4); an actor in it must name someone.</summary>
    private const string OperatorPrefix = "operator:";

    /// <summary>The not-enrolled detail for an agent that is no agent id: the caller's text is not echoed, as RaiseFlag's refusal of a value that is no post id is not.</summary>
    internal const string NoAgentId = "(agent is no agent id)";

    private readonly IEventStore _events;
    private readonly LifecycleVerifier _verifier;
    private readonly UlidGenerator _ids;

    /// <summary>
    /// The verifier decides which rows are honoured (R4.43, R6.67): a writer decides what it refuses over the honoured facts, so a forged row never tells an operator "owner binding immutable",
    /// and it re-reads after its own append to learn whether the row it wrote is one the Forum honours (R11.97).
    /// </summary>
    public AttestOwner(IEventStore events, LifecycleVerifier verifier, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _verifier = verifier;
        _ids = new UlidGenerator(clock);
    }

    /// <param name="agentId">The agent whose owner is being attested; also the aggregate the event lands in.
    /// The host's parse outcome (R11.34, errata G18), so each refusal is answered where this use case
    /// answered it before the type existed: one that is no agent id is refused as not enrolled, after
    /// the self-attestation, operator-name and reason checks.</param>
    /// <param name="owner">R4.1's owner. Must match any owner an earlier attestation named.</param>
    /// <param name="verified">Whether the owner is verified as of this attestation. <see langword="false"/> records a lapse.</param>
    /// <param name="method">Which of R4.24's proofs was satisfied, or lapsed.</param>
    /// <param name="reason">R4.21's free-text reason, carried on the event.</param>
    /// <param name="attestedBy">The attesting actor -- an operator, or the owner. Never the agent.</param>
    /// <param name="signer">The operator-acts key (R6.66), per call: the Forum holds no part of it (R4.43), so the host that runs this passes the key it holds. Must be the first operator key the log publishes.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<OwnerAttestation>> RecordAsync(
        Result<AgentId> agentId,
        OwnerId owner,
        bool verified,
        OwnerVerificationMethod method,
        string reason,
        ActorId attestedBy,
        IOperatorActSigner signer,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(signer);

        agentId.TryGetValue(out var agent, out _);

        if (agent is not null && string.Equals(attestedBy.Value, agent.Value, StringComparison.Ordinal))
            return Result<OwnerAttestation>.Fail(AttestationErrors.SelfAttestation(agent.Value));

        // "operator: " keeps the namespace convention and names no one, in a leaf that is public and
        // permanent.
        if (attestedBy.Value is { } actor
            && actor.StartsWith(OperatorPrefix, StringComparison.Ordinal)
            && actor.AsSpan(OperatorPrefix.Length).IsWhiteSpace())
            return Result<OwnerAttestation>.Fail(AttestationErrors.BlankOperatorName());

        if (!TransitionReason.Create(reason).TryGetValue(out _, out var reasonError))
            return Result<OwnerAttestation>.Fail(reasonError!);

        if (agent is null) return Result<OwnerAttestation>.Fail(AttestationErrors.NotEnrolled(NoAgentId));

        if (!AggregateId.Create(agent.Value).TryGetValue(out var aggregate, out var aggregateError))
            return Result<OwnerAttestation>.Fail(aggregateError!);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            // The whole log: a statement's prev is a position in it (R6.67), and what is honoured is decided over all of it.
            var read = await _events.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var log, out var readError))
                return Result<OwnerAttestation>.Fail(readError!);

            var seen = _verifier.OfLog(log!);
            var standing = seen.Standings().GetValueOrDefault(agent.Value);

            if (standing?.EnrolledAt is null)
                return Result<OwnerAttestation>.Fail(AttestationErrors.NotEnrolled(agent.Value));

            // R6.67: the stream's root is its first event, and an agent's stream begins with its enrolment. Without one no prev exists and every row would be skipped.
            var chain = OperatorActStatements.ChainOf(seen, agent.Value, AgentStandingProjector.EnrolledType, AgentStandingProjector.OwnerAttestedType);
            if (chain.Prev is not { } prev)
                return Result<OwnerAttestation>.Fail(AttestationErrors.NotEnrolled(agent.Value));

            if (standing.OwnerId is { } bound && !string.Equals(bound, owner.Value, StringComparison.Ordinal))
                return Result<OwnerAttestation>.Fail(AttestationErrors.OwnerBindingImmutable(agent.Value, bound, owner.Value));

            // A row signed under any key but the first the log published is skipped, so none is written (R6.67 step 6, R11.97).
            if (!OperatorActStatements.IsFirstOperatorKey(log!, signer))
                return Result<OwnerAttestation>.Fail(KeyActErrors.OperatorKeyNotPublished());

            if (!AggregateVersion.From(chain.Version).TryGetValue(out var version, out var versionError))
                return Result<OwnerAttestation>.Fail(versionError!);

            var attempted = await AppendAsync(
                aggregate, version, prev, agent.Value, owner, verified, method, reason, attestedBy, signer, cancellationToken)
                .ConfigureAwait(false);

            if (attempted.TryGetValue(out var attestation, out var attemptError))
                return Result<OwnerAttestation>.Ok(attestation!);

            if (!string.Equals(attemptError!.Type, DomainErrors.ConcurrencyConflictType, StringComparison.Ordinal))
                return Result<OwnerAttestation>.Fail(attemptError);
        }

        return Result<OwnerAttestation>.Fail(AttestationErrors.ContendedAggregate(agent.Value, Attempts));
    }

    private async Task<Result<OwnerAttestation>> AppendAsync(
        AggregateId aggregate,
        AggregateVersion expectedVersion,
        long prev,
        string agentId,
        OwnerId owner,
        bool verified,
        OwnerVerificationMethod method,
        string reason,
        ActorId attestedBy,
        IOperatorActSigner signer,
        CancellationToken cancellationToken)
    {
        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<OwnerAttestation>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<OwnerAttestation>.Fail(eventIdError!);

        if (!EventType.Create(AgentStandingProjector.OwnerAttestedType).TryGetValue(out var type, out var typeError))
            return Result<OwnerAttestation>.Fail(typeError!);

        if (!OperatorActStatements
                .SignAttestation(attestedBy.Value, agentId, OwnerVerificationMethods.Wire(method), owner.Value, verified, prev, reason, signer)
                .TryGetValue(out var signed, out var signError))
            return Result<OwnerAttestation>.Fail(signError!);

        // No server_ts in the payload, for the reason EnrollAgent gives: the store stamps the event,
        // and a second instant here would be a claim that could disagree with it. The row states the
        // statement's members (R6.67 step 3: each equal to the statement's own) beside the statement
        // and its authorization; the actor is the event's, and prev and typ are the statement's alone.
        var payload = new JsonValue.Object(
        [
            .. OperatorActStatements.Without(signed!.Members, "actor_id", "prev", "typ"),
            new(OperatorActStatements.StatementMember, new JsonValue.String(signed.Statement)),
            new(OperatorActStatements.AuthorizationMember, new JsonValue.String(signed.Authorization)),
        ]);

        var appended = await _events
            .AppendAsync(aggregate, expectedVersion, [new DomainEvent(eventId, type, attestedBy, payload)], cancellationToken)
            .ConfigureAwait(false);
        if (!appended.TryGetValue(out var written, out var appendError))
            return Result<OwnerAttestation>.Fail(appendError!);

        // R11.97: the writer cannot see its own row skipped and call it done. The row is in the log either way; whether the Forum honours it is the fold's to say.
        var again = await _events.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!again.TryGetValue(out var after, out var afterError))
            return Result<OwnerAttestation>.Fail(afterError!);

        var found = OperatorActStatements.Find(_verifier.OfLog(after!), eventId.Value);
        if (found is not { } row)
            return Result<OwnerAttestation>.Fail(ActErrors.NotHonoured(-1, "unread"));
        if (row.Predicate is { } skipped)
            return Result<OwnerAttestation>.Fail(ActErrors.NotHonoured(row.Index, skipped));

        return Result<OwnerAttestation>.Ok(new OwnerAttestation(written[0].ServerTimestamp.Value, owner, verified, method));
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

    /// <summary>An attestation names who attested, publicly and permanently, so the name after <c>operator:</c> may not be blank. Names no value.</summary>
    public static Error BlankOperatorName() => new(
        "curia/attest/blank-operator-name",
        "The operator's name is blank; an attestation names who attested (R4.30)",
        "the attesting actor must be named operator:<name>, with a <name> that is not blank");

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
