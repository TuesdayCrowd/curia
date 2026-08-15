using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure.Projections;

/// <summary>
/// The Stage 3 replay-rebuild drill's storage half: a real Postgres table holding
/// <see cref="AggregateSummary"/> rows, built, dropped, and rebuilt against the actual server
/// R11.9 requires the drill be exercised against, not merely against an in-memory fake. All the
/// content computation happens in <see cref="AggregateSummaryProjector"/> (pure, no I/O, no
/// clock); this type is only responsible for persisting that result and for the drop/rebuild
/// lifecycle around it.
///
/// Deliberately built against its own <see cref="NpgsqlDataSource"/> rather than the
/// R11.6-constrained application role <see cref="PostgresEventStore"/> writes through: R11.6
/// bounds what the write path to the *events* table may do (INSERT/SELECT only, enforced by
/// grant, never UPDATE/DELETE); it says nothing about a projection table, and R11.10 explicitly
/// wants a projection to be freely droppable and rebuildable ("a reindex... rather than a
/// migration"). Holding this table to the identical restrictive grant the event log needs would
/// make step 3 of the drill -- literally dropping the table -- impossible under the very
/// privilege boundary Stage 2 established for a different purpose. Exactly what grant shape a
/// *production* projector role should carry is a real question this type does not answer (the
/// Stage 3 report flags it explicitly as a follow-up, not a gap papered over silently); the test
/// fixture stands in with its admin connection for now.
///
/// Every replay this type performs reads through an <see cref="IEventReader"/> the caller
/// supplies fresh to <see cref="RebuildAsync"/> -- never held as a field -- so nothing here can
/// accidentally reuse state left over from a previous rebuild.
/// </summary>
public sealed class PostgresAggregateSummaryProjection
{
    private const string SelectColumns = "aggregate_id, event_count, first_seq, last_seq, last_server_ts";

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _clock;
    private readonly string _qualifiedTable;

    /// <summary>
    /// <paramref name="clock"/> (CS-9) stamps only the table's <c>rebuilt_at</c> bookkeeping
    /// column -- operational metadata in the spirit of Appendix D's
    /// <c>post_search.indexed_at</c>, answering "how stale is this projection." It is never
    /// consulted when computing an <see cref="AggregateSummary"/>'s content --
    /// <see cref="AggregateSummaryProjector"/> takes no clock at all -- and
    /// <see cref="ReadAllAsync"/> does not even select <c>rebuilt_at</c> back, so it structurally
    /// cannot leak into anything this type returns. See the Stage 3 report's determinism test for
    /// the proof: two rebuilds fed different clock readings still agree, because there is no path
    /// from the clock to the compared content.
    /// </summary>
    public PostgresAggregateSummaryProjection(NpgsqlDataSource dataSource, TimeProvider clock, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _clock = clock;
        _qualifiedTable = QuoteIdentifier(schema) + ".aggregate_event_summary";
    }

