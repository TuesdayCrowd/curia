using System.Diagnostics.CodeAnalysis;
using Curia.AuthN.Ports;
using Curia.Domain.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// R5.14-R5.17's <c>jti</c> replay cache, in Postgres.
///
/// <para><b>Why this is a table and not a dictionary.</b> A replay cache is a security control:
/// it is what stops a captured client assertion or DPoP proof being presented twice. R5.15 states
/// the consequence of holding one in a process -- "The cache SHALL be shared across all instances
/// of a resource server (Redis or equivalent) -- a per-process cache means an attacker replays
/// against a different pod and succeeds" -- and a restart is the same failure in the time
/// dimension: every <c>jti</c> the process had seen is forgotten, and every artifact still inside
/// its own lifetime becomes usable again. Postgres is the "or equivalent" this deployment already
/// runs. Redis is the better fit at volume, and swapping to it is a composition-root edit,
/// because <see cref="IReplayCache"/> is unchanged.</para>
///
/// <para><b>R5.17 is satisfied by the statement, not by the method.</b> "Cache insertion SHALL be
/// atomic (compare-and-set / <c>SET NX</c>). A check-then-insert sequence is a race that a
/// concurrent replay wins." So the accept/reject decision is one <c>INSERT ... ON CONFLICT</c>
/// against a primary key -- there is no SELECT anywhere in it, and no window between deciding and
/// recording for a concurrent caller to fit inside. Two callers presenting the same <c>jti</c>
/// simultaneously contend on the index; exactly one wins, and Postgres decides which, rather than
/// the order two application threads happened to interleave.</para>
/// </summary>
/// <remarks>
/// The <c>schema</c> parameter exists for the same reason <see cref="PostgresEventStore"/>'s does
/// -- genuine per-test isolation without a database per test; see that type's remarks. Production
/// uses the default.
/// </remarks>
public sealed class PostgresReplayCache : IReplayCache
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _clock;
    private readonly string _sql;

    public PostgresReplayCache(NpgsqlDataSource dataSource, TimeProvider clock, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        _dataSource = dataSource;
        _clock = clock;

        var table = SqlIdentifier.Quote(schema) + ".authn_replay";

        // Built once in the constructor, for the same reason PostgresEventStore computes its
        // qualified table name here: the only text that varies is the schema, it is not
        // caller-supplied per call, and doing the interpolation once makes that fact checkable
        // at a glance rather than at every call site.
        //
        // `AS cached` names the existing row for the ON CONFLICT clause. Postgres exposes the
        // conflicting row under the target table's name or its alias, and a schema-qualified
        // reference is not accepted there -- so the alias is not decoration, it is what lets this
        // statement work in a non-public schema at all.
        _sql = $"""
                INSERT INTO {table} AS cached (jti, expires_at)
                VALUES (@jti, @expires)
                ON CONFLICT (jti) DO UPDATE SET expires_at = EXCLUDED.expires_at
                  WHERE cached.expires_at <= @now
                RETURNING jti;
                DELETE FROM {table} WHERE expires_at <= @now;
                """;
    }

    /// <summary>
    /// Records <paramref name="jti"/> as seen. True: this call performed the insertion, so it is
    /// a first use -- accept. False: a live entry was already there -- replay, reject.
    ///
    /// <para><b>The <c>DO UPDATE ... WHERE</c> clause is what makes an expired entry stop
    /// blocking, atomically.</b> Three outcomes, one statement:</para>
    /// <list type="bullet">
    /// <item>No row for this <c>jti</c>: the INSERT happens, RETURNING yields a row, accept.</item>
    /// <item>A row whose <c>expires_at</c> has passed: the WHERE holds, the UPDATE overwrites it
    /// with the new window, RETURNING yields a row, accept. The old entry was retention past its
    /// purpose -- R5.14 bounds retention at "at least the maximum token lifetime plus the maximum
    /// permitted clock skew", and an artifact past that instant can no longer be presented, so it
    /// can no longer be replayed either.</item>
    /// <item>A row still inside its window: the WHERE fails, nothing is written, RETURNING yields
    /// nothing -- reject. This is the replay, and it is the only outcome that matters.</item>
    /// </list>
    ///
    /// <para>Spelling the same behavior as "DELETE the expired row, then INSERT" would be
    /// precisely the check-then-insert R5.17 names, with a wider window than the naive version:
    /// two callers could both delete-then-insert and both be told first use.</para>
    ///
    /// <para>The trailing DELETE is pure garbage collection -- no decision depends on it, which is
    /// why it needs no coordination and why its failure to keep up costs correctness nothing. It
    /// runs after the upsert, not before, so that Npgsql's first result set is unambiguously the
    /// RETURNING clause's. A deployment at volume moves this to a scheduled job; at prototype
    /// volume an extra indexed DELETE in the same round trip is cheaper than a second moving
    /// part.</para>
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
        Justification = "_sql is composed once in the constructor and the only text interpolated " +
            "into it is SqlIdentifier.Quote(schema) -- a constructor argument every caller in this " +
            "solution supplies itself, never external input. Every per-call value (jti and the two " +
            "instants) is bound through a parameterized NpgsqlParameter and never reaches the " +
            "command text.")]
    public async Task<Result<bool>> TryInsertAsync(
        string jti, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jti);

        // CS-9: the only source of "now" in this type. The same value is used for both the
        // expiry predicate and the prune, so a single call cannot decide that an entry is live
        // for one purpose and expired for the other.
        var now = _clock.GetUtcNow();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(_sql, connection);

        command.Parameters.Add(new NpgsqlParameter("jti", NpgsqlDbType.Text) { Value = jti });
        command.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = expiresAt });
        command.Parameters.Add(new NpgsqlParameter("now", NpgsqlDbType.TimestampTz) { Value = now });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        // "A row came back" is the entire decision. Reading it is what distinguishes an upsert
        // that wrote from one whose WHERE refused, and nothing about the row's contents matters.
        return Result<bool>.Ok(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }
}
