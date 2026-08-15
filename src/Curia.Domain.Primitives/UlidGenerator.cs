using System.Security.Cryptography;

namespace Curia.Domain.Primitives;

/// <summary>
/// The one legitimate way to mint a <see cref="Ulid"/>. The millisecond timestamp comes from the
/// injected <see cref="TimeProvider"/> clock port and nowhere else (CS-9) -- there is no
/// parameterless constructor and no ambient-clock code path.
///
/// <para>
/// Implements the ULID spec's monotonicity guidance
/// (https://github.com/ulid/spec#monotonicity): when <see cref="Next"/> is called twice within
/// the same millisecond, the second call reuses the first call's timestamp and increments its
/// 80-bit randomness component by exactly 1 (with carry) rather than drawing fresh randomness.
/// That is what keeps <see cref="Ulid"/>'s lexicographic sort order matching call order even when
/// the clock does not advance between two calls, which is the property the event store's
/// ordering leans on. A new millisecond resets the randomness to a fresh cryptographically secure
/// 80-bit draw, so two different millisecond buckets are not distinguishable by a "which one
/// incremented from what" argument -- only their timestamps, and then the spec's tie-break.
/// </para>
///
/// <para>
/// <b>Random-overflow within a millisecond.</b> Incrementing by 1 can only continue for 2^80
/// calls before the randomness component itself would need to wrap back to zero, which would
/// silently break the very sort guarantee this type exists to provide (a wrapped value sorts
/// before, not after, the value it wrapped from). On that (astronomically unlikely) event,
/// <see cref="Next"/> returns <see cref="Result{T}.Fail"/> rather than either wrapping the counter
/// or drawing fresh non-monotonic randomness -- consistent with CS-10 treating this as domain
/// fallibility a caller decides how to handle (e.g. retry once the clock ticks over), not a
/// condition worth crashing the process over.
/// </para>
///
/// <para>
/// Deliberately not static/ambient (the "explicit dependencies over globals" guideline): a caller
/// that wants monotonic IDs constructs and holds one <see cref="UlidGenerator"/>, the same way the
/// in-memory event store holds its injected <see cref="TimeProvider"/> rather than reading a
/// global clock. A single internal lock serializes calls on one instance, the same trade the
/// in-memory event store makes for the same reason: correctness under concurrent callers matters
/// more here than throughput.
/// </para>
/// </summary>
public sealed class UlidGenerator
{
    private const int RandomnessByteCount = 10; // 80 bits

    private readonly TimeProvider _clock;
    private readonly object _gate = new();

    private long _lastTimestampMilliseconds = -1;
    private UInt128 _lastRandomness;

    public UlidGenerator(TimeProvider clock) =>
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>
    /// Mints the next <see cref="Ulid"/>. Monotonic against every previous call on this same
    /// instance; carries no ordering guarantee against a different <see cref="UlidGenerator"/>
    /// instance beyond what the millisecond timestamp alone provides.
    /// </summary>
    public Result<Ulid> Next()
    {
        lock (_gate)
        {
            var timestampMilliseconds = _clock.GetUtcNow().ToUnixTimeMilliseconds();
            if (timestampMilliseconds < 0 || timestampMilliseconds > Ulid.MaxTimestampMilliseconds)
                return Result<Ulid>.Fail(DomainPrimitivesErrors.UlidTimestampOutOfRange(timestampMilliseconds));

            if (timestampMilliseconds == _lastTimestampMilliseconds)
            {
                if (_lastRandomness == Ulid.MaxRandomness)
                    return Result<Ulid>.Fail(DomainPrimitivesErrors.UlidRandomnessExhausted(timestampMilliseconds));

                _lastRandomness += 1;
            }
            else
            {
                _lastTimestampMilliseconds = timestampMilliseconds;
                _lastRandomness = DrawRandomness();
            }

            return Result<Ulid>.Ok(Ulid.FromParts(_lastTimestampMilliseconds, _lastRandomness));
        }
    }

    /// <summary>80 bits of cryptographically secure randomness, assembled big-endian to match <see cref="Ulid"/>'s bit layout.</summary>
    private static UInt128 DrawRandomness()
    {
        Span<byte> buffer = stackalloc byte[RandomnessByteCount];
        RandomNumberGenerator.Fill(buffer);

        UInt128 value = 0;
        foreach (var b in buffer)
            value = (value << 8) | b;

        return value;
    }
}
