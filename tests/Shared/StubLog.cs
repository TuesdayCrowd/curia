using System.Buffers.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon;
using Curia.Canon.Acta;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Client;
using Curia.Domain.Acta;
using Curia.Domain.Content;
using Curia.Domain.Serving;

namespace Curia.Tests.Shared;

/// <summary>
/// A Forum that serves one real signed post, one real Acta, and one real signed head.
///
/// <para><b>Why this file is linked into two test projects rather than written twice.</b> The
/// property R6.51 and R11.18 are about is that the <i>same</i> material behaves differently on two
/// surfaces: the reference client may fetch a log entry as proof material, and the MCP adapter may
/// not return it as content. Two fixtures could drift, and the first thing to drift would be the
/// marker whose absence is the assertion -- leaving a P22 gate that watches a string the entry no
/// longer contains, which is this project's trap 11 exactly. One fixture, linked.</para>
///
/// <para><b>Why the material is built rather than pinned.</b> Trap 1 in this project's list is a
/// probe that tests a shape production never produces. Every document below is assembled by the
/// same computations the Forum uses -- <see cref="SubmissionBuilder"/> for the envelope,
/// <see cref="LogLeaf"/>'s six members under pure RFC 8785 for the entry,
/// <see cref="MerkleTree"/> for the tree, <see cref="LogEntries.HeadDocument"/> under
/// <c>curia-head+jws</c> for the head -- so a change to any frozen encoding moves the fixture with
/// the code, and the cross-implementation test against <c>curia-testis</c> is what stops both
/// drifting together.</para>
///
/// <para><b>Every knob is a falsification.</b> Each property below turns off exactly one link in
/// the chain R6.52 requires, so a test can assert that removing it is detected rather than
/// asserting only that the intact case passes.</para>
/// </summary>
internal sealed class StubLog : IDisposable
{
    internal const string PostId = "01TESTPOSTID0000000000000A";
    internal const string Author = "https://agents.example/alice";
    internal const string AgentKid = "alice-1";
    internal const string LogKid = "log-1";

    internal static readonly Uri Forum = new("http://forum.test");

    /// <summary>
    /// A string that appears only inside the log entry's payload, never in anything the serving
    /// path returns. R6.51 forbids a client surfacing a log entry, and a marker is how a test can
    /// tell that a tool did not.
    /// </summary>
    internal const string EntryOnlyMarker = "MARKER-THIS-BODY-IS-ONLY-IN-THE-LOG-ENTRY";

    private readonly string _root;
    private readonly EnrolledAgent _agent;
    private readonly ECDsa _logKey;
    private HttpClient? _http;
    private StubHandler? _handler;

    internal StubLog(int treeSize = 5, int postIndex = 2)
    {
        _root = Directory.CreateTempSubdirectory("curia-stub-log-").FullName;

        var store = new ProfileStore(_root);
        _agent = store.Create("alice", Author, AgentKid, Forum)
            .TryGetValue(out var agent, out _)
            ? agent!
            : throw new InvalidOperationException("the stub could not create an agent");

        _logKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var draft = new PostDraft
        {
            Kind = PostKind.Question,
            Board = "b",
            Title = "t",
            Body = "A question whose body carries " + EntryOnlyMarker + " and nothing else of note.",
        };

        Submission = SubmissionBuilder.Build(_agent, draft, DateTimeOffset.UnixEpoch)
            .TryGetValue(out var signed, out _)
            ? signed!
            : throw new InvalidOperationException("the stub could not sign a post");

        Canonical = Encoding.UTF8.GetString(Submission.Canonical.Span);

        // The canonical thread's one answer, for R8.19's refusal: a real answer, by the same agent,
        // under the question above. It used to be the QUESTION itself served in the answers array,
        // which made "no span of the matched question is echoed" (R8.61) impossible to assert --
        // the matched question was the only thing the refusal carried.
        Answer = SubmissionBuilder.Build(
                _agent,
                new PostDraft
                {
                    Kind = PostKind.Answer,
                    Board = "b",
                    Parent = PostId,
                    Body = "An answer whose body carries " + AnswerMarker + ", served under the canonical question.",
                },
                DateTimeOffset.UnixEpoch)
            .TryGetValue(out var answer, out _)
            ? answer!
            : throw new InvalidOperationException("the stub could not sign the answer");

        PostIndex = postIndex;
        Entry = BuildEntry(PostId, "question", null, Canonical, Submission.Signature, Submission.PrefixedDigest);

        // The answer has a leaf of its own, so the proof R8.19's refusal serves with it is a real
        // proof: the Forum serves `log_index` and `inclusion_proof` on every post it returns, the
        // answers in a 409 included, and a fixture without them has nothing to lose (the MCP plan's
        // trap 4).
        if (treeSize <= postIndex + 1)
            throw new ArgumentOutOfRangeException(nameof(treeSize), "the stub needs a leaf after the post's for the canonical thread's answer");

        AnswerIndex = postIndex + 1;
        AnswerEntry = BuildEntry(
            AnswerPostId, "answer", PostId, Encoding.UTF8.GetString(Answer.Canonical.Span), Answer.Signature, Answer.PrefixedDigest);

        // The post's leaf is real; the rest are filler, distinct and in no particular relation to
        // it. A verifier only ever fetches the entry it is proving, so the others need only exist.
        var leaves = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(treeSize);
        for (var i = 0; i < treeSize; i++)
        {
            leaves.Add(i == postIndex
                ? LeafOf(Entry)
                : i == AnswerIndex
                    ? LeafOf(AnswerEntry)
                    : MerkleTree.LeafHash(Encoding.UTF8.GetBytes($"filler-leaf-{i}")));
        }

        Leaves = leaves.MoveToImmutable();
        HeadTreeSize = treeSize;
    }

