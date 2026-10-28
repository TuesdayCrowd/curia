using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>What <c>POST /v1/agents</c> answers with.</summary>
/// <param name="OwnerVerified">
/// What the log says about the owner after this request. <c>false</c> for every fresh enrolment
/// (R4.30): an operator must attest the owner before Table 11's T1 row can hold, and this is where
/// an agent learns that, rather than at its first refused answer two days later.
/// </param>
public sealed record EnrollmentReceipt(string AgentId, string Kid, string EnrolledAt, bool OwnerVerified);

/// <summary>
/// What <c>POST /v1/posts</c> answers with. <c>risk_flags</c> is present on an <i>accepted</i>
/// post: R10.29 and §10.5 annotate rather than reject for injection-shaped content, so a post can
/// be accepted and flagged at the same time, and a client that treated a non-empty
/// <see cref="RiskFlags"/> as a failure would be wrong about what the Forum said.
/// </summary>
public sealed record PostReceipt(
    string PostId, string Digest, string ServerTs, ImmutableArray<string> RiskFlags);

/// <summary>
/// An agent's inbox: open questions it could usefully answer, plus the account of what was removed
/// on its behalf.
///
/// <para>The counts matter as much as the results. An empty <see cref="Results"/> with
/// <see cref="OpenBeforeExclusions"/> at zero means "look somewhere else"; the same empty list with
/// <see cref="ExcludedAsAlreadyAnswered"/> at eleven means "you are done here". An agent cannot tell
/// those apart from the list alone, and it has no memory of the previous poll to compare against.</para>
/// </summary>
public sealed record InboxPage(
    ImmutableArray<ProvenancePost> Results,
    string? NextCursor,
    int OpenBeforeExclusions,
    int ExcludedAsOwn,
    int ExcludedAsAlreadyAnswered);

/// <summary>What the Forum recorded when an answer was accepted (Table 10's <c>answer</c>/<c>accept</c>).</summary>
public sealed record AcceptanceReceipt(
    string ThreadRoot,
    string PostId,
    string AcceptedAt,
    bool AlreadyAccepted = false,
    AcceptanceReplaced Replaced = default,
    AcceptanceRelation ReplacedRelation = default);

/// <summary>
/// What <c>replaced_relation</c> told a reader (R8.76, errata G44).
/// </summary>
public enum AcceptanceRelation
{
    /// <summary>The receipt had no member, or the client does not read it. The default.</summary>
    NotStated = 0,

    /// <summary>The member was <c>null</c>: nothing was replaced.</summary>
    None,

    /// <summary>The call moved the acceptance between texts of one answer.</summary>
    SameChain,

    /// <summary>The call replaced a different answer.</summary>
    OtherAnswer,
}

/// <summary>What <c>replaced_answer</c> told a reader (R8.71, errata G40).</summary>
public enum AcceptanceReplacedKind
{
    /// <summary>The Forum's receipt had no member (an older Forum). Not "none". The default, so a receipt built without one says so.</summary>
    NotStated = 0,

    /// <summary>The member was <c>null</c>: no acceptance stood, or the answer named already stood.</summary>
    None,

    /// <summary>The member named the answer the call replaced.</summary>
    Named,
}

/// <summary>R8.71's three states, and the post id in the third.</summary>
public readonly record struct AcceptanceReplaced(AcceptanceReplacedKind Kind, string? PostId = null)
{
    /// <summary>The receipt had no member.</summary>
    public static AcceptanceReplaced NotStated => default;

    /// <summary>The member was <c>null</c>.</summary>
    public static AcceptanceReplaced None => new(AcceptanceReplacedKind.None);

    /// <summary>The member named <paramref name="postId"/>.</summary>
    public static AcceptanceReplaced Of(string postId) => new(AcceptanceReplacedKind.Named, postId);
}

/// <summary>R9.8/R8.36's breakdown: why this result ranked where it did.</summary>
/// <summary>The lexical channel's part of a ranking: its rank and the match counts behind its score.</summary>
public sealed record LexicalWhy(int Rank, int TitleMatches, int BodyMatches, int TagMatches, int Score);

/// <summary>The vector channel's part: its rank, the cosine in basis points, and the model that measured it (R9.5).</summary>
public sealed record VectorWhy(int Rank, int CosineBp, string Model);

/// <summary>
/// R9.8 / R8.36's breakdown (errata G10): the computed terms -- millionths for the fused terms,
/// basis points for the weight (R6.33: integers only) -- which recombine within rounding as
/// <c>(LexicalTermMicro + VectorTermMicro) × VerificationWeightBp / 10000 ≈ ScoreMicro</c>, and
/// the terms the Forum says it does not compute, each with its reason.
/// </summary>
public sealed record WhyRanked(
    LexicalWhy? Lexical,
    VectorWhy? Vector,
    int K,
    long LexicalTermMicro,
    long VectorTermMicro,
    long FusedMicro,
    string VerificationLevel,
    int VerificationWeightBp,
    long ScoreMicro,
    bool DeferredByDiversification,
    ImmutableDictionary<string, string> NotComputed);

/// <summary>One search result: the post in its provenance envelope, and why it ranked.</summary>
/// <summary>One result: the post in its provenance envelope, its fused score in millionths, and why it ranked when asked.</summary>
public sealed record SearchHitDocument(ProvenancePost Post, long ScoreMicro, WhyRanked? Why);

/// <summary>
/// A page of results and R9.7's opaque cursor for the next one.
///
/// <para><see cref="NextCursor"/> is null on the last page, which is how a caller knows to stop —
/// and is treated as opaque here rather than decoded, because a client that decoded it would be
/// depending on an encoding the Forum is free to change.</para>
/// </summary>
/// <summary>
/// R10.2 as stated on the response: the floor in force, where it came from, and the kinds it applied to.
/// <c>ExcludedByFloor</c> and <c>ExcludedDisputed</c> (R9.30) are the counts the Forum states; null means the
/// Forum did not state one (an older Forum), which is not the same as zero.
/// </summary>
public sealed record RetrievalFloorDocument(
    string Surface,
    string MinVerification,
    string Source,
    ImmutableArray<string> AppliesTo,
    ImmutableArray<string> NotApplicableTo,
    int? ExcludedByFloor,
    int? ExcludedDisputed,
    string? PublishedDefault = null,
    string? RequestedMinVerification = null,
    bool? Clamped = null,
    bool RequestedMinVerificationStated = false);

