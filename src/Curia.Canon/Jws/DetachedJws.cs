using System.Buffers.Text;
using System.Collections.Immutable;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Domain.Primitives;

namespace Curia.Canon.Jws;

/// <summary>
/// Detached JWS per RFC 7515 Appendix F with the RFC 7797 unencoded-payload option.
/// The signing input is ASCII(BASE64URL(header)) ‖ "." ‖ canonical bytes; the payload
/// segment of the compact serialization is always empty (the bytes are never carried
/// in the token — the verifier supplies them from the envelope it already admitted).
///
/// The signer/verifier dictionaries passed to the constructor are an explicit
/// allow-list: an <c>alg</c> not present as a key is rejected before any adapter is
/// invoked. This is deliberate — a verifier that looked up whatever <c>alg</c> the
/// token's own header requested would let an attacker choose "none" or an HMAC scheme
/// keyed with the victim's public key (R4.15 forbids HS* outright). Every header check
/// (<c>typ</c>, <c>b64</c>, <c>crit</c>, <c>alg</c>) happens before any cryptographic
/// operation runs.
/// </summary>
public sealed class DetachedJws
{
    /// <summary>The <c>typ</c> of a signed post envelope (R6.37).</summary>
    public const string ExpectedTyp = "curia-post+jws";

    /// <summary>
    /// The <c>typ</c> of a signed tree head (R6.49). A head is a different statement from a post
    /// -- "this root covers these entries", not "I wrote these bytes" -- and the same key must not
    /// be able to make one look like the other. The header names which it is, and a verifier
    /// built for one refuses the other before any cryptography runs.
    /// </summary>
    public const string HeadTyp = "curia-head+jws";

    private static readonly ImmutableArray<string> RequiredCrit = ["b64"];
    private static readonly string[] CritHeaderValue = ["b64"];

    private readonly IReadOnlyDictionary<string, IContentSigner> _signers;
    private readonly IReadOnlyDictionary<string, IContentVerifier> _verifiers;
    private readonly string _typ;

