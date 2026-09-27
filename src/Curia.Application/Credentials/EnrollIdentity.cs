using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// CS-16's <c>Enroll</c>: the one path by which a key enters the Registrar for an identity (R4.16,
/// R4.31, R4.32, R4.33; errata G14, G15). A refusal, then three steps, in an order that matters:
/// <list type="number">
/// <item><b>The identifier is the agent's own (R4.33).</b> An identifier beginning with a
/// prefix the Forum's own writers mint aggregates under (<see cref="ReservedIdentifiers"/>) is refused
/// first, with no read. After the read, so is one whose aggregate holds events and none of them this
/// identifier's enrollment: a post's, for instance. An agent's stream always begins with its own
/// <c>agent.enrolled</c>, so any other first event means the log keeps the aggregate for something
/// else, and the record could never be appended to it.</item>
/// <item><b>The log's binding.</b> An enrolled identity is refused, before the key store is touched,
/// any <c>kid</c> the log binds no key under for it, and, under a <c>kid</c> whose binding carries
/// the key (R4.34), any other key. This is what holds when the store has lost the identity's rows,
/// or was written before G14 and holds keys no enrollment bound (R4.31 rev., errata G16). An
/// identifier the log has not enrolled is refused, before the store is asked to register, while the
/// store holds more than one key for it: nothing in the log says which is its own.</item>
/// <item><b>The key store's enrollment</b> (<see cref="IAuthorKeyRegistry.EnrollAsync"/>): registers
/// only for an identity holding no key, atomically against a concurrent enrollment of the same
/// identity, and refuses other bytes under a held <c>kid</c>. For an identity the log has already
/// enrolled, "holding no key" means the store lost its row, and the bound key is registered again
/// from the instant the log bound it -- R4.31's one exception -- unless another identity has
/// registered that <c>kid</c> since, which the store refuses as it refuses any <c>kid</c> held
/// elsewhere. For an identity enrolled before R4.34 the log binds the <c>kid</c> alone, and the
/// exception registers whatever bytes arrive under it.</item>
/// <item><b>The log's record</b> (<see cref="EnrollAgent"/>): appended once, and re-read and
/// reported thereafter. A refusal at any earlier step appends nothing.</item>
/// </list>
///
/// <para><b>Why the store before the log, and not the reverse.</b> The store is where a <c>kid</c>
/// already held by another identity is discovered; appending the enrollment first would bind the
/// identity, permanently, to a <c>kid</c> it can never register. Registered-then-not-recorded is what
/// this order leaves when the log's append fails after the store's, whether by a crash between the
/// two or by the event store refusing the append. It recovers when the append can succeed: the same
/// request, sent again, finds its own key held and appends the record. An identifier whose aggregate
/// the log uses for something else could never recover, which is why R4.33 refuses it before the
/// store is asked.</para>
/// </summary>
public sealed class EnrollIdentity
{
    private readonly IEventReader _events;
    private readonly IAuthorKeyRegistry _keys;
    private readonly EnrollAgent _log;
    private readonly TimeProvider _clock;

    public EnrollIdentity(IEventReader events, IAuthorKeyRegistry keys, EnrollAgent log, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _keys = keys;
        _log = log;
        _clock = clock;
    }

    /// <summary>Enrolls <paramref name="agentId"/> with <paramref name="key"/>, or reports why not.</summary>
    public async Task<Result<AgentEnrollment>> EnrollAsync(
        string agentId, PublicKeyMaterial key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        // R4.33's first clause, before any read: a prefix the Forum's own writers mint aggregates under.
        if (ReservedIdentifiers.IsReserved(agentId))
            return Result<AgentEnrollment>.Fail(EnrollmentErrors.IdentifierReserved(agentId));

        if (!AggregateId.Create(agentId).TryGetValue(out var aggregate, out var aggregateError))
            return Result<AgentEnrollment>.Fail(aggregateError!);

        var read = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var history, out var readError))
            return Result<AgentEnrollment>.Fail(readError!);

        var binding = EnrollmentBinding.Find(history!, agentId);

        // R4.33's second clause: the aggregate holds events and none is this identifier's enrollment,
        // so the log keeps it for something else and could never append the record. Refused here,
        // before the store, or the store would register a key no enrollment ever binds.
        if (binding is null && history!.Count > 0)
            return Result<AgentEnrollment>.Fail(EnrollmentErrors.IdentifierReserved(agentId));

        // R4.31 rev. (errata G16): an identifier the log records no enrollment of is bound by the first
        // request that re-presents a key the store holds for it, and only while the store holds one.
        // More than one is held only where errata G14's hole wrote them: since G14 the store registers
        // a key only for an identifier holding none, so no enrollment, concurrent or not, raises this
        // count past one, and the read needs no lock. Nothing in the log says which of several is the
        // identity's own. Binding the one a request presents would let anyone holding its public key
        // make it the identity's key, and refuse the identity its own (R4.35).
        if (binding is null && (await _keys.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false)).Count > 1)
            return Result<AgentEnrollment>.Fail(EnrollmentErrors.KeysAmbiguous(agentId));

        var bound = binding?.For(key.Kid);
        if (binding is not null && bound is null)
            return Result<AgentEnrollment>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));

        // R4.31 rev. (errata G16): where the log carries the key, only that key. This is what refuses
        // other bytes under a bound kid after the store lost its row, which the store cannot see.
        if (bound is not null && !bound.Holds(key))
            return Result<AgentEnrollment>.Fail(AuthorKeyErrors.MaterialImmutable(key.Kid));

        // A fresh identity's key is valid from now. An enrolled one reaches the store only with a key
        // the log binds, and the store registers it only if it lost the row: then the key is dated
        // from the instant the log bound it, or every post signed before the loss would fall outside
        // its window (R6.31). That instant can trail the lost row's start, a clock read taken before
        // its insert, and no post's server_ts precedes it: a post is admitted only once the log holds
        // the binding. When the store still holds the key, the date is not read.
        var notBefore = bound?.BoundAt ?? _clock.GetUtcNow();

        var registered = await _keys.EnrollAsync(agentId, key, notBefore, cancellationToken).ConfigureAwait(false);
        if (!registered.TryGetValue(out _, out var keyError))
            return Result<AgentEnrollment>.Fail(keyError!);

        return await _log.RecordAsync(agentId, key, cancellationToken).ConfigureAwait(false);
    }
}
