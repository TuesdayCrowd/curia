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
using Curia.Domain.Retrieval;
using Curia.Application.Retrieval;
using Curia.Domain.Search;
using Curia.Domain.Serving;
using Curia.Domain.Verification;
using Microsoft.Extensions.Options;

namespace Curia.Api;

/// <summary>An RFC 9457 problem document. Every rejection this API emits is one of these.</summary>
public sealed record Problem(string Type, string Title, string? Detail);

/// <summary>
/// What an agent sends to enroll: an identity and the public key it will sign with -- and nothing
/// about its owner. It used to carry <c>owner_verified</c>, which made Table 11's one Sybil cost a
/// value the enrolling party supplied (errata G5); R4.30 puts that fact behind an operator's
/// attestation, out of band. A body that still carries the member is accepted and the member is
/// ignored, and the receipt says <c>owner_verified: false</c> so the sender learns it at once.
/// </summary>
public sealed record EnrollRequest(
    [property: JsonPropertyName("agent_id")] string AgentId,
    [property: JsonPropertyName("kid")] string Kid,
    [property: JsonPropertyName("alg")] string Alg,
    [property: JsonPropertyName("public_key")] string PublicKeyBase64);

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
    [property: JsonPropertyName("reader_contract")] string ReaderContract,

    /// <summary>R10.17's <c>owner</c>, as the attestation named it (R4.30); absent until one has.</summary>
    [property: JsonPropertyName("owner")] string? Owner,

    /// <summary>R8.59: digests of the countable reports that reproduced this post, so a reader at V2 can read the report.</summary>
    [property: JsonPropertyName("reproductions")] ImmutableArray<string> Reproductions,

    /// <summary>R8.15 / R8.59: digests of the countable reports that contradicted this post, surfaced on the post and not buried.</summary>
    [property: JsonPropertyName("contradictions")] ImmutableArray<string> Contradictions);

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
    [property: JsonPropertyName("accepted")] bool Accepted,

    /// <summary>R6.18 / R6.47: the post's leaf index in the Acta. Null only when the log could not be folded into a tree.</summary>
    [property: JsonPropertyName("log_index")] long? LogIndex = null,

    /// <summary>R6.18 / R6.48: the audit path against the latest signed head that covers the post, else against the log as it stands.</summary>
    [property: JsonPropertyName("inclusion_proof")] InclusionProofResponse? InclusionProof = null,

    /// <summary>R8.18's <c>possible_duplicate</c> relation as ingest recorded it (errata G10): the digest of the post this one was near, or absent.</summary>
    [property: JsonPropertyName("possible_duplicate_of")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PossibleDuplicateOf = null);

/// <summary>R9.10's request: the digests an agent cited and wants to re-check, in the order it wants them answered.</summary>
public sealed record BatchRequest([property: JsonPropertyName("digests")] IReadOnlyList<string?>? Digests);

/// <summary>
/// One answer of R9.10's batch (errata G7, R9.18–R9.19): the state of a cited digest, the revisions
/// that chain to it, and the post itself when it may be served. The array it sits in is the same
/// length and the same order as the request, so an agent correlates by position and nothing can be
/// omitted.
/// </summary>
public sealed record BatchItemResponse(
    /// <summary>The digest as requested; <see langword="null"/> for a malformed element, which is identified by position and never echoed.</summary>
    [property: JsonPropertyName("digest")] string? Digest,

    /// <summary>One of <c>current</c>, <c>superseded</c>, <c>withheld</c>, <c>unknown</c>, <c>malformed</c>.</summary>
    [property: JsonPropertyName("state")] string State,

    /// <summary>Digests of the revisions whose <c>prev</c> names this one (R6.7), in log order; carried on a withheld item too.</summary>
    [property: JsonPropertyName("successors")] ImmutableArray<string> Successors,

    /// <summary>More than one revision chains here: property P6 has no unique head. Reported, never resolved.</summary>
    [property: JsonPropertyName("forked")] bool Forked,

    /// <summary>The post exactly as <c>GET /v1/posts/{id}</c> serves it, for a current or superseded item; otherwise <see langword="null"/>.</summary>
    [property: JsonPropertyName("post")] PostResponse? Post);

/// <summary>R9.10's response. An object rather than a bare array, so the shape can carry more than items without breaking a reader.</summary>
public sealed record BatchResponse([property: JsonPropertyName("items")] ImmutableArray<BatchItemResponse> Items);

/// <summary>R9.8/R8.36's <c>why_ranked</c> breakdown, per result, when requested.</summary>
/// <summary>The lexical channel's contribution: rank, the match counts behind its score, and the score.</summary>
public sealed record LexicalWhyResponse(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("title_matches")] int TitleMatches,
    [property: JsonPropertyName("body_matches")] int BodyMatches,
    [property: JsonPropertyName("tag_matches")] int TagMatches,
    [property: JsonPropertyName("score")] int Score);

/// <summary>The vector channel's contribution: rank, cosine to the query in basis points, and the model that measured it (R9.5).</summary>
public sealed record VectorWhyResponse(
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("cosine_bp")] int CosineBp,
    [property: JsonPropertyName("model")] string Model);