    /// <param name="signersByAlg">The signing adapters, keyed by <c>alg</c>; the allow-list for signing.</param>
    /// <param name="verifiersByAlg">The verifying adapters, keyed by <c>alg</c>; the allow-list for verification.</param>
    /// <param name="typ">
    /// The one <c>typ</c> this instance signs with and accepts: <see cref="ExpectedTyp"/> for
    /// posts, <see cref="HeadTyp"/> for tree heads. One instance, one statement kind.
    /// </param>
    public DetachedJws(
        IReadOnlyDictionary<string, IContentSigner> signersByAlg,
        IReadOnlyDictionary<string, IContentVerifier> verifiersByAlg,
        string typ = ExpectedTyp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typ);
        _signers = signersByAlg;
        _verifiers = verifiersByAlg;
        _typ = typ;
    }

    public Result<JwsSignature> Sign(CanonicalBytes canonical, SigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return _signers.TryGetValue(key.Alg, out var signer)
            ? Sign(canonical, new KeyBearingSigner(signer, key))
            : Result<JwsSignature>.Fail(JwsErrors.AlgNotAllowed(key.Alg));
    }

    /// <summary>
    /// R11.20's signing path: the same JWS, produced by something that holds the key instead of by
    /// the caller holding it.
    ///
    /// <para><b>One path, not two.</b> The <see cref="SigningKey"/> overload above delegates here
    /// through a private adapter, so the header composition and the signing input are computed in
    /// exactly one place. Two independent renderings of a protected header is the class of defect
    /// §6 exists to prevent: they agree on every document anybody tests, and disagree on the one an
    /// attacker constructs.</para>
    ///
    /// <para><b>The allow-list still applies, and that is not incidental.</b>
    /// <c>signersByAlg</c> is the only sign-side algorithm check in the system, and it stays a check
    /// here even though no adapter from it is invoked — a handle carrying its own <c>Alg</c> makes
    /// the lookup look like dead code, and it is not. Five call sites derive their meaning from
    /// passing an <b>empty</b> signer dictionary, most importantly <c>IngestPipeline</c>: that is
    /// why the Forum is structurally unable to sign a post envelope. Accepting a handle without
    /// consulting the list would hand the Forum a signing path it has never had.</para>
    /// </summary>
    public Result<JwsSignature> Sign(CanonicalBytes canonical, IAgentSigner signer)
    {
        ArgumentNullException.ThrowIfNull(signer);

        if (!_signers.ContainsKey(signer.Alg))
            return Result<JwsSignature>.Fail(JwsErrors.AlgNotAllowed(signer.Alg));

        var headerJson = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = signer.Alg,
            ["kid"] = signer.Kid,
            ["typ"] = _typ,
            ["b64"] = false,
            ["crit"] = CritHeaderValue,
        });

        var header = Base64Url.EncodeToString(headerJson);
        var input = SigningInput(header, canonical.Span);

        byte[] signature;
        try
        {
            signature = signer.Sign(input);
        }
        catch (CryptographicException ex)
        {
            // A delegated signer is another process or a keystore, and either can refuse. CS-10
            // makes a failed signature a value here rather than an exception, and the Verify path
            // already closed this crash class for algorithm confusion.
            return Result<JwsSignature>.Fail(JwsErrors.SignerRefused(signer.Kid, ex.Message));
        }
        catch (IOException ex)
        {
            return Result<JwsSignature>.Fail(JwsErrors.SignerRefused(signer.Kid, ex.Message));
        }

        return Result<JwsSignature>.Ok(new JwsSignature($"{header}..{Base64Url.EncodeToString(signature)}"));
    }

    /// <summary>
    /// The in-process adapter behind the <see cref="SigningKey"/> overload: it holds the key it was
    /// handed and calls the algorithm adapter. Private, because it is the one implementation of
    /// <see cref="IAgentSigner"/> that <i>does</i> hold key material, and nothing outside should be
    /// able to reach for it by accident when R11.20 is the point.
    /// </summary>
    private sealed class KeyBearingSigner(IContentSigner signer, SigningKey key) : IAgentSigner
    {
        public string Alg => key.Alg;

        public string Kid => key.Kid;

        public ReadOnlyMemory<byte> PublicKey => ReadOnlyMemory<byte>.Empty;

        public byte[] Sign(ReadOnlySpan<byte> signingInput) => signer.Sign(signingInput, key);
    }

    public Result<VerifiedContent> Verify(CanonicalBytes canonical, JwsSignature sig, PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(sig);
        ArgumentNullException.ThrowIfNull(key);

        if (!TrySplit(sig.Compact, out var parts, out var splitError))
            return Result<VerifiedContent>.Fail(splitError!);

        if (!ParseHeader(parts[0]).TryGetValue(out var header, out var headerError))
            return Result<VerifiedContent>.Fail(headerError!);

        // Reject before verifying: every header check runs before any adapter is touched.
        if (header.Typ != _typ) return Result<VerifiedContent>.Fail(JwsErrors.TypMismatch(header.Typ));
        if (header.B64) return Result<VerifiedContent>.Fail(JwsErrors.B64MustBeFalse());
        if (!header.Crit.SequenceEqual(RequiredCrit))
            return Result<VerifiedContent>.Fail(JwsErrors.CritUnsupported());
        if (!_verifiers.TryGetValue(header.Alg, out var verifier))
            return Result<VerifiedContent>.Fail(JwsErrors.AlgNotAllowed(header.Alg));

        // Algorithm confusion, refused before any adapter sees a byte. The header is the attacker's
        // and the key is the key set's, so without this the attacker chooses which primitive
        // interprets somebody else's key material -- an ES256 header over an OKP key hands 32 raw
        // bytes to ImportSubjectPublicKeyInfo, and an EdDSA header over an EC key hands an SPKI
        // blob to Ed25519. Both THROW (`ASN1 corrupted data`, `The key BLOB is not in the correct
        // format`), out through a method whose whole contract is that a bad signature is a value
        // rather than an exception (CS-10). A verifier that crashes on a chosen input is a verifier
        // an untrusted Forum can turn off.
        if (!string.Equals(header.Alg, key.Alg, StringComparison.Ordinal))
            return Result<VerifiedContent>.Fail(JwsErrors.AlgMismatch(header.Alg, key.Alg));

        if (parts[1].Length != 0)
            return Result<VerifiedContent>.Fail(JwsErrors.Malformed("detached JWS must have an empty payload segment"));

        if (!Base64Url.IsValid(parts[2]))
            return Result<VerifiedContent>.Fail(JwsErrors.Malformed("signature is not base64url"));

        var signatureBytes = Base64Url.DecodeFromChars(parts[2]);
        var input = SigningInput(parts[0], canonical.Span);

        return verifier.Verify(input, signatureBytes, key)
            ? Result<VerifiedContent>.Ok(new VerifiedContent(canonical, header))
            : Result<VerifiedContent>.Fail(JwsErrors.SignatureInvalid());
    }

    /// <summary>
    /// Decodes and structurally validates the protected header without trusting any of
    /// it: a forged header (wrong shape, wrong JSON kind, non-string <c>crit</c> entries)
    /// must produce a <see cref="Result{T}.Fail"/>, never an unhandled exception — the
    /// header is attacker-controlled input at this point, indistinguishable from a real
    /// one until every check below has run.
    /// </summary>
    public static Result<JwsProtectedHeader> ReadProtectedHeader(JwsSignature sig)
    {
        ArgumentNullException.ThrowIfNull(sig);

        return TrySplit(sig.Compact, out var parts, out var splitError)
            ? ParseHeader(parts[0])
            : Result<JwsProtectedHeader>.Fail(splitError!);
    }

    /// <summary>
    /// Splits the compact serialization into its three segments, or fails. Shared by
    /// <see cref="Verify"/> and <see cref="ReadProtectedHeader"/> so the wire string is
    /// parsed once per call rather than twice. <paramref name="compact"/> is
    /// attacker-supplied (<see cref="JwsSignature"/> is built directly from wire content
    /// with no construction-time validation), so <c>null</c> is reachable input here, not
    /// a caller bug — it must fail this check rather than reach <c>.Split('.')</c>.
    /// </summary>
    private static bool TrySplit(string? compact, out string[] parts, out Error? error)
    {
        if (string.IsNullOrEmpty(compact))
        {
            parts = [];
            error = JwsErrors.Malformed("compact serialization is null or empty");
            return false;
        }

        var split = compact.Split('.');
        if (split.Length != 3)
        {
            parts = [];
            error = JwsErrors.Malformed("expected three dot-separated segments");
            return false;
        }

        parts = split;
        error = null;
        return true;
    }

    /// <summary>Decodes and structurally validates one already-split header segment.</summary>
    private static Result<JwsProtectedHeader> ParseHeader(string headerSegment)
    {
        if (!Base64Url.IsValid(headerSegment))
            return Result<JwsProtectedHeader>.Fail(JwsErrors.Malformed("protected header is not base64url"));

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(Base64Url.DecodeFromChars(headerSegment));
        }
        catch (JsonException)
        {
            return Result<JwsProtectedHeader>.Fail(JwsErrors.Malformed("protected header is not valid JSON"));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Result<JwsProtectedHeader>.Fail(JwsErrors.Malformed("protected header must be a JSON object"));

            return Result<JwsProtectedHeader>.Ok(new JwsProtectedHeader(
                Alg: ReadString(root, "alg"),
                Kid: ReadString(root, "kid"),
                Typ: ReadString(root, "typ"),
                B64: ReadB64(root),
                Crit: ReadCrit(root)));
        }
    }

    /// <summary>Missing or wrong-kind reads as empty rather than throwing — see the type remarks.</summary>
    private static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    /// <summary>RFC 7797 defaults <c>b64</c> to true when absent; only an explicit JSON <c>false</c> turns it off.</summary>
    private static bool ReadB64(JsonElement obj) =>
        !obj.TryGetProperty("b64", out var v) || v.ValueKind != JsonValueKind.False;

    /// <summary>
    /// A non-array or non-string entry can never equal <see cref="RequiredCrit"/>, so it
    /// is mapped to a sentinel that reliably fails that comparison rather than throwing.
    /// </summary>
    private static ImmutableArray<string> ReadCrit(JsonElement obj) =>
        obj.TryGetProperty("crit", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : "\0non-string").ToImmutableArray()
            : [];

    private static byte[] SigningInput(string encodedHeader, ReadOnlySpan<byte> canonical)
    {
        var prefix = Encoding.ASCII.GetBytes(encodedHeader + ".");
        var input = new byte[prefix.Length + canonical.Length];
        prefix.CopyTo(input, 0);
        canonical.CopyTo(input.AsSpan(prefix.Length));
        return input;
    }
}

