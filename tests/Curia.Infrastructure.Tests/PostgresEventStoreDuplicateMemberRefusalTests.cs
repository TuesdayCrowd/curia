using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// The silent data-loss case errata E10 records, driven through the real event store rather
/// than asserted on the canonicalizer alone. A unit test on
/// <c>CanonicalJson.Canonicalize</c> proves the predicate; only this test proves the system
/// of record no longer accepts a document it cannot store faithfully, which is the claim that
/// actually matters and the one whose absence let the defect survive.
///
/// What used to happen, measured against this fixture's Postgres 18 before the fix, is
/// recorded here because it is the whole justification for the test: a payload of
/// <c>{"dup":"FIRST","dup":"SECOND"}</c> canonicalized to <c>{"dup":"FIRST","dup":"SECOND"}</c>
/// (accepted -- <c>Canonicalize</c> only sorted member names and had no way to fail),
/// <c>AppendAsync</c> succeeded, and both the event returned by the append itself and the
/// event read back afterwards carried <c>{"dup":"SECOND"}</c>. "FIRST" was gone, with no
/// error anywhere on the path. In an append-only store whose entire premise is that what was
/// written is what is read back (R11.9's replay rebuild depends on exactly that), losing a
/// member between AppendAsync's argument and its own return value is the worst shape a defect
/// can take: no exception, no rejection, no log line.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresEventStoreDuplicateMemberRefusalTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresEventStoreDuplicateMemberRefusalTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The payload is built as a <see cref="JsonValue"/> tree directly, not parsed from text,
    /// and that is the point rather than a shortcut: <see cref="JsonReader.Parse"/> and
    /// <see cref="JsonReader.ParseUnrestricted"/> both reject duplicate member names already,
    /// so a duplicate can only reach the store from a caller holding a tree no parse path
    /// inspected. <see cref="DomainEvent"/>'s payload is exactly such a tree.
    /// </summary>
    [Fact]
    public async Task AppendRefusesAPayloadWhoseObjectCarriesDuplicateMemberNames()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.ResetEventsTableAsync(ct);

        var store = new PostgresEventStore(_fixture.AppRoleDataSource, TimeProvider.System);
        var aggregateId = Require(AggregateId.Create("agg-duplicate-member"));

        var payload = new JsonValue.Object(
        [
            new("dup", new JsonValue.String("FIRST")),
            new("dup", new JsonValue.String("SECOND")),
        ]);

        var proposed = new DomainEvent(
            Require(EventId.Create("evt-duplicate-member")),
            Require(EventType.Create("test.duplicate.member")),
            Actor: null,
            Payload: payload);

        var appendResult = await store.AppendAsync(aggregateId, AggregateVersion.New, [proposed], ct);

        Assert.False(appendResult.IsOk);
        Assert.Equal("curia/admit/duplicate-key", appendResult.Match(_ => "ok", e => e.Type));

        // Refused, not partially written: the refusal happens before a transaction is opened,
        // so there is nothing to roll back and nothing for a later replay to encounter. Both
        // read paths are checked because "the aggregate is empty" and "the table is empty"
        // are different claims, and an append-only log has to satisfy the second one too.
        var byAggregate = Require(await store.ReadByAggregateAsync(aggregateId, ct));
        Assert.Empty(byAggregate);

        var everything = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));
        Assert.Empty(everything);
    }

    /// <summary>
    /// Nested one level deeper, because a check that only inspected the payload's root object
    /// would pass the test above and still lose data here. The store's refusal comes from
    /// <c>CanonicalJson.Canonicalize</c> walking the whole tree, so this must hold at any
    /// depth.
    /// </summary>
    [Fact]
    public async Task AppendRefusesADuplicateMemberNestedInsideThePayload()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.ResetEventsTableAsync(ct);

        var store = new PostgresEventStore(_fixture.AppRoleDataSource, TimeProvider.System);
        var aggregateId = Require(AggregateId.Create("agg-duplicate-member-nested"));

        var payload = new JsonValue.Object(
        [
            new("outer", new JsonValue.Array(
            [
                new JsonValue.Object(
                [
                    new("dup", new JsonValue.String("FIRST")),
                    new("dup", new JsonValue.String("SECOND")),
                ]),
            ])),
        ]);

        var proposed = new DomainEvent(
            Require(EventId.Create("evt-duplicate-member-nested")),
            Require(EventType.Create("test.duplicate.member.nested")),
            Actor: null,
            Payload: payload);

        var appendResult = await store.AppendAsync(aggregateId, AggregateVersion.New, [proposed], ct);

        Assert.False(appendResult.IsOk);
        Assert.Equal("curia/admit/duplicate-key", appendResult.Match(_ => "ok", e => e.Type));
        Assert.Empty(Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct)));
    }

    /// <summary>
    /// Pins the database behaviour that makes the refusal above necessary rather than merely
    /// tidy, so a future reader does not have to take the class remarks on trust. Postgres's
    /// <c>jsonb</c> input conversion resolves duplicate object keys last-wins as documented
    /// behaviour, silently and without an error -- which means the storage layer can never be
    /// the layer that catches this. Whatever refuses a duplicate member has to refuse it
    /// before the value reaches the database, and the canonicalizer that produces the stored
    /// text is the last place upstream of it that sees the whole tree.
    /// </summary>
    [Fact]
    public async Task JsonbItselfCollapsesDuplicateMemberNamesLastWins()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = await _fixture.AdminDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("""SELECT '{"dup":"FIRST","dup":"SECOND"}'::jsonb::text;""", connection);

        var stored = (string)(await command.ExecuteScalarAsync(ct))!;

        Assert.Equal("""{"dup": "SECOND"}""", stored);
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
}