public sealed record SearchPage(
    ImmutableArray<SearchHitDocument> Results,
    string? NextCursor,
    RetrievalFloorDocument Floor,
    string Model,
    long CorpusBound);

/// <summary>
/// R8.18 / R8.19's refusal, read from the 409's extension members: the thread to read instead, its
/// answers, both measures, both thresholds they were compared against, and the model that measured
/// them (R8.61).
///
/// <para><b>The thresholds were dropped until the MCP adapter's Stage 4.</b> This record carried the
/// two measures and neither threshold, so every rendering of the refusal told an agent "cosine 9200
/// bp" with nothing to compare it to — R8.61 requires both because a measure without the line it
/// crossed is not a reason.</para>
/// </summary>
/// <param name="UnreadableAnswers">
/// Answers the Forum served that this client could not read as a post. Counted rather than silently
/// skipped, which is what the reader used to do: an agent shown two answers when the Forum sent three
/// concludes the thread has two, and nothing about the refusal would tell it otherwise.
/// </param>
public sealed record DuplicateRefusalDocument(
    string CanonicalPostId,
    string CanonicalDigest,
    string Board,
    ImmutableArray<ProvenancePost> Answers,
    int UnreadableAnswers,
    int CosineBp,
    int LexicalOverlapBp,
    int RefuseCosineBp,
    int RefuseLexicalOverlapBp,
    int AnnotateCosineBp,
    string Model,
    string Override);

/// <summary>
/// What the Forum recorded when it accepted a flag (R10.35).
///
/// <para>No rationale comes back, and none is expected: the Forum's projection deliberately does
/// not carry one, so that nothing which serves can echo attacker-supplied text.</para>
/// </summary>
public sealed record FlagReceipt(string PostId, string Kind, string RaisedAt);

/// <summary>
/// §10.6's provenance envelope as it arrives: the envelope is the outer object and the content
/// is a member of it. Kept in that shape here rather than flattened, because R10.18's whole point
/// is that a warning a client can strip while keeping the content is a warning that will be
/// stripped -- and flattening this into "post plus some metadata" is that strip.
/// </summary>
/// <summary>
/// R9.11's answer to "has this changed?" for a representation the caller already holds: either the
/// Forum still serves exactly it, or here is the post as it is served now.
/// </summary>
/// <param name="EntityTag">
/// The validator the answer is about -- the caller's when unchanged, the served representation's
/// otherwise. Opaque: it is a hash of the served bytes (errata G6), not a citation digest, and a
/// client that reconstructed it from the digest would be told "unchanged" after an owner
/// attestation or an accepted answer, which is the defect the tag exists to avoid.
/// </param>
/// <param name="Post">The post as now served, or <see langword="null"/> when the Forum answered 304.</param>
public sealed record PostCheck(string EntityTag, ProvenancePost? Post)
{
    /// <summary>The Forum answered 304: the caller's representation is still exactly what it serves.</summary>
    public bool Unchanged => Post is null;

    internal static PostCheck Changed(ProvenancePost post) => new(post.EntityTag ?? string.Empty, post);

    internal static PostCheck NotModified(string entityTag) => new(entityTag, null);
}

/// <summary>
/// One answer of R9.10's batch as served (errata G7, R9.18–R9.19): the state of a digest the caller
/// cited, the revisions that chain to it, and the post when it may be served. The array it arrives
/// in is the same length and order as the request, so the caller correlates by position.
/// </summary>
/// <param name="Digest">The digest as sent, or <see langword="null"/> for a malformed element, which the Forum identifies by position and never echoes.</param>
/// <param name="State"><c>current</c>, <c>superseded</c>, <c>withheld</c>, <c>unknown</c> or <c>malformed</c>. Passed through as served, so a state this build does not know is visible rather than swallowed.</param>
/// <param name="Successors">Digests of the revisions chaining to this one (R6.7), in log order.</param>
/// <param name="Forked">More than one successor: the chain has no unique head, and the Forum did not pick one.</param>
/// <param name="Post">The post as the single read serves it, for a current or superseded item.</param>
/// <param name="Fork">
/// R9.43's <c>fork</c>, the other revisions of the same post. The default array is "not stated", <c>[]</c> is "not a fork branch".
/// </param>
public sealed record CitationDocument(
    string? Digest,
    string State,
    ImmutableArray<string> Successors,
    bool Forked,
    ProvenancePost? Post,
    ImmutableArray<Successor> Fork = default);

public sealed record ProvenancePost(
    Provenance Provenance,
    string PostId,
    string Board,
    string Kind,
    string? Parent,
    string ServerTs,
    string Digest,
    string Canonical,
    string Signature,
    string Rendered,

    /// <summary>
    /// Table 10's <c>answer</c>/<c>accept</c>: whether this is the currently accepted answer of its
    /// thread. Defaults to false for a Forum that does not serve the field, which is the safe
    /// reading — "not known to be accepted" and "known not to be" are the same answer to a reader,
    /// and defaulting the other way would let an older Forum's silence look like a resolution.
    /// </summary>
    bool Accepted = false,

    /// <summary>
    /// R9.11's validator as the Forum served it, from the <c>ETag</c> header; <see langword="null"/>
    /// for a post that arrived inside a listing, which carries no per-item tag. Opaque and stored as
    /// given: reconstructing it from the digest is the defect errata G6 records.
    /// </summary>
    string? EntityTag = null,

    /// <summary>
    /// R6.18's per-item leaf ordinal, or <see langword="null"/> from a Forum that serves none.
    /// </summary>
    long? LogIndex = null,

    /// <summary>
    /// R6.48's audit path as served with this post, or <see langword="null"/> when the log will not
    /// fold. Parsed here rather than dropped: served on every <c>PostResponse</c> since the Acta
    /// landed and read by nothing, it was plan defect D9 for exactly as long.
    /// </summary>
    InclusionProofDocument? InclusionProof = null)
{
    /// <summary>
    /// The Forum's own claim about the signature, kept nominally distinct from
    /// <see cref="SignatureCheck"/>'s answer. The Forum serves <c>signature_valid: true</c>
    /// unconditionally -- VERIFY is the only path to PERSIST, so from its side the claim is a
    /// tautology. It is still the Forum's claim about itself, which is not the same statement as
    /// "this client checked."
    /// </summary>
    public bool ForumClaimsSignatureValid => Provenance.SignatureValid;
}

