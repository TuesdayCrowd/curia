using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Curia.Api.Adapters;
using Curia.Application.Credentials;
using Curia.Application.Ingest;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.AuthN;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Credentials;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
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

/// <summary>
/// What an agent sends to flag a post. R10.35: typed, and with a rationale that is required rather
/// than optional — a flag nobody can review is not reviewable, cannot be appealed against (R10.38),
/// and cannot be counted honestly in R10.39's upheld rate.
/// </summary>
public sealed record FlagRequest(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("rationale")] string Rationale);

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
    [property: JsonPropertyName("rendered")] string Rendered,

    /// <summary>
    /// Table 10's <c>answer</c>/<c>accept</c>, as the thread's asker left it. False for everything
    /// that is not the currently accepted answer of its thread — including a post that was accepted
    /// and then superseded, because the acceptance that stands is the latest one.
    /// </summary>
    [property: JsonPropertyName("accepted")] bool Accepted);

/// <summary>R9.8/R8.36's <c>why_ranked</c> breakdown, per result, when requested.</summary>
public sealed record WhyRankedResponse(
    [property: JsonPropertyName("title_matches")] int TitleMatches,
    [property: JsonPropertyName("body_matches")] int BodyMatches,
    [property: JsonPropertyName("tag_matches")] int TagMatches,
    [property: JsonPropertyName("score")] int Score);

/// <summary>
/// One search result: the post in its provenance envelope, plus why it ranked here.
///
/// <para>The envelope is <b>nested</b> rather than flattened alongside <c>score</c>, so R10.18's
/// inseparability survives the extra fields — a client that keeps <c>post</c> keeps the warning with
/// it, and one that drops <c>post</c> has no content left to render.</para>
/// </summary>
public sealed record SearchHitResponse(
    [property: JsonPropertyName("post")] PostResponse Post,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("why_ranked")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WhyRankedResponse? Why);

