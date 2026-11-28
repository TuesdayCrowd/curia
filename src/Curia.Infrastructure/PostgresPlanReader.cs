using System.Data;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Reads;
using Curia.Domain;
using Curia.Domain.Acta;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// The Postgres answer to a <see cref="ReadPlan"/> (errata G87, R11.113): every stage of the plan in one <c>REPEATABLE READ, READ ONLY</c> transaction, so a row appended between stage 1 and stage 3 is in
/// none of them; one statement per clause, each served by the index its clause names (the tests read the planner's own plan for each); and every returned row's R6.47 index (the count of rows with
/// <c>seq &gt; 0</c> before it) computed in that snapshot by one pass over the primary key, whatever the number of rows, so an identity value burnt by a rolled-back insert leaves a gap in <c>seq</c>
/// and none in the index. A fault of the store is thrown as <see cref="LogUnreadableException"/> (R11.71), never returned as an empty slice.
/// </summary>
internal static class PostgresPlanReader
{
    /// <summary>
    /// The statement for <paramref name="clause"/> over <paramref name="table"/>: its text and its one parameter. The text names the clause's index by construction and reads no row it was not asked for;
    /// it is public to the tests so that the planner's choice for each can be read.
    /// </summary>
    internal static (string Sql, NpgsqlParameter? Parameter) Statement(ReadClause clause, string table, string columns)
    {
        ArgumentNullException.ThrowIfNull(clause);

        string[] keyTypes = [LogEntries.KeyType, LogEntries.DeclarationKeyType, LogEntries.OperatorKeyType];
        var select = $"SELECT {columns} FROM {table} WHERE seq > 0 AND ";

        return clause.Match<(string, NpgsqlParameter?)>(
            ids => ($"{select}aggregate_id = ANY(@p);", Array("p", ids)),
            () => ($"{select}event_type = ANY(@p);", Array("p", keyTypes)),
            actor => ($"{select}event_type = '{PostProjector.PostAcceptedType}' AND actor_id = @p;", Text("p", actor)),
            actor => ($"{select}event_type = '{AcceptanceProjector.AnswerAcceptedType}' AND actor_id = @p;", Text("p", actor)),
            (member, values) => member switch
            {
                ReadClause.PayloadMember.Digest => ($"{select}event_type = '{PostProjector.PostAcceptedType}' AND (payload->>'digest') = ANY(@p);", Array("p", values)),
                ReadClause.PayloadMember.SignalTarget => (
                    // R11.6 (add.): the column the append wrote from the signed envelope, never SQL over the canonical text; the partial index's predicate is implied by the equality.
                    $"{select}event_type = '{PostProjector.PostAcceptedType}' AND signal_target = ANY(@p);",
                    Array("p", values)),
                _ => throw new ArgumentOutOfRangeException(nameof(member), member, "Not a payload member"),
            },
            ids => ($"{select}event_id = ANY(@p);", Array("p", ids)),
            () => ($"{select}event_type = '{FlagProjector.FlagRaisedType}';", null),
            kid => ($"{select}event_type = ANY(@types) AND payload->>'kid' = @p;", Text("p", kid)));
    }

    /// <summary>The lifecycle types the kid clause's statement binds as <c>@types</c>.</summary>
    internal static readonly string[] LifecycleTypes = [AgentStandingProjector.EnrolledType, AgentStandingProjector.KeyBoundType, LifecycleAppendGuard.KeyRetiredType];

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is the table name, computed once from the schema in the store's constructor, and the clause text of Statement: constants. Every caller-supplied value is a parameter.")]
    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "`await using`'s compiler-generated DisposeAsync awaits have no call site to attach one to.")]
    internal static async Task<LogSlice> ReadAsync(NpgsqlDataSource dataSource, string table, ReadPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY;", connection, transaction))
                await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // The snapshot's own end of the log: what an append guard's After is when the decision is made on this read.
            long highWater;
            await using (var high = new NpgsqlCommand($"SELECT COALESCE(max(seq), 0) FROM {table} WHERE seq > 0;", connection, transaction))
                highWater = (long)(await high.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? 0L);

            var chosen = new SortedDictionary<long, AppendedEvent>();
            var scope = new ScopeBuilder();
            foreach (var stage in plan.Stages)
            {
                foreach (var clause in stage([.. chosen.Values]))
                {
                    scope.Note(clause);
                    if (IsEmpty(clause)) continue;

                    var (sql, parameter) = Statement(clause, table, PostgresEventStore.SelectColumns);
                    await using var command = new NpgsqlCommand(sql, connection, transaction);
                    if (parameter is not null) command.Parameters.Add(parameter);
                    if (sql.Contains("@types", StringComparison.Ordinal)) command.Parameters.Add(Array("types", LifecycleTypes));

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        var row = PostgresEventStore.MapRow(reader);
                        chosen[row.Seq.Value] = row;
                    }
                }
            }

            var rows = await Positioned(chosen.Values, connection, transaction, table, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return new LogSlice(rows, EventSequence.From(highWater).Match(v => v, e => throw new InvalidOperationException($"Corrupt events.seq: {e.Title}")), scope.Build());
        }
        catch (Exception e) when (PostgresStoreFaults.IsStoreFault(e, cancellationToken))
        {
            // R11.71: a fault of the store is thrown, never returned as an empty slice.
            throw PostgresStoreFaults.Unreadable(e);
        }
    }

    /// <summary>The rows with their R6.47 indices: one window over the primary key up to the greatest <c>seq</c> chosen, filtered to the chosen rows.</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is the table name computed once in the store's constructor.")]
    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "`await using`'s compiler-generated DisposeAsync awaits have no call site to attach one to.")]
    private static async Task<IReadOnlyList<PositionedEvent>> Positioned(
        IEnumerable<AppendedEvent> chosen, NpgsqlConnection connection, NpgsqlTransaction transaction, string table, CancellationToken cancellationToken)
    {
        var rows = chosen.ToArray();
        if (rows.Length == 0) return [];

        await using var command = new NpgsqlCommand(
            $"SELECT seq, idx FROM (SELECT seq, (row_number() OVER (ORDER BY seq)) - 1 AS idx FROM {table} WHERE seq > 0 AND seq <= @max) r WHERE seq = ANY(@seqs);", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("max", NpgsqlDbType.Bigint) { Value = rows[^1].Seq.Value });
        command.Parameters.Add(new NpgsqlParameter("seqs", NpgsqlDbType.Array | NpgsqlDbType.Bigint) { Value = rows.Select(r => r.Seq.Value).ToArray() });

        var index = new Dictionary<long, long>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            index[reader.GetInt64(0)] = reader.GetInt64(1);

        return [.. rows.Select(r => new PositionedEvent(index[r.Seq.Value], r))];
    }

    private static bool IsEmpty(ReadClause clause) => clause.Match(
        ids => ids.Count == 0,
        () => false,
        _ => false,
        _ => false,
        (_, values) => values.Count == 0,
        ids => ids.Count == 0,
        () => false,
        _ => false);

    private static NpgsqlParameter Text(string name, string value) => new(name, NpgsqlDbType.Text) { Value = value };

    private static NpgsqlParameter Array(string name, IEnumerable<string> values) => new(name, NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = values.ToArray() };
}