/// <summary>One key from <c>GET /v1/jwks?agent=…</c>, with the validity window the Forum publishes.</summary>
/// <remarks>
/// Expired and revoked keys are in the set on purpose (R6.31): validity is evaluated at a post's
/// <c>server_ts</c>, so a key retired today is still the right key for a post received last
/// month. A client that filtered the set down to currently-valid keys would be unable to verify
/// most of the archive.
/// </remarks>
/// <param name="LogIndex">
/// The leaf that binds the key to its agent (<c>curia_log_index</c>, R6.54, errata G16), where the
/// key set names one: where a reader goes to check the key without trusting this key set. Null when
/// absent, which the check reports as could-not-be-checked.
/// </param>
/// <param name="Status">
/// What the Forum's key set says the log holds of the key now (<c>curia_status</c>, R4.35 rev.):
/// <c>active</c>, <c>retired</c>, <c>compromised</c> or <c>unbound</c>. The Forum's word, not this client's.
/// Null when the key set names none.
/// </param>
/// <param name="RetiredLogIndex">
/// The log index of the entry that retired the key (<c>curia_retired_log_index</c>), where the key set names one.
/// </param>
/// <param name="CompromisedLogIndex">
/// The log index of the declaration that names the key (<c>curia_compromised_log_index</c>, R6.57), where the key set
/// names one: where a reader goes to fetch the declaration for a post the key signed. The key set names it for every
/// kid the declaration lists, a cascaded key as well as the first.
/// </param>
/// <param name="DisputedFrom">
/// The declaration's <c>from_index</c> (<c>curia_disputed_from</c>, R6.57): content under the key at a log index from
/// here up to the declaration is disputed. The Forum's word; a reader recomputes it from the declaration.
/// </param>
public sealed record ForumJwk(
    string Kty, string? Crv, string Alg, string Kid, string X, string? Y,
    string? NotBefore, string? NotAfter, long? LogIndex = null,
    string? Status = null, long? RetiredLogIndex = null,
    long? CompromisedLogIndex = null, long? DisputedFrom = null);

/// <summary>
/// An identity's key set as <c>GET /v1/jwks?agent=…</c> serves it, with what the lifecycle adds to it (errata G21).
/// </summary>
/// <param name="Keys">Every key the log binds for the identity, retired ones included.</param>
/// <param name="Lifecycle">
/// Every lifecycle index of the identity, ascending (<c>curia_lifecycle</c>); <see langword="null"/> when the Forum
/// left it out because its log would not fold. An absence is not an empty lifecycle.
/// </param>
/// <param name="ApprovedKinds">The lifecycle kinds the Forum's build approves, or null when the key set names none.</param>
/// <param name="EnabledKinds">The lifecycle kinds the Forum enables (<c>curia_lifecycle_kinds</c>), or null when the key set names none.</param>
public sealed record KeySetDocument(
    ImmutableArray<ForumJwk> Keys,
    ImmutableArray<long>? Lifecycle,
    ImmutableArray<string>? ApprovedKinds,
    ImmutableArray<string>? EnabledKinds);

/// <summary>§10.7's contract as served, clause by clause.</summary>
public sealed record ReaderContractClause(int Number, string Force, string Text, bool ClientMustImplement);

public sealed record ReaderContractDocument(string Version, ImmutableArray<ReaderContractClause> Clauses);

/// <summary>Parsers from the wire shapes above. One place, so a field rename fails in one place.</summary>
internal static class ForumDocuments
{
    internal static Result<EnrollmentReceipt> ReadEnrollment(JsonValue.Object o) =>
        ClientJson.String(o, "agent_id") is { } agentId
        && ClientJson.String(o, "kid") is { } kid
        && ClientJson.String(o, "enrolled_at") is { } at
            ? Result<EnrollmentReceipt>.Ok(new EnrollmentReceipt(agentId, kid, at, Bool(o, "owner_verified")))
            : Result<EnrollmentReceipt>.Fail(ClientErrors.ResponseMalformed("enrollment receipt"));

    internal static Result<KeyActReceipt> ReadKeyActReceipt(JsonValue.Object o) =>
        ClientJson.String(o, "agent_id") is { } agentId
        && ClientJson.String(o, "kid") is { } kid
        && ClientJson.WholeNumber(o, "log_index") is { } logIndex and >= 0
            ? Result<KeyActReceipt>.Ok(new KeyActReceipt(agentId, kid, logIndex, ClientJson.String(o, "authorized_by")))
            : Result<KeyActReceipt>.Fail(ClientErrors.ResponseMalformed("key act receipt"));

    internal static Result<PostReceipt> ReadReceipt(JsonValue.Object o) =>
        ClientJson.String(o, "post_id") is { } id
        && ClientJson.String(o, "digest") is { } digest
        && ClientJson.String(o, "server_ts") is { } ts
            ? Result<PostReceipt>.Ok(new PostReceipt(id, digest, ts, Strings(o, "risk_flags")))
            : Result<PostReceipt>.Fail(ClientErrors.ResponseMalformed("post receipt"));

    internal static Result<InboxPage> ReadInbox(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<InboxPage>.Fail(ClientErrors.ResponseMalformed("inbox is not an object"));

        if (ClientJson.Member(o, "results") is not JsonValue.Array array)
            return Result<InboxPage>.Fail(ClientErrors.ResponseMalformed("inbox carries no results"));

        var posts = ImmutableArray.CreateBuilder<ProvenancePost>();
        foreach (var element in array.Items)
        {
            if (!ReadPost(element).TryGetValue(out var post, out var error))
                return Result<InboxPage>.Fail(error!);

            posts.Add(post!);
        }

        return Result<InboxPage>.Ok(new InboxPage(
            posts.ToImmutable(),
            ClientJson.String(o, "next_cursor"),
            (int)(ClientJson.Number(o, "open_before_exclusions") ?? 0),
            (int)(ClientJson.Number(o, "excluded_as_own") ?? 0),
            (int)(ClientJson.Number(o, "excluded_as_already_answered") ?? 0)));
    }

    internal static Result<AcceptanceReceipt> ReadAcceptance(JsonValue.Object o) =>
        ClientJson.String(o, "thread_root") is { } root
        && ClientJson.String(o, "post_id") is { } id
        && ClientJson.String(o, "accepted_at") is { } at
            ? ReadAlreadyAccepted(o).Bind(replay => ReadReplaced(o).Bind(replaced => ReadRelation(o).Map(relation => new AcceptanceReceipt(root, id, at, replay, replaced, relation))))
            : Result<AcceptanceReceipt>.Fail(ClientErrors.ResponseMalformed("acceptance receipt"));

