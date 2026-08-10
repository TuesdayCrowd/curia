namespace Curia.Domain.Primitives;

/// <summary>SHA-256 over the canonical envelope bytes (R6.4). Not the transparency-log leaf digest.</summary>
public readonly record struct EnvelopeDigest(ReadOnlyMemory<byte> Sha256)
{
    public string ToHex() => Convert.ToHexStringLower(Sha256.Span);
    public string ToPrefixed() => "sha256:" + ToHex();
}
