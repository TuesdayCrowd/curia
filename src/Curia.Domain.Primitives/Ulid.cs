namespace Curia.Domain.Primitives;

/// <summary>
/// A ULID (Universally Unique Lexicographically Sortable Identifier): a 128-bit value formed
/// from a 48-bit big-endian millisecond Unix timestamp followed by 80 bits of randomness,
/// canonically rendered as 26 characters of Crockford's Base32 (spec: https://github.com/ulid/spec).
///
/// <para>
/// <b>Supersedes <c>curia-csharp-scoping.md</c> §9's package table</b>, which planned the
/// third-party <c>Ulid</c> NuGet package (tagged R8.3) as a dependency of <c>Curia.Domain</c>.
/// R11.1 forbids <c>Curia.Domain</c> from depending on anything outside the BCL, and
/// <c>tests/Curia.Architecture.Tests</c> enforces that today, so the scoping document's plan as
/// written cannot build. ULID is a small, fully specified 128-bit format; implementing it here in
/// the BCL-only floor of the hexagon (<c>Curia.Domain.Primitives</c>, per T1.3) costs a few
/// hundred lines and keeps R11.1 intact rather than trading it away for a dependency. See the
/// T1.3 report for the correction to record against the scoping document.
/// </para>
///
/// <para>
/// Crockford's alphabet deliberately excludes I, L, O, and U (visual confusion with 1/1/0, and
/// accidental-obscenity avoidance), leaving 32 symbols for 5 bits each. 26 characters therefore
/// carry 130 bits of nominal encoding capacity for a 128-bit payload, so the leading character's
/// value is restricted to 0-7 (its top two of five bits are always zero) to keep the encoding a
/// bijection with the 128-bit space: any 48-bit timestamp's top three bits, which is exactly what
/// the leading character encodes, can never exceed 7 on their own, so every value a valid
/// (timestamp, randomness) pair can produce already respects this bound. <see cref="Parse"/>
/// rejects a leading-character value above 7 rather than silently discarding the excess bits
/// during decoding, so the maximum valid ULID is <c>7ZZZZZZZZZZZZZZZZZZZZZZZZZ</c> and any text
/// above it does not parse.
/// </para>
///
/// <para>
/// Lexicographic ordering of the encoded string matches numeric ordering of <see cref="Value"/>
/// matches chronological order of generation (given <see cref="UlidGenerator"/>'s monotonicity
/// within a millisecond) -- that equivalence is the entire point of choosing ULID over a random
/// UUID for the event store's ordering (R8.3).
/// </para>
/// </summary>
public readonly record struct Ulid : IComparable<Ulid>
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private const int EncodedLength = 26;
    private const int RandomnessBits = 80;
    private const int TimestampBits = 48;

    /// <summary>The largest value a 48-bit millisecond timestamp can hold (the year 10889).</summary>
    internal const long MaxTimestampMilliseconds = (1L << TimestampBits) - 1;

    /// <summary>The largest value 80 bits of randomness can hold.</summary>
    internal static readonly UInt128 MaxRandomness = (UInt128.One << RandomnessBits) - 1;

    private static readonly int[] DecodeTable = BuildDecodeTable();

    /// <summary>The raw 128-bit value: the 48-bit timestamp in the high bits, the 80-bit randomness in the low bits.</summary>
    public UInt128 Value { get; }

    private Ulid(UInt128 value) => Value = value;

    /// <summary>
    /// Assembles a ULID from an already-validated timestamp and randomness. Trusted, no
    /// re-validation -- mirrors <c>EventSequence</c>'s internal raw constructor in
    /// <c>Curia.Domain/Identifiers.cs</c>. The only caller in this assembly,
    /// <see cref="UlidGenerator"/>, upholds both bounds itself before calling this: it rejects a
    /// clock reading outside 48-bit range instead of calling this, and it never advances
    /// randomness past <see cref="MaxRandomness"/>.
    /// </summary>
    internal static Ulid FromParts(long timestampMilliseconds, UInt128 randomness) =>
        new(((UInt128)(ulong)timestampMilliseconds << RandomnessBits) | randomness);

    /// <summary>The millisecond Unix timestamp this ULID's leading 48 bits encode.</summary>
    public long TimestampMilliseconds => (long)(Value >> RandomnessBits);

    /// <summary>
    /// Parses the canonical 26-character Crockford Base32 encoding. Case-insensitive over the 32
    /// valid symbols; rejects the wrong length, any character outside the alphabet (including the
    /// excluded I/L/O/U), and any encoding above the maximum representable ULID -- a
    /// <see cref="Result{T}"/> failure in every case, never an exception (CS-10).
    /// </summary>
    public static Result<Ulid> Parse(string text)
    {
        if (string.IsNullOrEmpty(text) || text.Length != EncodedLength)
            return Result<Ulid>.Fail(DomainPrimitivesErrors.UlidWrongLength(text?.Length ?? 0));

        UInt128 value = 0;
        for (var i = 0; i < EncodedLength; i++)
        {
            var digit = DecodeChar(text[i]);
            if (digit < 0)
                return Result<Ulid>.Fail(DomainPrimitivesErrors.UlidInvalidCharacter(text[i]));

            // The leading character contributes the top 3 (of its 5 decoded) bits of the 128-bit
            // value; a value above 7 here would require more than 128 bits to represent, which is
            // exactly the "above the maximum ULID" case -- reject now, before any shift below
            // silently discards the excess (see the type-level remarks).
            if (i == 0 && digit > 7)
                return Result<Ulid>.Fail(DomainPrimitivesErrors.UlidExceedsMaxValue(text));

            value = (value << 5) | (UInt128)(uint)digit;
        }

        return Result<Ulid>.Ok(new Ulid(value));
    }

    public int CompareTo(Ulid other) => Value.CompareTo(other.Value);
    public static bool operator <(Ulid left, Ulid right) => left.CompareTo(right) < 0;
    public static bool operator >(Ulid left, Ulid right) => left.CompareTo(right) > 0;
    public static bool operator <=(Ulid left, Ulid right) => left.CompareTo(right) <= 0;
    public static bool operator >=(Ulid left, Ulid right) => left.CompareTo(right) >= 0;

    /// <summary>Renders the canonical 26-character uppercase Crockford Base32 encoding.</summary>
    public override string ToString()
    {
        Span<char> chars = stackalloc char[EncodedLength];
        var remaining = Value;
        for (var i = EncodedLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(remaining & 0x1F)];
            remaining >>= 5;
        }

        return new string(chars);
    }

    private static int DecodeChar(char c) => c < DecodeTable.Length ? DecodeTable[c] : -1;

    private static int[] BuildDecodeTable()
    {
        var table = new int[128];
        Array.Fill(table, -1);
        for (var i = 0; i < Alphabet.Length; i++)
        {
            table[Alphabet[i]] = i;
            table[char.ToLowerInvariant(Alphabet[i])] = i;
        }

        return table;
    }
}