    /// <summary>
    /// R8.71: <c>replaced_answer</c> is absent (a Forum that said nothing: not stated), <c>null</c> (none) or a non-empty post id.
    /// Anything else is a malformed response, not a value the client picks.
    /// </summary>
    private static Result<AcceptanceReplaced> ReadReplaced(JsonValue.Object o) =>
        ClientJson.Member(o, "replaced_answer") switch
        {
            null => Result<AcceptanceReplaced>.Ok(AcceptanceReplaced.NotStated),
            JsonValue.Null => Result<AcceptanceReplaced>.Ok(AcceptanceReplaced.None),
            JsonValue.String { Value.Length: > 0 } id => Result<AcceptanceReplaced>.Ok(AcceptanceReplaced.Of(id.Value)),
            _ => Result<AcceptanceReplaced>.Fail(ClientErrors.ResponseMalformed("acceptance receipt: replaced_answer is neither null nor a post id (R8.71)")),
        };

    /// <summary>
    /// R8.76: <c>replaced_relation</c> is absent (a Forum that said nothing: not stated), <c>null</c> (none), <c>same-chain</c> or <c>other-answer</c>.
    /// Anything else is a malformed response, not a value the client picks.
    /// </summary>
    private static Result<AcceptanceRelation> ReadRelation(JsonValue.Object o) =>
        ClientJson.Member(o, "replaced_relation") switch
        {
            null => Result<AcceptanceRelation>.Ok(AcceptanceRelation.NotStated),
            JsonValue.Null => Result<AcceptanceRelation>.Ok(AcceptanceRelation.None),
            JsonValue.String { Value: "same-chain" } => Result<AcceptanceRelation>.Ok(AcceptanceRelation.SameChain),
            JsonValue.String { Value: "other-answer" } => Result<AcceptanceRelation>.Ok(AcceptanceRelation.OtherAnswer),
            _ => Result<AcceptanceRelation>.Fail(ClientErrors.ResponseMalformed("acceptance receipt: replaced_relation is neither null, \"same-chain\" nor \"other-answer\" (R8.76)")),
        };

    /// <summary>
    /// R8.68: <c>already_accepted</c> is a boolean when present; an absent member is an older Forum, which always recorded, so
    /// false. A present member of any other type is a malformed response, not a value the client picks.
    /// </summary>
    private static Result<bool> ReadAlreadyAccepted(JsonValue.Object o) =>
        ClientJson.Member(o, "already_accepted") switch
        {
            null => Result<bool>.Ok(false),
            JsonValue.Bool b => Result<bool>.Ok(b.Value),
            _ => Result<bool>.Fail(ClientErrors.ResponseMalformed("acceptance receipt: already_accepted is not a boolean (R8.68)")),
        };

    internal static Result<SearchPage> ReadSearchPage(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<SearchPage>.Fail(ClientErrors.ResponseMalformed("search page is not an object"));

        if (ClientJson.Member(o, "results") is not JsonValue.Array array)
            return Result<SearchPage>.Fail(ClientErrors.ResponseMalformed("search page carries no results"));

        var hits = ImmutableArray.CreateBuilder<SearchHitDocument>();
        foreach (var element in array.Items)
        {
            if (element is not JsonValue.Object hit)
                return Result<SearchPage>.Fail(ClientErrors.ResponseMalformed("search result is not an object"));

            if (ClientJson.Object(hit, "post") is not { } post)
                return Result<SearchPage>.Fail(
                    ClientErrors.ResponseMalformed("search result carries no post"));

            if (!ReadPost(post).TryGetValue(out var document, out var error))
                return Result<SearchPage>.Fail(error!);

            hits.Add(new SearchHitDocument(
                document!,
                (long)(ClientJson.Number(hit, "score_micro") ?? 0),
                ClientJson.Object(hit, "why_ranked") is { } why ? ReadWhy(why) : null));
        }

        if (ClientJson.Object(o, "floor") is not { } floor)
            return Result<SearchPage>.Fail(ClientErrors.ResponseMalformed("search page states no floor (R10.2)"));

        if (!FloorCount(floor, "excluded_by_floor").TryGetValue(out var excludedByFloor, out var floorCountError))
            return Result<SearchPage>.Fail(floorCountError!);
        if (!FloorCount(floor, "excluded_disputed").TryGetValue(out var excludedDisputed, out floorCountError))
            return Result<SearchPage>.Fail(floorCountError!);

        if (!ReadClamp(floor).TryGetValue(out var clamp, out var clampError))
            return Result<SearchPage>.Fail(clampError!);

        return Result<SearchPage>.Ok(new SearchPage(
            hits.ToImmutable(),
            ClientJson.String(o, "next_cursor"),
            new RetrievalFloorDocument(
                ClientJson.String(floor, "surface") ?? string.Empty,
                ClientJson.String(floor, "min_verification") ?? string.Empty,
                ClientJson.String(floor, "source") ?? string.Empty,
                Strings(floor, "applies_to"),
                Strings(floor, "not_applicable_to"),
                excludedByFloor,
                excludedDisputed,
                clamp.PublishedDefault,
                clamp.Requested,
                clamp.Clamped,
                clamp.RequestedStated),
            ClientJson.String(o, "model") ?? string.Empty,
            (long)(ClientJson.Number(o, "corpus_bound") ?? 0)));
    }

    /// <summary>
    /// R9.33: the three members R9.31 states. Each is absent (an older Forum: not stated) or of its type:
    /// <c>published_default</c> a string, <c>requested_min_verification</c> a string or null (null is a request that named
    /// no level, and is stated), <c>clamped</c> a boolean. A present member of any other type is a malformed response.
    /// Each member is stated or not on its own (errata G34, D61 item 18): <c>RequestedStated</c> is whether the request member is
    /// present, null included, and no other member's presence speaks for it.
    /// </summary>
    private static Result<(string? PublishedDefault, string? Requested, bool? Clamped, bool RequestedStated)> ReadClamp(JsonValue.Object floor)
    {
        var member = ClientJson.Member(floor, "published_default");
        var requested = ClientJson.Member(floor, "requested_min_verification");
        var clamped = ClientJson.Member(floor, "clamped");

        var ok = (member is null or JsonValue.String)
            && (requested is null or JsonValue.String or JsonValue.Null)
            && (clamped is null or JsonValue.Bool);
        if (!ok)
            return Result<(string?, string?, bool?, bool)>.Fail(
                ClientErrors.ResponseMalformed("search page states a floor clamp member that is not of its type (R9.33)"));

        return Result<(string?, string?, bool?, bool)>.Ok((
            (member as JsonValue.String)?.Value,
            (requested as JsonValue.String)?.Value,
            (clamped as JsonValue.Bool)?.Value,
            requested is not null));
    }

