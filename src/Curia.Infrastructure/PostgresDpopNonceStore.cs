using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.AuthN;
using Curia.AuthN.Ports;
using Curia.Domain.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// R5.19's (errata B4) DPoP server nonce store, in Postgres, rotating on the published interval.
///
/// <para>The nonce is what lets the server choose <i>when</i> a proof was made, rather than
/// trusting the client's clock -- it defends against a stockpile of proofs pre-signed before the
/// server picked today's freshness value. Held in one process it defends only that process: a
/// second instance behind a load balancer would be issuing and requiring a completely different
/// value, so a client that obtained its nonce from one pod would be refused by the next, and a
/// restart would invalidate every proof in flight.</para>
///
/// <para><b>Rows are keyed by epoch, and that is the whole design.</b> The epoch is
/// <c>floor(unixSeconds / rotationInterval)</c> -- a number every instance computes identically
/// from its own clock, with no coordination. The first instance to ask inserts the epoch's nonce;
/// every other instance reads that same row back rather than minting a rival value. A table of
/// independently issued nonces, each with its own expiry, would be the in-memory bug faithfully
/// reproduced in a database: two instances, two different beliefs about what is current.</para>
///
/// <para><b>Both the current epoch and the immediately previous one are accepted, deliberately.</b>
/// A store that honored only the newest value would reject every request in flight at the instant
/// of rotation, which reads to a client as a random failure and to an operator as a rotation bug.
/// Accepting the previous epoch bounds that exposure to one rotation interval instead of to zero,
/// and never accepts a value the server did not itself choose. R5.19's ceiling is on the rotation
/// interval, so a nonce honored across a boundary is live for between one and two intervals; that
/// is the trade the RFC 9449 §8 challenge-and-retry flow exists to make unnecessary for
/// correctness and pleasant for clients anyway.</para>
/// </summary>
/// <remarks>
/// The <c>schema</c> parameter exists for per-test isolation; see
/// <see cref="PostgresEventStore"/>'s remarks. Production uses the default.
/// </remarks>
public sealed class PostgresDpopNonceStore : IDpopNonceStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _rotationInterval;
    private readonly string _table;

    /// <param name="rotationInterval">
    /// Defaults to <see cref="AuthNConstants.MaxDpopNonceRotationInterval"/>, taken from there
    /// rather than restated as a literal so the rotation cannot drift from the figure R5.19
    /// fixes. A caller may pass a shorter interval -- R5.19's is a ceiling, not a target -- but
    /// not a longer one, and not a non-positive one: an interval of zero has no epochs to divide
    /// time into and would make every nonce simultaneously current and stale.
    /// </param>
    public PostgresDpopNonceStore(
        NpgsqlDataSource dataSource,
        TimeProvider clock,
        TimeSpan? rotationInterval = null,
        string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        var interval = rotationInterval ?? AuthNConstants.MaxDpopNonceRotationInterval;

        // At least one second because the epoch is integer seconds divided by integer seconds:
        // a sub-second interval truncates to a divisor of zero, which is not a slow rotation but
        // an arithmetic fault. At most R5.19's ceiling because that ceiling is the requirement.
        if (interval < TimeSpan.FromSeconds(1) || interval > AuthNConstants.MaxDpopNonceRotationInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rotationInterval),
                interval,
                "R5.19 caps DPoP nonce rotation at " +
                $"{AuthNConstants.MaxDpopNonceRotationInterval}; the interval must be at least one second and no longer.");
        }

        _dataSource = dataSource;
        _clock = clock;
        _rotationInterval = interval;
        _table = SqlIdentifier.Quote(schema) + ".authn_dpop_nonces";
    }

    /// <summary>
    /// The nonce for the current epoch, inserting it if this is the first instance to ask.
    ///
    /// <para>Two statements at most, and the second runs only when this caller lost the race to
    /// create the epoch's row. <c>ON CONFLICT DO NOTHING</c> makes losing that race silent and
    /// cheap rather than an exception to catch, and re-reading afterwards is what turns the loss
    /// into the winner's value -- which is the answer both instances must agree on.</para>
    ///
    /// <para>A no-op <c>DO UPDATE</c> would collapse this to one statement by always returning the
    /// row, and was rejected: it writes a dead tuple on every call to a table read on the
    /// authentication path, to save a round trip that only happens because the row already
    /// exists and can therefore be read.</para>
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See PostgresReplayCache's identical suppression: every explicit await " +
            "already carries ConfigureAwait(false), and what remains is `await using`'s " +
            "compiler-generated DisposeAsync() await, which has no call site to attach one to.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only text interpolated into either statement is _table, computed once " +
            "in the constructor from SqlIdentifier.Quote(schema) -- a constructor argument every " +
            "caller in this solution supplies itself, never external input. Every per-call value is " +
            "bound through a parameterized NpgsqlParameter.")]
    public async Task<Result<DpopNonce>> IssueAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();
        var epoch = EpochOf(now);
        var expiresAt = ExpiryOf(epoch);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var insert = new NpgsqlCommand(InsertSql, connection))
        {
            insert.Parameters.Add(new NpgsqlParameter("epoch", NpgsqlDbType.Bigint) { Value = epoch });
            insert.Parameters.Add(new NpgsqlParameter("nonce", NpgsqlDbType.Text) { Value = NewNonce() });
            insert.Parameters.Add(new NpgsqlParameter("expires", NpgsqlDbType.TimestampTz) { Value = expiresAt });

            // Prunes epochs that can no longer be accepted, in the same round trip. Garbage
            // collection only: IsCurrentAsync's own predicate is what decides acceptance, so a
            // row this DELETE has not reached yet is refused on its epoch, not on its presence.
            insert.Parameters.Add(new NpgsqlParameter("oldest", NpgsqlDbType.Bigint) { Value = epoch - 1 });

            await using var reader = await insert.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return Result<DpopNonce>.Ok(MapRow(reader));
        }

        await using (var select = new NpgsqlCommand(
            $"SELECT nonce, expires_at FROM {_table} WHERE epoch = @epoch;", connection))
        {
            select.Parameters.Add(new NpgsqlParameter("epoch", NpgsqlDbType.Bigint) { Value = epoch });

            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return Result<DpopNonce>.Ok(MapRow(reader));
        }

        // Unreachable barring an operator deleting the row between the two statements above.
        // Modeled as a thrown infrastructure fault rather than a Result failure, per CS-10:
        // exceptions are for bugs and infrastructure faults, and a row that vanished between two
        // statements of one method is not a domain outcome any caller could act on.
        throw new InvalidOperationException(
            $"DPoP nonce epoch {epoch} was neither inserted nor found immediately afterwards. " +
            "Something outside this adapter is deleting rows from authn_dpop_nonces.");
    }

    /// <summary>
    /// Whether <paramref name="nonce"/> is one this store issued for the current or immediately
    /// previous epoch. Does not consume it -- a nonce is not single-use; the <c>jti</c> replay
    /// cache owns repetition of one proof (see <see cref="IDpopNonceStore"/>'s remarks).
    /// </summary>
    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See IssueAsync's identical suppression.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See IssueAsync's identical suppression: the only interpolated text is " +
            "_table, computed once in the constructor, and the nonce is a bound parameter.")]
    public async Task<Result<bool>> IsCurrentAsync(string nonce, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(nonce))
            return Result<bool>.Ok(false);

        var epoch = EpochOf(_clock.GetUtcNow());

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            // The epoch bound, not expires_at, is what decides this. An expiry comparison would
            // reject the previous epoch's nonce by definition -- expiring is exactly what it did
            // -- and that is the rotation-boundary failure this store is shaped to avoid.
            $"SELECT 1 FROM {_table} WHERE nonce = @nonce AND epoch >= @oldest;", connection);

        command.Parameters.Add(new NpgsqlParameter("nonce", NpgsqlDbType.Text) { Value = nonce });
        command.Parameters.Add(new NpgsqlParameter("oldest", NpgsqlDbType.Bigint) { Value = epoch - 1 });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return Result<bool>.Ok(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Reconstructs a <see cref="DpopNonce"/> from one <c>(nonce, expires_at)</c> row. A separate
    /// non-async method for the reason <see cref="PostgresEventStore"/>'s <c>MapRow</c> is one:
    /// column access on a row already materialized by <c>ReadAsync</c> is synchronous by nature,
    /// and calling it inline in an <see langword="async"/> method makes CA1849 (correctly, for a
    /// rule that cannot see the row is already in hand) report a synchronous block.
    /// </summary>
    private static DpopNonce MapRow(NpgsqlDataReader reader) =>
        new(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1));

    private string InsertSql =>
        $"""
         INSERT INTO {_table} (epoch, nonce, expires_at)
         VALUES (@epoch, @nonce, @expires)
         ON CONFLICT (epoch) DO NOTHING
         RETURNING nonce, expires_at;
         DELETE FROM {_table} WHERE epoch < @oldest;
         """;

    /// <summary>
    /// The epoch number an instant falls in. Integer division of Unix seconds by the rotation
    /// interval's seconds -- deterministic, monotonic, and identical on every instance without a
    /// shared clock beyond the NTP discipline R5.16 already requires ("Hosts SHALL run NTP").
    /// </summary>
    private long EpochOf(DateTimeOffset instant) =>
        instant.ToUnixTimeSeconds() / (long)_rotationInterval.TotalSeconds;

    private DateTimeOffset ExpiryOf(long epoch) =>
        DateTimeOffset.FromUnixTimeSeconds((epoch + 1) * (long)_rotationInterval.TotalSeconds);

    /// <summary>
    /// 128 bits from the platform CSPRNG, base64url-encoded. A nonce is a security value whose
    /// only property is unguessability, so it is drawn from
    /// <see cref="RandomNumberGenerator"/> rather than from any convenient identifier generator
    /// that happens to look random.
    /// </summary>
    private static string NewNonce() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
}
