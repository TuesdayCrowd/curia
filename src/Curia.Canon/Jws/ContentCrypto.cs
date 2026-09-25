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

/// <summary>
/// R11.20's seam: something that can sign, without the caller ever holding what it signs with.
///
/// <para><b>Why this is not <see cref="IContentSigner"/>.</b> That port takes a
/// <see cref="SigningKey"/>, whose whole content is the private bytes — so every caller of it holds
/// the key by construction, and R11.20 ("SHALL NOT hold agent private keys where the deployment
/// allows separation") cannot be satisfied by pointing it somewhere else. This one carries no key
/// at all: it answers what it is (<see cref="Alg"/>, <see cref="Kid"/>, <see cref="PublicKey"/>)
/// and it signs. An implementation may hold a key in this process, or a keystore handle, or a pipe
/// to another process; a caller cannot tell and must not need to.</para>
///
/// <para><b>The property is structural, not a convention.</b> There is no member on this interface
/// through which private material can be obtained, so "the MCP process cannot extract the key" is a
/// fact about the type rather than an assertion about behaviour — which is what the plan means by
/// asking for it "asserted structurally, not by inspection". A test that watched a call and found
/// no key in it would pass while copies persisted elsewhere.</para>
///
/// <para><b>Prior art in this tree.</b> <c>Curia.Api</c>'s <c>IssuerSigningKey</c> is already this
/// shape — a <c>Kid</c>, a <c>Sign(ReadOnlySpan&lt;byte&gt;)</c>, and an <c>ECDsa</c> it never
/// exports. It is the shape to copy and not the code to reuse: it lives in a host project, and the
/// dependency direction CS-7 enforces forbids anything depending on it.</para>
/// </summary>
public interface IAgentSigner
{
    /// <summary>The JWS <c>alg</c> this signer produces. Goes into the protected header.</summary>
    string Alg { get; }

    /// <summary>The key identifier the Registrar holds for this agent. Goes into the protected header.</summary>
    string Kid { get; }

    /// <summary>
    /// The public half, as SubjectPublicKeyInfo.
    ///
    /// <para>Present because enrolment sends it (<c>POST /v1/agents</c>) and because a caller must
    /// be able to check that what came back verifies under the key it thinks it has. It is the
    /// public half, so exposing it costs nothing — and without it a delegated signer could not be
    /// enrolled at all, which would make the seam unusable for the case it exists for.</para>
    /// </summary>
    ReadOnlyMemory<byte> PublicKey { get; }

    /// <summary>
    /// Signs the JWS signing input, returning the raw signature bytes in the form
    /// <see cref="Alg"/> defines (IEEE P1363 fixed-field concatenation for ES256).
    /// </summary>
    byte[] Sign(ReadOnlySpan<byte> signingInput);
}

public sealed record PublicKeyMaterial(string Alg, string Kid, ReadOnlyMemory<byte> Public);
