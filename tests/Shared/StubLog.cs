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
        PostIndex = postIndex;
        Entry = BuildEntry(Canonical, Submission.Signature, Submission.PrefixedDigest);

        // The post's leaf is real; the rest are filler, distinct and in no particular relation to
        // it. A verifier only ever fetches the entry it is proving, so the others need only exist.
        var leaves = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(treeSize);
        for (var i = 0; i < treeSize; i++)
        {
            leaves.Add(i == postIndex
                ? LeafOf(Entry)
                : MerkleTree.LeafHash(Encoding.UTF8.GetBytes($"filler-leaf-{i}")));
        }

        Leaves = leaves.MoveToImmutable();
        HeadTreeSize = treeSize;
    }

    internal SignedSubmission Submission { get; }

    internal string Canonical { get; }

    internal int PostIndex { get; }

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
        {"provenance":{"content_type":"{{PostEnvelope.RequiredContentType}}","warning":"w",
        "author":"{{Author}}","owner_verified":true,"signature_valid":true,
        "verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c"},
        "post_id":"{{PostId}}","board":"b","kind":"question","parent":null,
        "server_ts":"1970-01-01T00:00:00.0000000+00:00","digest":"{{decoy.PrefixedDigest}}",
        "canonical":{{JsonString(canonical)}},"signature":"{{decoy.Signature}}",
        "rendered":{{JsonString(Datamarking.Render(canonical, MarkingMode.None))}},"accepted":false}
        """.ReplaceLineEndings(string.Empty);
    }

    /// <summary>Serve a <c>leaf_hash</c> on the proof that is not the leaf's hash.</summary>
    internal bool ProofCarriesAWrongLeafHash { get; set; }

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
        {"provenance":{"content_type":"{{PostEnvelope.RequiredContentType}}","warning":"w",
        "author":"{{Author}}","owner_verified":true,"signature_valid":true,
        "verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c"},
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

        var p = _agent.SigningKey.ExportParameters(includePrivateParameters: false);
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
        var entry = index == PostIndex ? Entry : null;
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
    private static JsonValue.Object BuildEntry(string canonical, string signature, string digest) => new(
    [
        new(LogLeaf.ActorIdMember, new JsonValue.String(Author)),
        new(LogLeaf.AggregateIdMember, new JsonValue.String(PostId)),
        new(LogLeaf.EventIdMember, new JsonValue.String(PostId)),
        new(LogLeaf.EventTypeMember, new JsonValue.String("post.accepted")),
        new(LogLeaf.PayloadMember, new JsonValue.Object(
        [
            new("author", new JsonValue.String(Author)),
            new("board", new JsonValue.String("b")),
            new("canonical", new JsonValue.String(canonical)),
            new("digest", new JsonValue.String(digest)),
            new("kind", new JsonValue.String("question")),
            new("parent", JsonValue.Null.Instance),
            new("post_id", new JsonValue.String(PostId)),
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

            if (path.StartsWith("/v1/jwks", StringComparison.Ordinal) && log.JwksUnreachable)
                throw new HttpRequestException("the key set host is unreachable");

            var (status, body) = Answer(log, path, query);

            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    body,
                    Encoding.UTF8,
                    status == HttpStatusCode.OK ? "application/json" : "application/problem+json"),
            };

            return Task.FromResult(response);
        }

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
}
