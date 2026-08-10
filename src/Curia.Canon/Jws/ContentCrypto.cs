namespace Curia.Canon.Jws;

/// <summary>The R11.2 seam: the domain decides what must be true, the adapter performs the operation.</summary>
public interface IContentSigner
{
    byte[] Sign(ReadOnlySpan<byte> input, SigningKey key);
}

public interface IContentVerifier
{
    bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key);
}

public sealed record SigningKey(string Alg, string Kid, ReadOnlyMemory<byte> Private);
public sealed record PublicKeyMaterial(string Alg, string Kid, ReadOnlyMemory<byte> Public);
