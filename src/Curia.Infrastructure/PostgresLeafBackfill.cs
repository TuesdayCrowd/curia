using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>What <see cref="PostgresLeafBackfill"/> did: rows it filled, and rows that already held a leaf and were left alone (right or wrong: the audit decides which).</summary>
public sealed record LeafBackfillReport(long Filled, long AlreadyHeld, long SignalTargetsFilled = 0);

/// <summary>
/// R6.69, db/0007: fills <c>events.leaf_hash</c> and <c>events.signal_target</c> (R11.6 (add.): the target of a vote or verification, read from its canonical envelope as the append reads it) for the rows appended before the columns existed. It runs with the <b>database owner's</b> role, never the application role, which holds no
/// <c>UPDATE</c> (R11.6) and is refused before a row is read. It fills a row only where the column is <c>NULL</c> (<c>WHERE leaf_hash IS NULL</c> in the statement itself, so a leaf that appears
/// between the read and the write is not overwritten), and it never replaces a leaf that is there: a wrong stored leaf is the audit's finding, and a repair that rewrote it from the row would be
/// this tool deciding what the log's leaf is. Idempotent, in batches (each its own transaction), and safe to run beside a Forum that is appending, which writes its own leaf.
/// </summary>
public sealed class PostgresLeafBackfill : IAsyncDisposable
{
    private const int BatchSize = 500;

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _qualifiedTable;
    private readonly bool _ownsDataSource;

    public PostgresLeafBackfill(NpgsqlDataSource dataSource, string schema = "public")
        : this(dataSource, schema, ownsDataSource: false)
    {
    }

    private PostgresLeafBackfill(NpgsqlDataSource dataSource, string schema, bool ownsDataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _qualifiedTable = SqlIdentifier.Quote(schema) + ".events";
        _ownsDataSource = ownsDataSource;
    }

    /// <summary>A backfill over its own connection pool, for a composition root that holds no Npgsql type (CS-7). Dispose it.</summary>
    public static PostgresLeafBackfill ForConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new PostgresLeafBackfill(NpgsqlDataSource.Create(connectionString), "public", ownsDataSource: true);
    }

    public ValueTask DisposeAsync() => _ownsDataSource ? _dataSource.DisposeAsync() : ValueTask.CompletedTask;

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Every awaited call carries ConfigureAwait(false); the analyzer flags the `await using` disposal.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is _qualifiedTable, built once from a quoted identifier; every value is a bound parameter.")]
    public async Task<Result<LeafBackfillReport>> RunAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        bool canUpdate;
        await using (var privilege = new NpgsqlCommand(
            "SELECT has_column_privilege(current_user, @table::regclass, 'leaf_hash', 'UPDATE') AND has_column_privilege(current_user, @table::regclass, 'signal_target', 'UPDATE');", connection))
        {
            privilege.Parameters.AddWithValue("table", _qualifiedTable);
            canUpdate = (bool)(await privilege.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        if (!canUpdate)
            return Result<LeafBackfillReport>.Fail(new Error(
                "curia/backfill/not-the-owner",
                "The role this tool connects as holds no UPDATE on events.leaf_hash or events.signal_target",
                "Run backfill-leaves with the database owner's connection string, not the Forum's. The application role has no UPDATE on events by design (R11.6)."));

        long filled = 0, held = 0, targets = 0, after = 0;
        while (true)
        {
            var batch = new List<AppendedEvent>(BatchSize);
            await using (var read = new NpgsqlCommand(
                $"SELECT {PostgresEventStore.SelectColumns} FROM {_qualifiedTable} WHERE {PostgresEventStore.InTheLog} AND seq > @after ORDER BY seq LIMIT @limit;", connection))
            {
                read.Parameters.Add(new NpgsqlParameter("after", NpgsqlDbType.Bigint) { Value = after });
                read.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = BatchSize });
                await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    batch.Add(PostgresEventStore.MapRow(reader));
            }

            if (batch.Count == 0)
                return Result<LeafBackfillReport>.Ok(new LeafBackfillReport(filled, held, targets));

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var row in batch)
            {
                // The signal target is filled where the canonical states one and the column is NULL, and only there: a column that holds a different value is the audit's finding and is never replaced.
                if (row.StoredSignalTarget is null && SignalTargetColumn.Of(row.Event) is { } target)
                {
                    await using var fill = new NpgsqlCommand($"UPDATE {_qualifiedTable} SET signal_target = @target WHERE seq = @seq AND signal_target IS NULL;", connection, transaction);
                    fill.Parameters.Add(new NpgsqlParameter("target", NpgsqlDbType.Text) { Value = target });
                    fill.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = row.Seq.Value });
                    targets += await fill.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                if (row.StoredLeafHash is not null)
                {
                    held++;
                    continue;
                }

                if (!LogLeaf.Hash(row).TryGetValue(out var leaf, out var leafError))
                    return Result<LeafBackfillReport>.Fail(leafError!);

                await using var update = new NpgsqlCommand($"UPDATE {_qualifiedTable} SET leaf_hash = @leaf WHERE seq = @seq AND leaf_hash IS NULL;", connection, transaction);
                update.Parameters.Add(new NpgsqlParameter("leaf", NpgsqlDbType.Bytea) { Value = (byte[])[.. leaf] });
                update.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = row.Seq.Value });
                filled += await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            after = batch[^1].Seq.Value;
        }
    }
}
