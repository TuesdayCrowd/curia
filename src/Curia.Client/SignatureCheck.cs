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
/// <param name="PrefixedDigest">
/// The same digest in the form the wire uses -- <see cref="EnvelopeDigest.ToPrefixed"/>'s
/// <c>sha256:</c> and 64 lowercase hex digits.
///
/// <para>Both forms are here because both are real and they are not interchangeable. The bare hex
/// is what this client's own output prints; the prefixed form is what <c>refs</c>, <c>prev</c>, a
/// vote's <c>target</c>, R9.10's batch and the <c>digest</c> member all key on. Comparing the two
/// spellings against each other is a comparison that can never come out equal, and doing exactly
/// that is the defect this member was added to close.</para>
/// </param>
/// <param name="CouldNotCheck">
/// Whether the check did not run — because the material it needs could not be reached, or because
/// the served document does not carry what R6.31 evaluates against — rather than running and
/// failing. R6.52 forbids collapsing the two: "the key set was unreachable" and "this signature is
/// forged" are a network fault and an attack, and a client that reports them alike reports an
/// attack whenever a host is down.
///
/// <para>Two things set it. <see cref="SignatureCheck.Unreachable"/>, for a key set that would not
/// fetch — a state that arises where the <i>fetch</i> is, which is why it was missed for as long as
/// it was. And <see cref="SignatureCheck.Verify"/> itself, for a key that declares a validity
/// window against a post whose <c>server_ts</c> will not parse: R6.31 evaluates validity <i>at</i>
/// that instant, so without one there is nothing to evaluate it at.</para>
/// </param>
public sealed record SignatureVerdict(
    bool Verified,
    string? Kid,
    string Detail,
    string? Digest = null,
    string? PrefixedDigest = null,
    bool CouldNotCheck = false)
{
    /// <summary>R6.52's three outcomes for this check.</summary>
    public CheckOutcome Outcome =>
        Verified ? CheckOutcome.Verified
        : CouldNotCheck ? CheckOutcome.CouldNotCheck
        : CheckOutcome.Failed;

    public string Describe => Outcome switch
    {
        CheckOutcome.Verified => $"verified locally against kid={Kid} ({Detail})",
        CheckOutcome.CouldNotCheck => $"COULD NOT BE CHECKED: {Detail}",
        CheckOutcome.Failed => $"NOT VERIFIED: {Detail}",
        _ => $"NOT VERIFIED: {Detail}",
    };
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
    /// <summary>
    /// The verdict for a post whose author's key set could not be fetched: R6.52's third outcome,
    /// at the one place a fetch happens.
    ///
    /// <para>Before this existed, both callers passed an empty key array instead, and
    /// <see cref="SelectKey"/> reported <c>curia/client/no-key-for-post</c> -- "The author's JWKS
    /// carries no key matching the post's kid" -- for an unreachable host. The reader was told the
    /// author had no such key, which is a statement about the author, on the evidence of a network
    /// fault.</para>
    /// </summary>
    public static SignatureVerdict Unreachable(ProvenancePost post, Refusal refusal)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(refusal);

        // Canonicalized anyway: the digest is a fact about the served document and does not depend
        // on any key, so it stays reportable when the keys do not arrive.
        var unkeyed = Verify(post, []);

        return new SignatureVerdict(
            false,
            unkeyed.Kid,
            $"the author's key set could not be fetched ({refusal.Error.Type}): {refusal.Summary}. "
            + "This is a fault reaching the keys, not a statement about the signature.",
            unkeyed.Digest,
            unkeyed.PrefixedDigest,
            CouldNotCheck: true);
    }

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
        var envelopeDigest = Digests.Sha256(canonical);
        var digest = envelopeDigest.ToHex();
        var prefixed = envelopeDigest.ToPrefixed();

        var key = SelectKey(jwks, header!.Kid, post.ServerTs);
        if (!key.TryGetValue(out var material, out var keyError))
        {
            // A window this client could not evaluate is R6.52's third outcome, not a refutation:
            // the signature may be perfectly good and this client cannot say so.
            return new SignatureVerdict(
                false,
                header.Kid,
                Describe(keyError!),
                digest,
                prefixed,
                CouldNotCheck: keyError!.Type == "curia/client/validity-not-evaluable");
        }

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal),
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal)
            {
                ["ES256"] = new Es256Adapter(),
                ["EdDSA"] = new Ed25519Adapter(),
            });

        if (!jws.Verify(canonical, signature, material!).TryGetValue(out _, out var verifyError))
            return new SignatureVerdict(false, header.Kid, Describe(verifyError!), digest, prefixed);

        return new SignatureVerdict(
            true,
            header.Kid,
            byteIdentical
                ? "recanonicalized bytes are byte-identical to the served canonical form"
                : "WARNING: the served canonical form is not canonical; the signature verifies over "
                  + "the recanonicalized bytes, which is not what the Forum sent",
            digest,
            prefixed);
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
                serverTs, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
        {
            return ValidityFailure(match, at) is { } failure
                ? Result<PublicKeyMaterial>.Fail(ClientErrors.SignatureUnverified(failure))
                : Material(match);
        }

        // The timestamp did not parse. Whether that matters depends entirely on whether there is a
        // window to evaluate: a key with neither bound has nothing R6.31 could check, so the
        // unusable instant is irrelevant and the signature check proceeds. A key that DOES declare
        // one cannot be evaluated at all, and this used to fall through to Material(match) --
        // accepting a key retired in 2021 because the Forum served a server_ts that would not
        // parse. The Forum chooses server_ts, so that was a check the Forum could switch off.
        return HasValidityWindow(match)
            ? Result<PublicKeyMaterial>.Fail(ClientErrors.ValidityNotEvaluable(
                $"kid={match.Kid} is valid from {match.NotBefore ?? "(unbounded)"} to "
                + $"{match.NotAfter ?? "(unbounded)"}, and the post's server_ts is "
                + (string.IsNullOrEmpty(serverTs) ? "absent" : $"'{serverTs}', which is not a timestamp")))
            : Material(match);
    }

    /// <summary>Whether this key declares any bound at all — the only case where an unusable <c>server_ts</c> matters.</summary>
    private static bool HasValidityWindow(ForumJwk key) =>
        !string.IsNullOrEmpty(key.NotBefore) || !string.IsNullOrEmpty(key.NotAfter);

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
    ///
    /// <para>Internal rather than private because the Acta's own key set (R6.50) carries the same
    /// key material and <see cref="ActaCheck"/> must decode it to verify a signed head. One decoder
    /// for both, because errata D4 records what a second one costs.</para>
    /// </summary>
    internal static Result<PublicKeyMaterial> Material(ForumJwk jwk)
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
