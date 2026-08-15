using System.Collections.Immutable;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// The JSONB round trip the Stage 2 brief calls out explicitly: "a payload that changes shape
/// through the database would silently corrupt replay." This is the JSONB-specific half --
/// that every <see cref="JsonValue"/> case survives Postgres's parsed binary storage with its
/// value intact -- and it stays here rather than moving into the shared
/// <see cref="Curia.Application.Tests.EventStorePortContractTests"/> suite because it is about
/// what this adapter's column type does, not about what every <c>IEventStore</c> promises.
///
/// Member *order* is no longer in that category. It was, when this test was written -- the
/// comment here used to say JSONB storage fidelity was "specific to this adapter, not
/// something every IEventStore need promise", and that reading is what let the two adapters
/// return different payloads for the same input with nothing failing. R11.23 (errata E12) now
/// makes canonical member order a promise of the port, pinned in the contract suite by
/// <c>PayloadMembersReadBackInCanonicalOrderAtEveryDepth</c>, which runs against both adapters.
///
/// "Lossless" here means structural/semantic equality, not byte-for-byte wire identity:
/// Postgres's <c>jsonb</c> type is documented to reorder object members and collapse
/// whitespace on the way in (it stores a parsed binary form, not the original text), so two
/// JSON documents that are the same value can legitimately come back with their members in a
/// different order -- which is exactly why the adapter re-establishes canonical order on read
/// and why this test cannot be the thing that checks it. <see cref="CanonicalJson.Canonicalize"/>
/// already defines that equivalence -- it sorts every object's members by key (RFC 8785) -- so
/// re-canonicalizing both the original tree and the tree read back after the round trip and
/// comparing the resulting bytes is an order-independent structural equality check built from
/// code this solution already trusts, rather than a new one written by hand for this test
/// alone. Being order-independent, it passed before R11.23 and passes after: it constrains the
/// values, and the contract suite constrains the order.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresEventStorePayloadRoundTripTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresEventStorePayloadRoundTripTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task NestedPayloadSurvivesAppendAndReadByteForByteAfterRecanonicalization()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.ResetEventsTableAsync(ct);

        var store = new PostgresEventStore(_fixture.AppRoleDataSource, TimeProvider.System);
        var aggregateId = Require(AggregateId.Create("agg-payload-roundtrip"));

        // Deliberately member-order-scrambled relative to how CanonicalJson would sort it
        // (keys here are neither alphabetical nor insertion-sorted), nested two levels,
        // mixing every JsonValue case (object, array, string, number -- integer and
        // fractional, bool, null) so any one of them silently losing fidelity would show up.
        var payload = new JsonValue.Object(
        [
            new("zeta", new JsonValue.Number(-17.5)),
            new("alpha", new JsonValue.Array(
            [
                new JsonValue.String("café"),
                new JsonValue.Bool(true),
                JsonValue.Null.Instance,
                new JsonValue.Number(9_007_199_254_740_991),
            ])),
            new("nested", new JsonValue.Object(
            [
                new("inner_true", new JsonValue.Bool(false)),
                new("inner_num", new JsonValue.Number(0)),
            ])),
        ]);

        var proposed = new DomainEvent(
            Require(EventId.Create("evt-payload-roundtrip")),
            Require(EventType.Create("test.payload")),
            Actor: null,
            Payload: payload);

        var appended = (await store.AppendAsync(aggregateId, AggregateVersion.New, [proposed], ct))
            .Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        var fromAppend = appended[0].Event.Payload;

        var read = (await store.ReadByAggregateAsync(aggregateId, ct))
            .Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        var fromRead = Assert.Single(read).Event.Payload;

        var originalCanonical = CanonicalJson.Canonicalize(payload)
            .Match(b => b.ToArray(), e => throw new InvalidOperationException(e.Title));
        var appendCanonical = CanonicalJson.Canonicalize(fromAppend)
            .Match(b => b.ToArray(), e => throw new InvalidOperationException(e.Title));
        var readCanonical = CanonicalJson.Canonicalize(fromRead)
            .Match(b => b.ToArray(), e => throw new InvalidOperationException(e.Title));

        Assert.Equal(originalCanonical, appendCanonical);
        Assert.Equal(originalCanonical, readCanonical);
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
}