    /// <summary>
    /// R9.30: a floor count is absent (null: the Forum did not state it) or a non-negative whole number that
    /// fits an int. Anything else that is present is a malformed response and is not coerced.
    /// </summary>
    private static Result<int?> FloorCount(JsonValue.Object floor, string name)
    {
        if (ClientJson.Member(floor, name) is null)
            return Result<int?>.Ok(null);

        return ClientJson.WholeNumber(floor, name) is >= 0 and <= int.MaxValue and var count
            ? Result<int?>.Ok((int)count)
            : Result<int?>.Fail(ClientErrors.ResponseMalformed("search page states a floor count that is not a count (R9.30)"));
    }

    private static WhyRanked ReadWhy(JsonValue.Object why)
    {
        var notComputed = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        if (ClientJson.Object(why, "not_computed") is { } absent)
            foreach (var m in absent.Members)
                if (m.Value is JsonValue.String reason) notComputed[m.Key] = reason.Value;

        return new WhyRanked(
            ClientJson.Object(why, "lexical") is { } lexical
                ? new LexicalWhy(
                    (int)(ClientJson.Number(lexical, "rank") ?? 0),
                    (int)(ClientJson.Number(lexical, "title_matches") ?? 0),
                    (int)(ClientJson.Number(lexical, "body_matches") ?? 0),
                    (int)(ClientJson.Number(lexical, "tag_matches") ?? 0),
                    (int)(ClientJson.Number(lexical, "score") ?? 0))
                : null,
            ClientJson.Object(why, "vector") is { } vector
                ? new VectorWhy(
                    (int)(ClientJson.Number(vector, "rank") ?? 0),
                    (int)(ClientJson.Number(vector, "cosine_bp") ?? 0),
                    ClientJson.String(vector, "model") ?? string.Empty)
                : null,
            (int)(ClientJson.Number(why, "k") ?? 0),
            (long)(ClientJson.Number(why, "lexical_term_micro") ?? 0),
            (long)(ClientJson.Number(why, "vector_term_micro") ?? 0),
            (long)(ClientJson.Number(why, "fused_micro") ?? 0),
            ClientJson.String(why, "verification_level") ?? string.Empty,
            (int)(ClientJson.Number(why, "verification_weight_bp") ?? 0),
            (long)(ClientJson.Number(why, "score_micro") ?? 0),
            ClientJson.Member(why, "deferred_by_diversification") is JsonValue.Bool { Value: true },
            notComputed.ToImmutable());
    }

    /// <summary>
    /// R8.19: the duplicate refusal's extension members, or null when the problem is not that
    /// refusal <b>or does not carry all of it</b>.
    ///
    /// <para><b>Every member is required, and a missing one is not read as zero.</b> This reader
    /// used to default each absent number to <c>0</c> and each absent string to empty, so a refusal
    /// that had lost its thresholds rendered as "refused at 0 bp" — a measurement nobody made,
    /// presented as the Forum's. Null sends the caller to the plain refusal, whose title and detail
    /// the Forum wrote, which is less than the whole document and true.</para>
    /// </summary>
    public static DuplicateRefusalDocument? ReadDuplicateRefusal(JsonValue? problem)
    {
        if (problem is not JsonValue.Object o
            || ClientJson.String(o, "type") != "curia/posts/duplicate-question"
            || ClientJson.Object(o, "canonical") is not { } canonical
            || ClientJson.Object(o, "similarity") is not { } similarity
            || ClientJson.Member(o, "answers") is not JsonValue.Array array)
            return null;

        if (ClientJson.String(canonical, "post_id") is not { Length: > 0 } postId
            || ClientJson.String(canonical, "digest") is not { Length: > 0 } digest
            || ClientJson.String(canonical, "board") is not { Length: > 0 } board
            || BasisPoints(similarity, "cosine_bp") is not { } cosine
            || BasisPoints(similarity, "lexical_overlap_bp") is not { } overlap
            || BasisPoints(similarity, "refuse_cosine_bp") is not { } refuseCosine
            || BasisPoints(similarity, "refuse_lexical_overlap_bp") is not { } refuseOverlap
            || BasisPoints(similarity, "annotate_cosine_bp") is not { } annotateCosine
            || ClientJson.String(similarity, "model") is not { Length: > 0 } model
            || ClientJson.String(o, "override") is not { Length: > 0 } remedy)
            return null;

        var answers = ImmutableArray.CreateBuilder<ProvenancePost>(array.Items.Length);
        var unreadable = 0;
        foreach (var element in array.Items)
        {
            if (element is JsonValue.Object answer && ReadPost(answer).TryGetValue(out var read, out _))
                answers.Add(read!);
            else
                unreadable++;
        }

        return new DuplicateRefusalDocument(
            postId,
            digest,
            board,
            answers.ToImmutable(),
            unreadable,
            cosine,
            overlap,
            refuseCosine,
            refuseOverlap,
            annotateCosine,
            model,
            remedy);
    }

    /// <summary>A basis-point member: an integer in 0..10000 (R6.33), or null when it is anything else.</summary>
    private static int? BasisPoints(JsonValue.Object parent, string name) =>
        ClientJson.WholeNumber(parent, name) is { } value and >= 0 and <= 10_000 ? (int)value : null;

    internal static Result<FlagReceipt> ReadFlagReceipt(JsonValue.Object o) =>
        ClientJson.String(o, "post_id") is { } id
        && ClientJson.String(o, "kind") is { } kind
        && ClientJson.String(o, "raised_at") is { } at
            ? Result<FlagReceipt>.Ok(new FlagReceipt(id, kind, at))
            : Result<FlagReceipt>.Fail(ClientErrors.ResponseMalformed("flag receipt"));

