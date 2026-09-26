using System.Data;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// The Registrar's key store (R4.16 rev., errata A16), in Postgres.
///
/// <para><b>What holding this in memory cost.</b> Every enrollment vanished on restart, and with
/// it the only record of which key belonged to which agent -- which means every post ever made
/// became unverifiable, because R6.31 asks "was this <c>kid</c> valid for this author at this
/// post's <c>server_ts</c>" and an empty store answers no to all of them. An archive whose
/// authorship claims evaporate on a process restart has the code of non-repudiation and none of
/// the property. R4.19 says the same thing about the narrower case: revoked <c>kid</c>s are
/// retained indefinitely "because verifying a historical signature requires knowing what was
/// valid when it was made."</para>
///
/// <para><b>Three ports, one table, and none of them can see the other two.</b>
/// <see cref="IAuthorKeyResolver"/> and <see cref="IAuthorKeyRegistry"/> are declared in
/// <c>Curia.Application</c>, which the architecture test confines to Domain, Canon and
/// Domain.Primitives; <see cref="Curia.AuthN.Ports.IAgentKeyResolver"/> is declared in
/// <c>Curia.AuthN</c>, which cannot see Application either. Two modules that cannot reference one
/// another each declare the capability they need, and one adapter satisfies all of them over one
/// table. That is the ordinary hexagonal answer, and it is what keeps the ingest path from
/// acquiring a dependency on the authentication module merely to look up a public key.</para>
///
/// <para><b>R4.16 rev. is visible in what is absent.</b> There is no HTTP client here and nowhere
/// to put one: the Registrar's store is authoritative and the Forum serves JWKS rather than
/// fetching an agent-hosted one, so the SSRF and availability surface errata A16 removed cannot
/// reappear by accident in this type.</para>
/// </summary>
/// <remarks>
/// The <c>schema</c> parameter exists for per-test isolation; see
/// <see cref="PostgresEventStore"/>'s remarks. Production uses the default.
/// </remarks>
public sealed class PostgresAgentKeyStore : IAuthorKeyResolver, IAuthorKeyRegistry, Curia.AuthN.Ports.IAgentKeyResolver
{
    private const string SelectColumns = "alg, kid, public_key, valid_from, valid_until";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly string _table;

    public PostgresAgentKeyStore(NpgsqlDataSource dataSource, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        _dataSource = dataSource;
        _schema = schema;
        _table = SqlIdentifier.Quote(schema) + ".agent_keys";
    }

    /// <summary>
    /// The advisory-lock key an enrollment of <paramref name="agentId"/> takes (R4.31), exposed so a
    /// test can hold it and observe that an enrollment waits. One key per identifier per schema:
    /// enrollments of different identifiers never wait for each other, and two isolated test schemas
    /// are two stores.
    /// </summary>
    public static string EnrollmentLockKey(string schema, string agentId) => schema + ":agent_keys:" + agentId;

