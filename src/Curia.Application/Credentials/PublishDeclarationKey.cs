using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// Errata G67, R6.62: the operator publishes the declaration key to the log, out of band, as a <c>log.declaration-key</c> entry (<c>{ kid, jwk, valid_from }</c>).
/// The entry is not a lifecycle act and the lifecycle guard does not govern it (Amendment 2 ruling 29): publishing a key ends no agent's key, and the owner
/// publishes it before the hosted instance is announced, so that the first slot (R6.62: the first declaration key published is the one) is closed. It refuses
/// a key that is a published log key and a second declaration key, before anything is appended.
/// </summary>
public sealed class PublishDeclarationKey
{
    private readonly IEventStore _events;
    private readonly TimeProvider _clock;
    private readonly UlidGenerator _ids;

    public PublishDeclarationKey(IEventStore events, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _clock = clock;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>Appends <paramref name="key"/>'s public JWK as a <c>log.declaration-key</c> entry and returns its log index.</summary>
    public async Task<Result<long>> PublishAsync(ILifecycleStatementSigner key, ActorId operatorName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        var read = await _events.ReadAllAsync(ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var log, out var readError))
            return Result<long>.Fail(readError!);
        if (!ActaLog.Fold(log!).TryGetValue(out var acta, out var foldError))
            return Result<long>.Fail(foldError!);

        if (acta!.Keys.Any(k => string.Equals(k.Kid, key.Kid, StringComparison.Ordinal)))
            return Result<long>.Fail(KeyActErrors.DeclarationKeyIsLogKey());
        if (!acta.DeclarationKeys.IsEmpty)
            return Result<long>.Fail(KeyActErrors.DeclarationKeyAlreadyPublished());

        var published = acta.DeclarationKeys.Length;
        if (!AggregateId.Create(LogEntries.DeclarationKeysAggregate).TryGetValue(out var aggregate, out var aggregateError))
            return Result<long>.Fail(aggregateError!);
        if (!AggregateVersion.From(published).TryGetValue(out var version, out var versionError))
            return Result<long>.Fail(versionError!);
        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<long>.Fail(idError!);
        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<long>.Fail(eventIdError!);
        if (!EventType.Create(LogEntries.DeclarationKeyType).TryGetValue(out var type, out var typeError))
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
