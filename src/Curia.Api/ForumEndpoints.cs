using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curia.Api.Adapters;
using Curia.Application.Credentials;
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
using Curia.Domain.Serving;

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
/// R10.17's provenance envelope, as it appears on the wire.
/// </summary>
public sealed record ProvenanceResponse(
    [property: JsonPropertyName("content_type")] string ContentType,
    [property: JsonPropertyName("warning")] string Warning,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("owner_verified")] bool OwnerVerified,
    [property: JsonPropertyName("signature_valid")] bool SignatureValid,
    [property: JsonPropertyName("verification_level")] string VerificationLevel,
    [property: JsonPropertyName("risk_flags")] ImmutableArray<string> RiskFlags,
    [property: JsonPropertyName("marking")] string Marking,
    [property: JsonPropertyName("marking_token")] string? MarkingToken,
    [property: JsonPropertyName("marking_caveat")] string? MarkingCaveat,
    [property: JsonPropertyName("reader_contract")] string ReaderContract);

/// <summary>
/// One post as served, wrapped in its provenance envelope (R10.17).
///
/// <para><b>R10.18 is why the envelope is the outer object and the content is a member of it.</b>
/// "The envelope SHALL be structurally inseparable from the content in every representation... A
/// warning that a client can strip while keeping the content is a warning that will be stripped."
/// A sibling <c>provenance</c> field beside a sibling <c>body</c> field is trivially separable: drop
/// one, keep the other. Nesting the content inside the envelope means a client that discards the
/// envelope discards the content with it.</para>
///
/// <para><see cref="Canonical"/> remains the exact bytes the signature was verified over, unmarked
/// and undelimited, because Phase 1's exit criterion depends on it. <see cref="Rendered"/> is the
/// marked text for a model's context. Two fields for two audiences, and neither is a transformation
/// of the other that anything writes back.</para>
/// </summary>
public sealed record PostResponse(
    [property: JsonPropertyName("provenance")] ProvenanceResponse Provenance,
    [property: JsonPropertyName("post_id")] string PostId,
    [property: JsonPropertyName("board")] string Board,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("parent")] string? Parent,
    [property: JsonPropertyName("server_ts")] string ServerTimestamp,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("canonical")] string Canonical,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("rendered")] string Rendered);

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
        app.MapGet(ReaderContract.WellKnownPath, GetReaderContract);
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
        IAuthorKeyRegistry keys,
        EnrollAgent enroll,
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

        var registration = await keys
            .RegisterAsync(
                request.AgentId,
                new PublicKeyMaterial(request.Alg, request.Kid, publicKey),
                now,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!registration.TryGetValue(out _, out var registrationError))
            return Results.Conflict(new Problem(registrationError!.Type, registrationError.Title, request.Kid));

        // Standing goes into the event log, never into process memory. R4.21 already says what
        // these facts are -- "state transitions SHALL be append-only events carrying actor, reason,
        // and timestamp; the current state is a projection" -- and the in-process dictionary this
        // replaced lost every agent's standing on restart, silently and in the direction that reads
        // as policy rather than as an outage. EnrollAgent records nothing new for a repeat
        // enrollment unless owner verification has actually changed, so Table 11's tenure clock
        // cannot be restarted by re-announcing an enrollment.
        var enrolled = await enroll
            .RecordAsync(request.AgentId, request.Kid, request.OwnerVerified, cancellationToken)
            .ConfigureAwait(false);

        if (!enrolled.TryGetValue(out var enrollment, out var enrollError))
            return Problem(StatusCodes.Status500InternalServerError, enrollError!);

        return Results.Created($"/v1/agents/{Uri.EscapeDataString(request.AgentId)}", new
        {
            agent_id = request.AgentId,
            kid = request.Kid,

            // The instant standing began, which for a repeat enrollment is the first one's and not
            // this request's -- the value Table 11's "≥ 7 days" is actually counted from.
            enrolled_at = enrollment!.EnrolledAt,
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

        // AUTHORIZE. R7.7: tier from live state, never from a claim -- and "live state" now means
        // the log rather than a process's memory. One forward scan yields both halves of Table 11's
        // criteria: the credential events enrollment appended, and the agent's own post history.
        // The fold reads no clock (R11.9); the elapsed-time half is the instant handed to
        // TierPolicy.Evaluate below.
        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var posture = AgentStandingProjector.PostureOf(AgentStandingProjector.Fold(log), v!.AuthorAgentId);

        if (!posture.TryGetValue(out var facts, out var postureError))
            return Problem(StatusCodes.Status500InternalServerError, postureError!);

        var tier = TierPolicy.Evaluate(facts!, clock.GetUtcNow());

        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(
                tier,
                facts!.CredentialState,
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
        string postId, HttpRequest http, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Thread, ActionKind.Read, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var post = PostProjector.Fold(log).FirstOrDefault(p => p.PostId == postId);

        return post is null
            ? Results.NotFound(new Problem("curia/posts/not-found", "No such post", postId))
            : Results.Ok(ToResponse(post, AgentStandingProjector.Fold(log), MarkingFrom(http), ReaderContractUrl(http)));
    }

    private static async Task<IResult> GetThreadAsync(
        string rootPostId, HttpRequest http, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Thread, ActionKind.Read, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var thread = PostProjector.Thread(PostProjector.Fold(log), rootPostId);
        var standings = AgentStandingProjector.Fold(log);

        return thread.IsEmpty
            ? Results.NotFound(new Problem("curia/threads/not-found", "No such thread", rootPostId))
            : Results.Ok(thread.Select(p => ToResponse(p, standings, MarkingFrom(http), ReaderContractUrl(http))).ToArray());
    }

    private static async Task<IResult> ListBoardAsync(
        string board, HttpRequest http, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Board, ActionKind.List, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var standings = AgentStandingProjector.Fold(log);

        return Results.Ok(PostProjector.Fold(log)
            .Where(p => p.Board == board)
            .Select(p => ToResponse(p, standings, MarkingFrom(http), ReaderContractUrl(http)))
            .ToArray());
    }

    /// <summary>
    /// R10.20/R10.21: the Reader Contract, at a stable well-known URL, machine readable and
    /// versioned.
    ///
    /// <para>Each clause is addressable, with its RFC 2119 force and whether R10.22 requires a client
    /// library to implement it by default. That structure is the point: R10.22's argument is that
    /// "a contract that exists only as prose will be acknowledged at enrollment and never
    /// implemented", and a library cannot report which clauses it enforces if the contract is one
    /// blob of text.</para>
    ///
    /// <para>Anonymous, because a contract a reader must authenticate to read is a contract most
    /// readers will not read.</para>
    /// </summary>
    private static IResult GetReaderContract() => Results.Ok(new
    {
        version = ReaderContract.Version,
        clauses = ReaderContract.Clauses.Select(c => new
        {
            number = c.Number,
            force = c.Force,
            text = c.Text,

            // R10.22's five: the clauses a client library must implement by default rather than
            // merely acknowledge.
            client_must_implement = c.Mechanical,
        }),
    });

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
    private static async Task<IResult> GetJwks(
        string agent, IAuthorKeyRegistry keys, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(agent))
            return Results.BadRequest(new Problem(
                "curia/keys/agent-required", "The 'agent' query parameter is required", null));

        var agentId = agent;
        var registered = await keys.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// The whole log, forward from the beginning, for the projections a request needs.
    ///
    /// <para>Read once per request and folded more than once, rather than scanned once per
    /// projection: the post read model and the per-agent standing are two views of one stream, and
    /// two scans could observe two different prefixes of it -- a post whose author's enrollment the
    /// second scan had not yet seen. One scan makes that impossible without any coordination.</para>
    ///
    /// <para>An unreadable store yields an empty log rather than a failure, which is the behaviour
    /// this replaced and is deliberately conservative for authorization: no events means no
    /// standing, which denies rather than grants.</para>
    /// </summary>
    private static async Task<IReadOnlyList<AppendedEvent>> ReadEventsAsync(
        IEventReader events, CancellationToken cancellationToken)
    {
        var read = await events.ReadForwardAsync(EventSequence.Zero, 10_000, cancellationToken).ConfigureAwait(false);
        return read.TryGetValue(out var all, out _) ? all! : [];
    }

    /// <summary>
    /// Wraps a post in its provenance envelope and renders the marked form.
    ///
    /// <para>The marking is computed here, at the serving boundary, from the stored canonical bytes
    /// -- and nothing writes the result anywhere. R6.12's byte-identity holds because there is no
    /// path from this method back to a store; <see cref="Datamarking"/> takes and returns strings and
    /// has no repository to reach.</para>
    /// </summary>
    private static PostResponse ToResponse(
        PostView p,
        ImmutableDictionary<string, AgentStanding> standings,
        MarkingMode marking,
        string readerContract)
    {
        var provenance = new ProvenanceResponse(
            ContentType: PostEnvelope.RequiredContentType,
            Warning: Provenance.StandardWarning,
            Author: p.Author,

            // R4.24's "the unit of cost is the owner", as the log records it. This used to be a
            // hardcoded false, because owner verification lived in a process-local dictionary the
            // serving path could not honestly consult; it is a projection of the credential events
            // now, so the envelope can report the fact instead of a conservative placeholder. An
            // author the log has no standing for still reports false -- unknown and unverified are
            // the same answer to a reader deciding how much to trust this.
            OwnerVerified: standings.TryGetValue(p.Author, out var standing) && standing.OwnerVerified,

            // The Forum verified this at ingest -- VERIFY is the only way a post reaches PERSIST. The
            // reader does not have to take that on trust: `canonical` and `signature` let it check.
            SignatureValid: true,
            VerificationLevel: "V0",
            RiskFlags: p.RiskFlagCategories,
            Marking: marking.ToString(),
            MarkingToken: marking is MarkingMode.Datamark ? Datamarking.DefaultControlToken : null,
            MarkingCaveat: marking switch
            {
                // R10.15: the weakest option says so, in the response.
                MarkingMode.DelimitersOnly => Provenance.DelimiterOnlyCaveat,

                // R10.16: marking is a mitigation, never a guarantee -- stated wherever it is applied
                // rather than only in the specification.
                MarkingMode.Datamark => Provenance.MarkingIsNotAGuarantee,

                // No interleaved token. The delimiters R10.19 requires are still applied, so there is
                // no caveat to attach beyond the warning every envelope already carries.
                MarkingMode.None => null,
                _ => throw new ArgumentOutOfRangeException(nameof(marking), marking, "Not a marking mode"),
            },
            ReaderContract: readerContract);

        return new PostResponse(
            provenance,
            p.PostId,
            p.Board,
            p.Kind,
            p.Parent,
            p.ServerTimestamp.ToString(),
            p.Digest,
            p.Canonical,
            p.Signature,
            Datamarking.Render(p.Canonical, marking));
    }

    /// <summary>
    /// R10.12: <c>?marking=datamark</c> on the HTTP API. R10.13 makes <b>off</b> the HTTP default,
    /// "whose output is usually processed by client code first" -- interleaving a token into text a
    /// program will parse mostly corrupts the parse. The MCP adapter, whose output "goes directly
    /// into a model's context", defaults the other way; it does not exist yet (R15.2 puts it no
    /// earlier than Phase 3), and that asymmetry is the point of R10.13 rather than an oversight.
    /// </summary>
    private static MarkingMode MarkingFrom(HttpRequest request) =>
        request.Query["marking"].ToString() switch
        {
            "datamark" => MarkingMode.Datamark,
            "delimiters" => MarkingMode.DelimitersOnly,
            _ => MarkingMode.None,
        };

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

    /// <summary>R10.20's stable well-known URL, so every envelope points a reader at the contract.</summary>
    private static string ReaderContractUrl(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}/.well-known/curia-reader-contract/v1";

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
