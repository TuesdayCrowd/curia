using System.Globalization;

namespace Curia.Domain.Primitives;

/// <summary>
/// RFC 9457 problem-type slugs for <c>Curia.Domain.Primitives</c>, mirroring
/// <c>Curia.Domain.DomainErrors</c>/<c>KeyErrors</c>'s one-factory-per-condition shape so every
/// rejection in this project names the rule it enforces, even though this project sits below
/// <c>Curia.Domain</c> in the hexagon and cannot reference those types itself (R11.1).
/// </summary>
public static class DomainPrimitivesErrors
{
    /// <summary>The canonical ULID encoding is exactly 26 characters; anything else is rejected outright.</summary>
    public static Error UlidWrongLength(int actualLength) => new(
        "curia/domain-primitives/ulid/wrong-length",
        "A ULID must be exactly 26 characters",
        $"length={actualLength.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Covers both "not a Crockford Base32 symbol at all" and the four symbols Crockford's
    /// alphabet deliberately excludes for visual-ambiguity/obscenity-avoidance reasons -- I, L,
    /// O, U -- which <see cref="Ulid.Parse"/> rejects rather than tolerantly mapping to 1/1/0/(none).
    /// </summary>
    public static Error UlidInvalidCharacter(char offendingCharacter) => new(
        "curia/domain-primitives/ulid/invalid-character",
        "A ULID may contain only Crockford Base32 characters: 0-9 and A-Z excluding I, L, O, and U",
        $"character='{offendingCharacter}'");

    /// <summary>
    /// 26 Crockford characters carry 130 bits of nominal capacity for a 128-bit payload, so the
    /// leading character's value is restricted to 0-7 to keep the encoding a bijection with the
    /// 128-bit space. The maximum valid ULID is therefore <c>7ZZZZZZZZZZZZZZZZZZZZZZZZZ</c>; a
    /// leading-character value of 8 or above encodes no representable 128-bit value at all.
    /// </summary>
    public static Error UlidExceedsMaxValue(string text) => new(
        "curia/domain-primitives/ulid/overflow",
        "A ULID's leading character may only encode 0-7; this text exceeds the maximum representable ULID (7ZZZZZZZZZZZZZZZZZZZZZZZZZ)",
        text);

    /// <summary>
    /// A ULID's timestamp component is 48 unsigned bits (0 to 2^48-1 milliseconds since the Unix
    /// epoch, i.e. through the year 10889); <see cref="UlidGenerator"/> raises this if its clock
    /// ever reads outside that range rather than silently truncating the timestamp it encodes.
    /// </summary>
    public static Error UlidTimestampOutOfRange(long timestampMilliseconds) => new(
        "curia/domain-primitives/ulid/timestamp-out-of-range",
        "A ULID's timestamp component must fit in 48 unsigned bits (0 to 2^48-1 milliseconds since the Unix epoch)",
        $"timestamp_ms={timestampMilliseconds.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// The ULID spec's monotonicity guidance: within one millisecond, <see cref="UlidGenerator"/>
    /// increments its 80-bit randomness component by 1 per call rather than drawing fresh
    /// randomness, so that sort order still matches call order. This is the (astronomically
    /// unlikely -- 1 in 2^80) failure of that scheme: every value in the 80-bit space has already
    /// been issued within the current millisecond, and incrementing further would either wrap
    /// around (silently breaking the sort guarantee) or require waiting for the clock to advance.
    /// </summary>
    public static Error UlidRandomnessExhausted(long timestampMilliseconds) => new(
        "curia/domain-primitives/ulid/randomness-exhausted",
        "Every 80-bit randomness value within this millisecond has already been issued by this generator",
        $"timestamp_ms={timestampMilliseconds.ToString(CultureInfo.InvariantCulture)}");
}
