using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Application.Ports;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// The Stage 2 <see cref="IEventStore"/> adapter: Npgsql over the <c>events</c> table
/// db/0001_create_events.sql creates. Held to the identical
/// <c>Curia.Application.Tests.EventStorePortContractTests</c> suite
/// <c>Curia.Application.Tests.InMemory.InMemoryEventStore</c> is (see
/// Curia.Infrastructure.Tests), and mirrors that adapter's shape: a constructor-injected
/// <see cref="TimeProvider"/> (CS-9) is the only source of <c>server_ts</c>, read exactly once
/// per <see cref="AppendAsync"/> call so every event in one batch shares the instant a single
/// batched Postgres transaction would see for one <c>now()</c> read.
///
/// Unlike the in-memory adapter's single in-process <c>lock</c>, concurrency control here has
/// to be real across processes: a Postgres advisory lock keyed on the target aggregate id
/// (transaction-scoped, released automatically on commit or rollback) serializes concurrent
/// appenders to the *same* aggregate without blocking appenders of different aggregates,
/// mirroring the advisory-lock idiom the scoping doc's "Infrastructure notes" section already
/// establishes for epoch sealing.
/// </summary>
public sealed class PostgresEventStore : IEventStore
{
    private const string SelectColumns = "seq, event_id, event_type, aggregate_id, actor_id, payload, server_ts";

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _clock;

    public PostgresEventStore(NpgsqlDataSource dataSource, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(clock);
        _dataSource = dataSource;
        _clock = clock;
    }

