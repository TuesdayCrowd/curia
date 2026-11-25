using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;

namespace Curia.Tests.Shared;

/// <summary>
/// A verifier of R4.46's proof written in the test project from the errata's sentence, for a double that stands for the Forum where the code under test is the <i>client</i>
/// (<c>Curia.Client.Tests</c> cannot reference <c>Curia.AuthN</c>, and a double that accepted whatever it was sent would pass a proof signed over the wrong URL, which is the defect
/// the shipped DPoP check had). It follows the seven steps in their order and answers the slug of the first that refuses, or null when the proof holds; it keeps the spent <c>jti</c>s
/// itself. Nothing here is shared with the validator.
/// </summary>
internal sealed class EnrollProofVerifier(string audience, Func<DateTimeOffset> now, Func<string, bool> nonceIsCurrent)
{
    private readonly HashSet<string> _spent = new(StringComparer.Ordinal);

    /// <summary>Marks a <c>jti</c> as already consumed, as a vector's context says.</summary>
    internal void Spend(string jti) => _spent.Add(jti);

    /// <summary>The proofs this verifier has accepted, in order.</summary>
    internal List<string> Accepted { get; } = [];

    /// <summary>The refusal's slug (<c>proof-required</c> ...), or null when the proof holds. A proof that holds spends its <c>jti</c>.</summary>
    internal string? Verify(string agentId, string alg, string kid, byte[] storedPublic, string? proof)
    {
        if (string.IsNullOrEmpty(proof)) return "proof-required";
        if (proof.Length > 8192) return "proof-malformed";
        var segments = proof.Split('.');
        if (segments.Length != 3 || segments.Any(s => s.Length == 0)) return "proof-malformed";
        var decoded = segments.Select(Strict).ToArray();
        if (decoded.Any(d => d is null)) return "proof-malformed";

        JsonElement header;
        try
        {
            header = JsonDocument.Parse(decoded[0]!).RootElement;
        }
        catch (JsonException)
        {
            return "proof-malformed";
        }

        if (header.ValueKind != JsonValueKind.Object) return "proof-malformed";
        if (!Text(header, "typ", out var typ) || typ != "curia-enroll-pop+jwt") return "proof-malformed";
        if (!Text(header, "alg", out var headerAlg) || headerAlg is not ("EdDSA" or "ES256")) return "proof-malformed";
        if (!Text(header, "kid", out var headerKid)) return "proof-malformed";
        if (headerAlg != alg || headerKid != kid) return "proof-mismatch";

        IContentVerifier verifier = alg == "EdDSA" ? new Ed25519Adapter() : new Es256Adapter();
        if (!verifier.Verify(Encoding.ASCII.GetBytes(segments[0] + "." + segments[1]), decoded[2]!, new PublicKeyMaterial(alg, kid, storedPublic))) return "proof-not-possessed";

        JsonElement claims;
        try
        {
            claims = JsonDocument.Parse(decoded[1]!).RootElement;
        }
        catch (JsonException)
        {
            return "proof-malformed";
        }

        if (claims.ValueKind != JsonValueKind.Object) return "proof-malformed";
        if (!Text(claims, "agent_id", out var claimAgent) || !Text(claims, "jkt", out var claimJkt) || !Text(claims, "aud", out var claimAud)
            || !claims.TryGetProperty("iat", out var iatElement) || iatElement.ValueKind != JsonValueKind.Number || !iatElement.TryGetInt64(out var iat)
            || !Text(claims, "jti", out var jti))
            return "proof-malformed";

        if (claimAgent != agentId) return "proof-mismatch";
        if (claimJkt != EnrollProofKit.Jkt(alg, storedPublic)) return "proof-mismatch";
        if (claimAud != audience) return "proof-mismatch";
        if (Math.Abs(now().ToUnixTimeSeconds() - iat) > 30) return "proof-window";
        if (!Text(claims, "nonce", out var nonce) || !nonceIsCurrent(nonce!)) return "proof-nonce-stale";
        if (!_spent.Add(jti!)) return "proof-replayed";

        Accepted.Add(proof);
        return null;
    }

    private static bool Text(JsonElement element, string member, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(member, out var found) || found.ValueKind != JsonValueKind.String) return false;
        value = found.GetString();
        return value is not null;
    }

    /// <summary>Unpadded base64url, alphabet and nothing else.</summary>
    private static byte[]? Strict(string segment)
    {
        if (!segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return null;
        try
        {
            return Base64Url.DecodeFromChars(segment);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
