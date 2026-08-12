using System.Diagnostics.CodeAnalysis;

namespace Curia.Canon.Canonical;

/// <summary>
/// Bytes produced by <see cref="CanonicalJson.Canonicalize"/> and by nothing else.
/// The constructor is internal so that no caller can present wire octets to
/// signing or verification — R6.10 as a compile-time fact rather than a convention.
/// </summary>
[SuppressMessage(
    "Usage",
    "CA1815:Override equals and operator equals on value types",
    Justification = "CanonicalBytes is an opaque carrier consumed via Span/ToArray for signing and " +
        "verification, not a value compared for equality (mirrors Result<T>'s CA1815 justification); " +
        "adding equality members would grow the API beyond what the interface spec requires.")]
public readonly struct CanonicalBytes
{
    private readonly byte[] _bytes;

    internal CanonicalBytes(byte[] bytes) => _bytes = bytes;

    public ReadOnlySpan<byte> Span => _bytes;
    public int Length => _bytes.Length;
    public byte[] ToArray() => (byte[])_bytes.Clone();
}