    internal SignedSubmission Submission { get; }

    internal string Canonical { get; }

    /// <summary>The canonical question's one answer, served in R8.19's duplicate refusal.</summary>
    internal SignedSubmission Answer { get; }

    /// <summary>A string that appears only in <see cref="Answer"/>'s body.</summary>
    internal const string AnswerMarker = "MARKER-THIS-BODY-IS-THE-CANONICAL-THREADS-ANSWER";

    internal const string AnswerPostId = "01TESTPOSTID0000000000000C";

    /// <summary>
    /// The profile directory holding the stub's own enrolled agent, <c>alice</c>. A write test loads
    /// its own <see cref="EnrolledAgent"/> from here, so the posts it signs verify under the JWKS the
    /// stub serves.
    /// </summary>
    internal ProfileStore Store => new(_root);

    internal int PostIndex { get; }

    /// <summary>The answer's leaf index: the one after the post's.</summary>
    internal int AnswerIndex { get; }

    /// <summary>R6.46's six members for the answer's <c>post.accepted</c>.</summary>
    internal JsonValue.Object AnswerEntry { get; }

    internal JsonValue.Object Entry { get; private set; }

    /// <summary>The size the served head covers. Set below the post's index to strand it (R6.48).</summary>
    internal int HeadTreeSize { get; set; }

    // ---- falsification knobs ------------------------------------------------------------------

    /// <summary>Answer <c>/v1/jwks</c> with a transport failure: the "could not check" case for R6.52's first check.</summary>
    internal bool JwksUnreachable { get; set; }

    /// <summary>Answer <c>/v1/log/head</c> with 404 <c>curia/log/no-head</c>: a log whose operator has never signed.</summary>
    internal bool NoSignedHead { get; set; }

    /// <summary>Serve a head signed by a key the log does not publish.</summary>
    internal bool HeadSignedByAStranger { get; set; }

    /// <summary>
    /// Serve a different, validly signed document under the same post id on the <i>second</i> read.
    ///
    /// <para>The Forum is not trusted and answers every request separately, so nothing stops it
    /// showing one document to a read and another to a verification. Both are signed by the same
    /// agent under the same key, so every per-response check passes on each — what fails is the
    /// assumption that "the post with this id" names one thing. R11.29 is about exactly this.</para>
    /// </summary>
    internal bool ServesADecoyOnTheSecondRead { get; set; }

    private int _postReads;

    /// <summary>A second, differently-bodied post by the same agent, under the same id.</summary>
    internal string DecoyJson()
    {
        var draft = new PostDraft
        {
            Kind = PostKind.Question,
            Board = "b",
            Title = "t",
            Body = "A decoy the Forum serves under the same id, signed by the same key.",
        };

        var decoy = SubmissionBuilder.Build(_agent, draft, DateTimeOffset.UnixEpoch)
            .TryGetValue(out var signed, out _)
            ? signed!
            : throw new InvalidOperationException("the stub could not sign the decoy");

        var canonical = Encoding.UTF8.GetString(decoy.Canonical.Span);

        return $$"""
        {"provenance":{"content_type":"{{PostEnvelope.RequiredContentType}}","warning":{{JsonString(Provenance.StandardWarning)}},
        "author":"{{Author}}","owner_verified":true,"signature_valid":true,
        "verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c",
        "owner":null,"reproductions":[],"contradictions":[]},
        "post_id":"{{PostId}}","board":"b","kind":"question","parent":null,
        "server_ts":"1970-01-01T00:00:00.0000000+00:00","digest":"{{decoy.PrefixedDigest}}",
        "canonical":{{JsonString(canonical)}},"signature":"{{decoy.Signature}}",
        "rendered":{{JsonString(Datamarking.Render(canonical, MarkingMode.None))}},"accepted":false}
        """.ReplaceLineEndings(string.Empty);
    }

    /// <summary>Serve a <c>leaf_hash</c> on the proof that is not the leaf's hash.</summary>
    internal bool ProofCarriesAWrongLeafHash { get; set; }

    // ---- the write surface --------------------------------------------------------------------

    /// <summary>The nonce a fresh stub issues. <see cref="RotateNonce"/> replaces it.</summary>
    internal const string NonceValue = "stub-nonce-0001";

    internal const string AcceptedPostId = "01TESTPOSTID0000000000000B";

    /// <summary>The id a submission the stub accepts is given.</summary>
    internal const string SubmittedPostId = "01TESTPOSTID0000000000000D";

    /// <summary>
    /// R8.18's duplicate refusal instead of an acceptance. §8.5 refuses a duplicate <i>question</i>
    /// with the thread that already answers it — the case that makes <c>curia_ask</c>'s result a
    /// third outcome rather than an error.
    /// </summary>
    internal bool RefusesAsDuplicate { get; set; }

