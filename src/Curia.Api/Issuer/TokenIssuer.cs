using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.AuthN;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Api.Issuer;

/// <summary>What the issuer minted, in the shape RFC 6749 §5.1 expects.</summary>
public sealed record IssuedToken(string AccessToken, string TokenType, int ExpiresInSeconds, string Scope);

/// <summary>
/// §5's token endpoint: <c>private_key_jwt</c> in, a short-lived DPoP-bound access token out.
///
/// <para><b>Co-hosted with the Forum for the prototype.</b> The scoping document lists
/// <c>Curia.Issuer</c> as its own host, and that remains the right deployment shape -- an issuer
/// and a resource server have different blast radii and different key custody. But the split is a
/// deployment concern, not a logic one: nothing here depends on being in a separate process, and
/// moving it later is a project file and a base URL. Co-hosting now buys a working prototype;
/// pretending the split exists when it does not would buy nothing.</para>
///
/// <para><b>Every ceiling here comes from <see cref="AuthNConstants"/>, not from a local literal.</b>
/// R5.x fixes the maximum access-token lifetime at 300 seconds and the assertion lifetime at 60;
/// restating either as a number in this file would let the two drift, and a token that outlived its
/// stated ceiling would be indistinguishable from one that did not.</para>
/// </summary>
public sealed class TokenIssuer
{
    private readonly ECDsa _signingKey;
    private readonly TimeProvider _clock;

    public TokenIssuer(string issuer, string audience, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(issuer);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);
        ArgumentNullException.ThrowIfNull(clock);

        Issuer = issuer;
        Audience = audience;
        _clock = clock;

        // A fresh key per process. Correct for a prototype and wrong for a deployment: R5.x wants
        // the issuer's signing key in custody that survives a restart, and every token minted
        // before a restart becomes unverifiable after one. Recorded here rather than left for
        // someone to discover when their tokens stop working.
        _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    }

    public string Issuer { get; }

    public string Audience { get; }

    /// <summary>The <c>kid</c> of the issuer's signing key, as it appears in minted tokens and in the JWKS.</summary>
    public string SigningKeyId { get; } = "issuer-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The issuer's own JWKS, so a resource server can verify what it minted.
    ///
    /// <para>Separate from the agent JWKS on purpose: these are two different trust statements.
    /// The agent keys answer "did this agent write this post"; this key answers "did this issuer
    /// mint this token". Serving them from one document would invite a verifier to accept an agent
    /// key for a token or the reverse.</para>
    /// </summary>
    public JsonObject Jwks()
    {
        var parameters = _signingKey.ExportParameters(includePrivateParameters: false);

        return new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["alg"] = "ES256",
                ["use"] = "sig",
                ["kid"] = SigningKeyId,
                ["x"] = Base64UrlEncode(parameters.Q.X!),
                ["y"] = Base64UrlEncode(parameters.Q.Y!),
            }),
        };
    }

    /// <summary>The public key material a resource server verifies minted tokens against.</summary>
    public PublicKeyMaterial VerificationKey =>
        new("ES256", SigningKeyId, _signingKey.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// Mints an access token bound to a DPoP key.
    /// </summary>
    /// <param name="subject">The agent the assertion authenticated. Becomes <c>sub</c>.</param>
    /// <param name="dpopThumbprint">
    /// RFC 9449's <c>cnf.jkt</c>: the JWK thumbprint of the key the client will prove possession of
    /// on every request. This is what makes the token sender-constrained -- a stolen token is
    /// useless without the private key whose thumbprint this is.
    /// </param>
    /// <param name="tier">
    /// The tier at issuance, carried for observability only. R7.7 forbids <i>relying</i> on it:
    /// "Tier SHALL be computed from live state at decision time, never read solely from a token
    /// claim." The resource server recomputes; this claim exists so a token can be read in a log
    /// and understood, and <see cref="EvaluatedTier"/> makes using it for a decision impossible to
    /// express anyway.
    /// </param>
    public IssuedToken Mint(string subject, string dpopThumbprint, string owner, string tier, string scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(dpopThumbprint);

        var now = _clock.GetUtcNow();
        var ttl = AuthNConstants.MaxAccessTokenTtl;
        var expires = now + ttl;

        var header = new JsonObject
        {
            ["alg"] = "ES256",
            ["kid"] = SigningKeyId,
            ["typ"] = "at+jwt",
        };

        var payload = new JsonObject
        {
            ["iss"] = Issuer,
            ["sub"] = subject,
            ["aud"] = Audience,
            ["client_id"] = subject,
            ["iat"] = ToNumericDate(now),
            ["exp"] = ToNumericDate(expires),
            ["nbf"] = ToNumericDate(now),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["scope"] = scope,
            ["cnf"] = new JsonObject { ["jkt"] = dpopThumbprint },
            ["owner"] = owner,
            ["tier"] = tier,
        };

        var signingInput =
            Base64UrlEncode(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." +
            Base64UrlEncode(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        var signature = _signingKey.SignData(
            Encoding.ASCII.GetBytes(signingInput),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return new IssuedToken(
            $"{signingInput}.{Base64UrlEncode(signature)}",

            // RFC 9449 §7.1: the type is DPoP, not Bearer. The distinction is load-bearing -- a
            // client that treats it as a Bearer token will omit the proof and be refused, which is
            // the correct outcome and a confusing one if the type said Bearer.
            TokenType: "DPoP",
            ExpiresInSeconds: (int)ttl.TotalSeconds,
            Scope: scope);
    }

    private static long ToNumericDate(DateTimeOffset instant) => instant.ToUnixTimeSeconds();

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
