using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Primitives;

namespace Curia.Domain;

/// <summary>
/// R4.15's closed algorithm set ("Ed25519 or ECDSA P-256. Nothing else.") rendered as CS-11's
/// closed hierarchy, in the wire shapes errata D4/R4.28 assigns each: RFC 8037 octet key pairs
/// (<c>kty: "OKP"</c>, <c>crv: "Ed25519"</c>, one coordinate <c>x</c>) for Ed25519, RFC 7518
/// <c>EC</c> keys (<c>crv: "P-256"</c>, coordinates <c>x</c> and <c>y</c>) for P-256. The two
/// variants genuinely have different shapes -- one coordinate versus two -- so there is no
/// constructor here that can produce "a P-256 key with only x" or "an Ed25519 key with y": the
/// mistake D4 warns a naive implementer will make (reusing the two-coordinate <c>EC</c> shape
/// for Ed25519) has no spelling in this type, not merely a runtime check against it.
///
/// <see cref="Match{TResult}"/> is <see langword="abstract"/> rather than a <c>switch</c> inside
/// a single concrete method: a <c>switch</c> expression over an unsealed base type is not
/// something the compiler will verify exhaustive here (confirmed empirically -- CS8509 fires
/// even with a case for every known derived record, because nothing about a <c>private
/// protected</c> constructor tells the switch-completeness checker the derived set is closed),
/// so the compiler-enforced version of CS-11's "a seventh kind breaks every call site" is the
/// abstract-method form instead: a third nested record that does not override
/// <see cref="Match{TResult}"/> fails to compile with "does not implement inherited abstract
/// member," which is the real guarantee, not the switch's absent discard arm.
///
/// A third algorithm is not a case this hierarchy is missing; RSA and every symmetric <c>HS*</c>
/// algorithm are the ones R4.15 names as explicitly out of scope for agent authentication, and
/// adding one back is deliberately a multi-file change: a new nested record here that must
/// implement <see cref="Match{TResult}"/> or fail to compile, a new arm in
/// <see cref="Match{TResult}"/>'s signature that every existing caller must also handle, and a
/// new case in <see cref="FromJwkShape"/>.
/// </summary>
public abstract record AgentPublicKey
{
    private protected AgentPublicKey(KeyId kid) => Kid = kid;

    public KeyId Kid { get; }

    /// <summary>CS-11's exhaustiveness guarantee: a third nested record that fails to override this
    /// method does not compile, and a third parameter added here breaks every existing call site.</summary>
    public abstract TResult Match<TResult>(Func<Ed25519Key, TResult> onEd25519, Func<P256Key, TResult> onP256);

    /// <summary>RFC 8037 octet key pair: <c>kty: "OKP"</c>, <c>crv: "Ed25519"</c>, a single 32-byte <c>x</c>.</summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "CS-11's closed-hierarchy idiom nests every variant inside its abstract base " +
            "(mirrors the scoping doc's own Envelope/Question/Answer example) so the hierarchy reads as " +
            "one closed set at the call site, not a scattered collection of unrelated top-level records.")]
    public sealed record Ed25519Key : AgentPublicKey
    {
        public const int PublicKeyLength = 32;

        public ReadOnlyMemory<byte> X { get; }

        internal Ed25519Key(KeyId kid, ReadOnlyMemory<byte> x) : base(kid) => X = x;

        public override TResult Match<TResult>(Func<Ed25519Key, TResult> onEd25519, Func<P256Key, TResult> onP256)
        {
            ArgumentNullException.ThrowIfNull(onEd25519);
            ArgumentNullException.ThrowIfNull(onP256);
            return onEd25519(this);
        }
    }

    /// <summary>RFC 7518 EC key: <c>kty: "EC"</c>, <c>crv: "P-256"</c>, two 32-byte coordinates <c>x</c>/<c>y</c>.</summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "See Ed25519Key's identical justification just above.")]
    public sealed record P256Key : AgentPublicKey
    {
        public const int CoordinateLength = 32;

        public ReadOnlyMemory<byte> X { get; }
        public ReadOnlyMemory<byte> Y { get; }

        internal P256Key(KeyId kid, ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y) : base(kid)
        {
            X = x;
            Y = y;
        }

        public override TResult Match<TResult>(Func<Ed25519Key, TResult> onEd25519, Func<P256Key, TResult> onP256)
        {
            ArgumentNullException.ThrowIfNull(onEd25519);
            ArgumentNullException.ThrowIfNull(onP256);
            return onP256(this);
        }
    }

    public static Result<AgentPublicKey> CreateEd25519(KeyId kid, ReadOnlyMemory<byte> x) =>
        x.Length != Ed25519Key.PublicKeyLength
            ? Result<AgentPublicKey>.Fail(KeyErrors.WrongCoordinateLength("Ed25519", "x", Ed25519Key.PublicKeyLength, x.Length))
            : Result<AgentPublicKey>.Ok(new Ed25519Key(kid, x));

    public static Result<AgentPublicKey> CreateP256(KeyId kid, ReadOnlyMemory<byte> x, ReadOnlyMemory<byte> y)
    {
        if (x.Length != P256Key.CoordinateLength)
            return Result<AgentPublicKey>.Fail(KeyErrors.WrongCoordinateLength("P-256", "x", P256Key.CoordinateLength, x.Length));
        if (y.Length != P256Key.CoordinateLength)
            return Result<AgentPublicKey>.Fail(KeyErrors.WrongCoordinateLength("P-256", "y", P256Key.CoordinateLength, y.Length));
        return Result<AgentPublicKey>.Ok(new P256Key(kid, x, y));
    }

    /// <summary>
    /// R4.28's shape classifier, and the one place an unsupported <c>kty</c>/<c>crv</c> combination
    /// is rejected at runtime rather than at compile time: given already-parsed wire fields (JSON
    /// parsing and base64url decoding are Canon's/Infrastructure's job, not this layer's -- see the
    /// Stage B report for why the JWK document itself is deliberately not modeled here), builds the
    /// one matching variant or fails. Every combination other than <c>(OKP, Ed25519)</c> and
    /// <c>(EC, P-256)</c> is rejected here, including a well-formed but wrong pairing such as
    /// <c>(OKP, X25519)</c> or <c>(EC, P-384)</c>.
    /// </summary>
    public static Result<AgentPublicKey> FromJwkShape(
        KeyId kid, string kty, string crv, ReadOnlyMemory<byte> x, ReadOnlyMemory<byte>? y) =>
        (kty, crv) switch
        {
            ("OKP", "Ed25519") => CreateEd25519(kid, x),
            ("EC", "P-256") => y is { } yValue
                ? CreateP256(kid, x, yValue)
                : Result<AgentPublicKey>.Fail(KeyErrors.MissingCoordinate("y")),
            _ => Result<AgentPublicKey>.Fail(KeyErrors.UnsupportedKeyShape(kty, crv)),
        };
}
