using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// Errata G70, R6.66: the operator publishes the key that signs its out-of-band acts to the log, out of band, as a <c>log.operator-key</c> entry (<c>{ kid, jwk, valid_from }</c>). The entry is not a
/// lifecycle act and the lifecycle guard does not govern it (publishing a key ends no agent's key and grants no act by itself), and the owner publishes it before the hosted instance is announced, so that
/// the first slot (R6.66: the first operator key published is the one) is closed. It refuses, before anything is appended and in this order: a key that is a published log key, a key that is a published
/// declaration key (each by <c>kid</c> or by public key: a key is published in the class it was first published in and in no other), and a second operator key.
/// </summary>
public sealed class PublishOperatorKey
{
    private readonly IEventStore _events;
    private readonly TimeProvider _clock;
    private readonly UlidGenerator _ids;

    public PublishOperatorKey(IEventStore events, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _clock = clock;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>Appends <paramref name="key"/>'s public JWK as a <c>log.operator-key</c> entry and returns its log index.</summary>
    public async Task<Result<long>> PublishAsync(IOperatorActSigner key, ActorId operatorName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        var read = await _events.ReadAllAsync(ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var log, out var readError))
            return Result<long>.Fail(readError!);
        if (!ActaLog.Fold(log!).TryGetValue(out var acta, out var foldError))
            return Result<long>.Fail(foldError!);

        // The class the log first published this key under, by kid or public key, from the one function the verifier reads (PublishedKeys).
        var firstUnder = PublishedKeys.OfLog(log!).FirstPublishedClass(key.Kid, key.PublicJwk());
        if (string.Equals(firstUnder, LogEntries.KeyType, StringComparison.Ordinal))
            return Result<long>.Fail(KeyActErrors.OperatorKeyIsLogKey());
        if (string.Equals(firstUnder, LogEntries.DeclarationKeyType, StringComparison.Ordinal))
            return Result<long>.Fail(KeyActErrors.OperatorKeyIsDeclarationKey());
        if (!acta!.OperatorKeys.IsEmpty)
            return Result<long>.Fail(KeyActErrors.OperatorKeyAlreadyPublished());

        // The aggregate's version is the entries it holds, which is not the keys that hold a position: a row of the aggregate that holds none still counts.
        var published = log!.Count(e => string.Equals(e.AggregateId.Value, LogEntries.OperatorKeysAggregate, StringComparison.Ordinal));
        if (!AggregateId.Create(LogEntries.OperatorKeysAggregate).TryGetValue(out var aggregate, out var aggregateError))
            return Result<long>.Fail(aggregateError!);
        if (!AggregateVersion.From(published).TryGetValue(out var version, out var versionError))
            return Result<long>.Fail(versionError!);
        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<long>.Fail(idError!);
        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<long>.Fail(eventIdError!);
        if (!EventType.Create(LogEntries.OperatorKeyType).TryGetValue(out var type, out var typeError))
            return Result<long>.Fail(typeError!);

        var payload = new JsonValue.Object(
        [
            new(LogEntries.KidMember, new JsonValue.String(key.Kid)),
            new(LogEntries.JwkMember, key.PublicJwk()),
            new(LogEntries.ValidFromMember, new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(_clock.GetUtcNow())))),
        ]);

        var appended = await _events.AppendAsync(aggregate!, version!, [new DomainEvent(eventId!, type!, operatorName, payload)], ct).ConfigureAwait(false);
        return appended.Map(entries => (long)log.Count);
    }
}
