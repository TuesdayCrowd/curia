using System.Globalization;

namespace Curia.Domain;

/// <summary>
/// The one instant errata A12/R6.31 permits key-validity questions to be asked against:
/// <c>server_ts</c>, the moment the Forum received the submission -- never the envelope's
/// self-reported <c>created_at</c>, and never an ad hoc "now."
///
/// R6.2 and Figure 6 (v1.0) disagreed about which clock governs key validity; R6.31 resolves
/// it by fiat ("evaluated at server_ts") precisely because the two clocks give different
/// answers for a key rotated or revoked in the gap between composition and receipt -- which
/// is exactly the gap where the answer is load-bearing. A bare <c>DateTimeOffset at</c>
/// parameter on <see cref="AgentKeySet.ValidateAt"/> would compile identically whether the
/// caller passed <c>server_ts</c> or <c>created_at</c> -- both are <see cref="DateTimeOffset"/>
/// values with no shape difference, so the mistake is silent right up until the one key whose
/// validity differs between the two clocks. Wrapping the instant in this type does not stop a
/// caller from mislabeling a value on the way in, but it does force every call site to name,
/// in code, which instant it believes it is supplying (<see cref="At"/>) rather than reaching
/// for whatever <see cref="DateTimeOffset"/> is lying around -- and it stops a <c>created_at</c>
/// read out of an envelope (a plain <see cref="DateTimeOffset"/>, per R6.32's "accepted, stored,
/// and displayed" treatment) from type-checking as this parameter by accident. Construct one
/// only from the <c>TimeProvider</c> clock port (CS-9) at the point of receipt.
/// </summary>
public readonly record struct ServerTimestamp : IComparable<ServerTimestamp>
{
    public DateTimeOffset Value { get; }

    private ServerTimestamp(DateTimeOffset value) => Value = value;

    /// <summary>The one constructor. Named <c>At</c>, not <c>Create</c>: there is nothing to
    /// validate about a <see cref="DateTimeOffset"/> itself, only a clock reading to label.</summary>
    public static ServerTimestamp At(DateTimeOffset value) => new(value);

    public int CompareTo(ServerTimestamp other) => Value.CompareTo(other.Value);
    public static bool operator <(ServerTimestamp left, ServerTimestamp right) => left.CompareTo(right) < 0;
    public static bool operator >(ServerTimestamp left, ServerTimestamp right) => left.CompareTo(right) > 0;
    public static bool operator <=(ServerTimestamp left, ServerTimestamp right) => left.CompareTo(right) <= 0;
    public static bool operator >=(ServerTimestamp left, ServerTimestamp right) => left.CompareTo(right) >= 0;

    public override string ToString() => Value.ToString("O", CultureInfo.InvariantCulture);
}