    /// <summary>
    /// Refuse every submission as the Forum refuses a Table 10 denial: <c>403</c>,
    /// <c>curia/authz/denied</c>, the reason and the tier the request was evaluated at, and no
    /// criterion by which that tier could change — which is exactly what R11.26 says an MCP tool must
    /// make up for.
    /// </summary>
    internal string? DeniesSubmissionsAtTier { get; set; }

    /// <summary>
    /// The nonce this stub currently accepts on a write. A write path requires one, as the Forum's
    /// does (R5.19): a proof without it, or with any other, is answered with RFC 9449 §8's challenge.
    ///
    /// <para><b>The challenge used to be on the token endpoint</b>, where the Forum never issues one
    /// — its token endpoint takes no nonce at all. A client tested against that shape was tested
    /// against a server that does not exist, and the retry it needed on every write path was the one
    /// thing never exercised. Trap 12, a fixture that agrees with nothing production does.</para>
    /// </summary>
    internal string CurrentNonce { get; private set; } = NonceValue;

    /// <summary>Replace the accepted nonce, as the Forum's rotation does. A client holding the old one is challenged again.</summary>
    internal void RotateNonce() =>
        CurrentNonce = "stub-nonce-" + (++_nonceGeneration).ToString("D4", CultureInfo.InvariantCulture);

    private int _nonceGeneration = 1;

    /// <summary>
    /// Every <c>jti</c> a write has spent. Burned only once the nonce has passed, in the Forum's own
    /// order (<c>AccessTokenValidator</c>): a proof refused for its nonce never reached the replay
    /// cache, so the proof that answers a challenge is not a replay of the one that met it.
    /// </summary>
    private readonly HashSet<string> _burnedJtis = new(StringComparer.Ordinal);

    /// <summary>The <c>client_id</c> the last token was issued to: the principal a write is then attributed to.</summary>
    internal string? TokenSubject { get; private set; }

    /// <summary>The DPoP proof claims of every write, in order, including the ones the stub refused.</summary>
    internal List<(string? Jti, string? Nonce)> WriteProofs { get; } = [];

    /// <summary>Every request the stub answered, in order, as "METHOD path".</summary>
    internal List<string> Requests { get; } = [];

    /// <summary>Every request's query string, in order, so a test can see what a write asked the serving boundary for.</summary>
    internal List<string> Queries { get; } = [];

    /// <summary>The bodies of the writes it received, for asserting on what was actually sent.</summary>
    internal List<string> WrittenBodies { get; } = [];

    internal void Record(HttpMethod method, string path, string query, HttpRequestMessage request)
    {
        Requests.Add($"{method} {path}");
        Queries.Add(query);

        if (method == HttpMethod.Post && request.Content is { } content)
            WrittenBodies.Add(content.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult());
    }

    internal static string TokenJson() => $$"""
    {"access_token":"stub-access-token","token_type":"DPoP","expires_in":300,
    "scope":"question:create answer:create"}
    """.ReplaceLineEndings(string.Empty);

    /// <summary>
    /// The receipt for what was actually submitted: its digest computed from the envelope the client
    /// sent, as the Forum computes it.
    ///
    /// <para>It used to carry the stub's own post's digest whatever arrived, so every submission
    /// came back with a digest belonging to a different document — and a client comparing the two,
    /// as the reference client does, would have raised its disagreement warning under every write
    /// the stub accepted.</para>
    /// </summary>
    internal static string ReceiptJson(string digest) => $$"""
    {"post_id":"{{SubmittedPostId}}","digest":"{{digest}}",
    "server_ts":"1970-01-01T00:00:00.0000000+00:00","risk_flags":[]}
    """.ReplaceLineEndings(string.Empty);

    internal static string FlagReceiptJson() => $$"""
    {"post_id":"{{PostId}}","kind":"incorrect","raised_at":"1970-01-01T00:00:00.0000000+00:00"}
    """.ReplaceLineEndings(string.Empty);

    internal static string AcceptanceJson() => $$"""
    {"thread_root":"{{PostId}}","post_id":"{{AcceptedPostId}}",
    "accepted_at":"1970-01-01T00:00:00.0000000+00:00"}
    """.ReplaceLineEndings(string.Empty);

    /// <summary>
    /// R8.18/R8.19's 409 in the shape <c>Curia.Api</c>'s <c>DuplicateProblem</c> serializes: the
    /// canonical thread as an object, its answers with their provenance envelopes, both measures and
    /// all three thresholds with the model inside <c>similarity</c> — and, per R8.61, no span of the
    /// matched question's own text.
    ///
    /// <para><b>This used to be a shape the Forum never serves</b> — <c>canonical_post_id</c>,
    /// <c>canonical_digest</c> and <c>board</c> at the top level and <c>model</c> beside
    /// <c>similarity</c> rather than inside it — and the client's reader returned null for it, so
    /// every rendering of a duplicate refusal built on this fixture would have fallen through to the
    /// plain one. Its only test searched the body for member names, which the wrong shape contained.
    /// <c>StubFidelityTests</c> in <c>Curia.Api.Tests</c> now holds this document's member set
    /// against the one the Forum produces, so the two cannot drift apart silently again.</para>
    /// </summary>
    internal string DuplicateRefusalJson() => $$"""
    {"type":"curia/posts/duplicate-question",
    "title":"A question this close to an open one on the same board is refused; here is that thread",
    "detail":"cosine=0.920 lexical_overlap=0.740 model=hashed-ngram@1 answers=1",
    "canonical":{"post_id":"{{PostId}}","digest":"{{Submission.PrefixedDigest}}","board":"b"},
    "answers":[{{AnswerJson()}}],
    "similarity":{"cosine_bp":9200,"lexical_overlap_bp":7400,
    "refuse_cosine_bp":9400,"refuse_lexical_overlap_bp":6000,"annotate_cosine_bp":8500,
    "model":"hashed-ngram@1"},
    "override":"R8.20: re-sign the question with not_duplicate: true and a duplicate_rationale; the override is logged and counts against you if later judged wrong"}
    """.ReplaceLineEndings(string.Empty);

