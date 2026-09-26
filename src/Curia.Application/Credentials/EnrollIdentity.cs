using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// CS-16's <c>Enroll</c>: the one path by which a key enters the Registrar for an identity (R4.16,
/// R4.31, R4.32; errata G14). Three steps, in an order that matters:
/// <list type="number">
/// <item><b>The log's binding.</b> An identity whose <c>agent.enrolled</c> names another <c>kid</c>
/// is refused before the key store is touched. This is what holds when the store has lost the
/// identity's rows, or was written before G14 and holds keys no enrollment bound.</item>
/// <item><b>The key store's enrollment</b> (<see cref="IAuthorKeyRegistry.EnrollAsync"/>): registers
/// only for an identity holding no key, atomically against a concurrent enrollment of the same
/// identity, and refuses other bytes under a held <c>kid</c>. For an identity the log has already
/// enrolled, "holding no key" means the store lost its row, and the bound <c>kid</c> is registered
/// again from the enrollment's instant -- R4.31's one exception -- unless another identity has
/// registered that <c>kid</c> since, which the store refuses as it refuses any <c>kid</c> held
/// elsewhere.</item>
/// <item><b>The log's record</b> (<see cref="EnrollAgent"/>): appended once, and re-read and
/// reported thereafter. A refusal at either earlier step appends nothing.</item>
/// </list>
///
/// <para><b>Why the store before the log, and not the reverse.</b> The store is where a <c>kid</c>
/// already held by another identity is discovered; appending the enrollment first would bind the
/// identity, permanently, to a <c>kid</c> it can never register. Registered-then-not-recorded is the
/// failure this order can leave -- a crash between the two -- and it is the recoverable one: the same
/// request, sent again, finds its own key held and appends the record.</para>
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

        if (!AggregateId.Create(agentId).TryGetValue(out var aggregate, out var aggregateError))
            return Result<AgentEnrollment>.Fail(aggregateError!);

        var read = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var history, out var readError))
            return Result<AgentEnrollment>.Fail(readError!);

        var binding = EnrollmentBinding.Find(history!, agentId);
        if (binding is not null && !binding.Binds(key.Kid))
            return Result<AgentEnrollment>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));

        // A fresh identity's key is valid from now. An enrolled one reaches the store only with its
        // bound kid, and the store registers it only if it lost the row: then the key is dated from
        // the enrollment the log records, or every post signed before the loss would fall outside its
        // window (R6.31). That instant can trail the lost row's start, a clock read taken before its
        // insert, and no post's server_ts precedes it: a post is admitted only once the log holds the
        // enrollment. When the store still holds the key, the date is not read.
        var notBefore = binding?.EnrolledAt ?? _clock.GetUtcNow();

        var registered = await _keys.EnrollAsync(agentId, key, notBefore, cancellationToken).ConfigureAwait(false);
        if (!registered.TryGetValue(out _, out var keyError))
            return Result<AgentEnrollment>.Fail(keyError!);

        return await _log.RecordAsync(agentId, key.Kid, cancellationToken).ConfigureAwait(false);
    }
}