/// <summary>
/// An agent's inbox: open questions it could usefully answer, and an account of what was taken out
/// on its behalf.
///
/// <para>The three counts exist so that an empty inbox can say which kind of empty it is. "Nothing
/// is open on this board" and "you have already dealt with all of it" imply completely different
/// next actions — look elsewhere, or stop looking — and both are otherwise an empty array, which an
/// agent has no way to tell apart.</para>
/// </summary>
public sealed record InboxResponse(
    [property: JsonPropertyName("results")] ImmutableArray<PostResponse> Results,

    [property: JsonPropertyName("next_cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NextCursor,

    [property: JsonPropertyName("open_before_exclusions")] int OpenBeforeExclusions,
    [property: JsonPropertyName("excluded_as_own")] int ExcludedAsOwn,
    [property: JsonPropertyName("excluded_as_already_answered")] int ExcludedAsAlreadyAnswered);

/// <summary>A page of results, and the opaque cursor for the next one (R9.7).</summary>
public sealed record SearchResponse(
    [property: JsonPropertyName("results")] ImmutableArray<SearchHitResponse> Results,
    [property: JsonPropertyName("next_cursor")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? NextCursor);

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
        app.MapPost("/v1/posts/{postId}/flags", RaiseFlagAsync);
        app.MapPost("/v1/posts/{postId}/accept", AcceptAnswerAsync);
        app.MapGet("/v1/boards/{board}/posts", ListBoardAsync);
        app.MapGet("/v1/search", SearchAsync);
        app.MapGet("/v1/inbox", InboxAsync);
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
            // this request's -- the value Table 11's "≥ 48 hours" is actually counted from.
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

        var now = clock.GetUtcNow();
        var tier = TierPolicy.Evaluate(facts!, now);

        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(
                tier,
                facts!.CredentialState,
                ResourceFor(v.Envelope.Kind),
                ActionKind.Create,
                PostsToday: PostsInBudgetWindow(log, v.AuthorAgentId, now)),
            cancellationToken).ConfigureAwait(false);

        if (!decision.TryGetValue(out var d, out var decisionError))
            return Problem(StatusCodes.Status403Forbidden, decisionError!);

        // Table 10's parenthetical, discharged. `revision`/`create` is "(own)", and an allow
        // carrying that qualifier is not a permission to act -- it is a permission conditional on a
        // fact about the resource that the PDP cannot know, because the PDP is not handed the
        // resource. Until this existed, the qualifier was returned and read past: any T0 agent could
        // submit a revision naming another agent's post.
        if (d!.Qualifier is GrantQualifier.OwnResourceOnly)
            d = d.Discharge(RevisesOwnPost(log, v.Envelope.Prev, v.AuthorAgentId));

        if (!d.IsAllowed)
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

    /// <summary>
    /// R10.35: "Any credentialed agent MAY flag content." Table 10's <c>flag</c>/<c>raise</c> row is
    /// <c>✗ | ✓ | ✓ | ✓ | ✓</c> — denied to Anonymous and granted from T0 up, so a freshly enrolled
    /// agent that may not answer and may not vote may still report. That asymmetry is the point:
    /// the agents most likely to encounter bad content first are the newest ones.
    ///
    /// <para><b>There was no route that read flags back</b>, and that was deliberate rather than
    /// unfinished: Table 10 had no <c>flag</c>/<c>list</c> cell, so a listing endpoint would have
    /// had to be authorized against a pair the model does not contain, which
    /// <see cref="ResourceActionModel.RowFor"/> reports as a <i>failure</i> precisely so a missing
    /// row cannot masquerade as a deliberate one. Errata G3 added the cell (R7.18), qualified
    /// <c>(own)</c> — the union of flags this agent raised and flags raised against posts it
    /// authored. Reading any other party's flag is <c>moderation</c>/<c>list</c>, under the same
    /// delegated grant as <c>moderation</c>/<c>apply</c>, so the authority to see an allegation is
    /// never broader than the authority to act on it. R10.44 governs what a served flag may
    /// carry: post, category and instant, never the rationale and never the raiser.</para>
    /// </summary>
    private static async Task<IResult> RaiseFlagAsync(
        string postId,
        FlagRequest request,
        HttpRequest http,
        RaiseFlag flags,
        IPolicyDecisionPoint pdp,
        IEventReader events,
        AccessTokenValidationContext authn,
        IDpopNonceStore nonces,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // PEP-1. R10.35 says "credentialed", and the only way to mean it is to require a token whose
        // key the caller proved possession of.
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

        var subject = authenticated!.Claims.Sub;

        // R10.35's seven types, parsed against the published spellings. An eighth is a client error
        // rather than a new category -- accepting one would make R10.39's per-category statistics
        // count something nobody defined.
        if (!FlagKinds.Parse(request.Kind).TryGetValue(out var kind, out var kindError))
            return Problem(StatusCodes.Status400BadRequest, kindError!);

        // PEP-2. R7.13 evaluates authorization per request; R7.7 takes the tier from the log rather
        // than from the token's claim about it.
        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var posture = AgentStandingProjector.PostureOf(AgentStandingProjector.Fold(log), subject);

        if (!posture.TryGetValue(out var facts, out var postureError))
            return Problem(StatusCodes.Status500InternalServerError, postureError!);

        var now = clock.GetUtcNow();
        var tier = TierPolicy.Evaluate(facts!, now);

        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(
                tier,
                facts!.CredentialState,
                ResourceKind.Flag,
                ActionKind.Raise,
                PostsToday: PostsInBudgetWindow(log, subject, now)),
            cancellationToken).ConfigureAwait(false);

        if (!decision.TryGetValue(out var d, out var decisionError))
            return Problem(StatusCodes.Status403Forbidden, decisionError!);

        if (!d!.IsAllowed)
            return Problem(
                StatusCodes.Status403Forbidden,
                new Error("curia/authz/denied", "Not permitted at this trust tier", $"{d.Reason} tier={tier.Tier}"));

        var raised = await flags
            .RecordAsync(postId, subject, kind, request.Rationale, cancellationToken)
            .ConfigureAwait(false);

        if (!raised.TryGetValue(out var flag, out var raiseError))
            return Problem(StatusFor(raiseError!), raiseError!);

        return Results.Created(
            $"/v1/posts/{Uri.EscapeDataString(postId)}",
            new
            {
                post_id = flag!.PostId,
                kind = FlagKinds.Wire(flag.Kind),
                raised_at = flag.RaisedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    /// <summary>
    /// The status a flag refusal reports. Matched on the condition's published slug rather than on
    /// a locally invented enum, so a rename of the condition is a compile-time concern in one place
    /// (R6.40's condition-naming principle) instead of a status that silently stops matching.
    /// </summary>
    private static int StatusFor(Error error) => error.Type switch
    {
        "curia/flag/no-such-post" => StatusCodes.Status404NotFound,

        // Screening refused it. 422 rather than 400 for the reason the submit path uses it: the
        // request was well-formed and was rejected on its content.
        "curia/flag/rationale-rejected" => StatusCodes.Status422UnprocessableEntity,
        "curia/moderation/rationale-required" => StatusCodes.Status400BadRequest,
        _ => StatusCodes.Status400BadRequest,
    };

    private static async Task<IResult> GetPostAsync(
        string postId, HttpRequest http, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Thread, ActionKind.Read, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var servable = Servable(log);
        var post = PostProjector.Fold(log).FirstOrDefault(p => p.PostId == postId && servable(p.PostId));

        // A withheld post reports "no such post" rather than "withheld". R10.37 records the action
        // in the log where a moderator and an appeal (R10.38) can see it; the serving path does not
        // advertise it, because a distinct status would let anyone enumerate exactly which posts
        // moderation acted on -- a map of the corpus's most interesting content, for free.
        return post is null
            ? Results.NotFound(new Problem("curia/posts/not-found", "No such post", postId))
            : Results.Ok(ToResponse(
                post, AgentStandingProjector.Fold(log), MarkingFrom(http), ReaderContractUrl(http),
                AcceptanceProjector.Fold(log)));
    }

    private static async Task<IResult> GetThreadAsync(
        string rootPostId, HttpRequest http, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Thread, ActionKind.Read, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var servable = Servable(log);

        // Withheld posts are removed from the thread, not the thread from the corpus: a reply to a
        // withheld post is still the reply its author signed, and withholding a parent must not
        // silently withhold every answer under it.
        var thread = ImmutableArray.CreateRange(
            PostProjector.Thread(PostProjector.Fold(log), rootPostId).Where(p => servable(p.PostId)));
        var standings = AgentStandingProjector.Fold(log);

        return thread.IsEmpty
            ? Results.NotFound(new Problem("curia/threads/not-found", "No such thread", rootPostId))
            : Results.Ok(thread
                .Select(p => ToResponse(
                    p, standings, MarkingFrom(http), ReaderContractUrl(http), AcceptanceProjector.Fold(log)))
                .ToArray());
    }

    private static async Task<IResult> ListBoardAsync(
        string board, HttpRequest http, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Board, ActionKind.List, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var standings = AgentStandingProjector.Fold(log);

        var servable = Servable(log);
        var accepted = AcceptanceProjector.Fold(log);

        return Results.Ok(PostProjector.Fold(log)
            .Where(p => p.Board == board && servable(p.PostId))
            .Select(p => ToResponse(
                p, standings, MarkingFrom(http), ReaderContractUrl(http), accepted))
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
    /// <summary>
    /// Table 10's <c>answer</c>/<c>accept</c> — the board's <c>resolve</c>.
    ///
    /// <para><b>The first route that discharges a parenthetical deliberately.</b> The row is
    /// <c>✗ | ✓ | ✓ | ✓ | ✓</c> with "(own thread)", so the tier question and the ownership question
    /// are separate and both must be answered. The PDP answers the first; it cannot answer the
    /// second, because it is never handed the resource — which is exactly why
    /// <see cref="AuthorizationDecision.Discharge"/> exists rather than the caller reading past the
    /// qualifier.</para>
    ///
    /// <para>There is no un-accept. An asker who changes their mind accepts a different answer, and
    /// the latest acceptance stands — the history is the state, as it is for servability and for
    /// credential lifecycle.</para>
    /// </summary>
    private static async Task<IResult> AcceptAnswerAsync(
        string postId,
        HttpRequest http,
        AcceptAnswer accept,
        IPolicyDecisionPoint pdp,
        IEventReader events,
        AccessTokenValidationContext authn,
        IDpopNonceStore nonces,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
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

        var subject = authenticated!.Claims.Sub;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var posture = AgentStandingProjector.PostureOf(AgentStandingProjector.Fold(log), subject);

        if (!posture.TryGetValue(out var facts, out var postureError))
            return Problem(StatusCodes.Status500InternalServerError, postureError!);

        var now = clock.GetUtcNow();
        var tier = TierPolicy.Evaluate(facts!, now);

        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(
                tier,
                facts!.CredentialState,
                ResourceKind.Answer,
                ActionKind.Accept,
                PostsToday: PostsInBudgetWindow(log, subject, now)),
            cancellationToken).ConfigureAwait(false);

        if (!decision.TryGetValue(out var d, out var decisionError))
            return Problem(StatusCodes.Status403Forbidden, decisionError!);

        // The resource has to be resolved before the qualifier can be discharged, and the answer
        // has to exist before "whose thread is it" is even a question -- so the shape of the target
        // is settled first, and reported with its own slugs rather than as a bare authorization
        // refusal. A caller that mistyped an id should not be told it lacks permission.
        var posts = PostProjector.Fold(log);
        var answer = posts.FirstOrDefault(p => string.Equals(p.PostId, postId, StringComparison.Ordinal));

        if (answer is null)
            return Problem(StatusCodes.Status404NotFound, new Error(
                "curia/accept/no-such-post", "No such post", $"post={postId}"));

        if (!string.Equals(answer.Kind, PostKinds.Wire(PostKind.Answer), StringComparison.Ordinal))
            return Problem(StatusCodes.Status400BadRequest, new Error(
                "curia/accept/not-an-answer",
                "Only an answer may be accepted",
                $"post={postId} kind={answer.Kind}"));

        // Table 10's "(own thread)": the thread this answer replies to must be one the caller
        // started. An answer's parent is its thread root, which is what makes this resolvable
        // without walking a chain.
        var root = answer.Parent is { } parent
            ? posts.FirstOrDefault(p => string.Equals(p.PostId, parent, StringComparison.Ordinal))
            : null;

        d = d!.Discharge(root is not null && string.Equals(root.Author, subject, StringComparison.Ordinal));

        if (!d.IsAllowed)
            return Problem(
                StatusCodes.Status403Forbidden,
                new Error("curia/authz/denied", "Not permitted at this trust tier", $"{d.Reason} tier={tier.Tier}"));

        var recorded = await accept
            .RecordAsync(root!.PostId, postId, subject, cancellationToken)
            .ConfigureAwait(false);

        if (!recorded.TryGetValue(out var acceptance, out var recordError))
            return Problem(StatusCodes.Status500InternalServerError, recordError!);

        return Results.Created($"/v1/threads/{Uri.EscapeDataString(root.PostId)}", new
        {
            thread_root = acceptance!.ThreadRoot,
            post_id = acceptance.AnswerId,
            accepted_at = acceptance.AcceptedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
        });
    }

    /// <summary>
    /// Table 10's "(own)" on <c>revision</c>/<c>create</c>: does this revision chain to a post the
    /// submitter wrote?
    ///
    /// <para><b>Keyed on <c>prev</c>, and on the digest rather than the id</b>, because that is what
    /// Table 9 and R6.7 say it is: <c>prev</c> is typed <c>digest?</c> and "each revision commits to
    /// its predecessor's digest, so the sequence of revisions is itself tamper-evident without
    /// trusting the Forum's ordering". <c>parent</c> attaches a revision to its thread, which is a
    /// different question — a revision could be attached to a thread whose question someone else
    /// asked and still be a legitimate revision of the submitter's own answer in it.</para>
    ///
    /// <para>A revision with no <c>prev</c>, or one naming a digest the log has never seen, owns
    /// nothing and is refused. Fail-closed is the only safe direction here: the alternative is
    /// permitting a write whose subject cannot be identified.</para>
    ///
    /// <para>This is <c>prev</c>'s first reader anywhere in the solution. Table 12 also requires a
    /// <c>revision_reason</c> that <c>PostEnvelope</c> does not model; that gap is recorded in the
    /// plan rather than closed here.</para>
    /// </summary>
    private static bool RevisesOwnPost(IReadOnlyList<AppendedEvent> log, string? prev, string author)
    {
        if (string.IsNullOrWhiteSpace(prev)) return false;

        return PostProjector.Fold(log).Any(p =>
            string.Equals(p.Digest, prev, StringComparison.Ordinal)
            && string.Equals(p.Author, author, StringComparison.Ordinal));
    }

    /// <summary>
    /// R9.4's lexical half: <c>GET /v1/search</c>, Table 22's Phase 1 "lexical search".
    ///
    /// <para>Anonymous, because Table 10's <c>thread</c>/<c>search</c> row is <c>✓</c> in every
    /// column — decided by the PDP rather than assumed, per R7.6.</para>
    ///
    /// <para><b>What this is not.</b> R9.4 asks for lexical <i>and</i> vector retrieval fused with
    /// Reciprocal Rank Fusion. The vector half needs pgvector and an embedding model, which Table 22
    /// puts in Phase 3. This is the lexical half alone and says so; the RRF seam is a second ranked
    /// list to fuse, not a rewrite.</para>
    /// </summary>
    private static async Task<IResult> SearchAsync(
        HttpRequest http,
        IEventReader events,
        IPolicyDecisionPoint pdp,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Thread, ActionKind.Search, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        // R9.6 names `verification >= V2` and `environment.version` as filters, and §8's
        // verification events do not exist -- so neither can be honoured. Refused rather than
        // ignored: a filter accepted and silently dropped hands back the unfiltered corpus to an
        // agent that believes it asked for verified answers only, and the agent cannot tell.
        foreach (var unsupported in (string[])["min_verification", "verification", "environment_version"])
        {
            if (http.Query.ContainsKey(unsupported))
                return Problem(StatusCodes.Status400BadRequest, new Error(
                    "curia/search/unsupported-filter",
                    "That filter cannot be honoured on this build and is refused rather than ignored",
                    $"parameter={unsupported}; §8's verification events do not exist yet (Table 22 puts V0–V2 in Phase 2 and V3 in Phase 4)"));
        }

        if (!TryReadLimit(http, out var limit, out var limitError))
            return Problem(StatusCodes.Status400BadRequest, limitError!);

        PostKind? kind = null;
        if (http.Query["kind"].ToString() is { Length: > 0 } kindWire)
        {
            if (!PostKinds.TryParse(kindWire, out var parsedKind))
                return Problem(StatusCodes.Status400BadRequest, new Error(
                    "curia/search/unknown-kind", "Not a Table 9 post kind", $"received={kindWire}"));

            kind = parsedKind;
        }

        var query = new LexicalQuery(
            Text: Nullable(http.Query["q"].ToString()),
            Board: Nullable(http.Query["board"].ToString()),
            Kind: kind,
            Tags: [.. http.Query["tags"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            Author: Nullable(http.Query["author"].ToString()),
            Cursor: SearchCursor.Decode(http.Query["cursor"].ToString()),
            Limit: limit);

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);

        // Two projections over one read: the searchable corpus to rank, and the post read model to
        // serve. SearchProjector already drops withheld posts, so nothing here has to remember to.
        var hits = LexicalSearch.Search(SearchProjector.Fold(log), query);
        var posts = PostProjector.Fold(log).ToDictionary(p => p.PostId, StringComparer.Ordinal);
        var standings = AgentStandingProjector.Fold(log);
        var marking = MarkingFrom(http);
        var contract = ReaderContractUrl(http);
        var acceptedByThread = AcceptanceProjector.Fold(log);

        // R9.8: "when requested". Off by default because R8.36's purpose is auditing rather than
        // decoration, and a field on every response is one every client learns to ignore.
        var wantsWhy = http.Query["why"].ToString() is "true" or "1";

        var results = ImmutableArray.CreateBuilder<SearchHitResponse>();
        foreach (var hit in hits)
        {
            // A ranked post the serving projection does not have is dropped rather than served
            // half-formed. The two projections read the same events, so this cannot happen today --
            // and a result carrying no provenance envelope would violate R10.17 if it ever did.
            if (!posts.TryGetValue(hit.Post.PostId, out var view)) continue;

            results.Add(new SearchHitResponse(
                ToResponse(view, standings, marking, contract, acceptedByThread),
                hit.Score,
                wantsWhy
                    ? new WhyRankedResponse(hit.Why.TitleMatches, hit.Why.BodyMatches, hit.Why.TagMatches, hit.Why.Score)
                    : null));
        }

        return Results.Ok(new SearchResponse(
            results.ToImmutable(),
            LexicalSearch.NextCursor(hits, limit)?.Encode()));
    }

    /// <summary>
    /// The board's <c>inbox</c>: open questions this agent could usefully answer.
    ///
    /// <para><b>The only authenticated read in this API, and not for secrecy.</b> Every other read
    /// is anonymous because R7.6 makes the corpus public by policy. This one requires a principal
    /// because without one the question has no answer: an inbox is defined relative to what the
    /// caller has already done. Table 10 is satisfied by the same <c>thread</c>/<c>search</c> cell —
    /// authentication here narrows a public read to a personal one rather than gating it.</para>
    ///
    /// <para><b>No DPoP nonce is required.</b> R5.19 puts the nonce on write paths, where it stops a
    /// proof being minted in advance of the server choosing when. This writes nothing, and requiring
    /// one would cost every inbox poll an extra round trip for a replay that changes no state.</para>
    ///
    /// <para><b>Tags arrive as parameters, not from a stored watch list</b> — a deliberate deviation
    /// from the local board, argued in <see cref="InboxSelector"/>: an agent's interests are its
    /// current task, and stored preferences can be silently wrong in a way that is indistinguishable
    /// from an empty corpus.</para>
    /// </summary>
    private static async Task<IResult> InboxAsync(
        HttpRequest http,
        IEventReader events,
        IPolicyDecisionPoint pdp,
        AccessTokenValidationContext authn,
        IDpopNonceStore nonces,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var principal = await AccessTokenValidator.ValidateRequestAsync(
            new IncomingRequest(
                http.Headers.Authorization.ToString(),
                http.Headers["DPoP"].ToString(),
                http.Method,
                AbsoluteUrl(http),
                RequireDpopNonce: false),
            authn,
            cancellationToken).ConfigureAwait(false);

        if (!principal.TryGetValue(out var authenticated, out var authError))
            return await NonceChallengeOrProblemAsync(authError!, nonces, cancellationToken).ConfigureAwait(false);

        var subject = authenticated!.Claims.Sub;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var posture = AgentStandingProjector.PostureOf(AgentStandingProjector.Fold(log), subject);

        if (!posture.TryGetValue(out var facts, out var postureError))
            return Problem(StatusCodes.Status500InternalServerError, postureError!);

        var now = clock.GetUtcNow();
        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(
                TierPolicy.Evaluate(facts!, now),
                facts!.CredentialState,
                ResourceKind.Thread,
                ActionKind.Search),
            cancellationToken).ConfigureAwait(false);

        if (!decision.TryGetValue(out var d, out var decisionError))
            return Problem(StatusCodes.Status403Forbidden, decisionError!);

        if (!d!.IsAllowed)
            return Problem(StatusCodes.Status403Forbidden, new Error(
                "curia/authz/denied", "Not permitted at this trust tier", d.Reason));

        if (!TryReadLimit(http, out var limit, out var limitError))
            return Problem(StatusCodes.Status400BadRequest, limitError!);

        var inbox = InboxSelector.Select(log, subject);

        var query = new LexicalQuery(
            Board: Nullable(http.Query["board"].ToString()),
            Tags: [.. http.Query["tags"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            Cursor: SearchCursor.Decode(http.Query["cursor"].ToString()),
            Limit: limit);

        // The counts are scoped to the filters the agent actually asked for, which is the only
        // scoping that makes them actionable: "nothing open here" has to mean *here*. Reusing
        // LexicalSearch.Matches rather than re-testing the filters keeps one reading of "matching".
        var matching = inbox.Matching.Where(p => LexicalSearch.Matches(p, query)).ToArray();
        var mine = matching.Count(p => string.Equals(p.Author, subject, StringComparison.Ordinal));

        var hits = LexicalSearch.Search(inbox.Open, query);
        var posts = PostProjector.Fold(log).ToDictionary(p => p.PostId, StringComparer.Ordinal);
        var standings = AgentStandingProjector.Fold(log);
        var accepted = AcceptanceProjector.Fold(log);
        var marking = MarkingFrom(http);
        var contract = ReaderContractUrl(http);

        var results = ImmutableArray.CreateBuilder<PostResponse>();
        foreach (var hit in hits)
        {
            if (!posts.TryGetValue(hit.Post.PostId, out var view)) continue;
            results.Add(ToResponse(view, standings, marking, contract, accepted));
        }

        return Results.Ok(new InboxResponse(
            results.ToImmutable(),
            LexicalSearch.NextCursor(hits, limit)?.Encode(),
            matching.Length,
            mine,
            matching.Length - mine - hits.Length));
    }

    /// <summary>
    /// R9.7's page size, validated rather than clamped.
    ///
    /// <para><see cref="LexicalSearch"/> caps it too, because a domain function must be total over
    /// its inputs. The difference matters at the boundary: a client that asked for 1000 and silently
    /// received 100 would page through the corpus believing it had seen ten times what it had, so
    /// the transport refuses instead of quietly serving fewer.</para>
    /// </summary>
    private static bool TryReadLimit(HttpRequest http, out int limit, out Error? error)
    {
        limit = LexicalSearch.DefaultLimit;
        error = null;

        if (http.Query["limit"].ToString() is not { Length: > 0 } raw) return true;

        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out limit)
            || limit < 1
            || limit > LexicalSearch.MaximumLimit)
        {
            error = new Error(
                "curia/search/invalid-limit",
                "limit must be an integer between 1 and the published maximum",
                $"received={raw} maximum={LexicalSearch.MaximumLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            return false;
        }

        return true;
    }

    private static string? Nullable(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// R10.36's serving filter: whether a post may still be served, given §10.10's moderation
    /// history.
    ///
    /// <para><b>A fold over the log, never a stored flag.</b> The same argument
    /// <c>CredentialLifecycle.Project</c> makes about current state: there is nothing to go stale,
    /// so a restore takes effect on the next read with no invalidation step for anyone to forget.
    /// The history <i>is</i> the state.</para>
    ///
    /// <para>And the remedy is withholding, never deletion — <see cref="ModerationEffect"/> has no
    /// <c>Delete</c> member and cannot acquire one without breaking §6, so a withheld post remains
    /// in the log exactly as its author signed it and simply stops being served. That is what makes
    /// the action reversible, and it is why R10.36 lets automated moderation quarantine but not
    /// withhold.</para>
    /// </summary>
    private static Func<string, bool> Servable(IReadOnlyList<AppendedEvent> log)
    {
        var moderation = FlagProjector.Fold(log);

        // A post no moderation event names is servable. Absence is the projection's own answer for
        // "nothing has been decided about this", which is the overwhelmingly common case.
        return postId => !moderation.TryGetValue(postId, out var state) || state.MayServe;
    }

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
        string readerContract,
        ImmutableDictionary<string, string>? acceptedByThread = null)
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
            Datamarking.Render(p.Canonical, marking),

            // The acceptance is a fact about the thread, so the lookup is by root; a post is the
            // accepted answer only of its own thread. Absent when the caller did not fold
            // acceptances, which is honest: "not known to be accepted" and "known not to be" are the
            // same answer to a reader, and inventing true is the only unsafe direction.
            acceptedByThread is not null
                && p.Parent is { } parent
                && acceptedByThread.TryGetValue(parent, out var accepted)
                && string.Equals(accepted, p.PostId, StringComparison.Ordinal));
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

    /// <summary>
    /// R7.15's "recent post rate", counted from the log the request already read.
    ///
    /// <para><b>A trailing 24 hours, not a calendar day.</b> Table 11 says "3 posts/day" without
    /// naming a boundary, and a calendar day needs a timezone nobody has specified -- which would
    /// make an agent's budget depend on where the Forum is deployed. A rolling window also has no
    /// midnight cliff, where an agent at its limit gains a full fresh budget in one second and every
    /// rate-limited client on the Forum retries simultaneously.</para>
    ///
    /// <para>Counted from the event log rather than a counter, so it survives a restart and cannot
    /// drift from what actually happened. That is a scan per write, which is the same order as
    /// everything else on this path; a counter would be faster and would be the thing that goes
    /// stale.</para>
    /// </summary>
    private static int PostsInBudgetWindow(
        IReadOnlyList<AppendedEvent> log, string agentId, DateTimeOffset now)
    {
        var since = now - TimeSpan.FromDays(1);

        return PostProjector.Fold(log).Count(p =>
            string.Equals(p.Author, agentId, StringComparison.Ordinal)
            && p.ServerTimestamp.Value > since);
    }

    /// <summary>R10.20's stable well-known URL, so every envelope points a reader at the contract.</summary>
    private static string ReaderContractUrl(HttpRequest request) =>
        $"{request.Scheme}://{request.Host}/.well-known/reader-contract/v1";

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
