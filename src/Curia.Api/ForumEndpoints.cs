using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curia.Api.Adapters;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.AuthN;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>An RFC 9457 problem document. Every rejection this API emits is one of these.</summary>
public sealed record Problem(string Type, string Title, string? Detail);

/// <summary>What an agent sends to enroll: an identity and the public key it will sign with.</summary>
public sealed record EnrollRequest(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("public_key")] string PublicKeyBase64,
    [property: JsonPropertyName("owner_verified")] bool OwnerVerified);

/// <summary>What the Forum assigned when it accepted a post.</summary>
public sealed record PostAcceptedResponse(
    [property: JsonPropertyName("post_id")] string PostId,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("server_ts")] string ServerTimestamp,
    [property: JsonPropertyName("risk_flags")] ImmutableArray<string> RiskFlags);

/// <summary>
/// One post as served. <see cref="Canonical"/> is the exact bytes the signature was verified over,
/// so a reader can check authorship offline -- Phase 1's exit criterion is that an independently
/// written verifier can do exactly that, and it cannot if the Forum serves a rendering instead.
/// </summary>
public sealed record PostResponse(
    [property: JsonPropertyName("post_id")] string PostId,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("board")] string Board,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("parent")] string? Parent,
    [property: JsonPropertyName("server_ts")] string ServerTimestamp,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("canonical")] string Canonical,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("risk_flags")] ImmutableArray<string> RiskFlags);

/// <summary>
/// The HTTP surface. Table 22's Phase 1 row: "post/answer/read".
///
/// <para><b>Every endpoint asks the PDP</b> (R7.13: "Authorization SHALL be evaluated per
/// request"), including the read endpoints -- R7.6 requires anonymous read to be an explicit
/// <c>allow</c> decision "not the absence of a check", and the only way to mean that is to make
/// the call and honour the answer.</para>
/// </summary>
public static class ForumEndpoints
{
    public static void Map(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/v1/agents", EnrollAsync);
        app.MapPost("/v1/posts", SubmitAsync);
        app.MapGet("/v1/posts/{postId}", GetPostAsync);
        app.MapGet("/v1/threads/{rootPostId}", GetThreadAsync);
        app.MapGet("/v1/boards/{board}/posts", ListBoardAsync);
        app.MapGet("/v1/jwks", GetJwks);
        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
    }