internal static class JwsErrors
{
    public static Error AlgNotAllowed(string alg) => new("curia/jws/alg-not-allowed", "Algorithm not in the allow-list", alg);
    public static Error TypMismatch(string typ) => new("curia/jws/typ-mismatch", "Unexpected typ header", typ);

    /// <summary>
    /// A signer declined or could not be reached. Its own slug because a delegated signer is another
    /// process or a keystore: "the key would not sign this" is an operational fact about the signer,
    /// not a statement about the content or the algorithm.
    /// </summary>
    public static Error SignerRefused(string kid, string detail) => new(
        "curia/jws/signer-refused",
        "The signer did not produce a signature",
        $"kid={kid}: {detail}");

    /// <summary>
    /// The header names one algorithm and the key another. Its own slug rather than
    /// <see cref="AlgNotAllowed"/>'s, because the two say different things to whoever reads the
    /// output: that algorithm is not one this verifier accepts at all, versus that algorithm is
    /// accepted and is not this key's.
    /// </summary>
    public static Error AlgMismatch(string headerAlg, string keyAlg) => new(
        "curia/jws/alg-mismatch",
        "The signature's alg is not the key's alg",
        $"header={headerAlg} key={keyAlg}");
    public static Error B64MustBeFalse() => new("curia/jws/b64-must-be-false", "RFC 7797 requires b64:false here");
    public static Error CritUnsupported() => new("curia/jws/crit-unsupported", "crit must be exactly [\"b64\"]");
    public static Error SignatureInvalid() => new("curia/jws/signature-invalid", "Signature does not verify");
    public static Error Malformed(string detail) => new("curia/jws/malformed", "Malformed JWS", detail);
}
