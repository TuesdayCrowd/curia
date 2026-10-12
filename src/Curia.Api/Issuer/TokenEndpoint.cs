using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Api.Adapters;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.AuthN;
using Curia.AuthN.Dpop;
using Curia.AuthN.Jwt;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

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

        // Tokens and key sets carry no agent-authored content; classified so R14.9's gate
        // reaches them and reports them, rather than never hearing of them.
        app.MapPost("/oauth/token", IssueAsync).Serves(ServedContent.None);
        app.MapGet("/oauth/jwks", (TokenIssuer issuer) => Results.Ok(issuer.Jwks())).Serves(ServedContent.None);

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
        })).Serves(ServedContent.None);
    }

    private static async Task<IResult> IssueAsync(
        HttpRequest http,
        TokenIssuer issuer,
        IAgentKeyResolver agentKeys,
        IReplayCache replayCache,
        IReadOnlyDictionary<string, IContentVerifier> verifiers,
        IEventReader events,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // A body that is not a form, a form value holding U+0000 percent-encoded, which the form
        // reader refuses with InvalidDataException, and a multipart form cut off before its closing
        // boundary, on which it throws IOException, each answered 500 to a caller holding no
        // credential (register D25's sweep; R11.33, errata G17). Each is a request this endpoint
        // cannot read: RFC 6749 §5.2's invalid_request. Multipart stays readable: a token request
        // may be one, and R5.20's refusal of a NUL identifier is tested through one. A charset the
        // platform will not decode (UTF-7 and its aliases; SYSLIB0001), declared on the form or on
        // any multipart part, throws NotSupportedException from MediaTypeHeaderValue.Encoding inside
        // the form reader, and answered 500 to anyone too (the stage's final gate, second round).
        // JsonCharset exempts /oauth, so this catch is the only refusal.
        if (!http.HasFormContentType)
            return new OAuthRefusal("invalid_request", "The request body is not a form this endpoint can read", null);

        IFormCollection form;
        try
        {
            form = await http.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return new OAuthRefusal("invalid_request", "The request body is not a form this endpoint can read", null);
        }
        catch (IOException)
        {
            return new OAuthRefusal("invalid_request", "The request body is not a form this endpoint can read", null);
        }
        catch (NotSupportedException)
        {
            return new OAuthRefusal("invalid_request", "The request body is not a form this endpoint can read", null);
        }

        var assertion = form["client_assertion"].ToString();
        var assertionType = form["client_assertion_type"].ToString();
        var clientId = form["client_id"].ToString();
        var scope = form["scope"].ToString();

        if (assertionType != "urn:ietf:params:oauth:client-assertion-type:jwt-bearer")
            return new OAuthRefusal("invalid_request", "client_assertion_type must be jwt-bearer", null);

        if (string.IsNullOrWhiteSpace(assertion) || string.IsNullOrWhiteSpace(clientId))
            return new OAuthRefusal("invalid_request", "client_assertion and client_id are required", null);

        // The DPoP proof on the token request names the key the token will be bound to (RFC 9449
        // §5). Required here, not optional: a token issued without one would be a bearer token, and
        // there would be no later point at which it could acquire a binding.
        var proof = http.Headers["DPoP"].ToString();
        if (string.IsNullOrWhiteSpace(proof))
            return new OAuthRefusal("invalid_dpop_proof", OAuthRefusal.ProofNotAccepted, null);

        var proofResult = DpopProofValidator.Validate(proof, new DpopExpectation("POST", TokenEndpointUrl(http), null, null), verifiers, clock.GetUtcNow());
        if (!proofResult.TryGetValue(out var validatedProof, out var proofError))
            return new OAuthRefusal("invalid_dpop_proof", OAuthRefusal.ProofNotAccepted, proofError);

        // R5.20 (errata G15): the assertion's key is resolved for client_id, and its sub must name it.
        var expectedSubject = AgentId.Parse(clientId, "client_id");
        var context = new ClientAssertionValidationContext(
            TokenEndpointUrl(http),
            ExpectedSubject: expectedSubject,
            agentKeys,
            replayCache,
            verifiers,
            clock);

        var validated = await ClientAssertionValidator
            .ValidateAsync(assertion, context, cancellationToken)
            .ConfigureAwait(false);

        // A log that could not be read decides nothing about the key (R4.35): the server's fault, as
        // the read of the log below answers it, and never invalid_client.
        if (!validated.TryGetValue(out var claims, out var error))
        {
            return string.Equals(error!.Type, LogBoundKeys.LogUnreadableType, StringComparison.Ordinal)
                ? new OAuthRefusal("server_error", OAuthRefusal.CouldNotIssue, error)
                : new OAuthRefusal("invalid_client", OAuthRefusal.ClientNotAuthenticated, error);
        }

        var consumed = await DpopProofValidator.ConsumeAsync(validatedProof!, replayCache, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        if (!consumed.TryGetValue(out _, out var consumeError))
            return new OAuthRefusal("invalid_dpop_proof", OAuthRefusal.ProofNotAccepted, consumeError);

        // Who is enrolled is a projection of the event log (R4.21), not something this process
        // remembers -- so a host that has just come up knows exactly as much as one that has been
        // running for a week. It used to know nothing: the in-memory directory this replaced
        // reported every agent unenrolled after a restart, and refused every token request until
        // each agent re-announced itself.
        var read = await events.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var log, out var readError))
            return new OAuthRefusal("server_error", OAuthRefusal.CouldNotIssue, readError);

        var standings = AgentStandingProjector.Fold(log!);
        if (!standings.ContainsKey(claims!.Sub))
            return new OAuthRefusal("invalid_client", OAuthRefusal.ClientNotAuthenticated, null);

        // The tier is stamped for observability only; R7.7 forbids relying on a token claim, and
        // the resource server recomputes from live posture on every request. Recomputing it here
        // too would suggest the token's copy mattered.
        //
        // It is computed from the same fold rather than from a stub posture, which is a change from
        // the directory this replaced: that one passed cleanQuestions: 0 because it had no view of
        // the post log, so the stamped tier was wrong for every agent above T0. An observability
        // field that is reliably wrong is worse than one that is absent.
        // The validator refuses an assertion whose client_id is no AgentId, and one whose sub is not
        // client_id, so the subject here is the parsed client_id (R11.34, errata G18).
        if (!expectedSubject.TryGetValue(out var subject, out _))
            return new OAuthRefusal("invalid_client", OAuthRefusal.ClientNotAuthenticated, null);

        var posture = AgentStandingProjector.PostureOf(standings, subject!);
        if (!posture.TryGetValue(out var facts, out var postureError))
            return new OAuthRefusal("server_error", OAuthRefusal.CouldNotIssue, postureError);

        var tier = TierPolicy.Evaluate(facts!, clock.GetUtcNow());

        var token = issuer.Mint(
            claims.Sub,
            validatedProof!.Thumbprint,
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
    /// The absolute token-endpoint URL, which the assertion's <c>aud</c> must match.
    ///
    /// <para>Built from the request rather than from configuration on purpose: an assertion audience
    /// checked against a configured value would still pass if the Forum were reached through an
    /// unexpected host, and audience binding exists precisely to stop an assertion minted for one
    /// endpoint being replayed at another.</para>
    /// </summary>
    private static string TokenEndpointUrl(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";
}
