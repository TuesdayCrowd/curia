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
    private readonly string _schema;
    private readonly string _qualifiedTable;

    /// <summary>
    /// <paramref name="schema"/> defaults to Postgres's own default, "public" -- where
    /// db/0001_create_events.sql creates <c>events</c> in every real deployment. The
    /// parameter exists for Curia.Infrastructure.Tests: <c>EventStorePortContractTests</c>'
    /// contract requires <c>CreateStore()</c> to return "a fresh, empty store" on every call,
    /// including "once per generated case" of its CsCheck property test -- and that property
    /// test runs its generated cases with real, CsCheck-internal concurrency (confirmed by
    /// falsification: a single-append minimal case failed with a spurious
    /// curia/domain/concurrency-conflict against a shared, TRUNCATE-between-calls table,
    /// because a concurrently running case's rows were visible in between). A schema is an
    /// inexpensive, genuinely isolated Postgres relation namespace -- unlike a fresh database
    /// per call (correct but far more expensive) or a shared table (cheap but, as measured,
    /// not actually isolated under concurrent callers) -- so the test fixture provisions one
    /// schema per <c>CreateStore()</c> call and this parameter is how it is threaded through
    /// without changing anything about the production, single-schema shape.
    /// </summary>
    public PostgresEventStore(NpgsqlDataSource dataSource, TimeProvider clock, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _clock = clock;
        _schema = schema;
        _qualifiedTable = SqlIdentifier.Quote(schema) + ".events";
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
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only interpolated text is _qualifiedTable, computed once in the " +
            "constructor from QuoteIdentifier(schema) -- never caller-supplied per call -- and every " +
            "per-call value (aggregateId, the lock key) is bound through a parameterized " +
            "NpgsqlParameter, never concatenated into CommandText.")]
    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
            return Result<IReadOnlyList<AppendedEvent>>.Fail(DomainErrors.EmptyAppendBatch());

        // Serialized before the connection is opened, not inside BuildInsertCommand, because
        // this step can now refuse the batch (see SerializePayload): a refusal has to happen
        // before any transaction exists, so a batch this store will not accept costs no
        // round trip and leaves no rolled-back work behind.
        var payloads = new string[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            if (!SerializePayload(events[i].Payload).TryGetValue(out var payloadText, out var payloadError))
                return Result<IReadOnlyList<AppendedEvent>>.Fail(payloadError!);
            payloads[i] = payloadText;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lockkey, 0));", connection, transaction))
        {
            // Keyed on schema+aggregate, not aggregate alone: two isolated per-test schemas
            // (see the schema parameter's remarks) that happen to reuse the same aggregate id
            // text -- exactly what the contract suite's own fixture-generated ids do -- must
            // not serialize against each other's advisory lock, since they are not actually
            // contending for the same rows.
            lockCommand.Parameters.Add(new NpgsqlParameter("lockkey", NpgsqlDbType.Text) { Value = _schema + ":" + aggregateId.Value });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        long actualVersionValue;
        await using (var countCommand = new NpgsqlCommand(
            $"SELECT count(*) FROM {_qualifiedTable} WHERE aggregate_id = @agg;", connection, transaction))
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
        await using (var insertCommand = BuildInsertCommand(_qualifiedTable, connection, transaction, aggregateId, serverTimestamp, events, payloads))
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
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See AppendAsync's identical suppression: the only interpolated text is " +
            "_qualifiedTable, computed once in the constructor, never caller-supplied per call.")]
    public async Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(
        AggregateId aggregateId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM {_qualifiedTable} WHERE aggregate_id = @agg ORDER BY seq;", connection);
        command.Parameters.Add(new NpgsqlParameter("agg", NpgsqlDbType.Text) { Value = aggregateId.Value });

        return Result<IReadOnlyList<AppendedEvent>>.Ok(await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false));
    }

    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See AppendAsync's identical suppression: the flags left after every " +
            "explicit await already has ConfigureAwait(false) are `await using`'s compiler-generated " +
            "DisposeAsync() awaits, which have no call site to attach one to.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See AppendAsync's identical suppression: the only interpolated text is " +
            "_qualifiedTable, computed once in the constructor, never caller-supplied per call.")]
    public async Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(
        EventSequence afterSeq,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            // "LIMIT @limit" with a NULL @limit is "LIMIT ALL" per Postgres semantics -- no
            // limit -- so maxCount: null needs no separate SQL text/branch.
            $"SELECT {SelectColumns} FROM {_qualifiedTable} WHERE seq > @after ORDER BY seq LIMIT @limit;", connection);
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
            "computes itself, not caller-supplied text), the SelectColumns constant, and " +
            "qualifiedTable -- computed once in the constructor from QuoteIdentifier(schema), never " +
            "caller-supplied text either -- so there is no path from a DomainEvent's field values, or " +
            "from unescaped user input of any kind, to the command text CA2100 is warning about.")]
    private static NpgsqlCommand BuildInsertCommand(
        string qualifiedTable,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AggregateId aggregateId,
        ServerTimestamp serverTimestamp,
        IReadOnlyList<DomainEvent> events,
        string[] payloads)
    {
        var command = new NpgsqlCommand { Connection = connection, Transaction = transaction };
        var sql = new StringBuilder(
            $"WITH ins AS (INSERT INTO {qualifiedTable} (event_id, event_type, aggregate_id, actor_id, payload, server_ts) VALUES ");

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
                Value = payloads[i],
            });
        }

        command.Parameters.Add(new NpgsqlParameter("agg", NpgsqlDbType.Text) { Value = aggregateId.Value });
        command.Parameters.Add(new NpgsqlParameter("ts", NpgsqlDbType.TimestampTz) { Value = serverTimestamp.Value });

        sql.Append("RETURNING ").Append(SelectColumns).Append(") SELECT ").Append(SelectColumns).Append(" FROM ins ORDER BY seq;");
        command.CommandText = sql.ToString();
        return command;
    }

    /// <summary>
    /// Payload -&gt; JSON text for the jsonb parameter, in two steps that answer two different
    /// questions -- <i>may this be stored at all</i>, and <i>what text goes in the column</i>.
    ///
    /// <b>Admission</b> is decided by <see cref="CanonicalJson.CanonicalizeWithNfc"/>, the
    /// Cūria profile, with its bytes discarded (R11.24, errata E12). <b>Storage</b> is rendered
    /// by the pure <see cref="CanonicalJson.Canonicalize"/> (RFC 8785, no NFC) -- storage is
    /// not signing or verification, R6.9's NFC mandate governs content that will be signed or
    /// verified, and NFC-normalizing a payload on its way into the system of record would be a
    /// mutation sec. 6.4 forbids outright.
    ///
    /// That "storage is not signing" defence, which this method used to give for using the
    /// pure profile throughout, is sound for what gets *written* and does not carry over to
    /// what gets *admitted*. Admission only ever refuses, and refusing mutates nothing, so the
    /// no-mutation invariant has nothing to say about admitting less. What does have something
    /// to say is R11.9: this table is the system of record. A payload whose member names
    /// collide only after NFC -- precomposed <c>café</c> against <c>cafe</c> + U+0301 -- has a
    /// pure canonical form, so the old check passed it, and <c>jsonb</c> compares keys bytewise
    /// so it retains both members (measured against this fixture's PostgreSQL 18.4:
    /// <c>'{"café":1,"café":2}'::jsonb</c> keeps both). Round-tripping is lossless
    /// and nothing is lost today. But <see cref="CanonicalJson.CanonicalizeWithNfc"/> fails on
    /// that same tree with <c>curia/canon/duplicate-normalized-key</c>, so the moment event
    /// payloads are digested into a Merkle leaf or an <i>Acta</i> entry -- sec. 9's dump
    /// manifests -- the table would already hold rows that cannot be canonicalized for the
    /// purpose, and the defect would surface at signing time rather than at write time. The
    /// tightening is narrow by construction: a payload full of NFD text still passes; only a
    /// collision is refused.
    ///
    /// The failure this propagates is real, not ceremonial, and this call site is why errata
    /// E10 exists. <see cref="CanonicalJson.Canonicalize"/> rejects an object carrying two
    /// members with the same name (R6.38), and a <see cref="DomainEvent"/>'s payload is a
    /// <see cref="JsonValue"/> tree a caller may have built in memory rather than parsed, so
    /// no earlier layer has necessarily inspected it. Before that rejection existed this
    /// method's own doc comment asserted "Canonicalize never fails" and threw on the branch
    /// that could not happen -- and a duplicate-membered payload was written to Postgres,
    /// whose <c>jsonb</c> input conversion resolves duplicate keys last-wins
    /// (<c>'{"a":1,"a":2}'::jsonb</c> is <c>{"a": 2}</c>), so the event table silently kept a
    /// document that was not the one the caller appended. Refusing the append is the only
    /// outcome compatible with an append-only system of record: there is no repair the
    /// no-mutation invariant (R6.12-R6.17) would permit, and storing a collapsed payload is
    /// data loss in the one table that is supposed to be the system of record.
    ///
    /// Two walks of the tree, then, not one, and deliberately so: the admission verdict and the
    /// stored rendering come from two different functions because they answer two different
    /// questions, and collapsing them into one call would mean either storing NFC-normalized
    /// text (a mutation) or admitting what cannot be signed (the hazard above). A raw duplicate
    /// still reports <c>curia/admit/duplicate-key</c> and not the normalized slug, because
    /// <see cref="CanonicalJson.CanonicalizeWithNfc"/> preserves errata E1's precedence between
    /// the two predicates -- this method does not re-derive it.
    /// </summary>
    private static Result<string> SerializePayload(JsonValue payload) =>
        CanonicalJson.CanonicalizeWithNfc(payload)
            .Bind(_ => CanonicalJson.Canonicalize(payload))
            .Map(bytes => Encoding.UTF8.GetString(bytes.Span));

    /// <summary>
    /// Reconstructs an <see cref="AppendedEvent"/> from one row shaped like
    /// <see cref="SelectColumns"/>. Every <c>Result&lt;T&gt;.Fail</c> branch below is corrupt-data,
    /// not a modeled domain outcome -- every value in <c>events</c> was written by this exact
    /// adapter after the same typed constructors already validated it (CS-10: "exceptions are
    /// reserved for bugs and infrastructure faults"), so a failure here means the row did not
    /// come from this adapter's own INSERT path, which is exactly the "bug or infrastructure
    /// fault" tier. That holds for the payload's reordering step too: the one condition
    /// <see cref="CanonicalJson.InCanonicalMemberOrder"/> fails on is an object with duplicate
    /// member names, which <c>jsonb</c> cannot produce (its input conversion resolves
    /// duplicates last-wins, so the value in the column never has any) and which
    /// <see cref="SerializePayload"/> refuses before the INSERT in any case.
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

        // Two steps, and the second one is the port promise R11.23 states, not a tidy-up.
        // jsonb is a parsed binary form, not the text this adapter handed it: it re-sorts every
        // object's keys by its own rule -- key length first, then bytewise -- so the RFC 8785
        // order SerializePayload wrote is emphatically not the order that comes back
        // ({"z":1,"longer_key_b":2,"a":3} appended reads as {"a": 3, "z": 1, "longer_key_b": 2},
        // measured against PostgreSQL 18.4). The ordering is lost inside the database, so it
        // has to be re-established here, on the way out. Restoring it is deterministic and
        // costs nothing in fidelity: member order is not information in JSON, so this changes
        // no fact, and the alternative -- letting the payload out in jsonb's order -- left the
        // in-memory adapter the more faithful of the two, which is the E11 shape (R11.22).
        var payloadText = reader.GetString(5);
        var payload = JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(payloadText))
            .Bind(CanonicalJson.InCanonicalMemberOrder)
            .Match(v => v, e => throw new InvalidOperationException($"Corrupt events.payload (not valid JSON): {e.Title}"));

        var serverTimestamp = ServerTimestamp.At(reader.GetFieldValue<DateTimeOffset>(6));

        return new AppendedEvent(seq, aggregateId, serverTimestamp, new DomainEvent(eventId, eventType, actorId, payload));
    }
}
