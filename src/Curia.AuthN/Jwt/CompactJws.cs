using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Jwt;

/// <summary>
/// The header of a standard (non-detached) compact JWS -- a JWT's first segment. Deliberately
/// not <c>Curia.Canon.Jws.JwsProtectedHeader</c>: that type carries <c>b64</c>/<c>crit</c>,
/// which describe RFC 7797's detached-payload profile (§6's content envelopes) and have no
/// meaning on an ordinary bearer JWT, whose payload segment is always present and
/// base64url-encoded, never detached.
/// </summary>
public sealed record CompactJwsHeader(string Alg, string Kid, string Typ);

/// <summary>The three raw, still-encoded segments of a compact JWS, split but not yet decoded.</summary>
public sealed record CompactJwsParts(string HeaderSegment, string PayloadSegment, string SignatureSegment);

/// <summary>
/// RFC 7515 compact serialization -- header.payload.signature, both header and payload
/// base64url JSON, signature over the ASCII bytes of the first two segments joined by ".".
/// This is Stage C's half of R5.13: the access token, the client assertion, and the DPoP proof
/// are all this same three-segment shape, split, header-checked, and signature-verified by the
/// same functions here, so a fix to one caller's parsing cannot silently miss another's.
///
/// Not <c>Curia.Canon.Jws.DetachedJws</c>: that type's signing input is
/// <c>header || "." || canonical-payload-bytes</c> with an always-empty payload segment
/// (RFC 7797's <c>b64:false</c> profile, §6's content envelopes) -- the wrong shape for a JWT,
/// whose payload segment is present and itself base64url-encoded into the signing input.
/// </summary>
public static class CompactJws
{
    /// <summary>Splits the wire string into its three segments without decoding either.
    /// Fails on anything that is not exactly three dot-separated, non-empty, base64url segments
    /// -- structurally malformed input is rejected here, before any JSON parsing or crypto runs.</summary>
    public static Result<CompactJwsParts> Split(string? compact)
    {
        if (string.IsNullOrEmpty(compact))
            return Result<CompactJwsParts>.Fail(AuthNErrors.Malformed("token is null or empty"));

        var segments = compact.Split('.');
        if (segments.Length != 3)
            return Result<CompactJwsParts>.Fail(AuthNErrors.Malformed("expected three dot-separated segments"));

        if (segments[0].Length == 0 || segments[1].Length == 0 || segments[2].Length == 0)
            return Result<CompactJwsParts>.Fail(AuthNErrors.Malformed("a compact JWS segment must not be empty"));

        if (!Base64Url.IsValid(segments[0]))
            return Result<CompactJwsParts>.Fail(AuthNErrors.Malformed("header is not base64url"));

        if (!Base64Url.IsValid(segments[1]))
            return Result<CompactJwsParts>.Fail(AuthNErrors.Malformed("payload is not base64url"));

        if (!Base64Url.IsValid(segments[2]))
            return Result<CompactJwsParts>.Fail(AuthNErrors.Malformed("signature is not base64url"));

        return Result<CompactJwsParts>.Ok(new CompactJwsParts(segments[0], segments[1], segments[2]));
    }

    /// <summary>Decodes and structurally validates the header segment as a JSON object, then hands
    /// the parsed root to <paramref name="project"/>. The <see cref="JsonDocument"/> backing the
    /// element is disposed before this method returns, so <paramref name="project"/> must extract
    /// everything it needs rather than closing over the element.</summary>
    public static Result<T> ParseHeader<T>(CompactJwsParts parts, Func<JsonElement, Result<T>> project)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(project);
        return ParseJsonObject(parts.HeaderSegment, "header", project);
    }

    /// <summary>Decodes and structurally validates the payload segment as a JSON object, then hands
    /// the parsed root to <paramref name="project"/>. Same disposal contract as
    /// <see cref="ParseHeader{T}"/>.</summary>
    public static Result<T> ParsePayload<T>(CompactJwsParts parts, Func<JsonElement, Result<T>> project)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(project);
        return ParseJsonObject(parts.PayloadSegment, "payload", project);
    }

    /// <summary>The convenience projection <see cref="ParseHeader{T}"/> uses for the three fields
    /// every compact JWS header shares. DPoP's extra <c>jwk</c> member is read separately (see
    /// <c>Curia.AuthN.Dpop.DpopProofHeaderParser</c>) -- it has no meaning on an access token or
    /// client assertion header, so it does not belong on the shared type.</summary>
    public static Result<CompactJwsHeader> DecodeHeader(CompactJwsParts parts) =>
        ParseHeader(parts, root => Result<CompactJwsHeader>.Ok(new CompactJwsHeader(
            Alg: ReadString(root, "alg"),
            Kid: ReadString(root, "kid"),
            Typ: ReadString(root, "typ"))));

    /// <summary>Verifies the signature segment over the standard JWS signing input
    /// (<c>ASCII(header-segment) || "." || ASCII(payload-segment)</c>, both still base64url-encoded
    /// -- unlike <c>DetachedJws</c>, the payload here is never canonical raw bytes).</summary>
    public static bool VerifySignature(CompactJwsParts parts, IContentVerifier verifier, PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(key);

        var input = Encoding.ASCII.GetBytes(parts.HeaderSegment + "." + parts.PayloadSegment);
        var signature = Base64Url.DecodeFromChars(parts.SignatureSegment);
        return verifier.Verify(input, signature, key);
    }

    private static Result<T> ParseJsonObject<T>(string segment, string segmentName, Func<JsonElement, Result<T>> project)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(Base64Url.DecodeFromChars(segment));
        }
        catch (JsonException)
        {
            return Result<T>.Fail(AuthNErrors.Malformed($"{segmentName} is not valid JSON"));
        }

        using (doc)
        {
            var root = doc.RootElement;
            return root.ValueKind != JsonValueKind.Object
                ? Result<T>.Fail(AuthNErrors.Malformed($"{segmentName} must be a JSON object"))
                : project(root);
        }
    }

    /// <summary>Missing or wrong-kind reads as empty rather than throwing -- mirrors
    /// <c>DetachedJws.ReadString</c>'s tolerant style, so a missing mandatory claim fails the
    /// semantic check downstream (e.g. an empty <c>iss</c> never equals the configured issuer)
    /// instead of a JSON-shape exception escaping from claim parsing.</summary>
    internal static string ReadString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}
