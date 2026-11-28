using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Infrastructure.Search;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// <see cref="ISearchIndex"/> over the tables of db/0008 (errata G88). Insert-only by construction: there is no <c>UPDATE</c> or <c>DELETE</c> in this type, and the role has no grant for one. The writes go through
/// <see cref="SearchIndexWriter"/>, the one writer, with <c>ON CONFLICT DO NOTHING</c>.
/// </summary>
public sealed class PostgresSearchIndex : ISearchIndex, IAsyncDisposable
{
    private static readonly string[] Indexes =
    [
        "search_docs_pkey", "search_docs_board", "search_docs_author", "search_docs_digest", "search_docs_post_id", "search_docs_tags_folded", "search_terms_pkey", "search_index_versions_pkey",
    ];

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _quotedSchema;
    private readonly bool _ownsDataSource;

    public PostgresSearchIndex(NpgsqlDataSource dataSource, string schema = "public")
        : this(dataSource, schema, ownsDataSource: false)
    {
    }

    private PostgresSearchIndex(NpgsqlDataSource dataSource, string schema, bool ownsDataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _quotedSchema = SqlIdentifier.Quote(schema);
        _ownsDataSource = ownsDataSource;
    }

    /// <summary>An index over its own connection pool, for a composition root that holds no Npgsql type (CS-7). Dispose it.</summary>
    public static PostgresSearchIndex ForConnection(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        return new PostgresSearchIndex(NpgsqlDataSource.Create(connectionString), "public", ownsDataSource: true);
    }

    public ValueTask DisposeAsync() => _ownsDataSource ? _dataSource.DisposeAsync() : ValueTask.CompletedTask;

    private static Result<T> Unavailable<T>(Exception e) =>
        Result<T>.Fail(new Error("curia/store/unavailable", "The store could not be asked", PostgresStoreFaults.Cause(e)));