    /// <summary>
    /// Three round trips inside one transaction -- advisory lock, version check, batched
    /// insert -- not one. The scoping doc's "single batched insert returning seq" describes
    /// the *insert* of however many events a single call carries (one INSERT, many VALUES
    /// rows, never N separate INSERTs); it is not a claim that optimistic-concurrency
    /// checking has to be folded into that same statement. Cramming the version check into
    /// the INSERT itself (a WHERE-gated CTE) was tried and rejected: correctness there
    /// depends on the advisory lock being taken strictly before the version is read, and
    /// Postgres does not guarantee evaluation order across sibling CTEs -- a real hazard for
    /// a check that exists specifically to be safe under concurrent access, not a
    /// micro-optimization worth risking that on.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every explicit await below already calls ConfigureAwait(false) (matches " +
            "Curia.AuthN's convention). The remaining, unfixable flags are the compiler-generated " +
            "DisposeAsync() awaits `await using` inserts at scope exit, which offer no ConfigureAwait " +
            "call site to attach to; this project has no SynchronizationContext to avoid resuming on " +
            "(a server-side data adapter, not UI or a library shared with one), so there is nothing for " +
            "ConfigureAwait to protect here even if it were expressible.")]
    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
            return Result<IReadOnlyList<AppendedEvent>>.Fail(DomainErrors.EmptyAppendBatch());

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@agg, 0));", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter("agg", NpgsqlDbType.Text) { Value = aggregateId.Value });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long actualVersionValue;
        await using (var countCommand = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE aggregate_id = @agg;", connection, transaction))
        {
            countCommand.Parameters.Add(new NpgsqlParameter("agg", NpgsqlDbType.Text) { Value = aggregateId.Value });
            actualVersionValue = (long)(await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (actualVersionValue != expectedVersion.Value)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            var actualVersion = AggregateVersion.From(actualVersionValue)
                .Match(v => v, e => throw new InvalidOperationException($"Impossible negative version from count(*): {e.Title}"));
            return Result<IReadOnlyList<AppendedEvent>>.Fail(
                DomainErrors.ConcurrencyConflict(aggregateId, expectedVersion, actualVersion));
        }

        var serverTimestamp = ServerTimestamp.At(_clock.GetUtcNow());

        var appended = new List<AppendedEvent>(events.Count);
        await using (var insertCommand = BuildInsertCommand(connection, transaction, aggregateId, serverTimestamp, events))
        await using (var reader = await insertCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                appended.Add(MapRow(reader));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        // Defensive, not load-bearing: the INSERT's source list is already in call order and
        // Postgres RETURNING for a single non-parallel INSERT observably preserves it, but
        // ORDER BY seq in BuildInsertCommand's SQL is the actual guarantee this relies on --
        // sorting again here costs nothing and removes any doubt for a reader of this method.
        appended.Sort(static (a, b) => a.Seq.CompareTo(b.Seq));
        return Result<IReadOnlyList<AppendedEvent>>.Ok(appended);
    }

    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See AppendAsync's identical suppression: the flags left after every " +
            "explicit await already has ConfigureAwait(false) are `await using`'s compiler-generated " +
            "DisposeAsync() awaits, which have no call site to attach one to.")]
    public async Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(
        AggregateId aggregateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM events WHERE aggregate_id = @agg ORDER BY seq;", connection);
        command.Parameters.Add(new NpgsqlParameter("agg", NpgsqlDbType.Text) { Value = aggregateId.Value });

        return Result<IReadOnlyList<AppendedEvent>>.Ok(await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false));
    }

    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See AppendAsync's identical suppression: the flags left after every " +
            "explicit await already has ConfigureAwait(false) are `await using`'s compiler-generated " +
            "DisposeAsync() awaits, which have no call site to attach one to.")]
    public async Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(
        EventSequence afterSeq,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            // "LIMIT @limit" with a NULL @limit is "LIMIT ALL" per Postgres semantics -- no
            // limit -- so maxCount: null needs no separate SQL text/branch.
            $"SELECT {SelectColumns} FROM events WHERE seq > @after ORDER BY seq LIMIT @limit;", connection);
        command.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Bigint) { Value = afterSeq.Value });

        var limitParameter = new NpgsqlParameter("limit", NpgsqlDbType.Bigint);
        // Written as an if/else, not a `maxCount is { } m ? m : DBNull.Value` conditional
        // expression: the ternary's common type is object, and CA1508 cannot see that boxing
        // a null int? produces a real null reference (not a boxed default(int?)), so it
        // reports the DBNull.Value arm as unreachable dead code -- it is not.
        if (maxCount is { } limit)
            limitParameter.Value = limit;
        else
            limitParameter.Value = DBNull.Value;
        command.Parameters.Add(limitParameter);

        return Result<IReadOnlyList<AppendedEvent>>.Ok(await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false));
    }

    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See AppendAsync's identical suppression: the flag left after the explicit " +
            "await already has ConfigureAwait(false) is `await using`'s compiler-generated " +
            "DisposeAsync() await, which has no call site to attach one to.")]
    private static async Task<IReadOnlyList<AppendedEvent>> ReadRowsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var rows = new List<AppendedEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(MapRow(reader));
        return rows;
    }

    /// <summary>
    /// One multi-row VALUES INSERT for however many events the batch carries -- "a single
    /// batched insert returning seq" (scoping doc, "Infrastructure notes"). RETURNING pulls
    /// back every column, not only seq/server_ts, so the row this method hands back is read
    /// straight from Postgres through the identical <see cref="MapRow"/> every read path uses
    /// -- including the payload's JSONB round trip -- rather than reassembling the result
    /// from the caller's original in-memory <see cref="DomainEvent"/> values.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every event-carried value (event_id, event_type, actor_id, payload) is bound " +
            "through a parameterized NpgsqlParameter below, never concatenated into CommandText. The " +
            "only text this method appends to the SQL string is the loop index i (an int this method " +
            "computes itself, not caller-supplied text) and the SelectColumns constant -- there is no " +
            "path from a DomainEvent's field values to the command text CA2100 is warning about.")]
    private static NpgsqlCommand BuildInsertCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AggregateId aggregateId,
        ServerTimestamp serverTimestamp,
        IReadOnlyList<DomainEvent> events)
    {
        var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        var sql = new StringBuilder(
            "WITH ins AS (INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) VALUES ");

        for (var i = 0; i < events.Count; i++)
        {
            if (i > 0)
                sql.Append(", ");
            sql.Append("(@id").Append(i).Append(", @type").Append(i).Append(", @agg, @actor").Append(i)
                .Append(", @payload").Append(i).Append(", @ts)");

            var domainEvent = events[i];
            command.Parameters.Add(new NpgsqlParameter($"id{i}", NpgsqlDbType.Text) { Value = domainEvent.Id.Value });
            command.Parameters.Add(new NpgsqlParameter($"type{i}", NpgsqlDbType.Text) { Value = domainEvent.Type.Value });
            command.Parameters.Add(new NpgsqlParameter($"actor{i}", NpgsqlDbType.Text)
            {
                Value = domainEvent.Actor is { } actor ? actor.Value : DBNull.Value,
            });
            command.Parameters.Add(new NpgsqlParameter($"payload{i}", NpgsqlDbType.Jsonb)
            {
                Value = SerializePayload(domainEvent.Payload),
            });
        }

        command.Parameters.Add(new NpgsqlParameter("agg", NpgsqlDbType.Text) { Value = aggregateId.Value });
        command.Parameters.Add(new NpgsqlParameter("ts", NpgsqlDbType.TimestampTz) { Value = serverTimestamp.Value });

        sql.Append("RETURNING ").Append(SelectColumns).Append(") SELECT ").Append(SelectColumns).Append(" FROM ins ORDER BY seq;");
        command.CommandText = sql.ToString();
        return command;
    }

    /// <summary>
    /// Payload -&gt; JSON text for the jsonb parameter. Plain <see cref="CanonicalJson.Canonicalize"/>
    /// (RFC 8785, no NFC), not <see cref="CanonicalJson.CanonicalizeWithNfc"/> -- storage is not
    /// signing or verification (R6.9's NFC mandate governs content that will be signed or
    /// verified), and <see cref="CanonicalJson.Canonicalize"/> never fails for any
    /// <see cref="JsonValue"/> tree (it has no normalization step to fail), so the Match below
    /// exists only to keep faith with CS-10 -- its failure branch is unreachable, not silently
    /// assumed away.
    /// </summary>
    private static string SerializePayload(JsonValue payload) =>
        CanonicalJson.Canonicalize(payload).Match(
            bytes => Encoding.UTF8.GetString(bytes.ToArray()),
            error => throw new InvalidOperationException($"Unreachable: Canonicalize(JsonValue) failed: {error.Title}"));

    /// <summary>
    /// Reconstructs an <see cref="AppendedEvent"/> from one row shaped like
    /// <see cref="SelectColumns"/>. Every <c>Result&lt;T&gt;.Fail</c> branch below is corrupt-data,
    /// not a modeled domain outcome -- every value in <c>events</c> was written by this exact
    /// adapter after the same typed constructors already validated it (CS-10: "exceptions are
    /// reserved for bugs and infrastructure faults"), so a failure here means the row did not
    /// come from this adapter's own INSERT path, which is exactly the "bug or infrastructure
    /// fault" tier.
    /// </summary>
    private static AppendedEvent MapRow(NpgsqlDataReader reader)
    {
        var seq = EventSequence.From(reader.GetInt64(0))
            .Match(v => v, e => throw new InvalidOperationException($"Corrupt events.seq: {e.Title}"));
        var eventId = EventId.Create(reader.GetString(1))
            .Match(v => v, e => throw new InvalidOperationException($"Corrupt events.event_id: {e.Title}"));
        var eventType = EventType.Create(reader.GetString(2))
            .Match(v => v, e => throw new InvalidOperationException($"Corrupt events.event_type: {e.Title}"));
        var aggregateId = AggregateId.Create(reader.GetString(3))
            .Match(v => v, e => throw new InvalidOperationException($"Corrupt events.aggregate_id: {e.Title}"));
        ActorId? actorId = reader.IsDBNull(4)
            ? null
            : ActorId.Create(reader.GetString(4)).Match(
                v => (ActorId?)v,
                e => throw new InvalidOperationException($"Corrupt events.actor_id: {e.Title}"));

        var payloadText = reader.GetString(5);
        var payload = JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(payloadText))
            .Match(v => v, e => throw new InvalidOperationException($"Corrupt events.payload (not valid JSON): {e.Title}"));

        var serverTimestamp = ServerTimestamp.At(reader.GetFieldValue<DateTimeOffset>(6));

        return new AppendedEvent(seq, aggregateId, serverTimestamp, new DomainEvent(eventId, eventType, actorId, payload));
    }
}