    /// <summary>
    /// Drops the projection table if it exists -- literally "drop it entirely" (drill step 3),
    /// not a TRUNCATE or a discarded in-memory reference standing in for one. Idempotent: calling
    /// it when the table is already gone (or was never built) is not an error, matching
    /// <c>DROP TABLE IF EXISTS</c> semantics rather than requiring callers to track existence
    /// themselves.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every explicit await below already calls ConfigureAwait(false) (matches " +
            "PostgresEventStore's convention). The remaining, unfixable flags are the compiler-generated " +
            "DisposeAsync() awaits `await using` inserts at scope exit, which offer no ConfigureAwait " +
            "call site to attach to; this project has no SynchronizationContext to avoid resuming on.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only interpolated text is _qualifiedTable, computed once in the " +
            "constructor from QuoteIdentifier(schema) -- never caller-supplied per call.")]
    public async Task DropAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS {_qualifiedTable};", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// True only if the table currently exists on the server -- lets the drill prove step 3
    /// ("drop it entirely") actually happened, rather than merely that <see cref="DropAsync"/>
    /// was called and returned without throwing.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See DropAsync's identical suppression: the flags left after every explicit " +
            "await already has ConfigureAwait(false) are `await using`'s compiler-generated " +
            "DisposeAsync() awaits, which have no call site to attach one to.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "_qualifiedTable is bound as a parameter here (unlike the DDL statements " +
            "elsewhere in this file, to_regclass takes its argument as an ordinary text value, not " +
            "as part of the command text), so there is no interpolation into CommandText at all.")]
    public async Task<bool> ExistsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("SELECT to_regclass(@qualified) IS NOT NULL;", connection);
        command.Parameters.Add(new NpgsqlParameter("qualified", NpgsqlDbType.Text) { Value = _qualifiedTable });
        return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Drill steps 2/4 in one call: replays the entire event stream from <paramref name="reader"/>
    /// via <see cref="AggregateSummaryProjector.RebuildAsync"/> (computed BEFORE anything is
    /// touched on the server -- if replay throws, whatever table already existed is left alone
    /// rather than dropped for nothing), then drops any existing projection table and recreates
    /// it fresh, then writes the freshly computed result. Calling this twice in a row already
    /// performs "drop it entirely, then rebuild from the event table alone, by replay" end to end;
    /// the drill test also calls <see cref="DropAsync"/> explicitly between two
    /// <see cref="RebuildAsync"/> calls so step 3 is proven on its own, not merely implied by
    /// step 4 also happening to drop first.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See DropAsync's identical suppression: the flag left after the explicit " +
            "await already has ConfigureAwait(false) is `await using`'s compiler-generated " +
            "DisposeAsync() await, which has no call site to attach one to.")]
    public async Task<IReadOnlyDictionary<AggregateId, AggregateSummary>> RebuildAsync(
        IEventReader reader, int pageSize = 500, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var summaries = await AggregateSummaryProjector.RebuildAsync(reader, pageSize, cancellationToken)
            .ConfigureAwait(false);

        await DropAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await CreateTableAsync(connection, cancellationToken).ConfigureAwait(false);

        if (summaries.Count > 0)
        {
            await using var insert = BuildInsertCommand(_qualifiedTable, connection, summaries.Values, _clock.GetUtcNow());
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return summaries;
    }

    /// <summary>
    /// Reads the projection back exactly as an <see cref="AggregateSummary"/> -- the same shape
    /// <see cref="AggregateSummaryProjector"/> produces in memory -- so the drill's final
    /// assertion compares like with like: what a fresh in-process rebuild computed versus what
    /// actually made it through a real INSERT and back out through a real SELECT. Does not select
    /// <c>rebuilt_at</c>; see this type's constructor remarks for why that is deliberate.
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See DropAsync's identical suppression: the flags left after every explicit " +
            "await already has ConfigureAwait(false) are `await using`'s compiler-generated " +
            "DisposeAsync() awaits, which have no call site to attach one to.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only interpolated text is _qualifiedTable, computed once in the " +
            "constructor from QuoteIdentifier(schema) -- never caller-supplied per call.")]
    public async Task<IReadOnlyDictionary<AggregateId, AggregateSummary>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"SELECT {SelectColumns} FROM {_qualifiedTable};", connection);

        var result = new Dictionary<AggregateId, AggregateSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var summary = MapRow(reader);
            result[summary.AggregateId] = summary;
        }

        return result;
    }

    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See DropAsync's identical suppression: the flag left after the explicit " +
            "await already has ConfigureAwait(false) is `await using`'s compiler-generated " +
            "DisposeAsync() await, which has no call site to attach one to.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only interpolated text is _qualifiedTable, computed once in the " +
            "constructor from QuoteIdentifier(schema) -- never caller-supplied per call.")]
    private async Task CreateTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var create = new NpgsqlCommand($"""
            CREATE TABLE {_qualifiedTable} (
              aggregate_id     TEXT PRIMARY KEY,
              event_count      BIGINT NOT NULL,
              first_seq        BIGINT NOT NULL,
              last_seq         BIGINT NOT NULL,
              last_server_ts   TIMESTAMPTZ NOT NULL,
              rebuilt_at       TIMESTAMPTZ NOT NULL
            );
            """, connection);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One multi-row VALUES INSERT for however many aggregates the rebuild produced,
    /// mirroring <see cref="PostgresEventStore"/>'s own batched-insert idiom.</summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every summary-carried value (aggregate_id, event_count, first_seq, last_seq, " +
            "last_server_ts) is bound through a parameterized NpgsqlParameter below, never concatenated " +
            "into CommandText. The only text this method appends to the SQL string is the loop index i " +
            "(an int this method computes itself) and qualifiedTable, computed once in the constructor " +
            "from QuoteIdentifier(schema), never caller-supplied text.")]
    private static NpgsqlCommand BuildInsertCommand(
        string qualifiedTable, NpgsqlConnection connection, IEnumerable<AggregateSummary> summaries, DateTimeOffset rebuiltAt)
    {
        var command = new NpgsqlCommand { Connection = connection };
        var sql = new StringBuilder(
            $"INSERT INTO {qualifiedTable} (aggregate_id, event_count, first_seq, last_seq, last_server_ts, rebuilt_at) VALUES ");

        var i = 0;
        foreach (var summary in summaries)
        {
            if (i > 0)
                sql.Append(", ");
            sql.Append('(').Append("@agg").Append(i).Append(", @count").Append(i).Append(", @first").Append(i)
                .Append(", @last").Append(i).Append(", @ts").Append(i).Append(", @rebuilt)");

            command.Parameters.Add(new NpgsqlParameter($"agg{i}", NpgsqlDbType.Text) { Value = summary.AggregateId.Value });
            command.Parameters.Add(new NpgsqlParameter($"count{i}", NpgsqlDbType.Bigint) { Value = summary.EventCount });
            command.Parameters.Add(new NpgsqlParameter($"first{i}", NpgsqlDbType.Bigint) { Value = summary.FirstSeq.Value });
            command.Parameters.Add(new NpgsqlParameter($"last{i}", NpgsqlDbType.Bigint) { Value = summary.LastSeq.Value });
            command.Parameters.Add(new NpgsqlParameter($"ts{i}", NpgsqlDbType.TimestampTz) { Value = summary.LastServerTimestamp.Value });
            i++;
        }

        command.Parameters.Add(new NpgsqlParameter("rebuilt", NpgsqlDbType.TimestampTz) { Value = rebuiltAt });
        sql.Append(';');
        command.CommandText = sql.ToString();
        return command;
    }

    /// <summary>
    /// Reconstructs an <see cref="AggregateSummary"/> from one row shaped like
    /// <see cref="SelectColumns"/>. Every failure branch below is corrupt-data, not a modeled
    /// domain outcome (CS-10) -- every row was written by this exact type's own INSERT path after
    /// the source values had already been validated, mirroring <see cref="PostgresEventStore"/>'s
    /// own <c>MapRow</c>.
    /// </summary>
    private static AggregateSummary MapRow(NpgsqlDataReader reader)
    {
        var aggregateId = AggregateId.Create(reader.GetString(0))
            .Match(v => v, e => throw new InvalidOperationException($"Corrupt aggregate_event_summary.aggregate_id: {e.Title}"));
        var eventCount = reader.GetInt64(1);
        var firstSeq = EventSequence.From(reader.GetInt64(2))
            .Match(v => v, e => throw new InvalidOperationException($"Corrupt aggregate_event_summary.first_seq: {e.Title}"));
        var lastSeq = EventSequence.From(reader.GetInt64(3))
            .Match(v => v, e => throw new InvalidOperationException($"Corrupt aggregate_event_summary.last_seq: {e.Title}"));
        var lastServerTimestamp = ServerTimestamp.At(reader.GetFieldValue<DateTimeOffset>(4));

        return new AggregateSummary(aggregateId, eventCount, firstSeq, lastSeq, lastServerTimestamp);
    }

    /// <summary>Standard SQL identifier quoting (double quotes, embedded quotes doubled).</summary>
    private static string QuoteIdentifier(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
