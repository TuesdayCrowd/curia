using System.Text.Json.Nodes;
using Curia.Api.Adapters;
using Curia.AuthN;
using Curia.AuthN.Dpop;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;

namespace Curia.Api.Issuer;

/// <summary>
/// §5's token endpoint. <c>private_key_jwt</c> authenticates the client; a DPoP proof names the key
/// the resulting token is bound to.
///
/// <para><b>Why both, and why neither alone would do.</b> The client assertion proves the agent
/// holds its registered private key -- that is authentication. The DPoP proof names a (possibly
/// different) key the client will prove possession of on every subsequent request -- that is
/// sender constraint. Without the assertion anyone could ask for a token; without the proof the
/// token would be a bearer credential, and R5's whole point is that a captured token is useless.</para>
/// </summary>
public static class TokenEndpoint
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/oauth/token", IssueAsync);
        app.MapGet("/oauth/jwks", (TokenIssuer issuer) => Results.Ok(issuer.Jwks()));

        // RFC 8414. A client that must be told its issuer's URLs out of band is a client that will
        // be told them wrongly.
        app.MapGet("/.well-known/oauth-authorization-server", (TokenIssuer issuer) => Results.Ok(new JsonObject
        {
            ["issuer"] = issuer.Issuer,
            ["token_endpoint"] = issuer.Issuer + "/oauth/token",
            ["jwks_uri"] = issuer.Issuer + "/oauth/jwks",
            ["token_endpoint_auth_methods_supported"] = new JsonArray("private_key_jwt"),
            ["dpop_signing_alg_values_supported"] = new JsonArray("ES256", "EdDSA"),
            ["grant_types_supported"] = new JsonArray("client_credentials"),
        }));
    }

    private static async Task<IResult> IssueAsync(
        HttpRequest http,
        TokenIssuer issuer,
        InMemoryAuthorKeyResolver keys,
        IAgentKeyResolver agentKeys,
        IReplayCache replayCache,
        IReadOnlyDictionary<string, IContentVerifier> verifiers,
        AgentDirectory directory,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var form = await http.ReadFormAsync(cancellationToken).ConfigureAwait(false);

        var assertion = form["client_assertion"].ToString();
        var assertionType = form["client_assertion_type"].ToString();
        var clientId = form["client_id"].ToString();
        var scope = form["scope"].ToString();

        if (assertionType != "urn:ietf:params:oauth:client-assertion-type:jwt-bearer")
            return OAuthError("invalid_request", "client_assertion_type must be jwt-bearer");

        if (string.IsNullOrWhiteSpace(assertion) || string.IsNullOrWhiteSpace(clientId))
            return OAuthError("invalid_request", "client_assertion and client_id are required");

        // The DPoP proof on the token request names the key the token will be bound to (RFC 9449
        // §5). Required here, not optional: a token issued without one would be a bearer token, and
        // there would be no later point at which it could acquire a binding.
        var proof = http.Headers["DPoP"].ToString();
        if (string.IsNullOrWhiteSpace(proof))
            return OAuthError("invalid_dpop_proof", "A DPoP proof is required on the token request");

        var thumbprint = DpopThumbprintOf(proof);
        if (thumbprint is null)
            return OAuthError("invalid_dpop_proof", "The DPoP proof's key could not be read");

        var context = new ClientAssertionValidationContext(
            TokenEndpointUrl(http),
            ExpectedSubject: clientId,
            agentKeys,
            replayCache,
            verifiers,
            clock);

        var validated = await ClientAssertionValidator
            .ValidateAsync(assertion, context, cancellationToken)
            .ConfigureAwait(false);

        if (!validated.TryGetValue(out var claims, out var error))
            return OAuthError("invalid_client", error!.Title, error.Type);

        if (!directory.Knows(claims!.Sub))
            return OAuthError("invalid_client", "That agent is not enrolled");

        // The tier is stamped for observability only; R7.7 forbids relying on a token claim, and
        // the resource server recomputes from live posture on every request. Recomputing it here
        // too would suggest the token's copy mattered.
        var tier = TierPolicy.Evaluate(directory.PostureOf(claims.Sub, cleanQuestions: 0), clock.GetUtcNow());

        var token = issuer.Mint(
            claims.Sub,
            thumbprint,
            owner: claims.Sub,
            tier: tier.Tier.ToString(),
            scope: string.IsNullOrWhiteSpace(scope) ? "question:create answer:create" : scope);

        return Results.Ok(new
        {
            access_token = token.AccessToken,
            token_type = token.TokenType,
            expires_in = token.ExpiresInSeconds,
            scope = token.Scope,
        });
    }

    /// <summary>
    /// The RFC 9449 <c>jkt</c> of the proof's embedded public key, read without verifying the proof.
    ///
    /// <para>Safe to read before verification because the thumbprint is only used to *bind* the
    /// token to whatever key the client presented. If the client presented a key it does not hold,
    /// it has bound its own token to something it cannot prove possession of -- a self-inflicted
    /// denial, not an escalation. The proof itself is verified on every resource request, which is
    /// where possession actually has to hold.</para>
    /// </summary>
    private static string? DpopThumbprintOf(string proof)
    {
        var parts = proof.Split('.');
        if (parts.Length != 3) return null;

        try
        {
            var headerJson = System.Text.Encoding.UTF8.GetString(
                System.Buffers.Text.Base64Url.DecodeFromChars(parts[0]));

            using var header = System.Text.Json.JsonDocument.Parse(headerJson);
            if (!header.RootElement.TryGetProperty("jwk", out var jwk)) return null;

            var parsed = JwkParser.Parse(jwk);
            return parsed.TryGetValue(out var key, out _) ? JwkThumbprint.Compute(key!) : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// The absolute token-endpoint URL, which the assertion's <c>aud</c> must match.
    ///
    /// <para>Built from the request rather than from configuration on purpose: an assertion audience
    /// checked against a configured value would still pass if the Forum were reached through an
    /// unexpected host, and audience binding exists precisely to stop an assertion minted for one
    /// endpoint being replayed at another.</para>
    /// </summary>
    private static string TokenEndpointUrl(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";

    private static IResult OAuthError(string code, string description, string? detail = null) =>
        Results.Json(
            new { error = code, error_description = description, detail },
            statusCode: code == "invalid_client" ? StatusCodes.Status401Unauthorized : StatusCodes.Status400BadRequest);
}