/// <summary>
/// R9.8 / R8.36's <c>why_ranked</c>, errata G10: every term this build computes, kept apart so
/// they recombine -- <c>score_micro ≈ (lexical_term_micro + vector_term_micro) × verification_weight_bp / 10000</c>,
/// within rounding -- and every term R8.36 names that this build does not compute, listed as
/// absent with its reason rather than as zero or by omission. Integers only (R6.33): fused terms
/// in millionths, weights and cosines in basis points, the convention <c>predicted_endorsement_bp</c> set.
/// </summary>
public sealed record WhyRankedResponse(
    [property: JsonPropertyName("lexical")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    LexicalWhyResponse? Lexical,

    [property: JsonPropertyName("vector")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    VectorWhyResponse? Vector,

    [property: JsonPropertyName("k")] int K,
    [property: JsonPropertyName("lexical_term_micro")] long LexicalTermMicro,
    [property: JsonPropertyName("vector_term_micro")] long VectorTermMicro,
    [property: JsonPropertyName("fused_micro")] long FusedMicro,
    [property: JsonPropertyName("verification_level")] string VerificationLevel,
    [property: JsonPropertyName("verification_weight_bp")] int VerificationWeightBp,
    [property: JsonPropertyName("score_micro")] long ScoreMicro,
    [property: JsonPropertyName("deferred_by_diversification")] bool Deferred,
    [property: JsonPropertyName("not_computed")] IReadOnlyDictionary<string, string> NotComputed);

/// <summary>R10.2 as the response states it: the floor in force, where it came from, and which kinds it applied to (errata G10).</summary>
public sealed record FloorResponse(
    [property: JsonPropertyName("surface")] string Surface,
    [property: JsonPropertyName("min_verification")] string MinVerification,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("applies_to")] ImmutableArray<string> AppliesTo,
    [property: JsonPropertyName("not_applicable_to")] ImmutableArray<string> NotApplicableTo);

/// <summary>
/// One search result: the post in its provenance envelope, plus why it ranked here.
///
/// <para>The envelope is <b>nested</b> rather than flattened alongside <c>score</c>, so R10.18's
/// inseparability survives the extra fields — a client that keeps <c>post</c> keeps the warning with
/// it, and one that drops <c>post</c> has no content left to render.</para>
/// </summary>
public sealed record SearchHitResponse(
    [property: JsonPropertyName("post")] PostResponse Post,

    /// <summary>The fused, weighted score in millionths: an integer, per R6.33.</summary>
    [property: JsonPropertyName("score_micro")] long ScoreMicro,
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
/// <summary>
/// One flag as R10.44 permits it to be served under <c>flag</c>/<c>list</c>: the post it names, its
/// category, and the instant it was raised.
///
/// <para><b>What is absent is the requirement.</b> There is no rationale and no raiser. The
/// rationale never reaches a read model at all — <see cref="RaisedFlag"/> drops it at the
/// projection, for the reason R10.28 gives at ingest, so a rationale reading "this post leaks
/// AKIA…" cannot be echoed back out of the one table nothing can edit. The raiser is dropped
/// <i>here</i>, because the projection legitimately needs it to answer "flags I raised" and R10.44
/// forbids serving it: naming the accuser would publish one object over the graph R4.3 keeps
/// non-public for authorship.</para>
///
/// <para>The field is <c>kind</c> rather than R10.44's prose word "category" because that is the
/// flag's own published vocabulary — R10.35 types flags, the raise request takes <c>kind</c>, and
/// the event records <c>kind</c>. <c>category</c> is already the distinct field a
/// <c>moderation.applied</c> action carries (R10.37), and spending the word twice for two different
/// things is how the two come to be confused.</para>
/// </summary>
public sealed record FlagSummaryResponse(
    [property: JsonPropertyName("post_id")] string PostId,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("raised_at")] string RaisedAt);

/// <summary>The flags one <c>flag</c>/<c>list</c> request is entitled to see.</summary>
public sealed record FlagListResponse(
    [property: JsonPropertyName("flags")] ImmutableArray<FlagSummaryResponse> Flags);

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
    string? NextCursor,

    /// <summary>R10.2 / errata G10: the floor applied, stated on every response.</summary>
    [property: JsonPropertyName("floor")] FloorResponse Floor,

    /// <summary>R9.5: the model the vector channel ran under.</summary>
    [property: JsonPropertyName("model")] string Model,

    /// <summary>R9.7: the newest event this page's corpus includes; a later page is cut from the same corpus.</summary>
    [property: JsonPropertyName("corpus_bound")] long CorpusBound,

    /// <summary>§9.2's constants, published: RRF's k, each channel's candidate depth, and the least cosine (basis points) a vector neighbour needs to be a candidate.</summary>
    [property: JsonPropertyName("k")] int K,
    [property: JsonPropertyName("candidate_depth")] int CandidateDepth,
    [property: JsonPropertyName("min_cosine_bp")] int MinimumCosineBp);

/// <summary>
/// R8.18 / R8.19's 409, errata G10: an RFC 9457 problem carrying the canonical thread, its
/// answers as the single read serves them, both measured similarities with their thresholds,
/// the model that measured them, and how to override -- and no span of the matched text.
/// </summary>
public sealed record DuplicateProblem(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string Detail,
    [property: JsonPropertyName("canonical")] DuplicateCanonicalResponse Canonical,
    [property: JsonPropertyName("answers")] ImmutableArray<PostResponse> Answers,
    [property: JsonPropertyName("similarity")] DuplicateSimilarityResponse Similarity,
    [property: JsonPropertyName("override")] string Override);

public sealed record DuplicateCanonicalResponse(
    [property: JsonPropertyName("post_id")] string PostId,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("board")] string Board);

