namespace Curia.Domain.Primitives;

/// <summary>SHA-256 over the canonical envelope bytes (R6.4). Not the transparency-log leaf digest.</summary>
/// <remarks>
/// <see cref="ReadOnlyMemory{T}"/>'s compiler-generated equality compares the underlying array
/// reference, offset, and length, not the bytes themselves — two digests over separately
/// allocated arrays with identical content would otherwise compare unequal, silently breaking
/// deduplication and revision-chain matching. <see cref="Equals(EnvelopeDigest)"/> and
/// <see cref="GetHashCode"/> are overridden below so equality (and the record struct's
/// generated <c>==</c>/<c>!=</c>, which route through the typed <c>Equals</c>) is content-based.
/// </remarks>
public readonly record struct EnvelopeDigest(ReadOnlyMemory<byte> Sha256)
{
    public bool Equals(EnvelopeDigest other) => Sha256.Span.SequenceEqual(other.Sha256.Span);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(Sha256.Span);
        return hash.ToHashCode();
    }

    public string ToHex() => Convert.ToHexStringLower(Sha256.Span);
    public string ToPrefixed() => "sha256:" + ToHex();
}
