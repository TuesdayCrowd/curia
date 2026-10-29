using System.Collections.Immutable;
using System.Globalization;
using System.Text;
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
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Credentials;
using Curia.Domain.Keys;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Domain.Retrieval;
using Curia.Application.Retrieval;
using Curia.Domain.Search;
using Curia.Domain.Serving;
using Curia.Domain.Verification;
using Curia.Infrastructure.Lifecycle;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;
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
    [property: JsonPropertyName("contradictions")] ImmutableArray<string> Contradictions,

    /// <summary>R6.57: what the log says of the key the post was signed under: <c>active</c>, <c>retired</c> or <c>compromised</c>. The Forum's word, not a verdict on the post.</summary>
    [property: JsonPropertyName("key_status")] string KeyStatus,

    /// <summary>R6.57: the log index of the declaration that disputes the post; absent unless the post lies between the declaration's <c>from_index</c> and the declaration.</summary>
    [property: JsonPropertyName("disputed_by_log_index")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? DisputedByLogIndex,

    /// <summary>
    /// R9.32: the revisions by this post's own author that chain to it (R9.28), in log order, always present: <c>[]</c> says
    /// nothing supersedes the post, and a reader that finds the member absent is talking to a Forum that does not say (R10.80).
    /// A revision the serving path will not serve is listed by its digest alone, its <c>post_id</c> left out (R10.36). The
    /// Forum's word, outside the signed bytes, the leaf and every digest; required and without a default, so a caller that
    /// forgot to build it does not compile into "nothing supersedes this". The validator of the single read (R9.11) covers it
    /// for a post a signed head covers, because the representation hashed is this one; before the first head every tag moves
    /// with the log, as the inclusion proof does.
    /// </summary>
    [property: JsonPropertyName("superseded_by")] ImmutableArray<SupersededByResponse> SupersededBy,

    /// <summary>
    /// R9.36: on a question only, the answer the thread's standing acceptance names, or <see cref="AcceptedAnswerResponse.None"/>
    /// (written as JSON <c>null</c>) where none stands. CLR <c>null</c> is a post that is not a question and is left out: the
    /// member is a class so the serializer's ignore condition can tell "none" from "not a question" (a <c>null</c> the converter
    /// writes is a present member, and only a CLR null is omitted).
    /// </summary>
    [property: JsonPropertyName("accepted_answer")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    AcceptedAnswerResponse? AcceptedAnswer = null);

/// <summary>
/// R9.36: the answer a question's standing acceptance names: its digest, and its id only where the Forum serves it (R10.36).
/// <see cref="None"/> is the instance that stands for "no acceptance" and is written as JSON <c>null</c>.
/// </summary>
[JsonConverter(typeof(AcceptedAnswerResponseConverter))]
public sealed record AcceptedAnswerResponse(string? Digest, string? PostId, ImmutableArray<SupersededByResponse> SupersededBy, string? Kind = null)
{
    /// <summary>No acceptance stands; written as <c>null</c>, present, and carrying no list.</summary>
    public static AcceptedAnswerResponse None { get; } = new(null, null, []);

    /// <summary>True when no acceptance stands.</summary>
    public bool IsNone => Digest is null;

    /// <summary>The wire form of the index's answer.</summary>
    public static AcceptedAnswerResponse From(AcceptedAnswerMember member)
    {
        ArgumentNullException.ThrowIfNull(member);
        return member.IsNone ? None : new AcceptedAnswerResponse(member.Digest, member.PostId, [.. member.SupersededBy.Select(s => new SupersededByResponse(s.Digest, s.PostId))], member.Kind);
    }
}

/// <summary>
/// Writes <see cref="AcceptedAnswerResponse.None"/> as JSON <c>null</c> and a standing acceptance as <c>{digest, post_id?, superseded_by}</c>
/// (R9.41: the list is always present inside the object, <c>[]</c> where nothing supersedes the answer).
/// </summary>
public sealed class AcceptedAnswerResponseConverter : JsonConverter<AcceptedAnswerResponse>
{
    /// <summary>The Forum writes this member and never reads one; a caller's JSON is read through the guarded reader (R11.34).</summary>
    public override AcceptedAnswerResponse? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("accepted_answer is written by the Forum and read by Curia.Client");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, AcceptedAnswerResponse value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        if (value.IsNone)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("digest", value.Digest);
        if (value.PostId is not null) writer.WriteString("post_id", value.PostId);
        if (value.Kind is not null) writer.WriteString("kind", value.Kind);
        writer.WriteStartArray("superseded_by");
        foreach (var successor in value.SupersededBy.IsDefault ? [] : value.SupersededBy)
        {
            writer.WriteStartObject();
            writer.WriteString("digest", successor.Digest);
            if (successor.PostId is not null) writer.WriteString("post_id", successor.PostId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    /// <summary>The converter must be asked for a present <c>null</c> too, because that is the "none" state.</summary>
    public override bool HandleNull => true;
}

/// <summary>R9.32: one revision that supersedes a post: its digest, and its id only where the Forum serves it.</summary>
public sealed record SupersededByResponse(
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("post_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PostId);

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
    [property: JsonPropertyName("post")] PostResponse? Post,

    /// <summary>
    /// R9.43's <c>fork</c>: the other successors of the original this item's post revises, <c>[]</c> where the post is not a fork branch.
    /// Present on an item that carries a post, and left out of one that carries none (a CLR null is omitted).
    /// </summary>
    [property: JsonPropertyName("fork")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ImmutableArray<SupersededByResponse>? Fork = null);

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
    [property: JsonPropertyName("not_applicable_to")] ImmutableArray<string> NotApplicableTo,

    /// <summary>
    /// R9.30: how many answer and finding candidates the floor removed from the fused candidate list
    /// before the page was cut, equal on every page of one query. R9.30's order is the floor first,
    /// then dispute: a disputed post below the floor is counted here and not in <c>excluded_disputed</c>.
    /// </summary>
    [property: JsonPropertyName("excluded_by_floor")] int ExcludedByFloor,

    /// <summary>R6.57: how many of the posts the floor kept are disputed, over the whole diversified list and so the same on every page.</summary>
    [property: JsonPropertyName("excluded_disputed")] int ExcludedDisputed,

    /// <summary>R9.31: the surface's published default floor, which is not the level applied where a deployment configures one.</summary>
    [property: JsonPropertyName("published_default")] string PublishedDefault,

    /// <summary>R9.31: the level the request named, or null where it named none; always a present member.</summary>
    [property: JsonPropertyName("requested_min_verification")] string? RequestedMinVerification,

    /// <summary>R9.31, R10.54 (rev.): true where the request named a level below the floor in force and was served at the floor.</summary>
    [property: JsonPropertyName("clamped")] bool Clamped);

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

        // R4.18 (rev.), R4.39: an identity's own key acts. Each handler's first act is the guard's gate
        // (R4.38 (add.)), so a Forum that has not lifted the guard answers 403 before anything else.
        app.MapPost("/v1/agents/{id}/keys", RotateKeyAsync).Serves(ServedContent.None);
        app.MapPost("/v1/agents/{id}/keys/{kid}/retire", RetireKeyAsync).Serves(ServedContent.None);

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
    /// <para><b>What is missing and is not pretended otherwise:</b> §4.3's owner authentication,
    /// and R4.11's proof of possession. This endpoint trusts what it is told, which is acceptable
    /// because nothing downstream trusts an agent's *claim* -- authorship is established by
    /// signature against the key registered here, so a false enrollment can only impersonate an
    /// agent whose private key the caller already holds. Each half of that sentence has been false.
    /// Its premise was false for as long as the request carried <c>owner_verified</c>, which Table
    /// 11's T1 row and every provenance envelope trusted (errata G5); R4.30 puts owner verification
    /// behind <see cref="AttestOwner"/>, under an operator's actor, with no HTTP route. Its
    /// conclusion was false until errata G14: the endpoint registered whatever key a request
    /// carried, so anyone could add a key to an enrolled identity and post as it, or replace the
    /// bytes behind its <c>kid</c> and unverify everything it had signed; and, until errata G15, at
    /// the token endpoint, which resolved an assertion's key by <c>kid</c> alone, so a key enrolled
    /// under its holder's own identifier authenticated every enrolled identity. R4.31, R4.32 and
    /// R5.20 make it true -- <see cref="EnrollIdentity"/> registers a key only for an identity that
    /// holds none, and never changes one it holds, and the token endpoint honours a key only for the
    /// identity it is registered to -- except in two cases. An identifier nobody has enrolled
    /// belongs to whoever enrolls it first (plan D4, D7), unless R4.33 refuses it because the event
    /// log keeps it for its own records, or R4.31 because the key store holds several keys for it and
    /// the log binds none (<c>curia/enroll/keys-ambiguous</c>, errata G16). And a lost key row is
    /// registered again only with the key the log binds (R4.31 rev., R4.34), unless another identity
    /// took its <c>kid</c>; for an identity enrolled before R4.34, whose log binds the <c>kid</c>
    /// alone, it is bound again on its <c>kid</c> alone, by whoever presents it first. Neither is open
    /// to an identity enrolled before R4.36 or R4.37 whose agent identifier or <c>kid</c> those rules
    /// refuse: the checks below run before anything is read, so its re-announcement is refused with
    /// theirs and a lost key row of its is never registered again. It keeps the rows it has.</para>
    ///
    /// <para><b>What the request may carry into the store and the log,</b> checked in this order,
    /// before anything is read or written, each refused 400 by name: the two identifiers' text
    /// (R6.15's condition, and U+0000); the agent identifier's normalization form, NFC (R4.36);
    /// a control, format or separator character in either (R4.37, errata G17);
    /// their length, at most
    /// <see cref="EnrollmentErrors.MaxIdentifierBytes"/> UTF-8 bytes each; the algorithm, against the
    /// allow-list the Forum verifies with (R4.15); and then the key, which must be present, base64,
    /// and a key of that algorithm in R4.28's stored form (<see cref="Jwks.CanPublish"/>, the rule
    /// that algorithm's verifier owns). Until these checks the route wrote whatever it was sent. A
    /// noncharacter wrote a public leaf the reference client refuses to read. U+0000, an identifier
    /// too long for the store's index, an algorithm the Forum does not verify and a missing key each
    /// answered 500, which tells an agent to retry. And bytes that were not a key of their algorithm
    /// were registered for good: junk the key set could not render, or a P-384 key the verifier
    /// accepted as <c>ES256</c> and the key set published as P-256.</para>
    /// </summary>
    private static async Task<IResult> EnrollAsync(
        EnrollRequest request,
        EnrollIdentity enroll,
        IReadOnlyDictionary<string, IContentVerifier> verifiers,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.AgentId) || string.IsNullOrEmpty(request.Kid))
            return Results.BadRequest(new Problem(
                "curia/enroll/invalid", "agent_id and kid are required", null));

        if ((RefusedText(request.AgentId, "agent_id") ?? RefusedText(request.Kid, "kid")) is { } textError)
            return Problem(StatusCodes.Status400BadRequest, textError);

        // R4.36 (errata G16): an agent identifier NFC would change names, in every envelope it signs,
        // the identifier NFC maps it to, which another identity can hold (R6.9, R6.55). Asked after
        // RefusedText, so it never meets a lone surrogate or a noncharacter. A kid is not asked: it
        // travels in a protected header signed as its bytes, and is never canonicalized.
        if (NotInNfc(request.AgentId, "agent_id") is { } formError)
            return Problem(StatusCodes.Status400BadRequest, formError);

        // R4.37 (errata G17): an identifier is printed wherever an agent or a key is named -- in the
        // log, the key set, a token's subject, every reader's frame -- and a control, format or
        // separator character there begins a line, reorders one, or hides. Refused in both fields,
        // after RefusedText, so U+0000 keeps its own name and no lone surrogate reaches the walk.
        if ((ControlCharacter(request.AgentId, "agent_id") ?? ControlCharacter(request.Kid, "kid")) is { } controlError)
            return Problem(StatusCodes.Status400BadRequest, controlError);

        // A blank identifier of characters outside Cc, Cf, Zl and Zp (Zs: U+0020, U+00A0, U+3000) is
        // still refused here, as required, unless an earlier check names a truer reason: an agent_id of
        // U+2000 or U+2001, which NFC maps to U+2002 and U+2003, is refused by R4.36's name. Asked after
        // R4.37, so an identifier made only of a control or separator character that is also white
        // space (U+000A, U+0085, U+2028) is refused by R4.37's name, which is its true reason, and not
        // as missing (Task 10's review).
        if (string.IsNullOrWhiteSpace(request.AgentId) || string.IsNullOrWhiteSpace(request.Kid))
            return Results.BadRequest(new Problem("curia/enroll/invalid", "agent_id and kid are required", null));

        if ((TooLong(request.AgentId, "agent_id") ?? TooLong(request.Kid, "kid")) is { } lengthError)
            return Problem(StatusCodes.Status400BadRequest, lengthError);

        // R4.15's algorithms: the allow-list DetachedJws verifies with, so no key is registered under
        // an algorithm the Forum has no verifier for. The database's CHECK agrees, and answered 500.
        // The key's own bytes are judged below, once decoded.
        if (request.Alg is null || !verifiers.ContainsKey(request.Alg))
            return Problem(StatusCodes.Status400BadRequest, EnrollmentErrors.UnsupportedAlgorithm(request.Alg, verifiers.Keys));

        if (request.PublicKeyBase64 is null)
            return Problem(StatusCodes.Status400BadRequest, EnrollmentErrors.PublicKeyMissing());

        byte[] publicKey;
        try
        {
            publicKey = Convert.FromBase64String(request.PublicKeyBase64);
        }
        catch (FormatException)
        {
            return Problem(StatusCodes.Status400BadRequest, EnrollmentErrors.PublicKeyNotBase64());
        }

        // R4.15 against R4.28's stored forms: a key of the algorithm it names, judged by the rule that
        // algorithm's verifier owns, which the key set publishes by too. A key the verifier cannot use
        // and the key set cannot render would stay in the store for good (R4.19, R4.32).
        if (!Jwks.CanPublish(request.Alg, publicKey))
            return Problem(StatusCodes.Status400BadRequest, EnrollmentErrors.PublicKeyNotOfItsAlgorithm(request.Alg));

        // R11.34 (errata G18): the identifier and the kid as the types the use case and the key store
        // take, constructed here, at their first use and after every check above, which together are
        // stricter than the type's rule; a refusal here is the route's own.
        if (!AgentId.Parse(request.AgentId, "agent_id").TryGetValue(out var agentId, out _)
            || !Kid.Parse(request.Kid, "kid").TryGetValue(out var kid, out _))
            return Results.BadRequest(new Problem("curia/enroll/invalid", "agent_id and kid are required", null));

        // R4.31 and R4.32 (errata G14), in EnrollIdentity: the log's binding, then the key store's,
        // then the log's record. Standing goes into the event log, never into process memory -- a
        // repeat enrollment appends nothing, so Table 11's tenure clock cannot be restarted by
        // re-announcing one -- and nothing about the owner is recorded at all; that is AttestOwner's,
        // under an operator's actor (R4.30).
        var enrolled = await enroll
            .EnrollAsync(agentId!, new PublicKeyMaterial(request.Alg, kid!.Value, publicKey), cancellationToken)
            .ConfigureAwait(false);

        if (!enrolled.TryGetValue(out var enrollment, out var enrollError))
        {
            // A refusal of the identity or the key is the caller's to act on, and says how; anything
            // else -- the log refusing an append, the store unreachable -- is the Forum's.
            return enrollError!.Type is AuthorKeyErrors.AlreadyEnrolledType
                    or AuthorKeyErrors.MaterialImmutableType
                    or AuthorKeyErrors.KidRegisteredToAnotherAgentType
                    or AuthorKeyErrors.KidTakenType
                    or EnrollmentErrors.IdentifierReservedType
                    or EnrollmentErrors.KeysAmbiguousType
                ? Results.Conflict(new Problem(enrollError.Type, enrollError.Title, enrollError.Detail))
                : Problem(StatusCodes.Status500InternalServerError, enrollError);
        }

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
    /// The refusal for an identifier an enrollment would carry into a public leaf, or null. First
    /// ADMIT's own rules (<see cref="JsonReader.CheckString"/>): a noncharacter or an unpaired
    /// surrogate, under ADMIT's slug, since the reference client refuses to read a leaf holding
    /// either. Then U+0000, which ADMIT accepts written as an escape and Postgres <c>text</c> cannot
    /// store. Each names the field and never echoes the value.
    /// </summary>
    private static Error? RefusedText(string value, string field)
    {
        if (!JsonReader.CheckString(value).TryGetValue(out _, out var textError))
            return textError! with { Detail = "field=" + field };

        return value.Contains('\0', StringComparison.Ordinal) ? EnrollmentErrors.NulCharacter(field) : null;
    }

    /// <summary>
    /// The refusal for an identifier that is not in Unicode Normalization Form C, or null (R4.36).
    /// Names the field and never echoes the value.
    /// </summary>
    private static Error? NotInNfc(string value, string field) =>
        value.IsNormalized(NormalizationForm.FormC) ? null : EnrollmentErrors.IdentifierNotNfc(field);

    /// <summary>
    /// The refusal for an identifier holding a character of general category Cc, Cf, Zl or Zp, or
    /// null (R4.37). Walks scalar values, so a format character beyond the Basic Multilingual Plane --
    /// a tag character, say -- is found as itself rather than as two surrogate halves. Asked after
    /// <see cref="RefusedText"/>, so every value it walks is well-formed. The category is the
    /// runtime's Unicode tables' (<see cref="Rune.GetUnicodeCategory"/>), and only these four are
    /// refused: a variation selector (Mn), a Hangul filler (Lo) and an unassigned code point (Cn) are
    /// not seen either, and enroll (errata G17, "What this costs" 5).
    /// </summary>
    private static Error? ControlCharacter(string value, string field)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var name = category == UnicodeCategory.Control ? "Cc"
                : category == UnicodeCategory.Format ? "Cf"
                : category == UnicodeCategory.LineSeparator ? "Zl"
                : category == UnicodeCategory.ParagraphSeparator ? "Zp"
                : null;

            if (name is not null)
                return EnrollmentErrors.IdentifierControlCharacter(field, rune.Value, name);
        }

        return null;
    }

    /// <summary>
    /// The refusal for an identifier over <see cref="EnrollmentErrors.MaxIdentifierBytes"/> UTF-8
    /// bytes, or null. Asked after <see cref="RefusedText"/>, so the count never meets a lone surrogate.
    /// </summary>
    private static Error? TooLong(string value, string field)
    {
        var bytes = Encoding.UTF8.GetByteCount(value);
        return bytes > EnrollmentErrors.MaxIdentifierBytes ? EnrollmentErrors.IdentifierTooLong(field, bytes) : null;
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
        IRevisionGate revisionGate,
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
        // The status of a VERIFY refusal is VerifyRefusals.StatusOf's (R6.58): a log that could not be
        // read is a 503 (R4.35), a member the log cannot store a 422 (R11.33), a refusal of the envelope's
        // own content a 400, and only a credential or key that fails a 401.
        if (!PrincipalOf(subject).TryGetValue(out var author, out var subjectError))
            return Problem(StatusCodes.Status403Forbidden, subjectError!);

        // R6.56: VERIFY through PERSIST is one act against the author's stream, repeated while the stream
        // moves under it (IngestAttempts), and answered 503 contended after three. The attempt answers
        // null when its append lost that race and every other outcome as the response it is.
        var attempted = await IngestAttempts.RunAsync(
            async token =>
            {
                var response = await SubmitAttemptAsync(
                    a!, author!, pipeline, pdp, events, revisionGate, indexer, dedupe, http, clock, token).ConfigureAwait(false);
                return response is null
                    ? Result<IResult>.Fail(new Error(DomainErrors.ConditionConflictType, "The author's stream moved"))
                    : Result<IResult>.Ok(response);
            },
            cancellationToken).ConfigureAwait(false);

        return attempted.Match(
            response => response,
            error => Problem(StatusCodes.Status503ServiceUnavailable, error));
    }

    /// <summary>
    /// One attempt at VERIFY, AUTHORIZE, SCREEN and PERSIST for <see cref="SubmitAsync"/>, decided on the
    /// log as it stands now. <see langword="null"/> when PERSIST's append found the author's stream moved
    /// since VERIFY read it (R6.56); the caller runs the attempt again.
    /// </summary>
    private static async Task<IResult?> SubmitAttemptAsync(
        AdmittedSubmission a,
        AgentId author,
        IIngestPipeline pipeline,
        IPolicyDecisionPoint pdp,
        IEventReader events,
        IRevisionGate revisionGate,
        EmbeddingIndexer indexer,
        DuplicateCheck dedupe,
        HttpRequest http,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var verified = await pipeline.VerifyAsync(a, author, cancellationToken).ConfigureAwait(false);
        if (!verified.TryGetValue(out var v, out var verifyError))
        {
            return Problem(VerifyRefusals.StatusOf(verifyError!), verifyError!);
        }

        // AUTHORIZE. R7.7: tier from live state, never from a claim -- and "live state" now means
        // the log rather than a process's memory. One forward scan yields both halves of Table 11's
        // criteria: the credential events enrollment appended, and the agent's own post history.
        // The fold reads no clock (R11.9); the elapsed-time half is the instant handed to
        // TierPolicy.Evaluate below.
        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var posture = PostureQuery.Of(log, author!);

        if (!posture.TryGetValue(out var facts, out var postureError))
            return Problem(StatusCodes.Status500InternalServerError, postureError!);

        var now = clock.GetUtcNow();
        var tier = TierPolicy.Evaluate(facts!, now);

        var (resource, action) = PairFor(v!.Envelope.Kind);
        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(
                tier,
                facts!.CredentialState,
                resource,
                action,
                PostsToday: PostsInBudgetWindow(log, v.AuthorAgentId.Value, now)),
            cancellationToken).ConfigureAwait(false);

        if (!decision.TryGetValue(out var d, out var decisionError))
            return Problem(StatusCodes.Status403Forbidden, decisionError!);

        // Table 10's parenthetical, discharged. `revision`/`create` is "(own)", and an allow
        // carrying that qualifier is not a permission to act -- it is a permission conditional on a
        // fact about the resource that the PDP cannot know, because the PDP is not handed the
        // resource. Until this existed, the qualifier was returned and read past: any T0 agent could
        // submit a revision naming another agent's post.
        if (d!.Qualifier is GrantQualifier.OwnResourceOnly)
            d = d.Discharge(RevisesOwnPost(log, v.Envelope.Prev, v.AuthorAgentId.Value));

        if (!d.IsAllowed)
            return Problem(
                StatusCodes.Status403Forbidden,
                Denial(d, $"{d.Reason} tier={tier.Tier}"));

        // R8.78 (errata G45): the same bytes are not recorded twice. The envelope carries its author and a signed nonce, so an
        // equal digest is the same author and the very bytes; a fresh nonce is a new post. After AUTHORIZE (a caller denied by
        // tier is told the tier and never whether a post exists), before the parent checks, the near-duplicate check, SCREEN and
        // PERSIST (nothing is screened, stored or spent). It counts stored posts, not served ones: withheld bytes sent again are
        // not a correction. The first post of the digest in log order is named, by its id and nothing an author wrote.
        if (AlreadyRecordedPost(log, Curia.Canon.Digests.Sha256(v.Canonical).ToPrefixed()) is { } recordedPostId)
            return Problem(StatusCodes.Status409Conflict, IngestErrors.AlreadyRecorded(recordedPostId));

        // R8.77 (errata G45): a revision is entered under a gate on its prev. After AUTHORIZE (a stranger is told 403 and never holds the gate or
        // learns a successor) and before the parent checks, the near-duplicate check, SCREEN and PERSIST. The log AUTHORIZE read is older than any
        // hold, so the checks are made again on a log read under the hold, and the hold is released when the attempt ends on any path (the
        // `await using`), after PERSIST has committed or refused. Try, never wait: a held gate is 409 revision-in-flight.
        if (v.Envelope.Kind is not PostKind.Revision || v.Envelope.Prev is not { Length: > 0 } prevDigest)
            return await PersistSubmissionAsync(v, log, pipeline, events, indexer, dedupe, http, cancellationToken).ConfigureAwait(false);

        var entered = await revisionGate.TryEnterAsync(prevDigest, cancellationToken).ConfigureAwait(false);
        if (!entered.TryGetValue(out var held, out var gateError))
        {
            return Problem(
                string.Equals(gateError!.Type, IngestErrors.RevisionInFlightType, StringComparison.Ordinal)
                    ? StatusCodes.Status409Conflict
                    : StatusCodes.Status503ServiceUnavailable,
                gateError);
        }

        await using (held!.ConfigureAwait(false))
        {
            // ReadEventsAsync answers an empty log when the store cannot be read, which for this check would mean "no successor" and allow.
            var read = await events.ReadAllAsync(cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var fresh, out var readError))
                return Problem(StatusCodes.Status503ServiceUnavailable, readError!);

            if (RevisionRefusal(fresh!, prevDigest, Curia.Canon.Digests.Sha256(v.Canonical).ToPrefixed()) is { } refusal)
                return refusal;

            return await PersistSubmissionAsync(v, fresh!, pipeline, events, indexer, dedupe, http, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// R8.77, R8.78: what a revision is refused with on the log as it stands under the hold, or <see langword="null"/> to go on. The same bytes are
    /// <c>already-recorded</c> (stored, served or not); a revision of a post the Forum already serves a revision of is <c>already-revised</c>, naming the
    /// first served successor in log order. "Served" is <see cref="CorrectionIndex.For"/>'s alone (through <see cref="Servable"/>), so this check, a
    /// post's <c>superseded_by</c> and the batch's <c>successors</c> cannot answer differently (R9.32); a successor the Forum does not serve does not count.
    /// </summary>
    private static IResult? RevisionRefusal(IReadOnlyList<AppendedEvent> fresh, string prevDigest, string digest)
    {
        if (AlreadyRecordedPost(fresh, digest) is { } recordedPostId)
            return Problem(StatusCodes.Status409Conflict, IngestErrors.AlreadyRecorded(recordedPostId));

        var posts = PostProjector.Fold(fresh);
        var original = posts.FirstOrDefault(p => string.Equals(p.Digest, prevDigest, StringComparison.Ordinal));

        // AUTHORIZE found this post in a log that has only grown, so its absence is a log this Forum cannot read now, never a pass.
        if (original is null)
            return Problem(StatusCodes.Status503ServiceUnavailable, IngestErrors.RevisionCheckUnavailable());

        var served = CorrectionIndex.Fold(posts, Servable(fresh)).For(original).FirstOrDefault(successor => successor.PostId is not null);
        return served is { PostId: { } successorPostId }
            ? Problem(StatusCodes.Status409Conflict, IngestErrors.AlreadyRevised(successorPostId))
            : null;
    }

    /// <summary>
    /// The rest of <see cref="SubmitAttemptAsync"/>, decided on <paramref name="log"/>: the parent checks, the signal refusal, the near-duplicate
    /// check, SCREEN and PERSIST, then the vector index. <see langword="null"/> when PERSIST's append found the author's stream moved (R6.56).
    /// </summary>
    private static async Task<IResult?> PersistSubmissionAsync(
        VerifiedSubmission v,
        IReadOnlyList<AppendedEvent> log,
        IIngestPipeline pipeline,
        IEventReader events,
        EmbeddingIndexer indexer,
        DuplicateCheck dedupe,
        HttpRequest http,
        CancellationToken cancellationToken)
    {
        // R8.70 (errata G38): an answer's parent is a question that exists and a comment's is a post that exists. After the
        // authorization above, so a caller denied by tier is never told whether a post exists; before the signal refusal, the
        // duplicate check, SCREEN and PERSIST, so nothing is screened or stored for a post that is refused. An unreadable log is
        // the Forum's failure (503) and never a missing parent.
        if (v.Envelope.Kind is PostKind.Answer or PostKind.Comment && v.Envelope.Parent is { } submittedParent)
        {
            // VERIFY has parsed this text already (a parent the log cannot store is its 422), so a failure here is the Forum's own.
            if (!ParentReference.Parse(submittedParent).TryGetValue(out var parentReference, out var parentReferenceError))
                return Problem(StatusCodes.Status500InternalServerError, parentReferenceError!);

            var found = await ParentLookup.FindAsync(events, parentReference!, cancellationToken).ConfigureAwait(false);
            if (!found.TryGetValue(out var parentFacts, out var parentReadError))
                return Problem(StatusCodes.Status503ServiceUnavailable, parentReadError!);

            if (ParentPolicy.Refusal(v.Envelope.Kind, submittedParent, parentFacts) is { } parentRefusal)
                return Problem(StatusCodes.Status400BadRequest, parentRefusal);

            // R8.72 (errata G41): the parent exists and is the kind this one needs, and moderation does not serve it. Last of the
            // parent checks (a withheld comment as an answer's parent is `parent-not-a-question`, the stored kind), after AUTHORIZE
            // (an under-tier caller is told the tier and never the post's state), before SCREEN and PERSIST (nothing stored, no
            // budget spent). `servable` is built from the log read above, so there is no second read and no caching (R7.4).
            if (parentFacts is not null && !Servable(log)(parentFacts.PostId))
                return Problem(StatusCodes.Status400BadRequest, ParentErrors.Withheld(submittedParent));
        }

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
        {
            // R6.56: the author's stream moved since VERIFY read it. Nothing was written; the caller
            // decides again from VERIFY (null answers "again").
            return string.Equals(persistError!.Type, DomainErrors.ConditionConflictType, StringComparison.Ordinal)
                ? null
                : Problem(StatusCodes.Status500InternalServerError, persistError);
        }

        // The vector channel sees the post in the same request the lexical channel does. The
        // index and the log are the same database, so a failure here is the database failing
        // after it just succeeded -- reported, not swallowed; the startup reconcile closes any gap
        // a crash between the two writes leaves (R11.10).
        if (PostKinds.IsDiscussion(v.Envelope.Kind))
        {
            var indexed = await indexer.IndexAsync(
                new SearchablePost(post!.PostId, post.Digest, v.Envelope.Board, v.Envelope.Kind, v.Envelope.Title,
                    v.Envelope.Body, v.Envelope.Tags, v.AuthorAgentId.Value, post.Sequence),
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
        IFlagDetailStore details,
        IFlagRaiserGate raisers,
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

        // R7.22, R10.70 (errata G18): the budget is counted and the flag recorded under one hold per
        // raiser, so concurrent flags cannot each read the same count. Refused, not queued.
        var entered = await raisers.TryEnterAsync(subject, cancellationToken).ConfigureAwait(false);
        if (!entered.TryGetValue(out var hold, out var holdError))
            return Problem(StatusFor(holdError!), holdError!);

        await using (hold!.ConfigureAwait(false))
        {
            // PEP-2. R7.13 evaluates authorization per request; R7.7 takes the tier from the log rather
            // than from the token's claim about it.
            var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
            if (!PrincipalOf(subject).TryGetValue(out var raiser, out var subjectError))
                return Problem(StatusCodes.Status403Forbidden, subjectError!);

            var posture = PostureQuery.Of(log, raiser!);

            if (!posture.TryGetValue(out var facts, out var postureError))
                return Problem(StatusCodes.Status500InternalServerError, postureError!);

            var now = clock.GetUtcNow();
            var tier = TierPolicy.Evaluate(facts!, now);

            // R7.22 (errata G18): the flag budget is counted from the log joined with the private store,
            // under the raiser's hold, so no other flag by this raiser is counted or recorded meanwhile.
            var (raisedFlags, flagsProblem) = await FlagsAsync(log, details, cancellationToken).ConfigureAwait(false);
            if (flagsProblem is not null)
                return flagsProblem;

            var decision = await pdp.EvaluateAsync(
                new AuthorizationRequest(
                    tier,
                    facts!.CredentialState,
                    ResourceKind.Flag,
                    ActionKind.Raise,
                    PostsToday: PostsInBudgetWindow(log, subject, now),
                    FlagsToday: FlagsInBudgetWindow(raisedFlags, subject, now)),
                cancellationToken).ConfigureAwait(false);

            if (!decision.TryGetValue(out var d, out var decisionError))
                return Problem(StatusCodes.Status403Forbidden, decisionError!);

            if (!d!.IsAllowed)
                return Problem(
                    StatusCodes.Status403Forbidden,
                    Denial(d, $"{d.Reason} tier={tier.Tier}"));

            // R11.34 (errata G18): the post id and the rationale reach RaiseFlag as the types it takes,
            // parsed at their first use, after authorization. Each parse outcome is passed rather than
            // refused here, so the use case answers every refusal where it answered it before the
            // types existed (R10.68's order: before screening, before the post is looked up).
            var raised = await flags
                .RecordAsync(
                    PostId.Parse(postId, "post_id"), raiser!, kind, Rationale.Parse(request.Rationale), cancellationToken)
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
        "curia/flag/rationale-too-long" => StatusCodes.Status422UnprocessableEntity,
        "curia/moderation/rationale-required" => StatusCodes.Status400BadRequest,

        // R10.70 (errata G18): this raiser already flagged this post with this type.
        "curia/flag/already-raised" => StatusCodes.Status409Conflict,

        // R7.22 (errata G18): another flag by this raiser is being counted and recorded. Nothing was
        // spent; the flag may be sent again.
        "curia/flag/raise-in-flight" => StatusCodes.Status409Conflict,

        // The private store could not be written; nothing was, in either store (R10.62).
        "curia/flag/detail-store-unavailable" => StatusCodes.Status503ServiceUnavailable,
        "curia/flag/raiser-gate-unavailable" => StatusCodes.Status503ServiceUnavailable,
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
        IFlagDetailStore details,
        AccessTokenValidationContext authn,
        IDpopNonceStore nonces,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var (ok, problem) = await AuthorizeFlagListAsync(http, pdp, events, authn, nonces, clock, cancellationToken)
            .ConfigureAwait(false);

        if (problem is not null) return problem;

        var (flags, detailProblem) = await FlagsAsync(ok!.Log, details, cancellationToken).ConfigureAwait(false);
        if (detailProblem is not null) return detailProblem;

        var mine = flags.Where(f => string.Equals(f.RaisedBy, ok.Subject, StringComparison.Ordinal));

        // Ownership is established by the filter immediately above, not claimed.
        var decision = ok.Decision.Discharge(satisfied: true);

        if (!decision.IsAllowed)
            return Problem(StatusCodes.Status403Forbidden, Denial(decision, decision.Reason));

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
        IFlagDetailStore details,
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
            return Problem(StatusCodes.Status403Forbidden, Denial(decision, $"{decision.Reason} post={postId}"));

        var (all, detailProblem) = await FlagsAsync(ok.Log, details, cancellationToken).ConfigureAwait(false);
        if (detailProblem is not null) return detailProblem;

        var flags = all.Where(f => string.Equals(f.PostId, postId, StringComparison.Ordinal));

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
        if (!PrincipalOf(subject).TryGetValue(out var lister, out var subjectError))
            return (null, Problem(StatusCodes.Status403Forbidden, subjectError!));

        var posture = PostureQuery.Of(log, lister!);

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
            return (null, Problem(StatusCodes.Status403Forbidden, Denial(d, d.Reason)));

        return (new FlagListContext(subject, log, d), null);
    }

    /// <summary>
    /// R7.18's flags, joined from the log's commitments and the private store (R10.62). A store that
    /// cannot be read is a 503: serving the log's half alone would list every committed flag as
    /// absent, which reads as "nothing was raised".
    /// </summary>
    private static async Task<(ImmutableArray<RaisedFlag> Flags, IResult? Problem)> FlagsAsync(
        IReadOnlyList<AppendedEvent> log, IFlagDetailStore details, CancellationToken cancellationToken)
    {
        var rows = await details.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!rows.TryGetValue(out var detailRows, out var error))
            return ([], Problem(StatusCodes.Status503ServiceUnavailable, error!));

        return (FlagDirectory.Join(log, detailRows!).Flags, null);
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

        // R9.42 (errata G41): a post that exists and is not served is answered "withheld", and an id no post bears
        // "not-found". The concealment this replaced bought nothing: the batch route (R9.10), the thread, a flag
        // and a reply each disclosed what it hid, and it cost an honest reader the difference between a typo and a
        // decision. Existence is the log's (the fold already run), servability is moderation's (the same fold's
        // `servable`), so there is no second read; a signal kind is no post a reader is served and keeps
        // not-found, so the new type cannot say that a vote exists. The body holds the id and fixed words, never
        // anything an author wrote. No ETag on either 404: a withheld post has changed in the one way a post can.
        if (post is null)
        {
            var exists = posts.Any(p => p.PostId == postId && ServedToReaders(p));
            return exists
                ? Results.NotFound(new Problem(ParentErrors.PostWithheldType, "This post is not being served", postId))
                : Results.NotFound(new Problem(ParentErrors.PostNotFoundType, "No such post", postId));
        }

        // R9.11 (rev., errata G6): the validator is a hash of the bytes served, not the content
        // digest. The envelope's owner_verified, verification_level and accepted members change
        // while the signed bytes do not, and a digest-keyed tag answered "unchanged" to exactly the
        // questions a citing agent asks -- see EntityTags. Serialised here, once, so the tag and the
        // body are computed from the same bytes and cannot disagree.
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);

        var standings = AgentStandingProjector.Fold(log);
        var disputed = DisputedPosts.Fold(log, posts);
        var representation = JsonSerializer.SerializeToUtf8Bytes(
            ToResponse(
                post, standings, marking, ReaderContractUrl(http),
                AcceptedAnswerIndex.Fold(log, posts, servable), VerificationProjector.Fold(posts, standings, servable, disputed), disputed,
                CorrectionIndex.Fold(posts, servable), ActaOf(log)),
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
        var accepted = AcceptedAnswerIndex.Fold(log, posts, servable);
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);
        var contract = ReaderContractUrl(http);

        var disputed = DisputedPosts.Fold(log, posts);
        var verification = VerificationProjector.Fold(posts, standings, servable, disputed);
        var corrections = CorrectionIndex.Fold(posts, servable);
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
                state.Post is null ? null : ToResponse(state.Post, standings, marking, contract, accepted, verification, disputed, corrections, acta),
                state.Post is null ? null : ImmutableArray.CreateRange(corrections.ForkOf(state.Post).Select(s => new SupersededByResponse(s.Digest, s.PostId)))));
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
        // A root id that is no post id names no post, so it has no thread: answered as any unknown
        // thread is. An id of white space alone once reached PostProjector.Thread, which threw, and an
        // anonymous GET /v1/threads/%0A answered 500 (register D25's sweep; R11.33, errata G17). Since
        // R11.34 (errata G18) Thread takes a PostId, parsed here at its first use.
        var posts = PostProjector.Fold(log);
        var thread = PostId.Parse(rootPostId, "root_post_id").TryGetValue(out var root, out _)
            ? ImmutableArray.CreateRange(
                PostProjector.Thread(posts, root!).Where(p => servable(p.PostId) && Discussion(p)))
            : [];
        var standings = AgentStandingProjector.Fold(log);
        var disputed = DisputedPosts.Fold(log, posts);
        var verification = VerificationProjector.Fold(posts, standings, servable, disputed);
        var corrections = CorrectionIndex.Fold(posts, servable);
        var acta = ActaOf(log);
        var accepted = AcceptedAnswerIndex.Fold(log, posts, servable);

        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);

        return thread.IsEmpty
            ? Results.NotFound(new Problem("curia/threads/not-found", "No such thread", rootPostId))
            : Results.Ok(thread
                .Select(p => ToResponse(p, standings, marking, ReaderContractUrl(http), accepted, verification, disputed, corrections, acta, threadRoot: root))
                .ToArray());
    }

    private static async Task<IResult> ListBoardAsync(
        string board, HttpRequest http, IEventReader events, IPolicyDecisionPoint pdp, TimeProvider clock, CancellationToken cancellationToken)
    {
        var allowed = await AnonymousReadAllowedAsync(pdp, clock, ResourceKind.Board, ActionKind.List, cancellationToken)
            .ConfigureAwait(false);
        if (allowed is not null) return allowed;

        // R9.37: a name the route does not read is refused by name, after the caller is known to be allowed and before the log is read.
        if (BoardListingQueryParameters.Check(http.Query.Keys) is { } unknownName)
            return Problem(StatusCodes.Status400BadRequest, unknownName);

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var standings = AgentStandingProjector.Fold(log);

        var servable = Servable(log);
        var posts = PostProjector.Fold(log);
        var accepted = AcceptedAnswerIndex.Fold(log, posts, servable);
        var disputed = DisputedPosts.Fold(log, posts);
        var verification = VerificationProjector.Fold(posts, standings, servable, disputed);
        var corrections = CorrectionIndex.Fold(posts, servable);
        var acta = ActaOf(log);

        // Discussion only: a vote is never served (R8.55) and a verification report is read on its
        // result's envelope, not listed beside the conversation (R8.59).
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);

        return Results.Ok(posts
            .Where(p => p.Board == board && servable(p.PostId) && Discussion(p))
            .Select(p => ToResponse(p, standings, marking, ReaderContractUrl(http), accepted, verification, disputed, corrections, acta))
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
    ///
    /// <para><b>Only the keys the log binds (R4.35, errata G16), each with where (R6.54).</b> A key the
    /// store holds and the log does not bind is honoured nowhere, so it is not published either: a
    /// post signed under one fails a reader's signature check, as it should. Each published key names
    /// the leaf that binds it (<c>curia_log_index</c>), so a reader can check the binding against a
    /// signed head instead of trusting this key set. An identity the store holds no row for is 404, as
    /// before; one whose every row the log refuses gets an empty set. An identity whose stream the fold
    /// REFUSES (R4.38: a lifecycle entry the fold cannot attribute, or a statement-less binding that is
    /// not the enrolment's own) is 503 with the fold's own problem type
    /// (<c>curia/keys/statement-mismatch</c>), the same answer as an unreadable log: no key of that
    /// agent is published until the stream folds, so a broken link never reads as a deliberate empty
    /// set.</para>
    /// </summary>
    private static async Task<IResult> GetJwks(
        string? agent, LogBoundKeys keys, IEventReader events, ILifecycleAppendGate gate, CancellationToken cancellationToken)
    {
        // R11.34 (errata G18): the agent as the AgentId the key set is read for. Bound as a nullable
        // string, so an absent parameter reaches this handler: bound non-nullable, the binder answered
        // curia/request/unreadable before this route's own answer was reached (the exercise's audit,
        // finding 5). Absent and blank are asked for an agent; any other value that is no agent id names
        // no agent the store holds keys for, and is answered as one.
        if (!AgentId.Parse(agent, "agent").TryGetValue(out var agentId, out var agentError))
        {
            return agentError!.Type is StorableText.AbsentType or StorableText.BlankType
                ? Results.BadRequest(new Problem(
                    "curia/keys/agent-required", "The 'agent' query parameter is required", null))
                : Results.NotFound(new Problem("curia/keys/unknown-agent", "No keys for that agent", agent));
        }

        var read = await keys.KeySetAsync(agentId!, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var keySet, out var readError))
            return Problem(StatusCodes.Status503ServiceUnavailable, readError!);

        if (keySet!.Stored == 0)
            return Results.NotFound(new Problem("curia/keys/unknown-agent", "No keys for that agent", agentId!.Value));

        // The leaf that binds each key, from the Acta folded once, as every route that serves a post
        // or a log document folds it: no index of the key set's own, which would be a second
        // computation of R6.47's leaf index that must agree with the fold forever (the spec's
        // Decision 8). A log that cannot be read is 503, as FoldAsync answers it. A log that reads
        // but will not fold into a tree publishes the keys without positions rather than no keys: a
        // reader then cannot check the binding, and says so (R6.54's absence is an absence).
        var (acta, failure) = await ActaEndpoints.FoldAsync(events, cancellationToken).ConfigureAwait(false);
        if (failure is ServerFault { Type: LogBoundKeys.LogUnreadableType })
            return failure;

        // R4.35 (rev.): the identity's lifecycle indices, from the same fold; left out when the log would
        // not fold, as the keys' own positions are, so an absence never reads as an empty lifecycle.
        var lifecycle = acta is not null && KeyLifecycle.Of(acta.Events, agentId!).TryGetValue(out var folded, out _)
            ? folded!.Entries.Select(e => acta.IndexOf(e.EventId)).Where(i => i is not null).Select(i => i!.Value).ToList()
            : null;

        // R4.38 (add.): the kinds the build approves and the Forum enables. A record that cannot be read
        // is the Forum's fault, never an empty approval.
        if (!LifecycleApprovalRecord.Embedded().TryGetValue(out var record, out var recordError))
            return Problem(StatusCodes.Status500InternalServerError, recordError!);

        return Results.Ok(Jwks.ForAgent(
            keySet.Bound,
            acta is null ? _ => null : acta.IndexOf,
            lifecycle,
            Jwks.LifecycleKinds(record!.Approved.Select(a => a.Kind), gate)));
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
        if (!PrincipalOf(subject).TryGetValue(out var acceptor, out var subjectError))
            return Problem(StatusCodes.Status403Forbidden, subjectError!);

        var posture = PostureQuery.Of(log, acceptor!);

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
        // R11.34 (errata G18): the id is parsed at its first use, this lookup; one that is no post id
        // names no post, and is answered as an id no post has.
        var posts = PostProjector.Fold(log);
        var answerId = PostId.Parse(postId, "post_id").TryGetValue(out var parsed, out _) ? parsed : null;
        var answer = answerId is null
            ? null
            : posts.FirstOrDefault(p => string.Equals(p.PostId, answerId.Value, StringComparison.Ordinal));

        if (answer is null)
            return Problem(StatusCodes.Status404NotFound, new Error(
                "curia/accept/no-such-post", "No such post", $"post={postId}"));

        // R8.75 (errata G44): an answer, or a revision whose correction chain ends at an answer. The walk is CorrectionIndex's (R9.28's rule,
        // hop by hop) over the log's posts, not the serving filter: a withheld hop is still a hop. `kind=` stays last in the detail.
        var corrections = CorrectionIndex.Fold(posts, static _ => true);
        var chainRoot = corrections.ChainRootOf(answer);
        var isAnswer = string.Equals(answer.Kind, PostKinds.Wire(PostKind.Answer), StringComparison.Ordinal);
        var isRevision = string.Equals(answer.Kind, PostKinds.Wire(PostKind.Revision), StringComparison.Ordinal);

        if (!isAnswer && !isRevision)
            return Problem(StatusCodes.Status400BadRequest, new Error(
                "curia/accept/not-an-answer",
                "Only an answer may be accepted",
                $"post={postId} kind={answer.Kind}"));

        if (isRevision && !string.Equals(chainRoot.Kind, PostKinds.Wire(PostKind.Answer), StringComparison.Ordinal))
            return Problem(StatusCodes.Status400BadRequest, new Error(
                "curia/accept/not-an-answer",
                "Only an answer may be accepted",
                $"post={postId} chain={chainRoot.Kind} kind={answer.Kind}"));

        // Table 10's "(own thread)": the thread this answer replies to must be one the caller
        // started. R8.75: the thread is the chain root's, an answer's parent being its thread root (R8.70); a revision's own `parent` is
        // written by its author and is read by nobody here.
        var root = chainRoot.Parent is { } parent
            ? posts.FirstOrDefault(p => string.Equals(p.PostId, parent, StringComparison.Ordinal))
            : null;

        d = d!.Discharge(root is not null && string.Equals(root.Author, subject, StringComparison.Ordinal));

        if (!d.IsAllowed)
            return Problem(
                StatusCodes.Status403Forbidden,
                Denial(d, $"{d.Reason} tier={tier.Tier}"));

        // R8.69 (errata G36): an acceptance is stated on the question (R9.36), so a root that is not one would be stated on no post
        // while the answer's own `accepted` read true. After the ownership check, so a caller who is not the asker is still told 403.
        if (!string.Equals(root!.Kind, PostKinds.Wire(PostKind.Question), StringComparison.Ordinal))
            return Problem(StatusCodes.Status400BadRequest, new Error(
                "curia/accept/root-not-a-question",
                "Only an answer to a question may be accepted",
                $"post={postId} root={root.PostId} kind={root.Kind}"));

        // The thread root is a post the log holds, whose id the Forum minted as a PostId; the type has
        // no other constructor, so it is rebuilt through Parse, and one that no longer parsed would be
        // the log's fault, answered as a record the log refused is (R11.34, errata G18).
        if (!PostId.Parse(root!.PostId, "thread_root").TryGetValue(out var threadRoot, out var rootError))
            return Problem(StatusCodes.Status500InternalServerError, rootError!);

        var recorded = await accept
            .RecordAsync(threadRoot!, answerId!, acceptor!, cancellationToken)
            .ConfigureAwait(false);

        if (!recorded.TryGetValue(out var acceptance, out var recordError))
            return Problem(StatusCodes.Status500InternalServerError, recordError!);

        // R8.68 (errata G33): 201 for an acceptance this call recorded, 200 for the one that already stood, with its
        // first time; `already_accepted` is always present, so a reader never has to infer a replay from the status.
        var receipt = new
        {
            thread_root = acceptance!.ThreadRoot,
            post_id = acceptance.AnswerId,
            accepted_at = acceptance.AcceptedAt.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            already_accepted = acceptance.AlreadyAccepted,
            // R8.71 (errata G40): always present; the replaced answer's post id, or JSON null.
            replaced_answer = acceptance.ReplacedAnswer,
            // R8.76 (errata G44): always present; null exactly when replaced_answer is, else whether the two texts share a chain root.
            replaced_relation = ReplacedRelation(acceptance.ReplacedAnswer, posts, corrections, acceptance.AnswerId),
        };

        return acceptance.AlreadyAccepted
            ? Results.Ok(receipt)
            : Results.Created($"/v1/threads/{Uri.EscapeDataString(root.PostId)}", receipt);
    }

    /// <summary>R8.76: <c>same-chain</c> where the replaced post and the named post end at one chain root, <c>other-answer</c> otherwise, <c>null</c> with no replacement.</summary>
    private static string? ReplacedRelation(string? replacedId, ImmutableArray<PostView> posts, CorrectionIndex corrections, string namedId)
    {
        if (replacedId is null) return null;

        var replaced = posts.FirstOrDefault(p => string.Equals(p.PostId, replacedId, StringComparison.Ordinal));
        var named = posts.FirstOrDefault(p => string.Equals(p.PostId, namedId, StringComparison.Ordinal));
        return replaced is not null && named is not null
            && string.Equals(corrections.ChainRootOf(replaced).PostId, corrections.ChainRootOf(named).PostId, StringComparison.Ordinal)
                ? "same-chain"
                : "other-answer";
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
        var disputed = DisputedPosts.Fold(log, views);
        var verification = VerificationProjector.Fold(views, standings, servable, disputed);
        var corrections = CorrectionIndex.Fold(views, servable);
        var accepted = AcceptedAnswerIndex.Fold(log, views, servable);
        var acta = ActaOf(log);
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);
        var contract = ReaderContractUrl(http);

        var answers = views
            .Where(p => string.Equals(p.Parent, canonical.PostId, StringComparison.Ordinal)
                && string.Equals(p.Kind, PostKinds.Wire(PostKind.Answer), StringComparison.Ordinal)
                && servable(p.PostId))
            .Select(p => ToResponse(p, standings, marking, contract, accepted, verification, disputed, corrections, acta))
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

    /// <summary>R8.78: the id of the first stored post (served or not) whose digest is <paramref name="digest"/>, in log order; <see langword="null"/> when none.</summary>
    private static string? AlreadyRecordedPost(IReadOnlyList<AppendedEvent> log, string digest) =>
        PostProjector.Fold(log).FirstOrDefault(p => string.Equals(p.Digest, digest, StringComparison.Ordinal))?.PostId;

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

        // R9.29: a name this route does not read is refused, after the refusable names (which keep their
        // redirect) and before any value is parsed, so the agent is told the name that is wrong and not the value
        // of a parameter it never meant. Keys, not ContainsKey: the fuzzer's read record is undisturbed.
        var unknownError = SearchQueryParameters.Check(http.Query.Keys);
        if (unknownError is not null)
            return Problem(StatusCodes.Status400BadRequest, unknownError);

        if (!TryReadLimit(http, "curia/search/invalid-limit", out var limit, out var limitError))
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

        // R11.34 (errata G18): `board` and `author` as the types the query takes. A value that is no
        // board name or no agent id names no post's board or author, so the filter matches nothing,
        // as it did when it was compared as text: the page is answered empty, at the place it was.
        var boardWire = Nullable(http.Query["board"].ToString());
        var authorWire = Nullable(http.Query["author"].ToString());
        BoardName? board = null;
        AgentId? author = null;
        var matchesNothing =
            (boardWire is not null && !BoardName.Parse(boardWire, "board").TryGetValue(out board, out _))
            | (authorWire is not null && !AgentId.Parse(authorWire, "author").TryGetValue(out author, out _));

        // R9.35: a filter that matches nothing mints no cursor, so a cursor presented with one came from some other query. It is a
        // mismatch by construction and not a first page, and it is refused before the query is built.
        if (matchesNothing && cursor is not null)
            return Problem(StatusCodes.Status400BadRequest, RetrievalCursor.QueryMismatch());

        var query = new SearchQuery(
            Text: Nullable(http.Query["q"].ToString()),
            Board: board,
            Kinds: kinds.ToImmutable(),
            Tags: TagFilter(http.Query["tags"].ToString()),
            Author: author,
            RequestedFloor: requestedFloor,
            Cursor: cursor,
            Limit: limit);

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        var views = PostProjector.Fold(log);
        var posts = views.ToDictionary(p => p.PostId, StringComparer.Ordinal);
        var standings = AgentStandingProjector.Fold(log);
        var disputed = DisputedPosts.Fold(log, views);
        var servable = Servable(log);
        var verification = VerificationProjector.Fold(views, standings, servable, disputed);
        var corrections = CorrectionIndex.Fold(views, servable);
        var acta = ActaOf(log);
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);
        var contract = ReaderContractUrl(http);
        var acceptedByThread = AcceptedAnswerIndex.Fold(log, views, servable);

        var searched = await search.SearchAsync(log, RetrievalSurface.RestSearch, query, verification, cancellationToken).ConfigureAwait(false);
        if (!searched.TryGetValue(out var page, out var searchError))
        {
            // R9.35: a cursor that belongs to another query is the caller's request and not a search failure.
            var mismatch = string.Equals(searchError!.Type, RetrievalCursor.QueryMismatch().Type, StringComparison.Ordinal);
            return Problem(mismatch ? StatusCodes.Status400BadRequest : StatusCodes.Status503ServiceUnavailable, searchError);
        }

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
        foreach (var hit in matchesNothing ? [] : page!.Results)
        {
            // A ranked post the serving projection does not have is dropped rather than served
            // half-formed. The two projections read the same events, so this cannot happen today --
            // and a result carrying no provenance envelope would violate R10.17 if it ever did.
            if (!posts.TryGetValue(hit.Post.PostId, out var view)) continue;

            results.Add(new SearchHitResponse(
                ToResponse(view, standings, marking, contract, acceptedByThread, verification, disputed, corrections, acta),
                Micro(hit.Score),
                wantsWhy ? WhyRanked(hit, page.Model.Id, page.K) : null));
        }

        return Results.Ok(new SearchResponse(
            results.ToImmutable(),
            matchesNothing ? null : page!.Next?.Encode(),
            new FloorResponse(
                RetrievalSurfaces.Wire(page.Surface),
                VerificationLevels.Wire(page.Floor),
                page.FloorSource,
                [.. RetrievalFloorPolicy.GradableKinds.Select(PostKinds.Wire)],
                [.. RetrievalFloorPolicy.UngradableKinds.Select(PostKinds.Wire)],
                // R9.30: a filter that matches nothing removed nothing; the counts are of the unfiltered search that ran.
                matchesNothing ? 0 : page.ExcludedByFloor,
                matchesNothing ? 0 : page.ExcludedDisputed,
                VerificationLevels.Wire(page.PublishedDefault),
                page.RequestedFloor is { } requested ? VerificationLevels.Wire(requested) : null,
                page.Clamped),
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
        if (!PrincipalOf(subject).TryGetValue(out var reader, out var subjectError))
            return Problem(StatusCodes.Status403Forbidden, subjectError!);

        var posture = PostureQuery.Of(log, reader!);

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
            return Problem(StatusCodes.Status403Forbidden, Denial(d, d.Reason));

        // R9.37: an unknown name is told before any value is judged (R9.29's order).
        if (InboxQueryParameters.Check(http.Query.Keys) is { } unknownName)
            return Problem(StatusCodes.Status400BadRequest, unknownName);

        if (!TryReadLimit(http, "curia/inbox/invalid-limit", out var limit, out var limitError))
            return Problem(StatusCodes.Status400BadRequest, limitError!);

        // R9.38: a cursor this route did not mint is refused, not read as a first page; judged before the corpus is selected.
        if (!SearchCursor.Decode(http.Query["cursor"].ToString()).TryGetValue(out var cursor, out var cursorError))
            return Problem(StatusCodes.Status400BadRequest, cursorError!);

        var inbox = InboxSelector.Select(log, reader!);

        var query = new LexicalQuery(
            Board: Nullable(http.Query["board"].ToString()),
            Tags: TagFilter(http.Query["tags"].ToString()),
            Cursor: cursor,
            Limit: limit);

        // The counts are scoped to the filters the agent actually asked for, which is the only
        // scoping that makes them actionable: "nothing open here" has to mean *here*. Reusing
        // LexicalSearch.Matches rather than re-testing the filters keeps one reading of "matching".
        var matching = inbox.Matching.Where(p => LexicalSearch.Matches(p, query)).ToArray();
        var mine = matching.Count(p => string.Equals(p.Author, subject, StringComparison.Ordinal));

        // R9.39: the answered figure is counted, like `mine`, from the corpus the request's filters scope, and so does not
        // depend on the page. It was once the remainder (matching - mine - hits), which says "answered" of questions never shown.
        var answered = inbox.Answered.Count(p => LexicalSearch.Matches(p, query));

        var hits = LexicalSearch.Search(inbox.Open, query);
        var views = PostProjector.Fold(log);
        var posts = views.ToDictionary(p => p.PostId, StringComparer.Ordinal);
        var standings = AgentStandingProjector.Fold(log);
        var disputed = DisputedPosts.Fold(log, views);
        var servable = Servable(log);
        var verification = VerificationProjector.Fold(views, standings, servable, disputed);
        var corrections = CorrectionIndex.Fold(views, servable);
        var acta = ActaOf(log);
        var accepted = AcceptedAnswerIndex.Fold(log, views, servable);
        if (!MarkingFrom(http).TryGetValue(out var marking, out var markingError))
            return Problem(StatusCodes.Status400BadRequest, markingError!);
        var contract = ReaderContractUrl(http);

        var results = ImmutableArray.CreateBuilder<PostResponse>();
        foreach (var hit in hits)
        {
            if (!posts.TryGetValue(hit.Post.PostId, out var view)) continue;
            results.Add(ToResponse(view, standings, marking, contract, accepted, verification, disputed, corrections, acta));
        }

        return Results.Ok(new InboxResponse(
            results.ToImmutable(),
            LexicalSearch.NextCursor(hits, limit)?.Encode(),
            matching.Length,
            mine,
            answered));
    }

    /// <summary>
    /// R9.7's page size, validated rather than clamped.
    ///
    /// <para><see cref="LexicalSearch"/> caps it too, because a domain function must be total over
    /// its inputs. The difference matters at the boundary: a client that asked for 1000 and silently
    /// received 100 would page through the corpus believing it had seen ten times what it had, so
    /// the transport refuses instead of quietly serving fewer.</para>
    /// </summary>
    private static bool TryReadLimit(HttpRequest http, string errorType, out int limit, out Error? error)
    {
        limit = LexicalSearch.DefaultLimit;
        error = null;

        if (http.Query["limit"].ToString() is not { Length: > 0 } raw) return true;

        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out limit)
            || limit < 1
            || limit > LexicalSearch.MaximumLimit)
        {
            // R9.44: the type names the route that refused; the title and the detail are the same for both.
            error = new Error(
                errorType,
                "limit must be an integer between 1 and the published maximum",
                $"received={raw} maximum={LexicalSearch.MaximumLimit.ToString(System.Globalization.CultureInfo.InvariantCulture)}");

            return false;
        }

        return true;
    }

    /// <summary>
    /// R9.26 and R9.45: the <c>tags</c> parameter as a set. Split on commas, read each element in Unicode normalization form C,
    /// then trimmed, and an element that is empty is dropped. A post is stored and signed in NFC (R6.9, R6.55), so a tag typed in
    /// NFD is found by the spelling its author typed as well as by its NFC spelling. Case is folded later, by the matcher.
    /// </summary>
    private static ImmutableArray<string> TagFilter(string raw) =>
        [.. raw.Split(',')
            .Select(element => NormalizedC(element).Trim())
            .Where(element => element.Length > 0)];

    /// <summary>
    /// NFC, or the element as it came when it is not normalizable: <c>string.Normalize</c> throws on a noncharacter such as U+FFFE, and a
    /// query is never a server fault (R11.33). Such an element names no stored tag, since a stored tag was normalized when it was signed.
    /// </summary>
    private static string NormalizedC(string element)
    {
        try
        {
            return element.Normalize(System.Text.NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return element;
        }
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
                v.AuthorAgentId.Value, OwnerOf(v.AuthorAgentId.Value), targetPost.Author, OwnerOf(targetPost.Author),
                targetKind, v.Envelope.Board, targetPost.Board) is { } refusal)
            return Problem(StatusCodes.Status422UnprocessableEntity, refusal);

        // R8.55: at most one vote stands from an agent for a target. A verification report may be
        // superseded by the same agent's later one (R8.56); a vote may not be recast, because a vote
        // that could be changed after the tally is visible is the thing epoch sealing exists to stop.
        if (v.Envelope.Kind is PostKind.Vote
            && posts.Any(p => string.Equals(p.Author, v.AuthorAgentId.Value, StringComparison.Ordinal)
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
        AcceptedAnswerIndex accepted,
        VerificationFold verification,
        DisputedPosts disputed,
        CorrectionIndex corrections,
        ActaLog? acta = null,
        PostId? threadRoot = null)
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
            Contradictions: state.Contradictions,

            // R6.57: the key's status and, for a post inside a declaration's window, the declaration's
            // log index. Disputed is a mark on a served post (R6.28), never a withholding.
            KeyStatus: disputed.KeyStatusOf(p),
            DisputedByLogIndex: disputed.DisputedByLogIndex(p),

            // R9.32: the Forum's word on who has corrected this post, folded once per request and asked per post.
            SupersededBy: [.. corrections.For(p).Select(s => new SupersededByResponse(s.Digest, s.PostId))],

            // R9.36: a question always carries the Forum's word on its accepted answer (none is a present null); no other kind does.
            // The one rule that sets the answer's `accepted` below also sets this, so the two cannot disagree.
            AcceptedAnswer: string.Equals(p.Kind, PostKinds.Wire(PostKind.Question), StringComparison.Ordinal)
                ? AcceptedAnswerResponse.From(accepted.StandingOf(p))
                : null);

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
            // accepted answer only of its own thread (AcceptedAnswerIndex, the rule the question's member shares).
            // R9.46: a thread listing marks a post accepted only in the thread that is the post's own.
            threadRoot is null ? accepted.IsAccepted(p) : accepted.IsAcceptedIn(p, threadRoot),
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
        // R5.22: a liveness read that failed decided nothing about the key; the server's fault, as every
        // other read of the log answers it (R4.35), and never the 401 that tells a client to mint again.
        if (string.Equals(error.Type, LogBoundKeys.LogUnreadableType, StringComparison.Ordinal))
            return Problem(StatusCodes.Status503ServiceUnavailable, error);

        if (error.Type is not ("curia/authn/nonce-missing" or "curia/authn/nonce-stale"))
            return Problem(StatusCodes.Status401Unauthorized, error);

        var issued = await nonces.IssueAsync(cancellationToken).ConfigureAwait(false);
        if (!issued.TryGetValue(out var nonce, out var nonceError))
            return Problem(StatusCodes.Status500InternalServerError, nonceError!);

        return new NonceChallenge(error, nonce!.Value.Value);
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

    /// <summary>
    /// R7.22's flag count (errata G18): the flags the agent raised in the posting budget's trailing
    /// 24 hours.
    ///
    /// <para><b>Counted from the joined directory, not the log alone.</b> A <c>flag.committed</c>
    /// leaf names its kind and a commitment and never its raiser (R10.62), so the log cannot say whose
    /// a flag is; the private store's row supplies the raiser, as it does for the flag listings.</para>
    /// </summary>
    private static int FlagsInBudgetWindow(
        ImmutableArray<RaisedFlag> flags, string agentId, DateTimeOffset now)
    {
        var since = now - TimeSpan.FromDays(1);

        return flags.Count(f =>
            string.Equals(f.RaisedBy, agentId, StringComparison.Ordinal)
            && f.At.Value > since);
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

    /// <summary>
    /// An RFC 9457 problem. A 5xx is served by <see cref="ServerFault"/>, without its detail, which
    /// goes to the log: a server fault's detail is what the failing component said about itself
    /// (R11.33, errata G17).
    /// </summary>
    private static IResult Problem(int status, Error error) =>
        status >= StatusCodes.Status500InternalServerError
            ? new ServerFault(status, error)
            : Results.Json(new Problem(error.Type, error.Title, error.Detail), statusCode: status);

    /// <summary>R7.24 (errata G24): a denial whose title is chosen from the decision's reason, never written at the site.</summary>
    private static Error Denial(AuthorizationDecision decision, string detail) =>
        new("curia/authz/denied", decision.Title, detail);

    private static readonly Error MalformedPath = new("curia/request/malformed-path", "The request path is malformed");

    /// <summary>
    /// The identity and the kid a key route names, read from the request target as the client wrote it and
    /// decoded exactly once (errata G21).
    ///
    /// <para>The framework's path has lost the distinction that matters here: Kestrel binds both <c>%2F</c>
    /// and <c>%252F</c> to the route value <c>%2F</c>, so a route value cannot say whether an identifier held
    /// a slash or a literal <c>%2F</c>, and restoring slashes from it made any identity with a percent
    /// sign in its identifier unable to rotate or retire. The raw target keeps the distinction: each
    /// segment is decoded once, left to right, by <see cref="DecodeOnce"/>, which refuses a percent sign
    /// not followed by two hex digits and a result that is not UTF-8, and never repairs one (R6.12). The
    /// segments are found by their position in the framework's normalised path (its <c>agents</c> segment,
    /// matched as the router matches it, without regard to case), after the query, any scheme and
    /// authority, and the path base are removed from the raw target; the raw target must have exactly as
    /// many segments as that path, which a dot-segment target does not, and it is never required to spell
    /// <c>agents</c> or <c>keys</c> as the router's literals do (<c>%61gents</c> routes). Each decoded
    /// value is then compared with the value the framework bound, in the form Kestrel binds it
    /// (<see cref="RouteForm"/>), so the routed URL, the <c>htu</c> built from the path and the identity
    /// acted on agree; a disagreement is refused as the request path.</para>
    ///
    /// <para>A host that hands the pipeline no raw target (the in-memory test server) takes the one fallback
    /// here: the framework's path with <c>%2F</c> restored once. It is the old, lossy reading, taken only
    /// when there is nothing better, and a fact on the real host pins that production does not take it.
    /// A proof's <c>htu</c> is still the path <see cref="AbsoluteUrl"/> builds, which cannot tell <c>%2F</c>
    /// from <c>%252F</c>, so a proof replays across those two URLs; that replay is bounded because the two
    /// URLs name two identities and the token's subject is compared with the identity decoded here, which
    /// is now checked against the routed value.</para>
    /// </summary>
    private static (string? Id, string? Kid, IResult? Refusal) KeyRouteValues(
        HttpRequest http, bool wantKid, string routedId, string? routedKid)
    {
        var rawTarget = http.HttpContext.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var fromRawTarget = !string.IsNullOrEmpty(rawTarget);

        var pathSegments = (http.Path.Value ?? string.Empty).Split('/');
        var agents = Array.FindIndex(pathSegments, p => string.Equals(p, "agents", StringComparison.OrdinalIgnoreCase));
        var idIndex = agents + 1;
        var kidIndex = idIndex + 2;
        if (agents < 0
            || idIndex + 1 >= pathSegments.Length
            || (wantKid && kidIndex >= pathSegments.Length))
            return (null, null, Problem(StatusCodes.Status400BadRequest, MalformedPath));

        var segments = pathSegments;
        if (fromRawTarget)
        {
            var path = rawTarget!;
            var query = path.IndexOf('?', StringComparison.Ordinal);
            if (query >= 0) path = path[..query];

            if (!path.StartsWith('/'))
            {
                var scheme = path.IndexOf("://", StringComparison.Ordinal);
                var slash = scheme < 0 ? -1 : path.IndexOf('/', scheme + 3);
                if (slash < 0) return (null, null, Problem(StatusCodes.Status400BadRequest, MalformedPath));
                path = path[slash..];
            }

            var pathBase = http.PathBase.Value;
            if (!string.IsNullOrEmpty(pathBase) && path.StartsWith(pathBase, StringComparison.OrdinalIgnoreCase))
                path = path[pathBase.Length..];

            segments = path.Split('/');
            if (segments.Length != pathSegments.Length)
                return (null, null, Problem(StatusCodes.Status400BadRequest, MalformedPath));
        }

        var id = fromRawTarget ? DecodeOnce(segments[idIndex]) : RestoreSlashes(segments[idIndex]);
        var kid = wantKid ? (fromRawTarget ? DecodeOnce(segments[kidIndex]) : RestoreSlashes(segments[kidIndex])) : string.Empty;
        if (id is null || kid is null
            || (fromRawTarget && !RouteFormAgrees(id, routedId))
            || (fromRawTarget && wantKid && !RouteFormAgrees(kid, routedKid ?? string.Empty)))
            return (null, null, Problem(StatusCodes.Status400BadRequest, MalformedPath));

        return (id, wantKid ? kid : null, null);
    }

    /// <summary>
    /// Whether a value decoded once from the raw target is the value the framework bound. Kestrel keeps
    /// <c>%2F</c> encoded in a route value and decodes everything else, <c>%5C</c> to a backslash included
    /// (seen by probe: a route value for <c>a%5Cb</c> is <c>a\b</c>), so the decoded value
    /// is put into that form (<see cref="RouteForm"/>) and compared ordinally, after the hex digits of the
    /// escapes are upper-cased on both sides: a letter's case is never folded, so <c>Alice</c> is not
    /// <c>alice</c>.
    /// </summary>
    private static bool RouteFormAgrees(string decoded, string routed) =>
        string.Equals(UpperEscapes(RouteForm(decoded)), UpperEscapes(routed), StringComparison.Ordinal);

    private static string RouteForm(string value) =>
        value.Replace("/", "%2F", StringComparison.Ordinal);

    private static string UpperEscapes(string value)
    {
        var chars = value.ToCharArray();
        for (var i = 0; i + 2 < chars.Length; i++)
        {
            if (chars[i] != '%' || !Uri.IsHexDigit(chars[i + 1]) || !Uri.IsHexDigit(chars[i + 2])) continue;
            chars[i + 1] = char.ToUpperInvariant(chars[i + 1]);
            chars[i + 2] = char.ToUpperInvariant(chars[i + 2]);
            i += 2;
        }

        return new string(chars);
    }

    private static string RestoreSlashes(string pathSegment) =>
        pathSegment.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One segment of a raw target decoded once, left to right: every <c>%</c> must be followed by two hex
    /// digits, and the bytes must be UTF-8. Anything else is null, never repaired. A decoded <c>%</c> is
    /// not read again, so <c>%252F</c> is the three characters <c>%2F</c>.
    /// </summary>
    private static string? DecodeOnce(string segment)
    {
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        var bytes = new List<byte>(segment.Length);
        try
        {
            for (var i = 0; i < segment.Length;)
            {
                if (segment[i] == '%')
                {
                    if (i + 2 >= segment.Length || !Uri.IsHexDigit(segment[i + 1]) || !Uri.IsHexDigit(segment[i + 2]))
                        return null;

                    bytes.Add(byte.Parse(segment.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture));
                    i += 3;
                    continue;
                }

                var end = segment.IndexOf('%', i);
                if (end < 0) end = segment.Length;
                bytes.AddRange(strict.GetBytes(segment[i..end]));
                i = end;
            }

            return strict.GetString(bytes.ToArray());
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The body limits of a key act (spec section 4.7): an object of at most three members, each a string,
    /// the statement the longest. Read through the guarded reader (R11.34), never the framework's binder,
    /// because the gate is the handler's first act and a binder reads the body before the handler runs.
    /// </summary>
    private static readonly AdmitLimits KeyActBodyLimits = new(
        MaxBytes: 32_768, MaxDepth: 2, MaxMembersPerObject: 3, MaxStringBytes: StatementText.MaxUtf8Bytes);

    private static readonly Error KeyActBodyUnreadable = new("curia/request/unreadable", "The request could not be read");

    /// <summary>
    /// POST /v1/agents/{id}/keys: R4.18 (rev.). The gate is the first statement (R4.38 (add.), GF1): before
    /// authentication, any token or proof is consumed, the body is read, the store is written or the log
    /// is read, a Forum that does not enable rotation answers 403 and nothing else happens. Then the token
    /// (its subject must be the route's identity), the PDP's <c>key</c>/<c>rotate</c>, the body, and the
    /// use case, whose refusals <see cref="StatusForKeyAct"/> maps.
    /// </summary>
    private static async Task<IResult> RotateKeyAsync(
        string id,
        HttpRequest http,
        RotateKey rotate,
        ILifecycleAppendGate gate,
        IPolicyDecisionPoint pdp,
        IEventReader events,
        AccessTokenValidationContext authn,
        IDpopNonceStore nonces,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!gate.Enables(LifecycleKind.Rotation))
            return Problem(StatusCodes.Status403Forbidden, KeyActErrors.RotationNotEnabled());

        var (routeId, _, pathProblem) = KeyRouteValues(http, wantKid: false, id, null);
        if (pathProblem is not null) return pathProblem;

        var (agent, refused) = await AuthorizeKeyActAsync(
            routeId!, ActionKind.Rotate, http, pdp, events, authn, nonces, clock, cancellationToken).ConfigureAwait(false);
        if (refused is not null) return refused;

        var (body, bodyProblem) = await ReadKeyActBodyAsync(http, ["statement", "authorization", "possession"], cancellationToken).ConfigureAwait(false);
        if (bodyProblem is not null) return bodyProblem;

        if (!StatementText.Parse(body!["statement"], "statement").TryGetValue(out var statement, out var statementError))
            return Problem(StatusCodes.Status400BadRequest, statementError!);
        if (!DetachedSignature.Parse(body["authorization"], "authorization").TryGetValue(out var authorization, out var authorizationError))
            return Problem(StatusCodes.Status400BadRequest, authorizationError!);
        if (!DetachedSignature.Parse(body["possession"], "possession").TryGetValue(out var possession, out var possessionError))
            return Problem(StatusCodes.Status400BadRequest, possessionError!);

        var rotated = await rotate
            .RotateAsync(agent!, statement!, authorization!, possession!, cancellationToken)
            .ConfigureAwait(false);

        if (!rotated.TryGetValue(out var recorded, out var rotateError))
            return Problem(StatusForKeyAct(rotateError!), rotateError!);

        return Results.Created(
            $"/v1/jwks?agent={Uri.EscapeDataString(recorded!.AgentId)}",
            new
            {
                agent_id = recorded.AgentId,
                kid = recorded.Kid,
                log_index = recorded.LogIndex,
                authorized_by = recorded.AuthorizedBy,
            });
    }

    /// <summary>
    /// POST /v1/agents/{id}/keys/{kid}/retire: R4.39. The gate first, as <see cref="RotateKeyAsync"/>'s is.
    /// A retirement spends neither the posting budget nor R7.23's rotation budget.
    /// </summary>
    private static async Task<IResult> RetireKeyAsync(
        string id,
        string kid,
        HttpRequest http,
        RetireKey retire,
        ILifecycleAppendGate gate,
        IPolicyDecisionPoint pdp,
        IEventReader events,
        AccessTokenValidationContext authn,
        IDpopNonceStore nonces,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!gate.Enables(LifecycleKind.Retirement))
            return Problem(StatusCodes.Status403Forbidden, KeyActErrors.RetirementNotEnabled());

        var (routeId, routeKid, pathProblem) = KeyRouteValues(http, wantKid: true, id, kid);
        if (pathProblem is not null) return pathProblem;

        var (agent, refused) = await AuthorizeKeyActAsync(
            routeId!, ActionKind.Retire, http, pdp, events, authn, nonces, clock, cancellationToken).ConfigureAwait(false);
        if (refused is not null) return refused;

        var (body, bodyProblem) = await ReadKeyActBodyAsync(http, ["statement", "authorization"], cancellationToken).ConfigureAwait(false);
        if (bodyProblem is not null) return bodyProblem;

        if (!Kid.Parse(routeKid, "kid").TryGetValue(out var parsedKid, out var kidError))
            return Problem(StatusCodes.Status400BadRequest, kidError!);
        if (!StatementText.Parse(body!["statement"], "statement").TryGetValue(out var statement, out var statementError))
            return Problem(StatusCodes.Status400BadRequest, statementError!);
        if (!DetachedSignature.Parse(body["authorization"], "authorization").TryGetValue(out var authorization, out var authorizationError))
            return Problem(StatusCodes.Status400BadRequest, authorizationError!);

        var retired = await retire
            .RetireAsync(agent!, parsedKid!, statement!, authorization!, cancellationToken)
            .ConfigureAwait(false);

        if (!retired.TryGetValue(out var recorded, out var retireError))
            return Problem(StatusForKeyAct(retireError!), retireError!);

        return Results.Created(
            $"/v1/jwks?agent={Uri.EscapeDataString(recorded!.AgentId)}",
            new { agent_id = recorded.AgentId, kid = recorded.Kid, log_index = recorded.LogIndex });
    }

    /// <summary>
    /// The authentication and authorization both key routes share. The token's subject must be the route's
    /// identity ("own", Table 10, checked here before the PDP is asked), the tier is read from the log
    /// (R7.7), and the PDP answers <c>key</c>/<paramref name="action"/>: a quarantined agent is refused
    /// <c>rotate</c> and permitted <c>retire</c> (R4.39). Returns the agent, or the refusal to answer with.
    /// </summary>
    private static async Task<(AgentId? Agent, IResult? Refusal)> AuthorizeKeyActAsync(
        string id,
        ActionKind action,
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
                RequireDpopNonce: true),
            authn,
            cancellationToken).ConfigureAwait(false);

        if (!principal.TryGetValue(out var authenticated, out var authError))
            return (null, await NonceChallengeOrProblemAsync(authError!, nonces, cancellationToken).ConfigureAwait(false));

        var subject = authenticated!.Claims.Sub;
        if (!string.Equals(subject, id, StringComparison.Ordinal))
            return (null, Problem(StatusCodes.Status403Forbidden, KeyActErrors.NotOwn()));

        if (!PrincipalOf(subject).TryGetValue(out var agent, out var subjectError))
            return (null, Problem(StatusCodes.Status403Forbidden, subjectError!));

        var log = await ReadEventsAsync(events, cancellationToken).ConfigureAwait(false);
        if (!PostureQuery.Of(log, agent!).TryGetValue(out var facts, out var postureError))
            return (null, Problem(StatusCodes.Status500InternalServerError, postureError!));

        var tier = TierPolicy.Evaluate(facts!, clock.GetUtcNow());
        var decision = await pdp.EvaluateAsync(
            new AuthorizationRequest(tier, facts!.CredentialState, ResourceKind.Key, action),
            cancellationToken).ConfigureAwait(false);

        if (!decision.TryGetValue(out var d, out var decisionError))
            return (null, Problem(StatusCodes.Status403Forbidden, decisionError!));

        // The (own) qualifier is answered: the subject is the route's identity, established above.
        d = d!.Discharge(true);
        return d.IsAllowed
            ? (agent, null)
            : (null, Problem(
                StatusCodes.Status403Forbidden,
                Denial(d, $"{d.Reason} tier={tier.Tier}")));
    }

    /// <summary>
    /// Reads a key act's body: at most <see cref="KeyActBodyLimits"/>, an object whose members are exactly
    /// <paramref name="names"/> and every one a string, through the guarded reader. Anything else is the
    /// unreadable request, with no detail, so nothing the caller sent is echoed.
    /// </summary>
    private static async Task<(Dictionary<string, string>? Members, IResult? Problem)> ReadKeyActBodyAsync(
        HttpRequest http, string[] names, CancellationToken cancellationToken)
    {
        var buffer = new byte[KeyActBodyLimits.MaxBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await http.Body.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }

        if (length > KeyActBodyLimits.MaxBytes)
            return (null, Problem(StatusCodes.Status413PayloadTooLarge, new Error("curia/request/too-large", "The request is larger than this route reads")));

        if (!JsonReader.Parse(buffer.AsSpan(0, length), KeyActBodyLimits).TryGetValue(out var root, out _)
            || root is not JsonValue.Object body
            || body.Members.Length != names.Length)
            return (null, Problem(StatusCodes.Status400BadRequest, KeyActBodyUnreadable));

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var member = body.Members.FirstOrDefault(m => string.Equals(m.Key, name, StringComparison.Ordinal));
            if (member.Value is not JsonValue.String text)
                return (null, Problem(StatusCodes.Status400BadRequest, KeyActBodyUnreadable));

            members[name] = text.Value;
        }

        return (members, null);
    }

    /// <summary>
    /// The status a key act's refusal reports (spec section 4.7). Matched on the condition's published
    /// slug; a refusal the table does not list is the Forum's, 500, never a 4xx that tells an agent its
    /// request was at fault.
    /// </summary>
    private static int StatusForKeyAct(Error error) => error.Type switch
    {
        KeyStatementErrors.UnreadableType
            or KeyStatementErrors.NotCanonicalType
            or KeyStatementErrors.MismatchType
            or KeyStatementErrors.SignatureInvalidType
            or KeyStatementErrors.JwkAlgMismatchType
            or KeyStatementErrors.SelfAuthorizedType
            or EnrollmentErrors.IdentifierControlCharacterType
            or StorableText.AbsentType
            or StorableText.BlankType
            or StorableText.NulType
            or StorableText.UnpairedSurrogateType
            or StorableText.TooLongType
            or KeyActErrors.BeforeBindingType
            or KeyActErrors.FromIndexBeyondLogType => StatusCodes.Status400BadRequest,

        KeyActErrors.AuthorizingKeyNotLiveType => StatusCodes.Status403Forbidden,
        KeyActErrors.UnknownKeyType => StatusCodes.Status404NotFound,

        AuthorKeyErrors.KidTakenType
            or KeyActErrors.LifecycleMovedType
            or KeyActErrors.TooManyLiveKeysType
            or KeyActErrors.RotationCeilingReachedType
            or KeyActErrors.NotLiveType
            or KeyActErrors.LastLiveKeyType
            or KeyActErrors.LogKeyNotPublishedType => StatusCodes.Status409Conflict,

        KeyActErrors.RotationBudgetExhaustedType => StatusCodes.Status429TooManyRequests,
        LogBoundKeys.LogUnreadableType => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>
    /// R11.34 (errata G18): the token's subject as the <see cref="AgentId"/> a use case takes, parsed
    /// at its first use, after authentication. The issuer signs a token only for an identity the log
    /// holds, under an identifier enrollment admitted, so no token this Forum issued is refused here;
    /// one that were would name no agent, and is denied, 403, as an agent the log does not hold is.
    /// </summary>
    private static Result<AgentId> PrincipalOf(string subject) =>
        AgentId.Parse(subject, "sub").Match(
            Result<AgentId>.Ok,
            _ => Result<AgentId>.Fail(new Error("curia/authz/denied", AccessPolicy.TitleFor(null), "the token's subject names no agent")));
}
