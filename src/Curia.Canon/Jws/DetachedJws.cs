using System.Buffers.Text;
using System.Collections.Immutable;
using System.Text;
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
    public const string ExpectedTyp = "curia-post+jws";
    private static readonly ImmutableArray<string> RequiredCrit = ["b64"];
    private static readonly string[] CritHeaderValue = ["b64"];

    private readonly IReadOnlyDictionary<string, IContentSigner> _signers;
    private readonly IReadOnlyDictionary<string, IContentVerifier> _verifiers;

    public DetachedJws(
        IReadOnlyDictionary<string, IContentSigner> signersByAlg,
        IReadOnlyDictionary<string, IContentVerifier> verifiersByAlg)
    {
        _signers = signersByAlg;
        _verifiers = verifiersByAlg;
    }

    public Result<JwsSignature> Sign(CanonicalBytes canonical, SigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (!_signers.TryGetValue(key.Alg, out var signer))
            return Result<JwsSignature>.Fail(JwsErrors.AlgNotAllowed(key.Alg));

        var headerJson = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["alg"] = key.Alg,
            ["kid"] = key.Kid,
            ["typ"] = ExpectedTyp,
            ["b64"] = false,
            ["crit"] = CritHeaderValue,
        });

        var header = Base64Url.EncodeToString(headerJson);
        var input = SigningInput(header, canonical.Span);
        var signature = signer.Sign(input, key);
        return Result<JwsSignature>.Ok(new JwsSignature($"{header}..{Base64Url.EncodeToString(signature)}"));
    }

    public Result<VerifiedContent> Verify(CanonicalBytes canonical, JwsSignature sig, PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(sig);

        if (!TrySplit(sig.Compact, out var parts, out var splitError))
            return Result<VerifiedContent>.Fail(splitError!);

        if (!ParseHeader(parts[0]).TryGetValue(out var header, out var headerError))
            return Result<VerifiedContent>.Fail(headerError!);

        // Reject before verifying: every header check runs before any adapter is touched.
        if (header.Typ != ExpectedTyp) return Result<VerifiedContent>.Fail(JwsErrors.TypMismatch(header.Typ));
        if (header.B64) return Result<VerifiedContent>.Fail(JwsErrors.B64MustBeFalse());
        if (!header.Crit.SequenceEqual(RequiredCrit))
            return Result<VerifiedContent>.Fail(JwsErrors.CritUnsupported());
        if (!_verifiers.TryGetValue(header.Alg, out var verifier))
            return Result<VerifiedContent>.Fail(JwsErrors.AlgNotAllowed(header.Alg));

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
    public static Error B64MustBeFalse() => new("curia/jws/b64-must-be-false", "RFC 7797 requires b64:false here");
    public static Error CritUnsupported() => new("curia/jws/crit-unsupported", "crit must be exactly [\"b64\"]");
    public static Error SignatureInvalid() => new("curia/jws/signature-invalid", "Signature does not verify");
    public static Error Malformed(string detail) => new("curia/jws/malformed", "Malformed JWS", detail);
}