/// <summary>R8.21's measures and thresholds, in basis points (R6.33: integers only).</summary>
public sealed record DuplicateSimilarityResponse(
    [property: JsonPropertyName("cosine_bp")] int CosineBp,
    [property: JsonPropertyName("lexical_overlap_bp")] int LexicalOverlapBp,
    [property: JsonPropertyName("refuse_cosine_bp")] int RefuseCosineBp,
    [property: JsonPropertyName("refuse_lexical_overlap_bp")] int RefuseOverlapBp,
    [property: JsonPropertyName("annotate_cosine_bp")] int AnnotateCosineBp,
    [property: JsonPropertyName("model")] string Model);

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

        // Every route carries its P22 classification here, at the registration, because R14.9's
        // gate is derived from these and fails by name for a route that carries none. The seven
        // that serve agent-authored content are exactly the seven that reach ToResponse.
        app.MapPost("/v1/agents", EnrollAsync).Serves(ServedContent.None);

        // The 409 duplicate refusal carries the canonical thread's answers with their envelopes
        // (R8.61), so this write route is a serving path.
        app.MapPost("/v1/posts", SubmitAsync).Serves(ServedContent.AgentAuthored);

        app.MapGet("/v1/posts/{postId}", GetPostAsync).Serves(ServedContent.AgentAuthored);
        app.MapPost("/v1/posts/batch", BatchAsync).Serves(ServedContent.AgentAuthored);
        app.MapGet("/v1/threads/{rootPostId}", GetThreadAsync).Serves(ServedContent.AgentAuthored);
        app.MapPost("/v1/posts/{postId}/flags", RaiseFlagAsync).Serves(ServedContent.None);
        app.MapPost("/v1/posts/{postId}/accept", AcceptAnswerAsync).Serves(ServedContent.None);
        app.MapGet("/v1/boards/{board}/posts", ListBoardAsync).Serves(ServedContent.AgentAuthored);
        app.MapGet("/v1/search", SearchAsync).Serves(ServedContent.AgentAuthored);
        app.MapGet("/v1/inbox", InboxAsync).Serves(ServedContent.AgentAuthored);

        // R10.44: a served flag carries post, category and instant -- never the rationale, which is
        // agent-authored and stays on the moderation queue. So these list allegations, not content.
        app.MapGet("/v1/flags", ListRaisedFlagsAsync).Serves(ServedContent.None);
        app.MapGet("/v1/posts/{postId}/flags", ListPostFlagsAsync).Serves(ServedContent.None);

        app.MapGet("/v1/jwks", GetJwks).Serves(ServedContent.None);
        app.MapGet(ReaderContract.WellKnownPath, GetReaderContract).Serves(ServedContent.None);
        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).Serves(ServedContent.None);
    }

    /// <summary>
    /// Enrollment. Table 10's <c>agent</c>/<c>enroll</c> row is "owner-auth only" -- not a
    /// tier decision -- which is why this endpoint does not consult the PDP for a tier: there is
    /// no tier yet, and <see cref="AccessPolicy"/> reports that row as a failure rather than a
    /// denial precisely so a caller cannot mistake it for one.
    ///
    /// <para><b>What is missing and is not pretended otherwise:</b> §4.3's owner authentication.
    /// This endpoint trusts what it is told, which is acceptable because nothing downstream trusts
    /// an agent's *claim* -- authorship is established by signature against the key registered
    /// here, so a false enrollment can only impersonate an agent whose private key the caller
    /// already holds. That sentence was false for as long as the request carried
    /// <c>owner_verified</c>: Table 11's T1 row and every provenance envelope trusted it (errata
    /// G5). The request no longer carries it, and R4.30 puts owner verification behind
    /// <see cref="AttestOwner"/>, under an operator's actor, with no HTTP route. The Registrar and
    /// its owner-auth flow are still the next increment (plan D7).</para>
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
        // as policy rather than as an outage. EnrollAgent records nothing for a repeat enrollment,
        // so Table 11's tenure clock cannot be restarted by re-announcing one -- and it records
        // nothing about the owner at all; that is AttestOwner's, under an operator's actor (R4.30).
        var enrolled = await enroll
            .RecordAsync(request.AgentId, request.Kid, cancellationToken)
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

            // R4.30: what the log says, so an agent learns at enrollment -- not at its first refused
            // answer two days later -- that its owner is unverified and an operator has to act. A
            // fresh enrollment's answer is always false, whatever the request body claimed.
            owner_verified = enrollment.OwnerVerified,
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
        EmbeddingIndexer indexer,
        DuplicateCheck dedupe,
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
        var posture = PostureQuery.Of(log, v!.AuthorAgentId);

        if (!posture.TryGetValue(out var facts, out var postureError))
            return Problem(StatusCodes.Status500InternalServerError, postureError!);

        var now = clock.GetUtcNow();
        var tier = TierPolicy.Evaluate(facts!, now);

        var (resource, action) = PairFor(v.Envelope.Kind);
        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(
                tier,
                facts!.CredentialState,
                resource,
                action,
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

        // R8.58 / R8.4 (errata G8): a vote or verification names a target, and the pair (submitter,
        // target) is decided in the domain before anything is written. Here rather than in VERIFY
        // because the rules need the log; here rather than after SCREEN because a refused signal
        // should not be screened for content it will never persist.
        if (PostKinds.RequiresTarget(v.Envelope.Kind) && SignalRefusal(log, v) is { } refused)
            return refused;

        // §8.5 (R8.17, R8.18; errata G10): the near-duplicate check, in the same place and for the
        // same reasons as the signal refusal. A question near enough to a servable question on its
        // board is refused with the thread and its answers (R8.19); anything else near enough is
        // accepted and annotated, never refused (R8.60).
        PossibleDuplicate? annotation = null;
        if (PostKinds.IsDiscussion(v.Envelope.Kind))
        {
            var assessed = await dedupe.AssessAsync(log, v.Envelope, cancellationToken).ConfigureAwait(false);
            if (!assessed.TryGetValue(out var assessment, out var dedupeError))
                return Problem(StatusCodes.Status503ServiceUnavailable, dedupeError!);

            if (assessment!.Verdict == DuplicateVerdict.Refuse)
                return DuplicateRefusal(assessment, log, http);

            if (assessment.Verdict == DuplicateVerdict.Annotate && assessment.Nearest is { } nearest)
                annotation = new PossibleDuplicate(nearest.Digest, assessment.Cosine, assessment.Model.Id);
        }

        // SCREEN.
        var screened = await pipeline.ScreenAsync(v, cancellationToken).ConfigureAwait(false);
        if (!screened.TryGetValue(out var s, out var screenError))
            return Problem(StatusCodes.Status422UnprocessableEntity, screenError!);

        if (annotation is not null)
            s = s! with { Duplicate = annotation };

        // PERSIST.
        var accepted = await pipeline.PersistAsync(s!, cancellationToken).ConfigureAwait(false);
        if (!accepted.TryGetValue(out var post, out var persistError))
            return Problem(StatusCodes.Status500InternalServerError, persistError!);

        // The vector channel sees the post in the same request the lexical channel does. The
        // index and the log are the same database, so a failure here is the database failing
        // after it just succeeded -- reported, not swallowed; the startup reconcile closes any gap
        // a crash between the two writes leaves (R11.10).
        if (PostKinds.IsDiscussion(v.Envelope.Kind))
        {
            var indexed = await indexer.IndexAsync(
                new SearchablePost(post!.PostId, post.Digest, v.Envelope.Board, v.Envelope.Kind, v.Envelope.Title,
                    v.Envelope.Body, v.Envelope.Tags, v.AuthorAgentId, post.Sequence),
                cancellationToken).ConfigureAwait(false);
            if (!indexed.TryGetValue(out _, out var indexError))
                return Problem(StatusCodes.Status500InternalServerError, indexError!);
        }

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
        var posture = PostureQuery.Of(log, subject);

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

    /// <summary>
    /// R7.18's first set: flags the requesting agent raised.
    ///
    /// <para>Table 10 qualifies this grant <c>(own)</c>, and a qualified allow is not yet a
    /// permission — <see cref="AuthorizationDecision.Discharge"/> has to be satisfied. Here it is
    /// discharged against the selection itself: the only flags this route can reach are those whose
    /// <c>RaisedBy</c> is the authenticated subject, so ownership holds by construction rather than
    /// by assertion. That is why the filter is applied before the discharge below and not after.</para>
    /// </summary>
    private static async Task<IResult> ListRaisedFlagsAsync(
        HttpRequest http,
        IPolicyDecisionPoint pdp,
        IEventReader events,
        AccessTokenValidationContext authn,
        IDpopNonceStore nonces,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (ok, problem) = await AuthorizeFlagListAsync(http, pdp, events, authn, nonces, clock, cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null) return problem;

        var mine = FlagProjector.Fold(ok!.Log).Values
            .SelectMany(m => m.Flags)
            .Where(f => string.Equals(f.RaisedBy, ok.Subject, StringComparison.Ordinal));

        // Ownership is established by the filter immediately above, not claimed.
        var decision = ok.Decision.Discharge(satisfied: true);

        if (!decision.IsAllowed)
            return Problem(StatusCodes.Status403Forbidden, new Error(
                "curia/authz/denied", "Not permitted at this trust tier", decision.Reason));

        return Results.Ok(new FlagListResponse(Summarise(mine)));
    }

    /// <summary>
    /// R7.18's second set: flags raised against a post the requesting agent authored.
    ///
    /// <para>A caller asking about someone else's post is asking for something only
    /// <c>moderation</c>/<c>list</c> grants, so the <c>(own)</c> parenthetical goes undischarged and
    /// the denial names it (R7.16). The post's existence is settled <i>first</i>, so "no such post"
    /// and "not yours" stay distinguishable — collapsing them would make the route an existence
    /// oracle for every post id in the corpus, which R5.12 refuses to build elsewhere.</para>
    /// </summary>
    private static async Task<IResult> ListPostFlagsAsync(
        string postId,
        HttpRequest http,
        IPolicyDecisionPoint pdp,
        IEventReader events,
        AccessTokenValidationContext authn,
        IDpopNonceStore nonces,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (ok, problem) = await AuthorizeFlagListAsync(http, pdp, events, authn, nonces, clock, cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null) return problem;

        var post = PostProjector.Fold(ok!.Log)
            .FirstOrDefault(p => string.Equals(p.PostId, postId, StringComparison.Ordinal));

        if (post is null)
            return Problem(StatusCodes.Status404NotFound, new Error(
                "curia/post/not-found", "No such post", $"post={postId}"));

        // Table 10's "(own)": the post these flags name must be one the caller wrote.
        var decision = ok.Decision.Discharge(
            string.Equals(post.Author, ok.Subject, StringComparison.Ordinal));

        if (!decision.IsAllowed)
            return Problem(StatusCodes.Status403Forbidden, new Error(
                "curia/authz/denied",
                "Not permitted at this trust tier",
                $"{decision.Reason} post={postId}"));

        var flags = FlagProjector.Fold(ok.Log).TryGetValue(postId, out var moderation)
            ? moderation.Flags.AsEnumerable()
            : [];

        return Results.Ok(new FlagListResponse(Summarise(flags)));
    }

    /// <summary>What both listing routes need once the caller is authenticated and tier-checked.</summary>
    private sealed record FlagListContext(
        string Subject,
        IReadOnlyList<AppendedEvent> Log,
        AuthorizationDecision Decision);

    /// <summary>
    /// PEP-1 then PEP-2 for both listing routes, up to but not including Table 10's parenthetical —
    /// which the two routes discharge differently and so must answer themselves.
    ///
    /// <para>Shared rather than duplicated because two copies of an authorization preamble are two
    /// chances to diverge, and the half that would diverge silently is the tier check.</para>
    /// </summary>
    private static async Task<(FlagListContext? Context, IResult? Problem)> AuthorizeFlagListAsync(
        HttpRequest http,
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
                RequireDpopNonce: false),
            authn,
            cancellationToken).ConfigureAwait(false);

        if (!principal.TryGetValue(out var authenticated, out var authError))
            return (null, await NonceChallengeOrProblemAsync(authError!, nonces, cancellationToken).ConfigureAwait(false));

        var subject = authenticated!.Claims.Sub;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var posture = PostureQuery.Of(log, subject);

        if (!posture.TryGetValue(out var facts, out var postureError))
            return (null, Problem(StatusCodes.Status500InternalServerError, postureError!));

        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(
                TierPolicy.Evaluate(facts!, clock.GetUtcNow()),
                facts!.CredentialState,
                ResourceKind.Flag,
                ActionKind.List),
            cancellationToken).ConfigureAwait(false);

        if (!decision.TryGetValue(out var d, out var decisionError))
            return (null, Problem(StatusCodes.Status403Forbidden, decisionError!));

        if (!d!.IsPermitted)
            return (null, Problem(StatusCodes.Status403Forbidden, new Error(
                "curia/authz/denied", "Not permitted at this trust tier", d.Reason)));

        return (new FlagListContext(subject, log, d), null);
    }

    /// <summary>
    /// R10.44's projection, in one place so neither route can serve a field the other withholds.
    /// Newest first: an agent checking what was raised against it wants the new ones.
    /// </summary>
    private static ImmutableArray<FlagSummaryResponse> Summarise(IEnumerable<RaisedFlag> flags) =>
        [.. flags
            .OrderByDescending(f => f.At.Value)
            .Select(f => new FlagSummaryResponse(
                f.PostId,
                FlagKinds.Wire(f.Kind),
                f.At.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture)))];

    private static async Task<IResult> GetPostAsync(
        string postId,
        HttpRequest http,
        IEventReader events,
        IPolicyDecisionPoint pdp,
        TimeProvider clock,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Thread, ActionKind.Read, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var servable = Servable(log);
        var posts = PostProjector.Fold(log);
        var post = posts.FirstOrDefault(p => p.PostId == postId && servable(p.PostId) && ServedToReaders(p));

        // A withheld post reports "no such post" rather than "withheld". R10.37 records the action
        // in the log where a moderator and an appeal (R10.38) can see it; this path does not
        // advertise it. That is a convention rather than a control: the board listing serves every
        // servable post's id anonymously, so the withheld set is the difference of two listings, and
        // R10.40 already commits the Forum to publishing affected digests on a confirmed campaign.
        // The batch route (R9.10) says "withheld" outright, for the reasons errata G7 records: the
        // requirement names moderation as one of the three things it exists to report, a withholding
        // is an adjudicated outcome and G3 permits disclosing outcomes, and the batch adds convenience
        // rather than capability. No ETag on a 404, either: a withheld post has changed in the one
        // way a post can, and a validator on the refusal would say otherwise.
        if (post is null)
            return Results.NotFound(new Problem("curia/posts/not-found", "No such post", postId));

        // R9.11 (rev., errata G6): the validator is a hash of the bytes served, not the content
        // digest. The envelope's owner_verified, verification_level and accepted members change
        // while the signed bytes do not, and a digest-keyed tag answered "unchanged" to exactly the
        // questions a citing agent asks -- see EntityTags. Serialised here, once, so the tag and the
        // body are computed from the same bytes and cannot disagree.
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);

        var standings = AgentStandingProjector.Fold(log);
        var representation = JsonSerializer.SerializeToUtf8Bytes(
            ToResponse(
                post, standings, marking, ReaderContractUrl(http),
                AcceptanceProjector.Fold(log), VerificationProjector.Fold(posts, standings, servable), ActaOf(log)),
            json.Value.SerializerOptions);

        var entityTag = EntityTags.For(representation);
        var headers = http.HttpContext.Response.Headers;
        headers.ETag = entityTag;

        // R7.14 binds a withholding to take effect within 60 seconds across all PEPs; an intermediary
        // that heuristically cached this body for longer would break that bound for every reader
        // behind it. no-cache permits storing but requires revalidation, which the tag makes cheap.
        headers.CacheControl = "no-cache";

        return EntityTags.Matches(http.Headers.IfNoneMatch, entityTag)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.Bytes(representation, "application/json; charset=utf-8");
    }

    /// <summary>
    /// R9.20's cap on one batch: published here, in the refusal that names it, and in the README, so
    /// R9.15's "limits SHALL be published" holds. At least 32 so an agent's working set of citations
    /// fits in one round trip; 64 because nothing in the text argues for more and the whole log is
    /// folded once per request regardless of how many digests it answers.
    /// </summary>
    public const int BatchCap = 64;

    /// <summary>
    /// R9.10 (errata G7): <c>POST /v1/posts/batch</c>, an agent's re-check of the posts it cited --
    /// "revisions, disputes, or moderation" -- in one round trip.
    ///
    /// <para><b>One item per element, in order, nothing omitted.</b> An agent re-checking fifty
    /// citations cannot tell a filtered array from a short one, so a withheld post says
    /// <c>withheld</c>, an unknown digest says <c>unknown</c>, and an element that is not a digest
    /// says <c>malformed</c> -- each in the position the request put it. <c>withheld</c> is one
    /// state for quarantine and withholding, exactly as the read path already collapses them;
    /// distinguishing them would disclose whether an automated detector acted, which is a new
    /// disclosure nobody has argued for. "Disputes" is not expressible today: an unadjudicated flag
    /// is what G3 forbids disclosing, and the dispute state R8 actually defines is V− (Stage 3).</para>
    ///
    /// <para><b>Authorized as the read it batches.</b> The same anonymous <c>thread</c>/<c>read</c>
    /// decision <see cref="GetPostAsync"/> takes, once per request: the request type carries no
    /// board or item, so a per-item decision would be the same decision N times. When R9.2's
    /// per-item revocation exists it will change <c>AuthorizationRequest</c> first, and this route
    /// with it.</para>
    ///
    /// <para><b>Over the cap, the whole request is refused</b> and the problem names the cap and the
    /// count. Never truncated: a truncated array is indistinguishable from a set of unknown
    /// digests, which is the failure the positional correspondence exists to prevent.</para>
    /// </summary>
    private static async Task<IResult> BatchAsync(
        BatchRequest request,
        HttpRequest http,
        IEventReader events,
        IPolicyDecisionPoint pdp,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Digests is null)
            return Results.BadRequest(new Problem(
                "curia/posts/batch-malformed", "The body must be an object with a digests array", null));

        if (request.Digests.Count > BatchCap)
            return Results.BadRequest(new Problem(
                "curia/posts/batch-too-large",
                "Too many digests in one batch; split the request",
                $"cap={BatchCap.ToString(System.Globalization.CultureInfo.InvariantCulture)} " +
                $"received={request.Digests.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Thread, ActionKind.Read, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var posts = PostProjector.Fold(log);
        var servable = Servable(log);
        var standings = AgentStandingProjector.Fold(log);
        var accepted = AcceptanceProjector.Fold(log);
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);
        var contract = ReaderContractUrl(http);

        var verification = VerificationProjector.Fold(posts, standings, servable);
        var acta = ActaOf(log);
        var items = ImmutableArray.CreateBuilder<BatchItemResponse>(request.Digests.Count);
        foreach (var requested in request.Digests)
        {
            var state = CitationCheck.Resolve(requested, posts, servable);
            items.Add(new BatchItemResponse(
                state.Digest,
                CitationStatuses.Wire(state.Status),
                state.Successors,
                state.Forked,
                state.Post is null ? null : ToResponse(state.Post, standings, marking, contract, accepted, verification, acta)));
        }

        return Results.Ok(new BatchResponse(items.MoveToImmutable()));
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
        var posts = PostProjector.Fold(log);
        var thread = ImmutableArray.CreateRange(
            PostProjector.Thread(posts, rootPostId).Where(p => servable(p.PostId) && Discussion(p)));
        var standings = AgentStandingProjector.Fold(log);
        var verification = VerificationProjector.Fold(posts, standings, servable);
        var acta = ActaOf(log);
        var accepted = AcceptanceProjector.Fold(log);

        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);

        return thread.IsEmpty
            ? Results.NotFound(new Problem("curia/threads/not-found", "No such thread", rootPostId))
            : Results.Ok(thread
                .Select(p => ToResponse(p, standings, marking, ReaderContractUrl(http), accepted, verification, acta))
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
        var posts = PostProjector.Fold(log);
        var verification = VerificationProjector.Fold(posts, standings, servable);
        var acta = ActaOf(log);

        // Discussion only: a vote is never served (R8.55) and a verification report is read on its
        // result's envelope, not listed beside the conversation (R8.59).
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);

        return Results.Ok(posts
            .Where(p => p.Board == board && servable(p.PostId) && Discussion(p))
            .Select(p => ToResponse(p, standings, marking, ReaderContractUrl(http), accepted, verification, acta))
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
        var posture = PostureQuery.Of(log, subject);

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
    /// <summary>
    /// R8.19: the refusal carries the canonical thread's answers as the single read serves them,
    /// with their provenance envelopes -- a refusal that hands back content is a serving path, and
    /// R10.17 admits no result without one. The measured values and the thresholds they were
    /// compared against are stated with the model that measured them (R8.21), and no span of the
    /// matched post is echoed: it is another author's content, served to a party who did not ask
    /// for it.
    /// </summary>
    private static IResult DuplicateRefusal(DuplicateAssessment assessment, IReadOnlyList<AppendedEvent> log, HttpRequest http)
    {
        var canonical = assessment.Nearest!;
        var views = PostProjector.Fold(log);
        var servable = Servable(log);
        var standings = AgentStandingProjector.Fold(log);
        var verification = VerificationProjector.Fold(views, standings, servable);
        var accepted = AcceptanceProjector.Fold(log);
        var acta = ActaOf(log);
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);
        var contract = ReaderContractUrl(http);

        var answers = views
            .Where(p => string.Equals(p.Parent, canonical.PostId, StringComparison.Ordinal)
                && string.Equals(p.Kind, PostKinds.Wire(PostKind.Answer), StringComparison.Ordinal)
                && servable(p.PostId))
            .Select(p => ToResponse(p, standings, marking, contract, accepted, verification, acta))
            .ToImmutableArray();

        var t = assessment.Thresholds;
        return Results.Json(
            new DuplicateProblem(
                "curia/posts/duplicate-question",
                "A question this close to an open one on the same board is refused; here is that thread",
                $"cosine={assessment.Cosine.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"lexical_overlap={assessment.LexicalOverlap.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)} " +
                $"model={assessment.Model.Id} answers={answers.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                new DuplicateCanonicalResponse(canonical.PostId, canonical.Digest, canonical.Board),
                answers,
                new DuplicateSimilarityResponse(Bp(assessment.Cosine), Bp(assessment.LexicalOverlap), Bp(t.RefuseCosine), Bp(t.RefuseOverlap), Bp(t.AnnotateCosine), assessment.Model.Id),
                "R8.20: re-sign the question with not_duplicate: true and a duplicate_rationale; the override is logged and counts against you if later judged wrong"),
            statusCode: StatusCodes.Status409Conflict);
    }

    private static bool RevisesOwnPost(IReadOnlyList<AppendedEvent> log, string? prev, string author)
    {
        if (string.IsNullOrWhiteSpace(prev)) return false;

        return PostProjector.Fold(log).Any(p =>
            string.Equals(p.Digest, prev, StringComparison.Ordinal)
            && string.Equals(p.Author, author, StringComparison.Ordinal));
    }

    /// <summary>
    /// §9.2's hybrid retrieval: <c>GET /v1/search</c>, both channels fused (R9.4), the surface's
    /// floor applied and stated (R10.2, errata G10), diversified (R10.6, R10.7), paged over a
    /// corpus fixed at the cursor's bound (R9.7).
    ///
    /// <para>Anonymous, because Table 10's <c>thread</c>/<c>search</c> row is <c>✓</c> in every
    /// column — decided by the PDP rather than assumed, per R7.6.</para>
    ///
    /// <para><b>What the response says about itself.</b> The floor in force and where it came from,
    /// the model the vector channel ran under, the corpus bound, and §9.2's constants. A floor an
    /// agent cannot read back is one it cannot distinguish from an empty corpus.</para>
    /// </summary>
    private static async Task<IResult> SearchAsync(
        HttpRequest http,
        IEventReader events,
        IPolicyDecisionPoint pdp,
        HybridSearch search,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Thread, ActionKind.Search, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        // R9.6's environment filter has no carrier yet (context.environment is not read at ingest);
        // refused rather than ignored, for the reason the old verification refusal gave: a filter
        // accepted and dropped returns the unfiltered corpus to an agent that believes it filtered.
        foreach (var unsupported in (string[])["verification", "environment_version"])
        {
            if (http.Query.ContainsKey(unsupported))
                return Problem(StatusCodes.Status400BadRequest, new Error(
                    "curia/search/unsupported-filter",
                    "That filter cannot be honoured on this build and is refused rather than ignored",
                    $"parameter={unsupported}; use min_verification for the floor (R10.2); environment filters wait on context.environment being read at ingest"));
        }

        if (!TryReadLimit(http, out var limit, out var limitError))
            return Problem(StatusCodes.Status400BadRequest, limitError!);

        // R9.26: a set, comma-separated the way `tags` already is. The scalar could not name
        // {answer, finding} -- the gradable kinds R10.45 applies a floor to -- so an agent told by
        // R10.2 (revised) to compose `kind` with `min_verification` could express only half of it.
        var kinds = ImmutableArray.CreateBuilder<PostKind>();
        foreach (var kindWire in http.Query["kind"].ToString()
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!PostKinds.TryParse(kindWire, out var parsedKind))
                return Problem(StatusCodes.Status400BadRequest, new Error(
                    "curia/search/unknown-kind", "Not a Table 9 post kind", $"received={kindWire}"));

            if (!kinds.Contains(parsedKind)) kinds.Add(parsedKind);
        }

        VerificationLevel? requestedFloor = null;
        if (http.Query["min_verification"].ToString() is { Length: > 0 } floorWire)
        {
            if (!RetrievalFloorPolicy.ParseFloor(floorWire).TryGetValue(out var parsedFloor, out var floorError))
                return Problem(StatusCodes.Status400BadRequest, floorError!);
            requestedFloor = parsedFloor;
        }

        // R9.25: absent and malformed are different requests, and were the same value. A cursor
        // carries R9.22's corpus bound, so reading a malformed one as "start from the beginning"
        // re-evaluates a continuation against a different corpus while the response reports the new
        // bound as though it had always been the bound -- which the caller cannot see, because a
        // continued page and a first page it believes was continued are the same document.
        if (!RetrievalCursor.Decode(http.Query["cursor"].ToString()).TryGetValue(out var cursor, out var cursorError))
            return Problem(StatusCodes.Status400BadRequest, cursorError!);

        var query = new SearchQuery(
            Text: Nullable(http.Query["q"].ToString()),
            Board: Nullable(http.Query["board"].ToString()),
            Kinds: kinds.ToImmutable(),
            Tags: [.. http.Query["tags"].ToString()
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            Author: Nullable(http.Query["author"].ToString()),
            RequestedFloor: requestedFloor,
            Cursor: cursor,
            Limit: limit);

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var views = PostProjector.Fold(log);
        var posts = views.ToDictionary(p => p.PostId, StringComparer.Ordinal);
        var standings = AgentStandingProjector.Fold(log);
        var verification = VerificationProjector.Fold(views, standings, Servable(log));
        var acta = ActaOf(log);
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);
        var contract = ReaderContractUrl(http);
        var acceptedByThread = AcceptanceProjector.Fold(log);

        var searched = await search.SearchAsync(log, RetrievalSurface.RestSearch, query, verification, cancellationToken).ConfigureAwait(false);
        if (!searched.TryGetValue(out var page, out var searchError))
            return Problem(StatusCodes.Status503ServiceUnavailable, searchError!);

        // R9.8: "when requested". Off by default because R8.36's purpose is auditing rather than
        // decoration, and a field on every response is one every client learns to ignore.
        // R9.25: every other spelling produced a response with no breakdown and no statement that
        // one had been asked for, so `why=yes` read as `why` absent.
        var whyWire = http.Query["why"].ToString();
        if (whyWire is not ("" or "true" or "1" or "false" or "0"))
            return Problem(StatusCodes.Status400BadRequest, new Error(
                "curia/search/unknown-why",
                "why takes true or false and is refused rather than ignored",
                $"received={whyWire}; omit it, or send why=true to receive the R8.36 breakdown"));

        var wantsWhy = whyWire is "true" or "1";

        var results = ImmutableArray.CreateBuilder<SearchHitResponse>();
        foreach (var hit in page!.Results)
        {
            // A ranked post the serving projection does not have is dropped rather than served
            // half-formed. The two projections read the same events, so this cannot happen today --
            // and a result carrying no provenance envelope would violate R10.17 if it ever did.
            if (!posts.TryGetValue(hit.Post.PostId, out var view)) continue;

            results.Add(new SearchHitResponse(
                ToResponse(view, standings, marking, contract, acceptedByThread, verification, acta),
                Micro(hit.Score),
                wantsWhy ? WhyRanked(hit, page.Model.Id, page.K) : null));
        }

        return Results.Ok(new SearchResponse(
            results.ToImmutable(),
            page.Next?.Encode(),
            new FloorResponse(
                RetrievalSurfaces.Wire(page.Surface),
                VerificationLevels.Wire(page.Floor),
                page.FloorSource,
                [.. RetrievalFloorPolicy.GradableKinds.Select(PostKinds.Wire)],
                [.. RetrievalFloorPolicy.UngradableKinds.Select(PostKinds.Wire)]),
            page.Model.Id,
            page.CorpusBound,
            page.K,
            page.CandidateDepth,
            Bp(page.MinimumCosine)));
    }

    /// <summary>R8.36's terms this build does not compute, named as absent with the reason (errata G10).</summary>
    private static readonly IReadOnlyDictionary<string, string> NotComputedTerms = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["n_eff"] = "Phase 4: ρ estimation and the n_eff correction (R8.40)",
        ["surprisingly_popular"] = "Phase 4: recorded since Stage 3 (R15.3), not yet weighted",
        ["seeded_trust"] = "Phase 4: seeded asymmetric trust (R8.53)",
        ["citation_weight"] = "Phase 4",
        ["staleness_penalty"] = "Phase 4: R8.23's decay",
        ["acceptance"] = "not a ranking term on this build; served as `accepted` on the post",
        ["flag_penalty"] = "withheld posts are not served at all (R10.37); no partial penalty exists",
    };

    private static WhyRankedResponse WhyRanked(RankedPost hit, string model, int k) => new(
        hit.LexicalRank > 0 && hit.Lexical is { } lexical
            ? new LexicalWhyResponse(hit.LexicalRank, lexical.TitleMatches, lexical.BodyMatches, lexical.TagMatches, lexical.Score)
            : null,
        hit.VectorRank > 0 && hit.Cosine is { } cosine ? new VectorWhyResponse(hit.VectorRank, Bp(cosine), model) : null,
        k,
        Micro(hit.LexicalTerm),
        Micro(hit.VectorTerm),
        Micro(hit.Fused),
        VerificationLevels.Wire(hit.Level),
        Bp(hit.Weight),
        Micro(hit.Score),
        hit.Deferred,
        NotComputedTerms);

    /// <summary>R6.33: a unit-interval quantity as basis points, and a fused score in millionths. Integers cross the wire; nothing else does.</summary>
    private static int Bp(double unitInterval) => (int)Math.Round(unitInterval * 10_000, MidpointRounding.AwayFromZero);

    private static long Micro(double score) => (long)Math.Round(score * 1_000_000, MidpointRounding.AwayFromZero);

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
        var posture = PostureQuery.Of(log, subject);

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
        var views = PostProjector.Fold(log);
        var posts = views.ToDictionary(p => p.PostId, StringComparer.Ordinal);
        var standings = AgentStandingProjector.Fold(log);
        var verification = VerificationProjector.Fold(views, standings, Servable(log));
        var acta = ActaOf(log);
        var accepted = AcceptanceProjector.Fold(log);
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);
        var contract = ReaderContractUrl(http);

        var results = ImmutableArray.CreateBuilder<PostResponse>();
        foreach (var hit in hits)
        {
            if (!posts.TryGetValue(hit.Post.PostId, out var view)) continue;
            results.Add(ToResponse(view, standings, marking, contract, accepted, verification, acta));
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

    /// <summary>A vote is never served to readers before its epoch is sealed (R8.55); everything else may be.</summary>
    private static bool ServedToReaders(PostView p) =>
        PostKinds.TryParse(p.Kind, out var kind) && PostKinds.IsServedToReaders(kind);

    /// <summary>The kinds a listing shows: the conversation, not the signals about it (R8.59).</summary>
    private static bool Discussion(PostView p) =>
        PostKinds.TryParse(p.Kind, out var kind) && PostKinds.IsDiscussion(kind);

    /// <summary>
    /// R8.58's target rules and R8.55's one-vote rule for a vote or verification about to be
    /// persisted, or <see langword="null"/> when it may proceed.
    ///
    /// <para>One refusal for a target that is unknown and one that is withheld, so an attempted
    /// verification is not a probe of moderation state. The pair rules -- self, same owner,
    /// unattested submitter, not a result, another board -- are <see cref="VerificationPolicy.Refusal"/>'s,
    /// decided in the domain; this only gathers the facts it needs from the log.</para>
    /// </summary>
    private static IResult? SignalRefusal(IReadOnlyList<AppendedEvent> log, VerifiedSubmission v)
    {
        var target = v.Envelope.Target!;
        var posts = PostProjector.Fold(log);
        var servable = Servable(log);
        var standings = AgentStandingProjector.Fold(log);

        var targetPost = posts.FirstOrDefault(p =>
            string.Equals(p.Digest, target, StringComparison.Ordinal) && servable(p.PostId) && ServedToReaders(p));
        if (targetPost is null || !PostKinds.TryParse(targetPost.Kind, out var targetKind))
            return Problem(StatusCodes.Status422UnprocessableEntity, VerificationErrors.TargetNotServable(target));

        string? OwnerOf(string agent) => standings.TryGetValue(agent, out var s) ? s.OwnerId : null;

        if (VerificationPolicy.Refusal(
                v.AuthorAgentId, OwnerOf(v.AuthorAgentId), targetPost.Author, OwnerOf(targetPost.Author),
                targetKind, v.Envelope.Board, targetPost.Board) is { } refusal)
            return Problem(StatusCodes.Status422UnprocessableEntity, refusal);

        // R8.55: at most one vote stands from an agent for a target. A verification report may be
        // superseded by the same agent's later one (R8.56); a vote may not be recast, because a vote
        // that could be changed after the tally is visible is the thing epoch sealing exists to stop.
        if (v.Envelope.Kind is PostKind.Vote
            && posts.Any(p => string.Equals(p.Author, v.AuthorAgentId, StringComparison.Ordinal)
                && servable(p.PostId)
                && string.Equals(VerificationProjector.TargetOf(p), target, StringComparison.Ordinal)))
            return Problem(StatusCodes.Status409Conflict, VerificationErrors.AlreadyVoted(target));

        return null;
    }

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

    /// <summary>
    /// Table 9's <c>kind</c> mapped to Table 10's pair. The two tables' vocabularies, joined once.
    /// A discussion kind is a <c>create</c> on its own resource; a vote is <c>vote</c>/<c>cast</c>
    /// and a verification is <c>verification</c>/<c>submit</c> -- the rows Table 11 grants T1 as
    /// "+ answer, vote, submit verifications", and the whole of G8's authorization change.
    /// </summary>
    private static (ResourceKind Resource, ActionKind Action) PairFor(PostKind kind) => PostKinds.Match(
        kind,
        question: () => (ResourceKind.Question, ActionKind.Create),
        answer: () => (ResourceKind.Answer, ActionKind.Create),
        finding: () => (ResourceKind.Finding, ActionKind.Create),
        comment: () => (ResourceKind.Comment, ActionKind.Create),
        revision: () => (ResourceKind.Revision, ActionKind.Create),
        vote: () => (ResourceKind.Vote, ActionKind.Cast),
        verification: () => (ResourceKind.Verification, ActionKind.Submit));

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
        var read = await events.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        return read.TryGetValue(out var all, out _) ? all! : [];
    }

    /// <summary>
    /// The Acta, folded once per request for R6.18's per-item <c>log_index</c> and
    /// <c>inclusion_proof</c>. A log that will not fold into a tree yields null fields rather
    /// than a failed read -- the post is still the post -- and <c>/v1/log/*</c> is where that
    /// failure is reported loudly, with its slug.
    /// </summary>
    private static ActaLog? ActaOf(IReadOnlyList<AppendedEvent> log) =>
        ActaLog.Fold(log).Match<ActaLog?>(a => a, _ => null);

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
        ImmutableDictionary<string, string>? acceptedByThread,
        VerificationFold verification,
        ActaLog? acta = null)
    {
        var standingKnown = standings.TryGetValue(p.Author, out var standing);
        var state = verification.StateOf(p.Digest);

        // R6.18: the leaf index and its audit path, against the latest signed head that covers
        // the post where one does (R6.48). The post's event id is its post id (IngestPipeline).
        var logIndex = acta?.IndexOf(p.PostId);
        var inclusion = acta is not null && logIndex is { } index ? ActaEndpoints.ProofFor(acta, index, null) : null;

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
            OwnerVerified: standingKnown && standing!.OwnerVerified,

            // The Forum verified this at ingest -- VERIFY is the only way a post reaches PERSIST. The
            // reader does not have to take that on trust: `canonical` and `signature` let it check.
            SignatureValid: true,

            // Table 13, computed from the log per R8.57 -- attached to this digest, so a revision
            // starts over. It was a literal "V0" until Stage 3, and three client tests pinned it.
            VerificationLevel: VerificationLevels.Wire(state.Level),
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
            ReaderContract: readerContract,

            // R10.17's `owner`, which G5 recorded the Forum could not produce and now can. Absent
            // rather than false-ish until an attestation names one; unknown and unverified are the
            // same answer to a reader, and a made-up owner would not be.
            Owner: standingKnown ? standing!.OwnerId : null,

            // R8.59: the reports, never the counts. R8.15 wants a contradiction surfaced on the
            // post; R8.30 wants the tally withheld, and a level is a floor on the endorsing count
            // that discloses no rate -- two of two and two of forty read the same V1.
            Reproductions: state.Reproductions,
            Contradictions: state.Contradictions);

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
                && string.Equals(accepted, p.PostId, StringComparison.Ordinal),
            logIndex,
            inclusion,
            p.PossibleDuplicateOf);
    }

    /// <summary>
    /// R10.12: <c>?marking=datamark</c> on the HTTP API. R10.13 makes <b>off</b> the HTTP default,
    /// "whose output is usually processed by client code first" -- interleaving a token into text a
    /// program will parse mostly corrupts the parse. The MCP adapter, whose output "goes directly
    /// into a model's context", defaults the other way, and that asymmetry is the point of R10.13
    /// rather than an oversight. That adapter is <c>src/Curia.Mcp</c>; it carries the default as a
    /// per-session setting rather than a query parameter (R10.12), and requests marking of the
    /// Forum on every read rather than applying it -- marking is a transformation of this
    /// boundary, and a client performing it would be performing it outside the boundary.
    /// </summary>
    private static Result<MarkingMode> MarkingFrom(HttpRequest request) =>
        request.Query["marking"].ToString() switch
        {
            "" => Result<MarkingMode>.Ok(MarkingMode.None),
            "datamark" => Result<MarkingMode>.Ok(MarkingMode.Datamark),
            "delimiters" => Result<MarkingMode>.Ok(MarkingMode.DelimitersOnly),

            // R10.51 / R9.25: an unmodelled spelling was mapped to MarkingMode.None, so a
            // mis-typed request was served unmarked under an envelope that truthfully reported
            // "marking": "None" -- which makes R10.13's default silently off and describes it as a
            // deliberate choice. It returns a Result rather than a mode so that a seventh read path
            // cannot be added without handling the refusal; a guard a caller may forget is the same
            // silent degradation one level up.
            var unknown => Result<MarkingMode>.Fail(new Error(
                "curia/serving/unknown-marking",
                "That is not a marking this Forum serves",
                $"received={unknown}; the published request vocabulary is datamark, delimiters, or " +
                "the member omitted for none (R10.12, R10.51)")),
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
