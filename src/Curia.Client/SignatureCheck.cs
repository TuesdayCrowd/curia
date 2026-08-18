using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>What this client established about a served post's authorship, and how.</summary>
/// <param name="Verified">Whether the detached JWS verifies over bytes this client recanonicalized itself.</param>
/// <param name="Kid">The key identifier the signature's protected header names.</param>
/// <param name="Detail">The failing predicate when <paramref name="Verified"/> is false; a note otherwise.</param>
/// <param name="Digest">
/// SHA-256 over the bytes this client recanonicalized, hex. Computed here rather than taken from
/// the response, for the same reason the signature is: a digest supplied alongside the content it
/// digests establishes nothing.
/// </param>
public sealed record SignatureVerdict(bool Verified, string? Kid, string Detail, string? Digest = null)
{
    public string Describe => Verified
        ? $"verified locally against kid={Kid} ({Detail})"
        : $"NOT VERIFIED: {Detail}";
}

/// <summary>
/// Reader Contract clause 8, implemented rather than acknowledged: "A consuming agent SHOULD
/// verify signatures."
///
/// <para><b>Recanonicalizes rather than trusting the served bytes.</b> The Forum's response
/// carries <c>canonical</c> -- the bytes it says the signature was verified over. This checks the
/// signature against bytes it derived itself, by parsing that document and running JCS+NFC over
/// the parsed form, and separately reports whether the served bytes were already canonical. Two
/// distinct claims: "this document is authentic" and "the Forum served it in canonical form". A
/// verifier that signed off on the supplied bytes could only ever confirm that the Forum agrees
/// with itself.</para>
///
/// <para><b>Key validity is evaluated at the post's <c>server_ts</c>, not now</b> (R6.31, errata
/// A12). A key retired last week is still the right key for a post received last month, which is
/// why the Forum's JWKS includes expired and revoked keys with their windows. A client that
/// checked validity against the wall clock would report most of the archive as unverifiable.</para>
/// </summary>
public static class SignatureCheck
{
    public static SignatureVerdict Verify(ProvenancePost post, ImmutableArray<ForumJwk> jwks)
    {
        ArgumentNullException.ThrowIfNull(post);

        var signature = new JwsSignature(post.Signature);

        if (!DetachedJws.ReadProtectedHeader(signature).TryGetValue(out var header, out var headerError))
            return new SignatureVerdict(false, null, Describe(headerError!));

        var served = Encoding.UTF8.GetBytes(post.Canonical);

        if (!JsonReader.Parse(served, AdmitLimits.Default).TryGetValue(out var tree, out var admitError))
            return new SignatureVerdict(false, header!.Kid, Describe(admitError!));

        if (!CanonicalJson.CanonicalizeWithNfc(tree!).TryGetValue(out var canonical, out var canonError))
            return new SignatureVerdict(false, header!.Kid, Describe(canonError!));

        var byteIdentical = canonical.Span.SequenceEqual(served);
        var digest = Digests.Sha256(canonical).ToHex();

        var key = SelectKey(jwks, header!.Kid, post.ServerTs);
        if (!key.TryGetValue(out var material, out var keyError))
            return new SignatureVerdict(false, header.Kid, Describe(keyError!), digest);

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal),
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal)
            {
                ["ES256"] = new Es256Adapter(),
                ["EdDSA"] = new Ed25519Adapter(),
            });

        if (!jws.Verify(canonical, signature, material!).TryGetValue(out _, out var verifyError))
            return new SignatureVerdict(false, header.Kid, Describe(verifyError!), digest);

        return new SignatureVerdict(
            true,
            header.Kid,
            byteIdentical
                ? "recanonicalized bytes are byte-identical to the served canonical form"
                : "WARNING: the served canonical form is not canonical; the signature verifies over "
                  + "the recanonicalized bytes, which is not what the Forum sent",
            digest);
    }

    /// <summary>
    /// A failure as one line: slug, prose, and the detail. The detail is the half that names
    /// <i>which</i> key and <i>which</i> instant, so dropping it turns "kid=alice-2 was no longer
    /// valid at server_ts 2026-07-01" into "the signature does not verify" -- true, and useless.
    /// </summary>
    private static string Describe(Error error) =>
        error.Detail is { Length: > 0 } detail
            ? $"{error.Type}: {error.Title} ({detail})"
            : $"{error.Type}: {error.Title}";

    /// <summary>
    /// The key whose <c>kid</c> the signature names, if it was valid at the post's
    /// <c>server_ts</c>.
    /// </summary>
    private static Result<PublicKeyMaterial> SelectKey(
        ImmutableArray<ForumJwk> jwks, string kid, string serverTs)
    {
        var match = jwks.FirstOrDefault(k => string.Equals(k.Kid, kid, StringComparison.Ordinal));
        if (match is null)
            return Result<PublicKeyMaterial>.Fail(ClientErrors.NoKeyForPost($"kid={kid}"));

        if (DateTimeOffset.TryParse(
                serverTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            && ValidityFailure(match, at) is { } failure)
            return Result<PublicKeyMaterial>.Fail(ClientErrors.SignatureUnverified(failure));

        return Decode(match);
    }

    private static string? ValidityFailure(ForumJwk key, DateTimeOffset at)
    {
        if (Parse(key.NotBefore) is { } from && at < from)
            return $"kid={key.Kid} was not yet valid at server_ts {at:o}";

        if (Parse(key.NotAfter) is { } until && at >= until)
            return $"kid={key.Kid} was no longer valid at server_ts {at:o}";

        return null;

        static DateTimeOffset? Parse(string? text) =>
            text is not null
            && DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
                ? value
                : null;
    }

    /// <summary>
    /// JWK to key material. Errata D4's corrected mapping: <c>EC</c>/<c>x</c>+<c>y</c> for ES256
    /// (RFC 7518 §6.2.1), <c>OKP</c>/<c>x</c> for Ed25519 (RFC 8037 §2). Reusing the <c>EC</c>
    /// shape for an Ed25519 key produces JSON that parses and then verifies nothing.
    /// </summary>
    private static Result<PublicKeyMaterial> Decode(ForumJwk jwk)
    {
        try
        {
            switch (jwk.Alg)
            {
                case "ES256" when jwk.Y is { } y:
                {
                    var parameters = new ECParameters
                    {
                        Curve = ECCurve.NamedCurves.nistP256,
                        Q = new ECPoint
                        {
                            X = System.Buffers.Text.Base64Url.DecodeFromChars(jwk.X),
                            Y = System.Buffers.Text.Base64Url.DecodeFromChars(y),
                        },
                    };

                    using var ecdsa = ECDsa.Create(parameters);
                    return Result<PublicKeyMaterial>.Ok(
                        new PublicKeyMaterial("ES256", jwk.Kid, ecdsa.ExportSubjectPublicKeyInfo()));
                }

                case "EdDSA":
                    return Result<PublicKeyMaterial>.Ok(new PublicKeyMaterial(
                        "EdDSA", jwk.Kid, System.Buffers.Text.Base64Url.DecodeFromChars(jwk.X)));

                default:
                    return Result<PublicKeyMaterial>.Fail(
                        ClientErrors.NoKeyForPost($"kid={jwk.Kid} alg={jwk.Alg} is not usable"));
            }
        }
        catch (FormatException ex)
        {
            return Result<PublicKeyMaterial>.Fail(
                ClientErrors.NoKeyForPost($"kid={jwk.Kid}: {ex.Message}"));
        }
        catch (CryptographicException ex)
        {
            return Result<PublicKeyMaterial>.Fail(
                ClientErrors.NoKeyForPost($"kid={jwk.Kid}: {ex.Message}"));
        }
    }
}