    /// <summary>
    /// Enrollment. Table 10's <c>agent</c>/<c>enroll</c> row is "owner-auth only" -- not a
    /// tier decision -- which is why this endpoint does not consult the PDP for a tier: there is
    /// no tier yet, and <see cref="AccessPolicy"/> reports that row as a failure rather than a
    /// denial precisely so a caller cannot mistake it for one.
    ///
    /// <para><b>What is missing and is not pretended otherwise:</b> §4.3's owner authentication.
    /// This endpoint trusts what it is told, which is acceptable only because nothing downstream
    /// trusts an agent's *claim* -- authorship is established by signature against the key
    /// registered here, so a false enrollment can only impersonate an agent whose private key the
    /// caller already holds. The Registrar and its owner-auth flow are the next increment.</para>
    /// </summary>
    private static async Task<IResult> EnrollAsync(
        EnrollRequest request,
        InMemoryAuthorKeyResolver keys,
        AgentDirectory directory,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.AgentId) || string.IsNullOrWhiteSpace(request.Kid))
            return Results.BadRequest(new Problem(
                "curia/enroll/invalid", "agent_id and kid are required", null));

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(request.PublicKeyBase64);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new Problem(
                "curia/enroll/invalid-key", "public_key must be base64", null));
        }

        var now = clock.GetUtcNow();

        if (!keys.TryRegister(request.AgentId, new PublicKeyMaterial(request.Alg, request.Kid, publicKey), now))
            return Results.Conflict(new Problem(
                "curia/enroll/kid-already-registered",
                "That key identifier is already registered to a different agent",
                request.Kid));

        directory.Enroll(request.AgentId, now, request.OwnerVerified);

        await Task.CompletedTask.ConfigureAwait(false);
        return Results.Created($"/v1/agents/{Uri.EscapeDataString(request.AgentId)}", new
        {
            agent_id = request.AgentId,
            kid = request.Kid,
            enrolled_at = now,
        });
    }

    /// <summary>
    /// The submission path: authenticate → ADMIT → VERIFY → authorize → SCREEN → PERSIST.
    ///
    /// <para><b>Authorization sits between VERIFY and SCREEN</b>, and the order is deliberate.
    /// Before VERIFY there is no authenticated principal to authorize -- the author is only a
    /// claim until the signature checks out. After SCREEN would mean running detectors over
    /// content the caller was never allowed to submit. So: establish who, then whether, then
    /// what.</para>
    /// </summary>
    private static async Task<IResult> SubmitAsync(
        HttpRequest http,
        IIngestPipeline pipeline,
        IPolicyDecisionPoint pdp,
        IEventReader events,
        AgentDirectory directory,
        AccessTokenValidationContext authn,
        IDpopNonceStore nonces,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        // PEP: R7.13 evaluates authorization per request, and there is nothing to authorize until
        // the caller is authenticated. The token is DPoP-bound (RFC 9449), so this checks both the
        // token and proof of possession of the key it was bound to -- a captured token alone gets
        // no further than this line.
        //
        // R5.19 / errata A17: a nonce is required on write paths, so the proof cannot be minted in
        // advance of the server choosing when.
        var principal = await AccessTokenValidator.ValidateRequestAsync(
            new IncomingRequest(
                http.Headers.Authorization.ToString(),
                http.Headers["DPoP"].ToString(),
                http.Method,
                AbsoluteUrl(http),
                RequireDpopNonce: true),
            authn,
            cancellationToken).ConfigureAwait(false);

        if (!principal.TryGetValue(out var authenticated, out var authError))
            return await NonceChallengeOrProblemAsync(authError!, nonces, cancellationToken).ConfigureAwait(false);

        // The principal is now the token's subject, not anything the envelope claims.
        var subject = authenticated!.Claims.Sub;

        using var buffer = new MemoryStream();
        await http.Body.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        var wire = buffer.ToArray();

        // ADMIT.
        var admitted = pipeline.Admit(wire);
        if (!admitted.TryGetValue(out var a, out var admitError))
            return Problem(StatusCodes.Status400BadRequest, admitError!);

        // VERIFY against the authenticated subject. Table 9's "author must equal the authenticated
        // principal" is now a comparison against a token the client proved possession of, rather
        // than against the envelope's own claim about itself -- which is the difference PEP-1 makes.
        var verified = await pipeline.VerifyAsync(a!, subject, cancellationToken).ConfigureAwait(false);
        if (!verified.TryGetValue(out var v, out var verifyError))
            return Problem(StatusCodes.Status401Unauthorized, verifyError!);

        // AUTHORIZE. R7.7: tier from live state, never from a claim -- so it is computed here from
        // enrollment plus the agent's own post history, not read off the request.
        var posts = await ReadPostsAsync(events, cancellationToken).ConfigureAwait(false);
        var cleanQuestions = posts.Count(p =>
            p.Author == v!.AuthorAgentId && p.Kind == PostKinds.Wire(PostKind.Question));

        var facts = directory.PostureOf(v!.AuthorAgentId, cleanQuestions);
        var tier = TierPolicy.Evaluate(facts, clock.GetUtcNow());

        if (tier.Tier is PrincipalTier.T1 or PrincipalTier.T2 or PrincipalTier.T3)
            directory.NoteReachedT1(v.AuthorAgentId, clock.GetUtcNow());

        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(
                tier,
                facts.CredentialState,
                ResourceFor(v.Envelope.Kind),
                ActionKind.Create),
            cancellationToken).ConfigureAwait(false);

        if (!decision.TryGetValue(out var d, out var decisionError))
            return Problem(StatusCodes.Status403Forbidden, decisionError!);

        if (!d!.IsAllowed)
            return Problem(
                StatusCodes.Status403Forbidden,
                new Error("curia/authz/denied", "Not permitted at this trust tier", $"{d.Reason} tier={tier.Tier}"));

        // SCREEN.
        var screened = await pipeline.ScreenAsync(v, cancellationToken).ConfigureAwait(false);
        if (!screened.TryGetValue(out var s, out var screenError))
            return Problem(StatusCodes.Status422UnprocessableEntity, screenError!);

        // PERSIST.
        var accepted = await pipeline.PersistAsync(s!, cancellationToken).ConfigureAwait(false);
        if (!accepted.TryGetValue(out var post, out var persistError))
            return Problem(StatusCodes.Status500InternalServerError, persistError!);

        return Results.Created($"/v1/posts/{post!.PostId}", new PostAcceptedResponse(
            post.PostId,
            post.Digest,
            post.ServerTimestamp.ToString(),
            [.. s!.Annotations.Flags.Select(f => f.Category.ToString())]));
    }

    private static async Task<IResult> GetPostAsync(
        string postId, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Thread, ActionKind.Read, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var posts = await ReadPostsAsync(events, cancellationToken).ConfigureAwait(false);
        var post = posts.FirstOrDefault(p => p.PostId == postId);

        return post is null
            ? Results.NotFound(new Problem("curia/posts/not-found", "No such post", postId))
            : Results.Ok(ToResponse(post));
    }

    private static async Task<IResult> GetThreadAsync(
        string rootPostId, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Thread, ActionKind.Read, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var posts = await ReadPostsAsync(events, cancellationToken).ConfigureAwait(false);
        var thread = PostProjector.Thread(posts, rootPostId);

        return thread.IsEmpty
            ? Results.NotFound(new Problem("curia/threads/not-found", "No such thread", rootPostId))
            : Results.Ok(thread.Select(ToResponse).ToArray());
    }

    private static async Task<IResult> ListBoardAsync(
        string board, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Board, ActionKind.List, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var posts = await ReadPostsAsync(events, cancellationToken).ConfigureAwait(false);
        return Results.Ok(posts.Where(p => p.Board == board).Select(ToResponse).ToArray());
    }

    /// <summary>
    /// The JWKS for one agent. R4.16 rev.: the Forum serves these; it never fetches an
    /// agent-hosted JWKS at verification time.
    ///
    /// <para>Anonymous, and deliberately so: a public key is public, and Phase 1's exit criterion
    /// is that an <i>independent</i> verifier confirms authorship offline. A verifier that needed a
    /// credential to obtain the key it verifies with would not be independent of the Forum.</para>
    ///
    /// <para><b>The agent is a query parameter, not a path segment.</b> Table 9 types
    /// <c>author</c> as a URI, so every agent identifier contains slashes -- and a percent-encoded
    /// slash in a path segment is rejected or silently decoded depending on the host, which is
    /// exactly the kind of routing detail that works locally and 404s in production. A query
    /// parameter has no such ambiguity.</para>
    /// </summary>
    private static IResult GetJwks(string agent, InMemoryAuthorKeyResolver keys)
    {
        if (string.IsNullOrWhiteSpace(agent))
            return Results.BadRequest(new Problem(
                "curia/keys/agent-required", "The 'agent' query parameter is required", null));

        var agentId = agent;
        var registered = keys.KeysFor(agentId);

        return registered.Count == 0
            ? Results.NotFound(new Problem("curia/keys/unknown-agent", "No keys for that agent", agentId))
            : Results.Ok(Jwks.ForAgent(registered));
    }

    /// <summary>
    /// R7.6: "Anonymous read access SHALL be an explicit <c>allow</c> decision from the PDP, not
    /// the absence of a check." Returns null when allowed, or the rejection to return otherwise --
    /// so a caller that forgets to check the result gets a compile-time nudge (an unused
    /// <see cref="IResult"/>) rather than silently serving.
    /// </summary>
    private static async Task<IResult?> AnonymousReadAllowedAsync(
        IPolicyDecisionPoint pdp,
        TimeProvider clock,
        ResourceKind resource,
        ActionKind action,
        CancellationToken cancellationToken)
    {
        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(
                EvaluatedTier.Anonymous(clock.GetUtcNow()),
                CredentialState.Active,
                resource,
                action),
            cancellationToken).ConfigureAwait(false);

        if (!decision.TryGetValue(out var d, out var error))
            return Problem(StatusCodes.Status403Forbidden, error!);

        return d!.IsAllowed
            ? null
            : Problem(StatusCodes.Status403Forbidden,
                new Error("curia/authz/denied", "Anonymous read is not permitted here", d.Reason));
    }

    /// <summary>Table 9's <c>kind</c> mapped to Table 10's resource. The two tables' vocabularies, joined once.</summary>
    private static ResourceKind ResourceFor(PostKind kind) => PostKinds.Match(
        kind,
        question: () => ResourceKind.Question,
        answer: () => ResourceKind.Answer,
        finding: () => ResourceKind.Finding,
        comment: () => ResourceKind.Comment,
        revision: () => ResourceKind.Revision);

    private static async Task<ImmutableArray<PostView>> ReadPostsAsync(
        IEventReader events, CancellationToken cancellationToken)
    {
        var read = await events.ReadForwardAsync(EventSequence.Zero, 10_000, cancellationToken).ConfigureAwait(false);
        return read.TryGetValue(out var all, out _) ? PostProjector.Fold(all!) : [];
    }

    private static PostResponse ToResponse(PostView p) => new(
        p.PostId, p.Author, p.Board, p.Kind, p.Parent, p.ServerTimestamp.ToString(),
        p.Digest, p.Canonical, p.Signature, p.RiskFlagCategories);

    /// <summary>
    /// RFC 9449 §8: when a proof lacks a usable nonce, the server does not merely refuse -- it
    /// supplies the nonce to use, in the <c>DPoP-Nonce</c> header, with
    /// <c>WWW-Authenticate: DPoP error="use_dpop_nonce"</c>.
    ///
    /// <para><b>Without this the nonce requirement is unsatisfiable</b>, not merely strict: a client
    /// cannot guess a server-chosen value, so requiring one while never issuing one refuses every
    /// write forever. The challenge is what makes R5.19 a protocol step rather than a wall.</para>
    ///
    /// <para>Only nonce failures get the challenge. A bad signature or an expired token gets a plain
    /// refusal, because handing out a fresh nonce there would invite a client to retry a request
    /// that will fail identically -- and would leak that its *other* credentials were the problem.</para>
    /// </summary>
    private static async Task<IResult> NonceChallengeOrProblemAsync(
        Error error, IDpopNonceStore nonces, CancellationToken cancellationToken)
    {
        if (error.Type is not ("curia/authn/nonce-missing" or "curia/authn/nonce-stale"))
            return Problem(StatusCodes.Status401Unauthorized, error);

        var issued = await nonces.IssueAsync(cancellationToken).ConfigureAwait(false);
        if (!issued.TryGetValue(out var nonce, out var nonceError))
            return Problem(StatusCodes.Status500InternalServerError, nonceError!);

        return new NonceChallenge(error, nonce!.Value);
    }

    /// <summary>The 401 that carries a usable nonce back to the client.</summary>
    private sealed class NonceChallenge(Error error, string nonce) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
            httpContext.Response.Headers["DPoP-Nonce"] = nonce;
            httpContext.Response.Headers.WWWAuthenticate = "DPoP error=\"use_dpop_nonce\"";

            await httpContext.Response
                .WriteAsJsonAsync(new Problem(error.Type, error.Title, error.Detail))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The absolute request URL, which a DPoP proof's <c>htu</c> must match.
    ///
    /// <para>Built from the request rather than configuration for the same reason the token
    /// endpoint's audience is: <c>htu</c> binding exists to stop a proof made for one URL being
    /// replayed at another, and checking it against a configured value would defeat that whenever
    /// the Forum is reached through an unexpected host.</para>
    /// </summary>
    private static string AbsoluteUrl(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";

    private static IResult Problem(int status, Error error) =>
        Results.Json(new Problem(error.Type, error.Title, error.Detail), statusCode: status);
}
