using System.Globalization;

namespace Curia.Domain.Primitives;

/// <summary>
/// The Forum's one authoritative instant: <c>server_ts</c>, the moment the Forum itself received
/// or recorded something -- never an envelope's self-reported <c>created_at</c>, and never an ad
/// hoc "now" read from wherever a caller happens to be standing. Errata A12/R6.31 is the
/// requirement this type exists to make un-skippable: key validity is evaluated at
/// <c>server_ts</c>, precisely because a key rotated or revoked in the gap between an envelope's
/// composition and the Forum's receipt of it makes <c>server_ts</c> and <c>created_at</c> give
/// different answers for the one question that matters -- and that gap is exactly where getting
/// it wrong is silent.
///
/// A bare <see cref="DateTimeOffset"/> parameter would compile identically whether the caller
/// passed <c>server_ts</c> or <c>created_at</c> -- both are <see cref="DateTimeOffset"/> values
/// with no shape difference, so a mislabeled instant type-checks right up until the one case
/// whose answer differs between the two clocks. Wrapping the instant here does not stop a caller
/// from mislabeling a value on the way in, but it does force every call site to name, in code,
/// which instant it believes it is supplying (<see cref="At"/>) rather than reaching for whatever
/// <see cref="DateTimeOffset"/> is lying around -- and it stops an envelope's <c>created_at</c>
/// (a plain <see cref="DateTimeOffset"/>, per R6.32's "accepted, stored, and displayed" treatment)
/// from type-checking as this type by accident.
///
/// Lives in <c>Curia.Domain.Primitives</c>, not <c>Curia.Domain</c>, because it is the shared
/// floor beneath more than one consumer that must never see each other's internals (CS-5):
/// <c>Curia.Domain</c>'s key-validity model (<c>AgentKeySet.ValidateAt</c>) and a
/// <c>Curia.AuthN</c> port both need to name this exact instant, and <c>Curia.AuthN</c>
/// deliberately does not reference <c>Curia.Domain</c>. Construct one only from the
/// <c>TimeProvider</c> clock port (CS-9) at the point of receipt.
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