    private string Name(string table) => $"{_quotedSchema}.{table}";

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Every awaited call carries ConfigureAwait(false); the analyzer flags the `await using` disposal.")]
    public async Task<Result<SearchIndexSchema>> CheckSchemaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            async Task<bool> ExistsAsync(string name)
            {
                await using var command = new NpgsqlCommand("SELECT to_regclass(@name) IS NOT NULL;", connection);
                command.Parameters.AddWithValue("name", Name(name));
                return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            var tables = await ExistsAsync("search_docs").ConfigureAwait(false)
                && await ExistsAsync("search_terms").ConfigureAwait(false)
                && await ExistsAsync("search_index_versions").ConfigureAwait(false);
            var indexes = true;
            foreach (var index in Indexes) indexes &= await ExistsAsync(index).ConfigureAwait(false);

            if (!tables) return Result<SearchIndexSchema>.Ok(new SearchIndexSchema(false, indexes, false, false));

            async Task<bool> HeldAsync(string table, string privilege)
            {
                await using var command = new NpgsqlCommand("SELECT has_table_privilege(current_user, @table::regclass, @privilege);", connection);
                command.Parameters.AddWithValue("table", Name(table));
                command.Parameters.AddWithValue("privilege", privilege);
                return (bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            // SELECT and INSERT on every table (a role that holds INSERT on two of three still cannot append); and any of UPDATE, DELETE, TRUNCATE on any (a role the migration did not narrow).
            var needed = true;
            var excess = false;
            foreach (var table in new[] { "search_docs", "search_terms", "search_index_versions" })
            {
                needed &= await HeldAsync(table, "SELECT").ConfigureAwait(false) && await HeldAsync(table, "INSERT").ConfigureAwait(false);
                foreach (var privilege in new[] { "UPDATE", "DELETE", "TRUNCATE" }) excess |= await HeldAsync(table, privilege).ConfigureAwait(false);
            }

            return Result<SearchIndexSchema>.Ok(new SearchIndexSchema(true, indexes, needed, excess));
        }
        catch (Exception e) when (e is not OperationCanceledException && PostgresStoreFaults.Is(e))
        {
            return Unavailable<SearchIndexSchema>(e);
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Every awaited call carries ConfigureAwait(false); the analyzer flags the `await using` disposal.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is the quoted schema name built once in the constructor; every value is a bound parameter.")]
    public async Task<Result<SearchIndexVersionRecord?>> ReadVersionAsync(short version, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"SELECT tokenizer_digest, fold_digest FROM {Name("search_index_versions")} WHERE index_version = @v;", connection);
            command.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Smallint) { Value = version });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return Result<SearchIndexVersionRecord?>.Ok(
                await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? new SearchIndexVersionRecord([.. (byte[])reader[0]], [.. (byte[])reader[1]]) : null);
        }
        catch (Exception e) when (e is not OperationCanceledException && PostgresStoreFaults.Is(e))
        {
            return Unavailable<SearchIndexVersionRecord?>(e);
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Every awaited call carries ConfigureAwait(false); the analyzer flags the `await using` disposal.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is the quoted schema name built once in the constructor; every value is a bound parameter.")]
    public async Task<Result<bool>> RecordVersionAsync(short version, ImmutableArray<byte> tokenizerDigest, ImmutableArray<byte> foldDigest, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {Name("search_index_versions")} (index_version, tokenizer_digest, fold_digest) VALUES (@v, @t, @f) ON CONFLICT DO NOTHING;", connection);
            command.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Smallint) { Value = version });
            command.Parameters.Add(new NpgsqlParameter("t", NpgsqlDbType.Bytea) { Value = tokenizerDigest.ToArray() });
            command.Parameters.Add(new NpgsqlParameter("f", NpgsqlDbType.Bytea) { Value = foldDigest.ToArray() });
            return Result<bool>.Ok(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1);
        }
        catch (Exception e) when (e is not OperationCanceledException && PostgresStoreFaults.Is(e))
        {
            return Unavailable<bool>(e);
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Every awaited call carries ConfigureAwait(false); the analyzer flags the `await using` disposal.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is the quoted schema name built once in the constructor; every value is a bound parameter.")]
    public async Task<Result<IReadOnlySet<long>>> IndexedSeqsAsync(short version, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"SELECT seq FROM {Name("search_docs")} WHERE index_version = @v;", connection);
            command.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Smallint) { Value = version });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var seqs = new HashSet<long>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) seqs.Add(reader.GetInt64(0));
            return Result<IReadOnlySet<long>>.Ok(seqs);
        }
        catch (Exception e) when (e is not OperationCanceledException && PostgresStoreFaults.Is(e))
        {
            return Unavailable<IReadOnlySet<long>>(e);
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Every awaited call carries ConfigureAwait(false); the analyzer flags the `await using` disposal.")]
    public async Task<Result<long>> InsertAsync(short version, IReadOnlyList<IndexedPost> posts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(posts);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            long written = 0;
            foreach (var post in posts)
            {
                if (await SearchIndexWriter.WriteAsync(connection, transaction, _quotedSchema, version, post, skipExisting: true, cancellationToken).ConfigureAwait(false)) written++;
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result<long>.Ok(written);
        }
        catch (Exception e) when (e is not OperationCanceledException && PostgresStoreFaults.Is(e))
        {
            return Unavailable<long>(e);
        }
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is the quoted schema name built once in the constructor; every value is a bound parameter.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Both commands are assigned inside the try that opens the readers and disposed in the finally below; the analyzer cannot follow an iterator's finally.")]
    public async IAsyncEnumerable<StoredIndexEntry> ReadAllAsync(short version, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        NpgsqlConnection? docsConnection = null, termsConnection = null;
        NpgsqlDataReader? docs = null, terms = null;
        NpgsqlCommand? docsCommand = null, termsCommand = null;
        try
        {
            try
            {
                docsConnection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                termsConnection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                docsCommand = new NpgsqlCommand(
                    $"SELECT seq, post_id, digest, board, kind, author, tags_folded, terms_truncated FROM {Name("search_docs")} WHERE index_version = @v ORDER BY seq;", docsConnection);
                docsCommand.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Smallint) { Value = version });
                docs = await docsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                termsCommand = new NpgsqlCommand(
                    $"SELECT seq, term, title_tf, tag_tf, body_tf FROM {Name("search_terms")} WHERE index_version = @v ORDER BY seq;", termsConnection);
                termsCommand.Parameters.Add(new NpgsqlParameter("v", NpgsqlDbType.Smallint) { Value = version });
                terms = await termsCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (PostgresStoreFaults.IsStoreFault(e, cancellationToken))
            {
                throw PostgresStoreFaults.Unreadable(e);
            }

            var hasDoc = await NextAsync(docs, cancellationToken).ConfigureAwait(false);
            var hasTerm = await NextAsync(terms, cancellationToken).ConfigureAwait(false);
            while (hasDoc || hasTerm)
            {
                var seq = hasDoc && hasTerm ? Math.Min(docs.GetInt64(0), terms.GetInt64(0)) : hasDoc ? docs.GetInt64(0) : terms.GetInt64(0);

                string postId = string.Empty, digest = string.Empty, board = string.Empty, kind = string.Empty, author = string.Empty;
                string[] tags = [];
                var truncated = false;
                var hasDocument = hasDoc && docs.GetInt64(0) == seq;
                if (hasDocument)
                {
                    (postId, digest, board, kind, author) = (docs.GetString(1), docs.GetString(2), docs.GetString(3), docs.GetString(4), docs.GetString(5));
                    tags = (string[])docs.GetValue(6);
                    truncated = docs.GetBoolean(7);
                    hasDoc = await NextAsync(docs, cancellationToken).ConfigureAwait(false);
                }

                var rows = ImmutableArray.CreateBuilder<IndexedTerm>();
                while (hasTerm && terms.GetInt64(0) == seq)
                {
                    rows.Add(new IndexedTerm(terms.GetString(1), terms.GetInt32(2), terms.GetInt32(3), terms.GetInt32(4)));
                    hasTerm = await NextAsync(terms, cancellationToken).ConfigureAwait(false);
                }

                yield return new StoredIndexEntry(seq, hasDocument, new IndexedPost(seq, postId, digest, board, kind, author, [.. tags], truncated, rows.ToImmutable()));
            }
        }
        finally
        {
            if (docs is not null) await docs.DisposeAsync().ConfigureAwait(false);
            if (terms is not null) await terms.DisposeAsync().ConfigureAwait(false);
            if (docsCommand is not null) await docsCommand.DisposeAsync().ConfigureAwait(false);
            if (termsCommand is not null) await termsCommand.DisposeAsync().ConfigureAwait(false);
            if (docsConnection is not null) await docsConnection.DisposeAsync().ConfigureAwait(false);
            if (termsConnection is not null) await termsConnection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<bool> NextAsync(NpgsqlDataReader reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (PostgresStoreFaults.IsStoreFault(e, cancellationToken))
        {
            throw PostgresStoreFaults.Unreadable(e);
        }
    }
}