    /// <summary>
    /// The digest of a report the answer's provenance names as reproducing it. Present so the
    /// answer's envelope is the richest the Forum can serve — an owner, a reproduction, a proof —
    /// and a rendering that dropped any of them has something to lose.
    /// </summary>
    internal const string AnswerReproducedBy = "sha256:5eed000000000000000000000000000000000000000000000000000000000001";

    /// <summary>The canonical thread's answer as the single-post read serves it.</summary>
    internal string AnswerJson()
    {
        var canonical = Encoding.UTF8.GetString(Answer.Canonical.Span);

        return $$"""
        {"provenance":{"content_type":"{{PostEnvelope.RequiredContentType}}","warning":{{JsonString(Provenance.StandardWarning)}},
        "author":"{{Author}}","owner_verified":true,"signature_valid":true,
        "verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c",
        "owner":"owner:stub-attested","reproductions":["{{AnswerReproducedBy}}"],"contradictions":[]},
        "post_id":"{{AnswerPostId}}","board":"b","kind":"answer","parent":"{{PostId}}",
        "server_ts":"1970-01-01T00:00:00.0000000+00:00","digest":"{{Answer.PrefixedDigest}}",
        "canonical":{{JsonString(canonical)}},"signature":"{{Answer.Signature}}",
        "rendered":{{JsonString(Datamarking.Render(canonical, MarkingMode.None))}},"accepted":false,
        "log_index":{{AnswerIndex}},"inclusion_proof":{{ProofAt(AnswerIndex, AnswerIndex < HeadTreeSize ? HeadTreeSize : Leaves.Length)}}}
        """.ReplaceLineEndings(string.Empty);
    }

    /// <summary>
    /// Serve a wrong <c>leaf_hash</c> on the <b>log-entry</b> route alone, leaving the proof honest.
    ///
    /// <para>The two values arrive on two independent responses and a Forum controls both, so a
    /// verifier has to disagree with each separately. Without this knob the entry route and the
    /// proof route always agreed with each other, and the comparison against the entry's own
    /// reported hash was never exercised.</para>
    /// </summary>
    internal bool EntryRouteReportsAWrongLeafHash { get; set; }

    /// <summary>
    /// Answer <c>/v1/jwks</c> with <c>200</c> and no keys — a successful fetch that carries nothing
    /// to verify against, which is a different state from a fetch that failed.
    /// </summary>
    internal bool JwksIsEmpty { get; set; }

    /// <summary>Serve a head whose <c>kid</c> names no key the log publishes: rotation, or substitution.</summary>
    internal bool HeadNamesAnUnpublishedKid { get; set; }

    /// <summary>
    /// Serve the post's proof against the whole log rather than against the signed head's size.
    ///
    /// <para>What a real Forum does whenever the leaf is not covered by the latest head; here it is
    /// forced for a leaf that <i>is</i> covered, so the verifier's re-request path can be reached
    /// with a proof that would otherwise already be against the right size.</para>
    /// </summary>
    internal bool PostProofIsAgainstTheWholeLog { get; set; }

    /// <summary>
    /// Sign a head, correctly and with the log's own key, over a root that is not this tree's.
    ///
    /// <para>The one case an intact end-to-end run cannot produce, and therefore the one a live
    /// Forum can never falsify: the head verifies, the audit path verifies, and the two are about
    /// different trees. Without this knob, deleting the head-covers comparison leaves every test
    /// green.</para>
    /// </summary>
    internal bool HeadCommitsToTheWrongRoot { get; set; }

    /// <summary>
    /// Alter the entry's payload after the tree was built. The tree still verifies against the
    /// root; the entry no longer hashes to the leaf the proof is about -- which is exactly the
    /// falsification the errata names for R6.52.
    /// </summary>
    internal void AlterTheEntryPayload()
    {
        var payload = (JsonValue.Object)Entry.Members.First(m => m.Key == LogLeaf.PayloadMember).Value;
        var altered = new JsonValue.Object(
        [
            .. payload.Members.Select(m => m.Key == "board"
                ? new KeyValuePair<string, JsonValue>("board", new JsonValue.String("somewhere-else"))
                : m),
        ]);

        Entry = new JsonValue.Object(
        [
            .. Entry.Members.Select(m => m.Key == LogLeaf.PayloadMember
                ? new KeyValuePair<string, JsonValue>(LogLeaf.PayloadMember, altered)
                : m),
        ]);
    }