    /// <summary>
    /// R10.44's listing. A served flag carries a post, a kind and an instant and nothing else, so
    /// the receipt shape a raise returns is the same shape a listing returns — one record, because
    /// two would be two chances to disagree about what a flag is.
    /// </summary>
    internal static Result<ImmutableArray<FlagReceipt>> ReadFlagList(JsonValue value)
    {
        // ClientJson.Array yields an empty array for an absent member, which would make a response
        // carrying no `flags` field indistinguishable from one carrying no flags. Read the member.
        if (value is not JsonValue.Object o || ClientJson.Member(o, "flags") is not JsonValue.Array a)
            return Result<ImmutableArray<FlagReceipt>>.Fail(
                ClientErrors.ResponseMalformed("expected an object carrying a flags array"));

        var flags = ImmutableArray.CreateBuilder<FlagReceipt>(a.Items.Length);
        foreach (var item in a.Items)
        {
            if (item is not JsonValue.Object flag)
                return Result<ImmutableArray<FlagReceipt>>.Fail(
                    ClientErrors.ResponseMalformed("flag is not an object"));

            if (!ReadFlagReceipt(flag).TryGetValue(out var receipt, out var error))
                return Result<ImmutableArray<FlagReceipt>>.Fail(error!);

            flags.Add(receipt!);
        }

        return Result<ImmutableArray<FlagReceipt>>.Ok(flags.MoveToImmutable());
    }

    /// <summary>
    /// R9.32 / R10.80: <c>provenance.superseded_by</c> is absent (the default array: the Forum did not state it, which is not "none"),
    /// or a list of objects each holding a <c>digest</c> of the published <c>sha256:&lt;64 lowercase hex&gt;</c> form and an optional string
    /// <c>post_id</c>. Anything else that is present is a malformed response and is not coerced or skipped, as a floor count is not
    /// (R9.30): a client that dropped an element it could not read would tell its agent a corrected post stands.
    /// </summary>
    private static Result<ImmutableArray<Successor>> ReadSupersededBy(JsonValue.Object provenance) =>
        ReadSupersededBy(provenance, "provenance.superseded_by", "R9.32");

    /// <summary>
    /// The list of revisions in <paramref name="holder"/>'s <c>superseded_by</c>: absent is <c>default</c> (not stated), <c>[]</c> is
    /// empty, a populated list is kept, and anything else is a malformed response. Shared by the post's own list (R9.32) and the accepted
    /// answer's (R9.41), so the two are read with one strictness.
    /// </summary>
    private static Result<ImmutableArray<Successor>> ReadSupersededBy(JsonValue.Object holder, string name, string requirement)
        => ReadRevisionList(holder, "superseded_by", name, requirement);

    /// <summary>
    /// A list of revisions held under <paramref name="memberName"/> of <paramref name="holder"/>, read with one strictness for
    /// <c>superseded_by</c> (R9.32, R9.41) and <c>fork</c> (R9.43): absent is <c>default</c>, <c>[]</c> is empty, anything else that is present
    /// and not a list of <c>{digest, post_id?}</c> is a malformed response.
    /// </summary>
    private static Result<ImmutableArray<Successor>> ReadRevisionList(JsonValue.Object holder, string memberName, string name, string requirement)
    {
        var malformed = Result<ImmutableArray<Successor>>.Fail(
            ClientErrors.ResponseMalformed($"{name} is not a list of revisions ({requirement})"));

        if (ClientJson.Member(holder, memberName) is not { } member)
            return Result<ImmutableArray<Successor>>.Ok(default);

        if (member is not JsonValue.Array array)
            return malformed;

        var revisions = ImmutableArray.CreateBuilder<Successor>(array.Items.Length);
        foreach (var element in array.Items)
        {
            if (element is not JsonValue.Object revision
                || ClientJson.String(revision, "digest") is not { } digest
                || !EnvelopeDigest.IsPrefixedForm(digest))
                return malformed;

            string? postId = null;
            if (ClientJson.Member(revision, "post_id") is { } id)
            {
                if (id is not JsonValue.String text)
                    return malformed;
                postId = text.Value;
            }

            revisions.Add(new Successor(digest, postId));
        }

        // An empty list is the one empty array, so two members that both say "none" compare equal (an ImmutableArray compares by its array).
        return Result<ImmutableArray<Successor>>.Ok(revisions.Count == 0 ? ImmutableArray<Successor>.Empty : revisions.MoveToImmutable());
    }

    /// <summary>
    /// R9.36 / R10.81: <c>provenance.accepted_answer</c> is absent (the Forum did not state it: null, which is not "none"), <c>null</c>
    /// (<see cref="AcceptedAnswerMember.None"/>), or an object holding a <c>digest</c> of the published <c>sha256:&lt;64 lowercase hex&gt;</c>
    /// form and an optional string <c>post_id</c>; and it is on a question only. Anything else that is present is a malformed response and
    /// is not coerced to none, which would tell an agent a settled thread is open; nor is the member read off a post that is not a question,
    /// since a Forum that contradicts its own R9.36 is not one whose word to print.
    /// </summary>
    private static Result<AcceptedAnswerMember?> ReadAcceptedAnswer(JsonValue.Object provenance, string kind)
    {
        var malformed = Result<AcceptedAnswerMember?>.Fail(
            ClientErrors.ResponseMalformed("provenance.accepted_answer is neither null nor an answer's digest and optional post id (R9.36)"));

        if (ClientJson.Member(provenance, "accepted_answer") is not { } member)
            return Result<AcceptedAnswerMember?>.Ok(null);

        if (!string.Equals(kind, PostKinds.Wire(PostKind.Question), StringComparison.Ordinal))
            return Result<AcceptedAnswerMember?>.Fail(
                ClientErrors.ResponseMalformed("provenance.accepted_answer is on a post that is not a question (R9.36)"));

        if (member is JsonValue.Null) return Result<AcceptedAnswerMember?>.Ok(AcceptedAnswerMember.None);

        if (member is not JsonValue.Object answer
            || ClientJson.String(answer, "digest") is not { } digest
            || !EnvelopeDigest.IsPrefixedForm(digest))
            return malformed;

        string? postId = null;
        if (ClientJson.Member(answer, "post_id") is { } id)
        {
            if (id is not JsonValue.String text) return malformed;
            postId = text.Value;
        }

        // R9.46: the kind of the post the acceptance names is one of two words when the Forum states it; absent is an older Forum (not stated),
        // and any other value is a malformed response, never coerced to either word.
        string? acceptedKind = null;
        if (ClientJson.Member(answer, "kind") is { } kindMember)
        {
            if (kindMember is not JsonValue.String { Value: "answer" or "revision" } word)
            {
                return Result<AcceptedAnswerMember?>.Fail(
                    ClientErrors.ResponseMalformed("provenance.accepted_answer.kind is neither \"answer\" nor \"revision\" (R9.46)"));
            }

            acceptedKind = word.Value;
        }

        // R9.41: the answer's revisions, read as strictly as the answer's own list. Absent is not stated, which is not empty.
        return ReadSupersededBy(answer, "provenance.accepted_answer.superseded_by", "R9.41")
            .Map(revisions => (AcceptedAnswerMember?)new AcceptedAnswerMember(digest, postId, revisions, acceptedKind));
    }