    /// <summary>
    /// R4.31 and R4.32 as one transaction: take the identifier's lock, read every key it holds,
    /// apply <see cref="KeyEnrollment.Decide"/>, and insert only when it says so.
    ///
    /// <para><b>The lock is what makes the decision true when it is acted on.</b> Without it, two
    /// enrollments of one fresh identifier under two <c>kid</c>s each read "no key", each insert, and
    /// the identifier ends with two keys and two holders -- the defect errata G14 records, reached by
    /// a race instead of a request. A <c>WHERE NOT EXISTS</c> in the insert would not help: under
    /// READ COMMITTED both statements can see the absence. The advisory lock is taken before the read
    /// and held to commit, which is the idiom <see cref="PostgresEventStore"/> uses for R6.47.</para>
    ///
    /// <para><b>No UPDATE anywhere in this path.</b> A held key is returned as it is, window and all;
    /// the insert is <c>ON CONFLICT (kid) DO NOTHING</c>, and a conflict there -- the identifier held
    /// no key, so the <c>kid</c> is someone else's -- is the one refusal the rule cannot see from this
    /// identifier's rows.</para>
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See RegisterAsync's identical suppression.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See RegisterAsync's identical suppression: the only interpolated text is " +
            "_table and the SelectColumns constant, both fixed before any call.")]
    public async Task<Result<RegisteredKey>> EnrollAsync(
        string agentId,
        PublicKeyMaterial key,
        DateTimeOffset notBefore,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        // READ COMMITTED, named rather than left to the driver: the lock below serializes enrollments
        // of one identifier only because the SELECT after it takes a fresh snapshot once the lock is
        // granted. Under REPEATABLE READ the snapshot is taken at the lock statement, before the lock
        // is granted, so two racers on a fresh identifier would each read no key and each insert one.
        // Npgsql already sends READ COMMITTED for an unspecified level; naming it makes that a decision.
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtextextended(@lockkey, 0));", connection, transaction))
        {
            lockCommand.Parameters.Add(new NpgsqlParameter("lockkey", NpgsqlDbType.Text) { Value = EnrollmentLockKey(_schema, agentId) });
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var held = new List<RegisteredKey>();
        await using (var select = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM {_table} WHERE agent_id = @agent;", connection, transaction))
        {
            select.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = agentId });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                held.Add(MapRow(reader));
        }

        var decided = KeyEnrollment.Decide(agentId, key, held);
        if (!decided.TryGetValue(out var existing, out var refusal))
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result<RegisteredKey>.Fail(refusal!);
        }

        if (existing is not null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result<RegisteredKey>.Ok(existing);
        }

        RegisteredKey? registered = null;
        await using (var insert = new NpgsqlCommand(
            $"""
             INSERT INTO {_table} (kid, agent_id, alg, public_key, valid_from, valid_until)
             VALUES (@kid, @agent, @alg, @public, @from, NULL)
             ON CONFLICT (kid) DO NOTHING
             RETURNING {SelectColumns};
             """,
            connection,
            transaction))
        {
            insert.Parameters.Add(new NpgsqlParameter("kid", NpgsqlDbType.Text) { Value = key.Kid });
            insert.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = agentId });
            insert.Parameters.Add(new NpgsqlParameter("alg", NpgsqlDbType.Text) { Value = key.Alg });
            insert.Parameters.Add(new NpgsqlParameter("public", NpgsqlDbType.Bytea) { Value = key.Public.ToArray() });
            insert.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = notBefore });

            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                registered = MapRow(reader);
        }

        if (registered is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return Result<RegisteredKey>.Fail(AuthorKeyErrors.KidRegisteredToAnotherAgent(agentId, key.Kid));
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result<RegisteredKey>.Ok(registered);
    }

    /// <summary>
    /// The key store's history primitive: records a key with a window, refusing a <c>kid</c> already
    /// registered to a different agent (<see cref="AuthorKeyErrors.KidRegisteredToAnotherAgent"/>) or
    /// already registered with different material (<see cref="AuthorKeyErrors.MaterialImmutable"/>,
    /// R4.32).
    ///
    /// <para><b>Internal, and on no port.</b> Enrollment's write is <see cref="EnrollAsync"/>, which
    /// registers only for an identifier that holds no key (R4.31). This method registers for any
    /// identifier, which is the capability errata G14 found the enrollment endpoint exercising for
    /// every caller; it stays because R4.18's rotation and R4.19's revocation will each need the
    /// window arithmetic below behind a port that proves what they must, and until then its callers
    /// are this assembly's tests.</para>
    ///
    /// <para><b>The PRIMARY KEY on <c>kid</c> decides ownership, not the application.</b> The whole
    /// decision is one <c>INSERT ... ON CONFLICT (kid) DO UPDATE ... WHERE</c> the existing row names
    /// the same agent, algorithm and bytes. A row comes back when the caller re-presents the key it
    /// holds; nothing comes back otherwise, and a second read then says which refusal it was.</para>
    ///
    /// <para><b><c>valid_from</c> only ever moves earlier</b> -- <c>LEAST</c>, not assignment: R6.31
    /// evaluates validity at each post's <c>server_ts</c>, and a <c>valid_from</c> dragged forward
    /// declares last week's posts signed by a key that did not yet exist. <b><c>valid_until</c> only
    /// ever moves earlier</b> too, so a repeat registration cannot un-revoke a key; <c>LEAST</c>
    /// ignores nulls, which reads null as "no revocation recorded".</para>
    ///
    /// <para><b>The material is never written over.</b> The statement sets the window alone, and
    /// db/0005 grants the application role UPDATE on those two columns and no others, so a statement
    /// that tried to set <c>alg</c> or <c>public_key</c> would be refused by Postgres rather than
    /// obeyed. Before errata G14 this method set both, last write winning, and the enrollment endpoint
    /// reached it with whatever bytes a request carried.</para>
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every explicit await below already calls ConfigureAwait(false) (matches " +
            "PostgresEventStore's convention). The remaining, unfixable flags are the " +
            "compiler-generated DisposeAsync() awaits `await using` inserts at scope exit, which " +
            "offer no ConfigureAwait call site to attach to; this assembly is a server-side data " +
            "adapter with no SynchronizationContext to avoid resuming on.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only text interpolated into the statement is _table, computed once in " +
            "the constructor from SqlIdentifier.Quote(schema) -- a constructor argument every caller " +
            "in this solution supplies itself, never external input -- plus the SelectColumns " +
            "constant. Every per-call value (agent id, kid, algorithm, key bytes, the two instants) " +
            "is bound through a parameterized NpgsqlParameter and never reaches the command text.")]
    internal async Task<Result<RegisteredKey>> RegisterAsync(
        string agentId,
        PublicKeyMaterial key,
        DateTimeOffset notBefore,
        DateTimeOffset? notAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = new NpgsqlCommand(
            $"""
             INSERT INTO {_table} AS existing (kid, agent_id, alg, public_key, valid_from, valid_until)
             VALUES (@kid, @agent, @alg, @public, @from, @until)
             ON CONFLICT (kid) DO UPDATE
               SET valid_from  = LEAST(existing.valid_from, EXCLUDED.valid_from),
                   valid_until = LEAST(existing.valid_until, EXCLUDED.valid_until)
               WHERE existing.agent_id   = EXCLUDED.agent_id
                 AND existing.alg        = EXCLUDED.alg
                 AND existing.public_key = EXCLUDED.public_key
             RETURNING {SelectColumns};
             """,
            connection))
        {
            command.Parameters.Add(new NpgsqlParameter("kid", NpgsqlDbType.Text) { Value = key.Kid });
            command.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = agentId });
            command.Parameters.Add(new NpgsqlParameter("alg", NpgsqlDbType.Text) { Value = key.Alg });
            command.Parameters.Add(new NpgsqlParameter("public", NpgsqlDbType.Bytea) { Value = key.Public.ToArray() });
            command.Parameters.Add(new NpgsqlParameter("from", NpgsqlDbType.TimestampTz) { Value = notBefore });
            command.Parameters.Add(new NpgsqlParameter("until", NpgsqlDbType.TimestampTz)
            {
                Value = notAfter is { } until ? until : DBNull.Value,
            });

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return Result<RegisteredKey>.Ok(MapRow(reader));
        }

        // No row: the kid exists and the WHERE refused. Which refusal is a fact about the row that
        // stands, and a kid's owner never changes (db/0005 grants no UPDATE on agent_id), so reading
        // it after the statement cannot race into a different answer.
        await using var owner = new NpgsqlCommand($"SELECT agent_id FROM {_table} WHERE kid = @kid;", connection);
        owner.Parameters.Add(new NpgsqlParameter("kid", NpgsqlDbType.Text) { Value = key.Kid });
        var holder = (string?)await owner.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return string.Equals(holder, agentId, StringComparison.Ordinal)
            ? Result<RegisteredKey>.Fail(AuthorKeyErrors.MaterialImmutable(key.Kid))
            : Result<RegisteredKey>.Fail(AuthorKeyErrors.KidRegisteredToAnotherAgent(agentId, key.Kid));
    }

    /// <inheritdoc />
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See RegisterAsync's identical suppression.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See RegisterAsync's identical suppression: the only interpolated text is " +
            "_table and the SelectColumns constant, both fixed before any call.")]
    public async Task<IReadOnlyList<RegisteredKey>> KeysForAsync(
        string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM {_table} WHERE agent_id = @agent ORDER BY valid_from DESC, kid;",
            connection);
        command.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = agentId });

        var keys = new List<RegisteredKey>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            keys.Add(MapRow(reader));

        return keys;
    }

    /// <summary>
    /// R6.31 (errata A12) for both resolvers: the key registered <b>to this agent</b> under this
    /// <c>kid</c>, valid at <paramref name="at"/> -- a <c>server_ts</c>, never "now". Ingest asks for
    /// a post's author (R6.2, <see cref="IAuthorKeyResolver"/>); the token endpoint asks for the agent
    /// a request names as its client (R5.20, <c>Curia.AuthN.Ports.IAgentKeyResolver</c>). The two
    /// ports' signatures are identical, so this one method implements both, and nothing here resolves
    /// a key by <c>kid</c> alone: a signature shows that its signer holds some registered key, and
    /// only this table says whose (errata G15).
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See RegisterAsync's identical suppression.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See RegisterAsync's identical suppression: the only interpolated text is " +
            "_table and the SelectColumns constant, both fixed before any call.")]
    public async Task<Result<PublicKeyMaterial>> ResolveAsync(
        string agentId,
        string kid,
        ServerTimestamp at,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(kid);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM {_table} WHERE kid = @kid AND agent_id = @agent;", connection);
        command.Parameters.Add(new NpgsqlParameter("kid", NpgsqlDbType.Text) { Value = kid });
        command.Parameters.Add(new NpgsqlParameter("agent", NpgsqlDbType.Text) { Value = agentId });

        return ValidateAt(await ReadOneAsync(command, cancellationToken).ConfigureAwait(false), agentId, kid, at);
    }

    /// <summary>
    /// The single R6.31 evaluation, kept apart from the query. Written once because two copies of a
    /// validity check are two chances for one of them to compare the wrong instant, which is the
    /// exact shape of errata A12.
    /// </summary>
    private static Result<PublicKeyMaterial> ValidateAt(
        RegisteredKey? registered, string agentId, string kid, ServerTimestamp at)
    {
        if (registered is null)
            return Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NotRegisteredToAgent(agentId, kid));

        // R6.31 / errata A12: validity is evaluated at server_ts, not at "now". There is no
        // TimeProvider in this type at all, which is the strongest available statement that the
        // instant can only come from the caller.
        if (at.Value < registered.NotBefore)
            return Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NotYetValid(kid, at));

        if (registered.NotAfter is { } notAfter && at.Value >= notAfter)
            return Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NoLongerValid(kid, at));

        return Result<PublicKeyMaterial>.Ok(registered.Key);
    }

    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See RegisterAsync's identical suppression.")]
    private static async Task<RegisteredKey?> ReadOneAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MapRow(reader) : null;
    }

    /// <summary>Reconstructs a <see cref="RegisteredKey"/> from one row shaped like
    /// <see cref="SelectColumns"/>.</summary>
    private static RegisteredKey MapRow(NpgsqlDataReader reader) => new(
        new PublicKeyMaterial(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<byte[]>(2)),
        reader.GetFieldValue<DateTimeOffset>(3),
        reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4));
}
