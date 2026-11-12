using Curia.Application.Ports;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Acta;

/// <summary>
/// PR H (errata G67): a log of named entries over the in-memory store, for the probes that fold one (growth, the declaration key's entry, the lifecycle rows).
/// Each entry is <c>(aggregate, type, payload)</c>, appended in order, one event to its stream's next version, so the log index of an entry is its position
/// in the list. A head's payload is well-formed and its signature is a placeholder: the fold reads shapes, and no probe here verifies a head.
/// </summary>
internal static class LogBuilder
{
    internal sealed record Entry(string Aggregate, string Type, JsonValue Payload);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} {e.Detail}"));

    internal static Entry Post(string aggregate = "post") => new(aggregate, "post.accepted", new JsonValue.Object([new("n", new JsonValue.Number(1))]));

    internal static Entry LogKey(string kid = "log-key") => new(LogEntries.KeysAggregate, LogEntries.KeyType, KeyPayload(kid));

    internal static Entry DeclarationKey(string kid = "declaration-key") =>
        new(LogEntries.DeclarationKeysAggregate, LogEntries.DeclarationKeyType, KeyPayload(kid));

    /// <summary>A head covering the first <paramref name="treeSize"/> entries; it must sit at a log index of at least that.</summary>
    internal static Entry Head(long treeSize) => new(
        LogEntries.HeadsAggregate,
        LogEntries.HeadType,
        new JsonValue.Object(
        [
            new(LogEntries.HeadMember, new JsonValue.Object(
            [
                new(LogEntries.RootHashMember, new JsonValue.String("sha256:" + new string('0', 64))),
                new(LogEntries.TimestampMember, new JsonValue.String("2026-11-11T09:30:00.000000Z")),
                new(LogEntries.TreeSizeMember, new JsonValue.Number(treeSize)),
            ])),
            new(LogEntries.KidMember, new JsonValue.String("log-key")),
            new(LogEntries.SignatureMember, new JsonValue.String("placeholder")),
        ]));

    internal static Entry Of(string aggregate, string type) => new(aggregate, type, new JsonValue.Object([new("agent_id", new JsonValue.String(aggregate))]));

    internal static JsonValue.Object KeyPayload(string kid) => new(
    [
        new(LogEntries.KidMember, new JsonValue.String(kid)),
        new(LogEntries.JwkMember, new JsonValue.Object([new("kid", new JsonValue.String(kid)), new("kty", new JsonValue.String("EC"))])),
        new(LogEntries.ValidFromMember, new JsonValue.String("2026-11-11T09:00:00.000000Z")),
    ]);

    internal static IReadOnlyList<AppendedEvent> LogOf(params Entry[] entries)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(new ManualTimeProvider(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero)), new PermissiveLifecycleAppendGate());
        var versions = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            versions.TryGetValue(entry.Aggregate, out var version);
            Require(store.AppendAsync(
                Require(AggregateId.Create(entry.Aggregate)),
                Require(AggregateVersion.From(version)),
                [new DomainEvent(Require(EventId.Create($"e{i}")), Require(EventType.Create(entry.Type)), Require(ActorId.Create("operator:ops")), entry.Payload)],
                ct).GetAwaiter().GetResult());
            versions[entry.Aggregate] = version + 1;
        }

        return Require(store.ReadAllAsync(ct).GetAwaiter().GetResult());
    }
}