    /// <summary>Replace the entry's <c>canonical</c> with another document's, breaking R11.29's binding.</summary>
    internal void BindTheEntryToADifferentPost()
    {
        var payload = (JsonValue.Object)Entry.Members.First(m => m.Key == LogLeaf.PayloadMember).Value;
        var rebound = new JsonValue.Object(
        [
            .. payload.Members.Select(m => m.Key == "canonical"
                ? new KeyValuePair<string, JsonValue>("canonical", new JsonValue.String("{\"v\":1}"))
                : m),
        ]);

        Entry = new JsonValue.Object(
        [
            .. Entry.Members.Select(m => m.Key == LogLeaf.PayloadMember
                ? new KeyValuePair<string, JsonValue>(LogLeaf.PayloadMember, rebound)
                : m),
        ]);

        // The tree is rebuilt around the rebound entry, so the arithmetic still verifies and only
        // the binding is broken. Without this the test would pass on the leaf mismatch instead, and
        // would say nothing about R11.29.
        Rebuild();
    }

    /// <summary>The tree the served proofs and roots are computed from.</summary>
    internal ImmutableArray<ImmutableArray<byte>> Leaves { get; private set; }

    // ---- the served documents -----------------------------------------------------------------

    internal string PostJson()
    {
        // ActaEndpoints.ProofFor's own rule: against the latest signed head when it covers the
        // leaf, otherwise against the whole log. A post accepted since the operator last signed
        // therefore arrives with a proof against a size no head commits to -- the normal case the
        // verifier has to tell apart from an attack.
        var against = PostIndex < HeadTreeSize && !PostProofIsAgainstTheWholeLog
            ? HeadTreeSize
            : Leaves.Length;
        var proof = ProofAt(PostIndex, against);

        return $$"""
        {"provenance":{"content_type":"{{PostEnvelope.RequiredContentType}}","warning":{{JsonString(Provenance.StandardWarning)}},
        "author":"{{Author}}","owner_verified":true,"signature_valid":true,
        "verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c",
        "owner":null,"reproductions":[],"contradictions":[]},
        "post_id":"{{PostId}}","board":"b","kind":"question","parent":null,
        "server_ts":"1970-01-01T00:00:00.0000000+00:00","digest":"{{Submission.PrefixedDigest}}",
        "canonical":{{JsonString(Canonical)}},"signature":"{{Submission.Signature}}",
        "rendered":{{JsonString(Datamarking.Render(Canonical, MarkingMode.None))}},"accepted":false,
        "log_index":{{PostIndex}},"inclusion_proof":{{proof}}}
        """.ReplaceLineEndings(string.Empty);
    }

    /// <summary>
    /// A one-hit search page carrying the same post, so <c>curia_search</c> can be driven by the
    /// P22 gate. A tool a gate classifies as returning agent-authored content and never invokes is
    /// a row that asserts nothing.
    /// </summary>
    internal string SearchJson() => $$"""
    {"results":[{"post":{{PostJson()}},"score_micro":1000000}],
    "next_cursor":null,
    "floor":{"surface":"mcp-search","min_verification":"V0","source":"published",
    "applies_to":["answer","finding"],"not_applicable_to":["question","comment"]},
    "model":"hashed-ngram@1","corpus_bound":1}
    """.ReplaceLineEndings(string.Empty);

    internal string JwksJson()
    {
        if (JwksIsEmpty) return """{"keys":[]}""";

        var p = PublicSigningParameters();
        return $$"""
        {"keys":[{"kty":"EC","crv":"P-256","alg":"ES256","kid":"{{AgentKid}}",
        "x":"{{Base64Url.EncodeToString(p.Q.X!)}}","y":"{{Base64Url.EncodeToString(p.Q.Y!)}}"}]}
        """.ReplaceLineEndings(string.Empty);
    }

    internal string LogJwksJson()
    {
        var p = _logKey.ExportParameters(includePrivateParameters: false);
        return $$"""
        {"keys":[{"kty":"EC","crv":"P-256","alg":"ES256","kid":"{{LogKid}}",
        "x":"{{Base64Url.EncodeToString(p.Q.X!)}}","y":"{{Base64Url.EncodeToString(p.Q.Y!)}}",
        "valid_from":"1970-01-01T00:00:00Z","log_index":0}]}
        """.ReplaceLineEndings(string.Empty);
    }

