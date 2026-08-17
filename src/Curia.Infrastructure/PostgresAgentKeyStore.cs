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
    private readonly string _table;

    public PostgresAgentKeyStore(NpgsqlDataSource dataSource, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        _dataSource = dataSource;
        _table = SqlIdentifier.Quote(schema) + ".agent_keys";
    }

    /// <summary>
    /// Registers a key, refusing a <c>kid</c> already registered to a different agent.
    ///
    /// <para><b>The PRIMARY KEY on <c>kid</c> is what enforces this, not the application.</b> The
    /// in-memory predecessor scanned its own dictionary for a colliding <c>kid</c> and then wrote
    /// -- a check-then-act that two concurrent enrollments can both pass, leaving exactly the
    /// ambiguity the check existed to prevent. Here the scan is gone: the whole decision is one
    /// <c>INSERT ... ON CONFLICT (kid) DO UPDATE ... WHERE agent_id matches</c>. A row comes back
    /// when the caller owns the <c>kid</c>; nothing comes back when someone else does, and
    /// Postgres decided that, under an index, for whichever enrollment arrived first.</para>
    ///
    /// <para><b><c>valid_from</c> only ever moves earlier</b> -- <c>LEAST</c>, not assignment.
    /// The in-memory version overwrote it, so an agent re-enrolling (which a client does whenever
    /// it wants a fresh key registration) silently invalidated every signature that key had
    /// already made: R6.31 evaluates validity at each post's <c>server_ts</c>, and a
    /// <c>valid_from</c> dragged forward to today is a declaration that last week's posts were
    /// signed by a key that did not yet exist. This is the same defect
    /// <c>Curia.Api.AgentDirectory.Enroll</c> already fixed for the tenure clock, in the one place
    /// where it destroys evidence rather than standing. The day a key first became valid is a
    /// fact about the archive, not a field the latest request gets to set.</para>
    ///
    /// <para><b><c>valid_until</c> only ever moves earlier</b>, symmetrically, and for a sharper
    /// reason: a repeat enrollment must not be able to <i>un</i>-revoke a key. Postgres's
    /// <c>LEAST</c> ignores nulls, which gives exactly the semantics wanted here with null read as
    /// "no revocation recorded" rather than as "valid forever" -- registering with no expiry over
    /// an existing revocation keeps the revocation, registering a revocation over an open window
    /// applies it, and an earlier revocation beats a later one. The predecessor assigned this
    /// column outright, so an enrollment call could quietly restore a compromised key to service;
    /// R4.19 requires revocation to take effect within 60 seconds, not to take effect until
    /// somebody enrolls again.</para>
    ///
    /// <para>The two key-material columns are last-write-wins, matching the predecessor. A repeat
    /// enrollment that supplies <i>different key bytes</i> under an existing <c>kid</c> is
    /// therefore accepted and silently changes what that <c>kid</c> means -- a real hazard, and
    /// one this increment does not close because closing it properly is R4.18's rotation flow (a
    /// new key signed by a currently valid one), not an extra predicate bolted onto an enrollment
    /// endpoint that has no owner authentication yet either. Recorded here so the next increment
    /// finds it named rather than having to rediscover it.</para>
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
    public async Task<Result<RegisteredKey>> RegisterAsync(
        string agentId,
        PublicKeyMaterial key,
        DateTimeOffset notBefore,
        DateTimeOffset? notAfter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
             INSERT INTO {_table} AS existing (kid, agent_id, alg, public_key, valid_from, valid_until)
             VALUES (@kid, @agent, @alg, @public, @from, @until)
             ON CONFLICT (kid) DO UPDATE
               SET alg         = EXCLUDED.alg,
                   public_key  = EXCLUDED.public_key,
                   valid_from  = LEAST(existing.valid_from, EXCLUDED.valid_from),
                   valid_until = LEAST(existing.valid_until, EXCLUDED.valid_until)
               WHERE existing.agent_id = EXCLUDED.agent_id
             RETURNING {SelectColumns};
             """,
            connection);

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

        // No row means the ON CONFLICT's WHERE refused: the kid exists and belongs to someone
        // else. There is no other way for this statement to write nothing.
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? Result<RegisteredKey>.Ok(MapRow(reader))
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
    /// R6.31 (errata A12) for the ingest path: the key registered <b>to this agent</b> under this
    /// <c>kid</c>, valid at <paramref name="at"/> -- the post's <c>server_ts</c>, never "now".
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
    /// R6.31 for the authentication path, where the question arrives without an agent.
    ///
    /// <para>Resolving by <c>kid</c> alone is correct here and not a weakening: a client assertion
    /// names its key, and the subject is established by <i>which key verified</i> rather than by a
    /// claim, so a <c>kid</c> resolving to some agent's key still only authenticates whoever holds
    /// the matching private key. What it requires is that a <c>kid</c> identify exactly one key --
    /// which is why <c>kid</c> is the table's PRIMARY KEY and why
    /// <see cref="RegisterAsync"/> refuses a collision. Under the in-memory predecessor this was a
    /// scan across every registered agent whose result depended on iteration order; here it is a
    /// primary-key lookup that cannot return two rows because the index cannot hold two.</para>
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
        string kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kid);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM {_table} WHERE kid = @kid;", connection);
        command.Parameters.Add(new NpgsqlParameter("kid", NpgsqlDbType.Text) { Value = kid });

        // "(any)" is the agent in the failure detail, matching what the in-memory adapter
        // reported: the caller genuinely did not name one, and inventing a plausible agent id for
        // the message would make an operator's grep for a real one match this line.
        return ValidateAt(await ReadOneAsync(command, cancellationToken).ConfigureAwait(false), "(any)", kid, at);
    }

    /// <summary>
    /// The single R6.31 evaluation both resolve overloads share. Written once because two copies
    /// of a validity check are two chances for one of them to compare the wrong instant, which is
    /// the exact shape of errata A12.
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
