using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// <see cref="IVectorIndex"/> over pgvector, on db/0003's <c>post_embeddings</c>.
///
/// <para>Vectors cross the wire as pgvector's text form (<c>[0.1,0.2,...]</c>) with an explicit
/// <c>::vector</c> cast, in both directions, rather than through a type-handler package. One
/// fewer dependency (CS-3), and the text form is pgvector's own published serialization, so
/// there is nothing to get subtly wrong at the boundary except number formatting -- which is
/// the round-trip <c>"R"</c> format, culture-invariant.</para>
///
/// <para>Nearest-neighbour search is exact: <c>ORDER BY embedding &lt;=&gt; query</c> over every
/// row of the model, ties by <c>seq</c> so a page is stable (R9.7). db/0003's header says why no
/// approximate index exists yet and what the bound is.</para>
/// </summary>
public sealed class PostgresVectorIndex : IVectorIndex
{
    /// <summary>The most neighbours one query may ask for. Published through <see cref="RetrievalErrors.LimitOutOfRange"/>.</summary>
    public const int MaximumLimit = 1_000;

    /// <summary>Postgres's <c>undefined_object</c>: the state a missing <c>vector</c> type reports.</summary>
    private const string UndefinedObject = "42704";

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _clock;
    private readonly string _qualifiedTable;

    public PostgresVectorIndex(NpgsqlDataSource dataSource, TimeProvider clock, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _clock = clock;
        _qualifiedTable = SqlIdentifier.Quote(schema) + ".post_embeddings";
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every awaited call carries ConfigureAwait(false); what the analyzer flags is the `await using` disposal of the connection and command, which have no ConfigureAwait to give, exactly as in PostgresEventStore.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only interpolated text is the quoted table name built in the constructor from a schema the composition root supplies; every value is a parameter.")]
    public async Task<Result<IndexedVector>> UpsertAsync(
        string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        ArgumentNullException.ThrowIfNull(embedding);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"""
                INSERT INTO {_qualifiedTable} (digest, model, post_id, seq, embedding, indexed_at)
                VALUES (@digest, @model, @post, @seq, @embedding::vector, @at)
                ON CONFLICT (digest, model) DO UPDATE
                    SET post_id = EXCLUDED.post_id, seq = EXCLUDED.seq, embedding = EXCLUDED.embedding, indexed_at = EXCLUDED.indexed_at;
                """, connection);
            command.Parameters.Add(new NpgsqlParameter("digest", NpgsqlDbType.Text) { Value = digest });
            command.Parameters.Add(new NpgsqlParameter("model", NpgsqlDbType.Text) { Value = embedding.Model.Id });
            command.Parameters.Add(new NpgsqlParameter("post", NpgsqlDbType.Text) { Value = postId });
            command.Parameters.Add(new NpgsqlParameter("seq", NpgsqlDbType.Bigint) { Value = sequence });
            command.Parameters.Add(new NpgsqlParameter("embedding", NpgsqlDbType.Text) { Value = Text(embedding.Vector) });
            command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = _clock.GetUtcNow() });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return Result<IndexedVector>.Ok(new IndexedVector(digest, postId, sequence));
        }
        catch (PostgresException e)
        {
            return Result<IndexedVector>.Fail(Translate(e));
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every awaited call carries ConfigureAwait(false); what the analyzer flags is the `await using` disposal of the connection and command, which have no ConfigureAwait to give, exactly as in PostgresEventStore.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See UpsertAsync's identical suppression.")]
    public async Task<Result<ImmutableArray<VectorMatch>>> NearestAsync(
        Embedding query, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (limit < 1 || limit > MaximumLimit)
            return Result<ImmutableArray<VectorMatch>>.Fail(RetrievalErrors.LimitOutOfRange(limit, MaximumLimit));

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"""
                SELECT digest, post_id, seq, (embedding <=> @query::vector) AS distance
                FROM {_qualifiedTable}
                WHERE model = @model
                ORDER BY embedding <=> @query::vector, seq
                LIMIT @limit;
                """, connection);
            command.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Text) { Value = Text(query.Vector) });
            command.Parameters.Add(new NpgsqlParameter("model", NpgsqlDbType.Text) { Value = query.Model.Id });
            command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = limit });

            var matches = ImmutableArray.CreateBuilder<VectorMatch>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                matches.Add(new VectorMatch(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetDouble(3)));

            return Result<ImmutableArray<VectorMatch>>.Ok(matches.ToImmutable());
        }
        catch (PostgresException e)
        {
            return Result<ImmutableArray<VectorMatch>>.Fail(Translate(e));
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every awaited call carries ConfigureAwait(false); what the analyzer flags is the `await using` disposal of the connection and command, which have no ConfigureAwait to give, exactly as in PostgresEventStore.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See UpsertAsync's identical suppression.")]
    public async Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"SELECT count(*) FROM {_qualifiedTable} WHERE model = @model;", connection);
            command.Parameters.Add(new NpgsqlParameter("model", NpgsqlDbType.Text) { Value = model.Id });
            var count = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            return Result<long>.Ok(count);
        }
        catch (PostgresException e)
        {
            return Result<long>.Fail(Translate(e));
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See UpsertAsync's identical suppression.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See UpsertAsync's identical suppression.")]
    public async Task<Result<long>> MaxSequenceAsync(EmbeddingModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"SELECT coalesce(max(seq), 0) FROM {_qualifiedTable} WHERE model = @model;", connection);
            command.Parameters.Add(new NpgsqlParameter("model", NpgsqlDbType.Text) { Value = model.Id });
            var max = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            return Result<long>.Ok(max);
        }
        catch (PostgresException e)
        {
            return Result<long>.Fail(Translate(e));
        }
    }

    /// <summary>pgvector's text form: <c>[x,y,z]</c>, each component in the round-trip format.</summary>
    public static string Text(ImmutableArray<float> vector)
    {
        var text = new StringBuilder(vector.Length * 12 + 2).Append('[');
        for (var i = 0; i < vector.Length; i++)
        {
            if (i > 0) text.Append(',');
            text.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }
        return text.Append(']').ToString();
    }

    private static Error Translate(PostgresException e) =>
        e.SqlState == UndefinedObject && e.MessageText.Contains("vector", StringComparison.OrdinalIgnoreCase)
            ? RetrievalErrors.PgvectorMissing()
            : RetrievalErrors.IndexUnavailable($"{e.SqlState}: {e.MessageText}");
}