    internal string HeadJson(int? treeSize = null)
    {
        var size = treeSize ?? HeadTreeSize;
        var root = HeadCommitsToTheWrongRoot
            ? MerkleTree.LeafHash("a root from a tree that is not this one"u8)
            : RootAt(size);

        var document = LogEntries.HeadDocument(root, "1970-01-01T00:00:00Z", size);
        var canonical = LogEntries.HeadCanonical(document).TryGetValue(out var bytes, out _)
            ? bytes
            : throw new InvalidOperationException("the head has no canonical form");

        using var signer = HeadSignedByAStranger ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var key = signer ?? _logKey;

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal) { ["ES256"] = new Es256Adapter() },
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal),
            DetachedJws.HeadTyp);

        // The kid the head claims. An unpublished one is what a log-key rotation the Acta has not
        // registered looks like, and also what substitution looks like: a verifier cannot tell them
        // apart and must refuse either way.
        var kid = HeadNamesAnUnpublishedKid ? "log-unpublished" : LogKid;

        var signature = jws
            .Sign(canonical, new SigningKey("ES256", kid, key.ExportECPrivateKey()))
            .TryGetValue(out var value, out _)
            ? value!.Compact
            : throw new InvalidOperationException("the head would not sign");

        return $$"""
        {"head":{"root_hash":"{{LogEntries.Prefixed(root)}}","timestamp":"1970-01-01T00:00:00Z",
        "tree_size":{{size}}},"kid":"{{kid}}","signature":"{{signature}}","log_index":{{size}},
        "signature_valid":true,"current_tree_size":{{Leaves.Length}}}
        """.ReplaceLineEndings(string.Empty);
    }

    internal string EntryJson(int index)
    {
        var entry = index == PostIndex ? Entry : index == AnswerIndex ? AnswerEntry : null;
        var body = entry is null
            ? "{\"actor_id\":null,\"aggregate_id\":\"x\",\"event_id\":\"x\",\"event_type\":\"filler\",\"payload\":{},\"server_ts\":\"1970-01-01T00:00:00.000000Z\"}"
            : Render(entry);

        var leafHash = EntryRouteReportsAWrongLeafHash
            ? LogEntries.Prefixed(MerkleTree.LeafHash("a hash this entry does not have"u8))
            : LogEntries.Prefixed(Leaves[index]);

        return $$"""
        {"log_index":{{index}},"leaf_hash":"{{leafHash}}","entry":{{body}}}
        """.ReplaceLineEndings(string.Empty);
    }

    internal string ProofAt(int index, int treeSize)
    {
        var prefix = Leaves[..treeSize];
        var path = MerkleTree.InclusionPath(prefix, index);
        var leafHash = ProofCarriesAWrongLeafHash
            ? LogEntries.Prefixed(MerkleTree.LeafHash("not this leaf"u8))
            : LogEntries.Prefixed(Leaves[index]);

        return $$"""
        {"log_index":{{index}},"tree_size":{{treeSize}},"leaf_hash":"{{leafHash}}",
        "audit_path":[{{string.Join(",", path.Select(n => $"\"{LogEntries.Prefixed(n)}\""))}}],
        "root_hash":"{{LogEntries.Prefixed(MerkleTree.Root(prefix))}}","head_signed":true}
        """.ReplaceLineEndings(string.Empty);
    }

    internal string ConsistencyJson(int fromSize, int toSize)
    {
        var path = MerkleTree.ConsistencyPath(Leaves[..toSize], fromSize);

        return $$"""
        {"from_size":{{fromSize}},"to_size":{{toSize}},
        "from_root":"{{LogEntries.Prefixed(RootAt(fromSize))}}","to_root":"{{LogEntries.Prefixed(RootAt(toSize))}}",
        "path":[{{string.Join(",", path.Select(n => $"\"{LogEntries.Prefixed(n)}\""))}}]}
        """.ReplaceLineEndings(string.Empty);
    }

    internal ImmutableArray<byte> RootAt(int treeSize) => MerkleTree.Root(Leaves[..treeSize]);

    /// <summary>
    /// A client over a handler this stub owns. The seven routes a verification touches are served;
    /// anything else is a 404 naming the path, so a route added to the verifier and forgotten here
    /// shows up as an unrouted path rather than as a mysterious refusal.
    /// </summary>
    /// <summary>
    /// The raw <see cref="HttpClient"/> over this stub, for tests that assert on status codes and
    /// headers rather than on what <see cref="ForumClient"/> made of them. Caller-owned.
    /// </summary>
    internal HttpClient RawClient()
    {
        _handler ??= new StubHandler(this);
        return new HttpClient(_handler, disposeHandler: false) { BaseAddress = Forum };
    }

    internal ForumClient Client()
    {
        // One client for the stub's lifetime. Called more than once per test, and a fresh
        // HttpClient per call would abandon the previous one rather than replace it.
        _handler ??= new StubHandler(this);
        _http ??= new HttpClient(_handler, disposeHandler: false) { BaseAddress = Forum };
        return new ForumClient(_http, Forum);
    }

    public void Dispose()
    {
        _http?.Dispose();
        _handler?.Dispose();
        _agent.Dispose();
        _logKey.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private void Rebuild()
    {
        var leaves = Leaves.ToBuilder();
        leaves[PostIndex] = LeafOf(Entry);
        Leaves = leaves.ToImmutable();
    }

    private static ImmutableArray<byte> LeafOf(JsonValue.Object entry) =>
        CanonicalJson.Canonicalize(entry).TryGetValue(out var bytes, out _)
            ? MerkleTree.LeafHash(bytes.Span)
            : throw new InvalidOperationException("the entry has no canonical form");

    private static string Render(JsonValue.Object entry) =>
        CanonicalJson.Canonicalize(entry).TryGetValue(out var bytes, out _)
            ? Encoding.UTF8.GetString(bytes.Span)
            : throw new InvalidOperationException("the entry has no canonical form");

    /// <summary>R6.46's six members, as the Forum's event store holds one <c>post.accepted</c>.</summary>
    private static JsonValue.Object BuildEntry(
        string postId, string kind, string? parent, string canonical, string signature, string digest) => new(
    [
        new(LogLeaf.ActorIdMember, new JsonValue.String(Author)),
        new(LogLeaf.AggregateIdMember, new JsonValue.String(postId)),
        new(LogLeaf.EventIdMember, new JsonValue.String(postId)),
        new(LogLeaf.EventTypeMember, new JsonValue.String("post.accepted")),
        new(LogLeaf.PayloadMember, new JsonValue.Object(
        [
            new("author", new JsonValue.String(Author)),
            new("board", new JsonValue.String("b")),
            new("canonical", new JsonValue.String(canonical)),
            new("digest", new JsonValue.String(digest)),
            new("kind", new JsonValue.String(kind)),
            new("parent", parent is null ? JsonValue.Null.Instance : new JsonValue.String(parent)),
            new("post_id", new JsonValue.String(postId)),
            new("risk_flags", new JsonValue.Array([])),
            new("server_ts", new JsonValue.String("1970-01-01T00:00:00.000000Z")),
            new("signature", new JsonValue.String(signature)),
        ])),
        new(LogLeaf.ServerTimestampMember, new JsonValue.String("1970-01-01T00:00:00.000000Z")),
    ]);

    private static string JsonString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private sealed class StubHandler(StubLog log) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            var method = request.Method;

            if (path.StartsWith("/v1/jwks", StringComparison.Ordinal) && log.JwksUnreachable)
                throw new HttpRequestException("the key set host is unreachable");

            log.Record(method, path, query, request);

            var (status, body, challenge) = method == HttpMethod.Post
                ? Written(log, path, request)
                : Answered(Answer(log, path, query));

            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    status == HttpStatusCode.OK || status == HttpStatusCode.Created
                        ? "application/json"
                        : "application/problem+json"),
            };

            // RFC 9449 §8: the nonce challenge is the normal flow, not an error path, and it carries
            // the nonce to use. A 401 without a DPoP-Nonce header is a different thing entirely -- a
            // replay, a bad signature -- so the header goes on only when the nonce was the problem,
            // exactly as the Forum's NonceChallengeOrProblemAsync does.
            if (challenge is not null)
            {
                response.Headers.TryAddWithoutValidation("DPoP-Nonce", challenge);
                response.Headers.TryAddWithoutValidation("WWW-Authenticate", "DPoP error=\"use_dpop_nonce\"");
            }

            return Task.FromResult(response);
        }

        private static (HttpStatusCode Status, string Body, string? Challenge) Answered(
            (HttpStatusCode Status, string Body) answer) => (answer.Status, answer.Body, null);

        /// <summary>
        /// The write surface. Separated from <see cref="Answer"/> by <b>method</b>, which the
        /// handler used not to read at all — so a <c>POST /v1/posts/{id}/flags</c> matched the
        /// <c>StartsWith("/v1/posts/")</c> read branch and came back as a <i>post document</i> with
        /// 200. A write test on that fixture could pass having exercised the read path, which is
        /// trap 12: a fixture that agrees with the defect.
        ///
        /// <para><b>Every write path requires a DPoP proof carrying the current nonce</b>, as the
        /// Forum's does (R5.19, errata A17), and burns its <c>jti</c> only once the nonce has passed
        /// -- the Forum's own order. The token endpoint takes no nonce, again as the Forum's does.</para>
        /// </summary>
        private static (HttpStatusCode Status, string Body, string? Challenge) Written(
            StubLog log, string path, HttpRequestMessage request)
        {
            var body = log.WrittenBodies.Count > 0 ? log.WrittenBodies[^1] : string.Empty;

            if (path == "/oauth/token")
            {
                log.TokenSubject = FormValue(body, "client_id");
                return (HttpStatusCode.OK, StubLog.TokenJson(), null);
            }

            var routed = path == "/v1/posts"
                || (path.StartsWith("/v1/posts/", StringComparison.Ordinal)
                    && (path.EndsWith("/flags", StringComparison.Ordinal) || path.EndsWith("/accept", StringComparison.Ordinal)));

            if (!routed)
            {
                return (HttpStatusCode.NotFound,
                    $$"""{"type":"curia/stub/unrouted","title":"The stub serves no POST {{path}}"}""", null);
            }

            if (Proof(request) is not { } proof)
                return (HttpStatusCode.Unauthorized, Problem("curia/authn/missing-dpop-proof", "A DPoP proof is required"), null);

            var (jti, nonce) = ProofClaims(proof);
            log.WriteProofs.Add((jti, nonce));

            if (nonce is null)
                return (HttpStatusCode.Unauthorized, Problem("curia/authn/nonce-missing", "A DPoP nonce is required"), log.CurrentNonce);

            if (!string.Equals(nonce, log.CurrentNonce, StringComparison.Ordinal))
                return (HttpStatusCode.Unauthorized, Problem("curia/authn/nonce-stale", "The DPoP nonce is not current"), log.CurrentNonce);

            if (jti is null || !log._burnedJtis.Add(jti))
                return (HttpStatusCode.Unauthorized, Problem("curia/authn/replay", "This DPoP proof has already been used"), null);

            if (path == "/v1/posts")
            {
                if (log.DeniesSubmissionsAtTier is { } tier)
                {
                    return (HttpStatusCode.Forbidden, $$"""
                        {"type":"curia/authz/denied","title":"Not permitted at this trust tier","detail":"table-10/denied tier={{tier}}"}
                        """, null);
                }

                if (log.RefusesAsDuplicate) return (HttpStatusCode.Conflict, log.DuplicateRefusalJson(), null);

                // The Forum verifies against the token's subject, not the envelope's claim about
                // itself (ForumEndpoints.SubmitAsync, VERIFY). Modelled so a client that signed as one
                // agent while holding another's token is refused here as it would be there.
                var (author, digest) = Submitted(body);
                if (!string.Equals(author, log.TokenSubject, StringComparison.Ordinal))
                {
                    return (HttpStatusCode.Unauthorized, Problem(
                        "curia/content/author-principal-mismatch",
                        "The envelope's author does not match the authenticated principal"), null);
                }

                return (HttpStatusCode.Created, StubLog.ReceiptJson(digest), null);
            }

            return path.EndsWith("/flags", StringComparison.Ordinal)
                ? (HttpStatusCode.Created, StubLog.FlagReceiptJson(), null)
                : (HttpStatusCode.OK, StubLog.AcceptanceJson(), null);
        }

        private static string? Proof(HttpRequestMessage request) =>
            request.Headers.TryGetValues("DPoP", out var values) ? values.FirstOrDefault() : null;

        /// <summary>A proof's <c>jti</c> and <c>nonce</c>, read from its payload. The signature is not checked: the stub is not the thing under test.</summary>
        private static (string? Jti, string? Nonce) ProofClaims(string proof)
        {
            var parts = proof.Split('.');
            if (parts.Length != 3) return (null, null);

            using var payload = System.Text.Json.JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var root = payload.RootElement;

            return (
                root.TryGetProperty("jti", out var jti) ? jti.GetString() : null,
                root.TryGetProperty("nonce", out var nonce) ? nonce.GetString() : null);
        }

        /// <summary>The submitted envelope's author, and its digest computed the way the Forum computes it.</summary>
        private static (string? Author, string Digest) Submitted(string wire)
        {
            var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(wire), AdmitLimits.Default);
            if (!parsed.TryGetValue(out var value, out _)
                || value is not JsonValue.Object submission
                || submission.Members.FirstOrDefault(m => m.Key == "envelope").Value is not JsonValue.Object envelope)
            {
                return (null, string.Empty);
            }

            var author = envelope.Members.FirstOrDefault(m => m.Key == "author").Value is JsonValue.String s ? s.Value : null;
            var digest = CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out _)
                ? Digests.Sha256(canonical).ToPrefixed()
                : string.Empty;

            return (author, digest);
        }

        private static string FormValue(string form, string name)
        {
            foreach (var pair in form.Split('&'))
            {
                var at = pair.IndexOf('=', StringComparison.Ordinal);
                if (at > 0 && pair[..at] == name) return Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' '));
            }

            return string.Empty;
        }

        private static string Problem(string type, string title) => $$"""{"type":"{{type}}","title":"{{title}}"}""";

        private static (HttpStatusCode Status, string Body) Answer(StubLog log, string path, string query)
        {
            if (path.StartsWith("/v1/jwks", StringComparison.Ordinal))
                return (HttpStatusCode.OK, log.JwksJson());

            if (path == "/v1/log/jwks") return (HttpStatusCode.OK, log.LogJwksJson());

            if (path == "/v1/log/head")
            {
                return log.NoSignedHead
                    ? (HttpStatusCode.NotFound,
                        """{"type":"curia/log/no-head","title":"No head has been signed","detail":"0 leaves"}""")
                    : (HttpStatusCode.OK, log.HeadJson());
            }

            if (path.StartsWith("/v1/log/entries/", StringComparison.Ordinal))
            {
                var index = int.Parse(path["/v1/log/entries/".Length..], CultureInfo.InvariantCulture);
                return (HttpStatusCode.OK, log.EntryJson(index));
            }

            if (path.StartsWith("/v1/log/proof/", StringComparison.Ordinal))
            {
                var index = int.Parse(path["/v1/log/proof/".Length..], CultureInfo.InvariantCulture);
                var size = query.Contains("tree_size=", StringComparison.Ordinal)
                    ? int.Parse(Value(query, "tree_size"), CultureInfo.InvariantCulture)
                    : log.HeadTreeSize;
                return (HttpStatusCode.OK, log.ProofAt(index, size));
            }

            if (path == "/v1/log/consistency")
            {
                return (HttpStatusCode.OK, log.ConsistencyJson(
                    int.Parse(Value(query, "from"), CultureInfo.InvariantCulture),
                    int.Parse(Value(query, "to"), CultureInfo.InvariantCulture)));
            }

            if (path.StartsWith("/v1/posts/", StringComparison.Ordinal))
            {
                log._postReads++;
                return (HttpStatusCode.OK, log.ServesADecoyOnTheSecondRead && log._postReads > 1
                    ? log.DecoyJson()
                    : log.PostJson());
            }

            if (path == "/v1/search") return (HttpStatusCode.OK, log.SearchJson());

            return (HttpStatusCode.NotFound,
                $$"""{"type":"curia/stub/unrouted","title":"The stub serves no {{path}}"}""");
        }

        private static string Value(string query, string name)
        {
            var start = query.IndexOf(name + "=", StringComparison.Ordinal) + name.Length + 1;
            var end = query.IndexOf('&', start);
            return end < 0 ? query[start..] : query[start..end];
        }
    }

    /// <summary>
    /// The registered key's public parameters. Reached through <c>ExportPublicKey()</c> because
    /// <c>EnrolledAgent</c> no longer exposes the private half at all (R11.20).
    /// </summary>
    private ECParameters PublicSigningParameters()
    {
        using var key = _agent.ExportPublicKey();
        return key.ExportParameters(includePrivateParameters: false);
    }

}