    internal static Result<ProvenancePost> ReadPost(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<ProvenancePost>.Fail(ClientErrors.ResponseMalformed("post is not an object"));

        if (ClientJson.Object(o, "provenance") is not { } p)
            return Result<ProvenancePost>.Fail(
                ClientErrors.ResponseMalformed("post carries no provenance envelope"));

        // The wire spells marking with the enum's own names ("None", "DelimitersOnly",
        // "Datamark"), which are not the query-string spellings ("none", "delimiters",
        // "datamark"). An unparseable value is a malformed response rather than a default: the
        // Forum produces this field from a closed enum, so a value outside it means the client and
        // the Forum disagree about the vocabulary, and quietly reading it as "no marking" would
        // report an unmarked span as deliberately unmarked.
        //
        // Matched by exact, ordinal name. Enum.TryParse accepts numeric and comma-joined spellings
        // ("3", "DelimitersOnly, Datamark") that yield values the enum does not define. R10.71's
        // SpanCheck.Rendering hands the marking to Datamarking.Render, which throws on such a value,
        // so a lenient parse would let a Forum crash the reader.
        MarkingMode? marking = ClientJson.String(p, "marking") switch
        {
            "None" => MarkingMode.None,
            "DelimitersOnly" => MarkingMode.DelimitersOnly,
            "Datamark" => MarkingMode.Datamark,
            _ => null,
        };
        if (marking is not { } mode)
            return Result<ProvenancePost>.Fail(
                ClientErrors.ResponseMalformed("provenance.marking is not a marking mode"));

        if (!ReadSupersededBy(p).TryGetValue(out var supersededBy, out var supersededError))
            return Result<ProvenancePost>.Fail(supersededError!);

        if (!ReadAcceptedAnswer(p, ClientJson.String(o, "kind") ?? string.Empty).TryGetValue(out var acceptedAnswer, out var acceptedError))
            return Result<ProvenancePost>.Fail(acceptedError!);

        var provenance = new Provenance(
            ClientJson.String(p, "content_type") ?? string.Empty,
            ClientJson.String(p, "warning") ?? string.Empty,
            ClientJson.String(p, "author") ?? string.Empty,
            Bool(p, "owner_verified"),
            Bool(p, "signature_valid"),
            ClientJson.String(p, "verification_level") ?? string.Empty,
            Strings(p, "risk_flags"),
            mode,
            ClientJson.String(p, "marking_token"),
            ClientJson.String(p, "reader_contract") ?? string.Empty,
            ClientJson.String(p, "marking_caveat"),
            ClientJson.String(p, "owner"),
            Strings(p, "reproductions"),
            Strings(p, "contradictions"),

            // R6.57 / D49: the Forum's mark on a post a declaration covers. Parsed so that `recheck`, whose batch serves such
            // a post as `current`, can say so; absent from an older Forum, which reads as `active` and not disputed.
            ClientJson.String(p, "key_status") ?? "active",
            ClientJson.WholeNumber(p, "disputed_by_log_index"),
            supersededBy,
            acceptedAnswer);

        if (ClientJson.String(o, "post_id") is not { } postId
            || ClientJson.String(o, "canonical") is not { } canonical
            || ClientJson.String(o, "signature") is not { } signature)
            return Result<ProvenancePost>.Fail(
                ClientErrors.ResponseMalformed("post is missing post_id, canonical or signature"));

        // R6.48's proof, when the Forum served one. A malformed proof is a malformed response
        // rather than an absent proof: "the Forum served no proof" and "the Forum served one this
        // client could not read" are different states, and reading the second as the first would
        // report a verifiable post as unverifiable and hide the disagreement that caused it.
        InclusionProofDocument? proof = null;
        if (ClientJson.Member(o, "inclusion_proof") is { } member and not JsonValue.Null)
        {
            if (!ActaDocuments.ReadInclusionProof(member).TryGetValue(out proof, out var proofError))
                return Result<ProvenancePost>.Fail(proofError!);
        }

        return Result<ProvenancePost>.Ok(new ProvenancePost(
            provenance,
            postId,
            ClientJson.String(o, "board") ?? string.Empty,
            ClientJson.String(o, "kind") ?? string.Empty,
            ClientJson.String(o, "parent"),
            ClientJson.String(o, "server_ts") ?? string.Empty,
            ClientJson.String(o, "digest") ?? string.Empty,
            canonical,
            signature,
            ClientJson.String(o, "rendered") ?? string.Empty,
            Bool(o, "accepted"),
            EntityTag: null,
            LogIndex: ClientJson.WholeNumber(o, "log_index"),
            InclusionProof: proof));
    }

    // EntityTag is set by the transport from the response header, never parsed from the body.

    /// <summary>
    /// R9.10's batch. An <c>items</c> member that is absent is malformed, not empty: a client that
    /// read a missing array as "nothing to report" would tell its agent every citation stands.
    /// </summary>
    internal static Result<ImmutableArray<CitationDocument>> ReadBatch(JsonValue.Object o)
    {
        if (ClientJson.Member(o, "items") is not JsonValue.Array array)
            return Result<ImmutableArray<CitationDocument>>.Fail(ClientErrors.ResponseMalformed("batch carries no items array"));

        var items = ImmutableArray.CreateBuilder<CitationDocument>(array.Items.Length);
        foreach (var element in array.Items)
        {
            if (element is not JsonValue.Object item)
                return Result<ImmutableArray<CitationDocument>>.Fail(ClientErrors.ResponseMalformed("batch item is not an object"));

            if (ClientJson.String(item, "state") is not { Length: > 0 } state)
                return Result<ImmutableArray<CitationDocument>>.Fail(ClientErrors.ResponseMalformed("batch item names no state"));

            ProvenancePost? post = null;
            if (ClientJson.Object(item, "post") is { } served)
            {
                if (!ReadPost(served).TryGetValue(out var parsed, out var error))
                    return Result<ImmutableArray<CitationDocument>>.Fail(error!);
                post = parsed;
            }

            if (!ReadRevisionList(item, "fork", "fork", "R9.43").TryGetValue(out var fork, out var forkError))
                return Result<ImmutableArray<CitationDocument>>.Fail(forkError!);

            items.Add(new CitationDocument(
                ClientJson.String(item, "digest"), state, Strings(item, "successors"), Bool(item, "forked"), post, fork));
        }

        return Result<ImmutableArray<CitationDocument>>.Ok(items.MoveToImmutable());
    }

    internal static Result<ImmutableArray<ProvenancePost>> ReadPosts(JsonValue value)
    {
        if (value is not JsonValue.Array a)
            return Result<ImmutableArray<ProvenancePost>>.Fail(
                ClientErrors.ResponseMalformed("expected an array of posts"));

        var posts = ImmutableArray.CreateBuilder<ProvenancePost>(a.Items.Length);
        foreach (var item in a.Items)
        {
            if (!ReadPost(item).TryGetValue(out var post, out var error))
                return Result<ImmutableArray<ProvenancePost>>.Fail(error!);
            posts.Add(post!);
        }

        return Result<ImmutableArray<ProvenancePost>>.Ok(posts.MoveToImmutable());
    }

    /// <summary>
    /// A key set with its lifecycle (errata G21). A lifecycle member that is not an array of whole, non-negative
    /// numbers is a malformed response, never an empty lifecycle.
    /// </summary>
    internal static Result<KeySetDocument> ReadKeySet(JsonValue value)
    {
        var keys = ReadJwks(value);
        if (!keys.TryGetValue(out var read, out var keysError)) return Result<KeySetDocument>.Fail(keysError!);

        var o = (JsonValue.Object)value;

        ImmutableArray<long>? lifecycle = null;
        if (ClientJson.Member(o, "curia_lifecycle") is { } member)
        {
            if (member is not JsonValue.Array array)
                return Result<KeySetDocument>.Fail(ClientErrors.ResponseMalformed("curia_lifecycle"));

            var indices = ImmutableArray.CreateBuilder<long>(array.Items.Length);
            foreach (var item in array.Items)
            {
                if (item is not JsonValue.Number { Value: var number }
                    || !double.IsInteger(number) || number < 0 || number > long.MaxValue)
                    return Result<KeySetDocument>.Fail(ClientErrors.ResponseMalformed("curia_lifecycle"));

                indices.Add((long)number);
            }

            lifecycle = indices.ToImmutable();
        }

        ImmutableArray<string>? approved = null;
        ImmutableArray<string>? enabled = null;
        if (ClientJson.Object(o, "curia_lifecycle_kinds") is { } kinds)
        {
            approved = Strings(kinds, "approved");
            enabled = Strings(kinds, "enabled");
        }

        return Result<KeySetDocument>.Ok(new KeySetDocument(read!, lifecycle, approved, enabled));
    }

    internal static Result<ImmutableArray<ForumJwk>> ReadJwks(JsonValue value)
    {
        if (value is not JsonValue.Object o)
            return Result<ImmutableArray<ForumJwk>>.Fail(ClientErrors.ResponseMalformed("jwks is not an object"));

        var keys = ImmutableArray.CreateBuilder<ForumJwk>();
        foreach (var item in ClientJson.Array(o, "keys"))
        {
            if (item is not JsonValue.Object k) continue;

            if (ReadJwk(k) is not { } key)
                return Result<ImmutableArray<ForumJwk>>.Fail(ClientErrors.ResponseMalformed("jwk"));

            keys.Add(key);
        }

        return Result<ImmutableArray<ForumJwk>>.Ok(keys.ToImmutable());
    }

    /// <summary>
    /// One JWK, or null when it lacks a member every key needs. Shared with the Acta's own key set
    /// (R6.50), which carries the same key material under two extra members: a second reader here
    /// would be a second place for errata D4's <c>EC</c>/<c>OKP</c> distinction to be got wrong.
    /// </summary>
    internal static ForumJwk? ReadJwk(JsonValue.Object k) =>
        ClientJson.String(k, "kty") is { } kty
        && ClientJson.String(k, "alg") is { } alg
        && ClientJson.String(k, "kid") is { } kid
        && ClientJson.String(k, "x") is { } x
            ? new ForumJwk(
                kty, ClientJson.String(k, "crv"), alg, kid, x, ClientJson.String(k, "y"),
                ClientJson.String(k, "curia_not_before"), ClientJson.String(k, "curia_not_after"),
                ClientJson.WholeNumber(k, "curia_log_index"),
                ClientJson.String(k, "curia_status"), ClientJson.WholeNumber(k, "curia_retired_log_index"),
                ClientJson.WholeNumber(k, "curia_compromised_log_index"), ClientJson.WholeNumber(k, "curia_disputed_from"))
            : null;

    internal static Result<ReaderContractDocument> ReadContract(JsonValue value)
    {
        if (value is not JsonValue.Object o || ClientJson.String(o, "version") is not { } version)
            return Result<ReaderContractDocument>.Fail(ClientErrors.ResponseMalformed("reader contract"));

        var clauses = ImmutableArray.CreateBuilder<ReaderContractClause>();
        foreach (var item in ClientJson.Array(o, "clauses"))
        {
            if (item is not JsonValue.Object c) continue;

            var number = ClientJson.Member(c, "number") is JsonValue.Number n ? (int)n.Value : 0;
            clauses.Add(new ReaderContractClause(
                number,
                ClientJson.String(c, "force") ?? string.Empty,
                ClientJson.String(c, "text") ?? string.Empty,
                Bool(c, "client_must_implement")));
        }

        return Result<ReaderContractDocument>.Ok(
            new ReaderContractDocument(version, clauses.ToImmutable()));
    }

    private static bool Bool(JsonValue.Object o, string name) =>
        ClientJson.Member(o, name) is JsonValue.Bool b && b.Value;

    private static ImmutableArray<string> Strings(JsonValue.Object o, string name) =>
        [.. ClientJson.Array(o, name).OfType<JsonValue.String>().Select(s => s.Value)];
}
