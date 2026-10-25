using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// The stub Forum that <c>Curia.Client.Tests</c> and <c>Curia.Mcp.Tests</c> are proved against,
/// held against this Forum.
///
/// <para><b>Why this exists.</b> Every write-side test in those two suites runs against
/// <c>StubLog</c>, and a stub checked only against itself agrees with whatever its authors believed.
/// Its duplicate refusal was a shape this Forum never serves — the canonical thread flattened into
/// top-level members, the model beside <c>similarity</c> rather than in it — and the client's reader
/// returned null for it, while the stub's own test passed by searching the body for member names
/// that the wrong shape also contained. That is trap 12, a fixture agreeing with a defect, and the
/// only instrument that can see it is one that has both documents in hand.</para>
///
/// <para><b>Member sets, compared exactly, in both directions.</b> A member the Forum serves and the
/// stub omits is a branch no stub-driven test reaches; a member the stub serves and the Forum does
/// not is a branch those tests reach that production never will. Both are reported by path.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class StubFidelityTests(ForumFixture forum) : IClassFixture<ForumFixture>, IDisposable
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private const string Title = "Why does the pooler drop idle connections";
    private const string Body = "The pooler drops a connection idle for ten minutes and the client sees ECONNRESET";

    private readonly StubLog _stub = new();

    public void Dispose()
    {
        _stub.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>R8.19's 409: the stub's document carries exactly the members the Forum's does.</summary>
    [Fact]
    public async Task R8_19_TheStubsDuplicateRefusalHasTheForumsMembers()
    {
        var problem = await RealDuplicateRefusalAsync(TestContext.Current.CancellationToken);

        AssertSameMembers(
            "the duplicate refusal",
            Paths(JsonNode.Parse(problem)!),
            Paths(JsonNode.Parse(_stub.DuplicateRefusalJson())!));
    }

    /// <summary>
    /// R9.32 (errata G32): the post the stub serves carries <c>provenance.superseded_by</c>, as the post this Forum serves does. Found by path in each
    /// document, so a stub that drops the member, or a Forum that does, is named by whichever lacks it.
    /// </summary>
    [Fact]
    public async Task R9_32_TheStubsPostCarriesTheForumsSupersededByMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var agent = ForumAgent.Create(Unique("superseded"), "superseded-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        using var posted = await dpop.PostAsync(http, PostsUrl, token, agent.SignQuestion(Board(), Body, Title, forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var postId = JsonNode.Parse(await posted.Content.ReadAsStringAsync(ct))!["post_id"]!.GetValue<string>();

        var served = Paths(JsonNode.Parse(await http.GetStringAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct))!);
        var stub = Paths(JsonNode.Parse(_stub.PostJson())!);

        Assert.NotEmpty(served);
        Assert.NotEmpty(stub);
        const string Member = "provenance.superseded_by";
        var lacking = new List<string>();
        if (!stub.Contains(Member)) lacking.Add($"the stub's post lacks {Member}");
        if (!served.Contains(Member)) lacking.Add($"the Forum's post lacks {Member}");
        Assert.True(lacking.Count == 0, string.Join("; ", lacking));
    }

    /// <summary>
    /// R9.36 (errata G36): the question the stub serves carries <c>provenance.accepted_answer</c> in each state the Forum serves it in: <c>null</c> where
    /// no acceptance stands, an object with <c>digest</c> and <c>post_id</c> where the answer is served, and an object with <c>digest</c> alone where it is
    /// not (the Forum's word on an answer it withholds). Each stub state is held to the response of this Forum for the same state, by key set and value kind;
    /// a stub that serves nothing, or a different shape, is named by the state.
    /// </summary>
    [Fact]
    public async Task R9_36_TheStubServesTheMemberAsTheForumDoes()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);

        async Task<string> RealMemberAsync()
        {
            var raw = await forum.Client.GetStringAsync(new Uri($"/v1/posts/{scene.Question.PostId}", UriKind.Relative), ct);
            var provenance = JsonNode.Parse(raw)!["provenance"]!.AsObject();
            return provenance.ContainsKey("accepted_answer") ? Describe(provenance["accepted_answer"]) : "absent";
        }

        static string Describe(JsonNode? node) => node switch
        {
            null => "null",
            JsonObject o => "object{" + string.Join(",", o.Select(p => p.Key + ":" + (p.Value?.GetValueKind() ?? JsonValueKind.Null)).Order(StringComparer.Ordinal)) + "}",
            _ => node.GetValueKind().ToString(),
        };

        string StubMember(StubAcceptedAnswer state)
        {
            _stub.AcceptedAnswer = state;
            var provenance = JsonNode.Parse(_stub.PostJson())!["provenance"]!.AsObject();
            return provenance.ContainsKey("accepted_answer") ? Describe(provenance["accepted_answer"]) : "absent";
        }

        // The shapes R9.36 gives, written from the errata's paragraph (a null, an object with digest and post_id, an object with digest alone), so the
        // comparison below holds both documents to the requirement and not only to each other.
        var names = PublishedSearchPage.AcceptedAnswerNames();
        Assert.Equal(["accepted_answer", "digest", "post_id"], names);
        const string Null = "null";
        const string Served = "object{digest:String,post_id:String}";
        const string Unserved = "object{digest:String}";

        var mismatches = new List<string>();

        void Hold(string state, StubAcceptedAnswer stub, string real, string expected)
        {
            var stubbed = StubMember(stub);
            if (real != expected) mismatches.Add($"{state}: the Forum serves {real}, R9.36 gives {expected}");
            if (stubbed != real) mismatches.Add($"{state}: the stub serves {stubbed}, the Forum serves {real}");
        }

        // No acceptance stands.
        Hold("none", StubAcceptedAnswer.None, await RealMemberAsync(), Null);

        // An acceptance stands and the answer is served.
        await scene.AcceptAsync(forum, scene.Answer1, ct);
        Hold("standing", StubAcceptedAnswer.Standing, await RealMemberAsync(), Served);

        // An acceptance stands and the answer is withheld.
        await forum.WithholdAsync(scene.Answer1.PostId, ct);
        Hold("unserved", StubAcceptedAnswer.StandingUnserved, await RealMemberAsync(), Unserved);

        Assert.True(mismatches.Count == 0, "the stub serves the member differently from the Forum: " + string.Join("; ", mismatches));
    }

    /// <summary>
    /// R9.10 (errata G35): the stub's batch answers as this Forum's does for the same request. Per element, an item's members are compared by path
    /// (a current item, an unknown digest, a malformed element); the batch is anonymous on both; and a batch over the cap is refused with the Forum's
    /// problem document, holding <c>cap=</c> and <c>received=</c>. The tool's facts agree with the stub, so the stub is held to the route.
    /// </summary>
    [Fact]
    public async Task R9_34_TheStubsBatchHasTheForumsMembersAndTheForumsCapRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var agent = ForumAgent.Create(Unique("batch"), "batch-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        using var posted = await dpop.PostAsync(http, PostsUrl, token, agent.SignQuestion(Board(), Body, Title, forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var digest = JsonNode.Parse(await posted.Content.ReadAsStringAsync(ct))!["digest"]!.GetValue<string>();
        var unknown = "sha256:" + new string('0', 64);

        async Task<JsonNode> BatchAsync(HttpClient client, params string[] digests)
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { digests }), Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(new Uri("/v1/posts/batch", UriKind.Relative), content, ct);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        }

        // R9.36: the Forum serves the member on a question (null, no acceptance stands), so the stub does too.
        _stub.AcceptedAnswer = StubAcceptedAnswer.None;
        using var stubHttp = _stub.RawClient();
        var served = await BatchAsync(http, digest, unknown, "nope");
        var stubbed = await BatchAsync(stubHttp, _stub.Submission.PrefixedDigest, unknown, "nope");

        var (forumItems, stubItems) = (served["items"]!.AsArray(), stubbed["items"]!.AsArray());
        Assert.Equal(forumItems.Count, stubItems.Count);
        string[] positions = ["a current item", "an unknown digest", "a malformed element"];
        for (var i = 0; i < forumItems.Count; i++)
            AssertSameMembers(positions[i], Paths(forumItems[i]!), Paths(stubItems[i]!));

        // The cap: sixty-five digests are over the Forum's cap of sixty-four.
        var over = Enumerable.Range(0, 65).Select(i => "sha256:" + i.ToString("x", System.Globalization.CultureInfo.InvariantCulture).PadLeft(64, '0')).ToArray();
        _stub.BatchCap = 64;
        AssertSameMembers("the over-cap refusal", Paths(await BatchAsync(http, over)), Paths(await BatchAsync(stubHttp, over)));
        Assert.Equal(
            (await BatchAsync(http, over))["detail"]!.GetValue<string>(),
            (await BatchAsync(stubHttp, over))["detail"]!.GetValue<string>());
    }

    /// <summary>
    /// The reference client reads this Forum's 409 completely — the first time its reader has run
    /// against the Forum rather than against a document written beside it.
    /// </summary>
    [Fact]
    public async Task R8_61_TheClientReadsTheForumsDuplicateRefusalCompletely()
    {
        var problem = await RealDuplicateRefusalAsync(TestContext.Current.CancellationToken);
        using var expected = JsonDocument.Parse(problem);
        var similarity = expected.RootElement.GetProperty("similarity");

        var document = JsonReader.Parse(Encoding.UTF8.GetBytes(problem), AdmitLimits.Default)
            .TryGetValue(out var value, out var error)
            ? value!
            : throw new InvalidOperationException(error!.Type);

        var read = new Refusal(
            RefusalKind.Conflict,
            409,
            new Curia.Domain.Primitives.Error("curia/posts/duplicate-question", "duplicate"),
            document).AsDuplicate;

        Assert.NotNull(read);
        Assert.Equal(similarity.GetProperty("refuse_cosine_bp").GetInt32(), read.RefuseCosineBp);
        Assert.Equal(similarity.GetProperty("refuse_lexical_overlap_bp").GetInt32(), read.RefuseLexicalOverlapBp);
        Assert.Equal(similarity.GetProperty("annotate_cosine_bp").GetInt32(), read.AnnotateCosineBp);
        Assert.Equal(similarity.GetProperty("model").GetString(), read.Model);

        // Every answer the Forum served was read as a post; none was skipped or counted unreadable.
        Assert.Equal(expected.RootElement.GetProperty("answers").GetArrayLength(), read.Answers.Length);
        Assert.Equal(0, read.UnreadableAnswers);
        Assert.NotEmpty(read.Answers);
    }

    /// <summary>
    /// R6.54 (errata G16): the two documents the client's fourth check reads -- an agent's key set,
    /// which names the leaf binding each key, and that leaf's entry -- carry exactly the members the
    /// Forum's do. Until this fact the stub's key set had never been held to the Forum's, and it lacked
    /// a member the Forum has always served (<c>curia_not_before</c>).
    /// </summary>
    [Fact]
    public async Task R6_54_TheStubsKeySetAndKeyBindingHaveTheForumsMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var agent = ForumAgent.Create(Unique("keyset"), "keyset-" + Guid.NewGuid().ToString("N")[..8]);
        using (var enrolled = await agent.EnrollAsync(http, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var keySet = await http.GetStringAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(agent.AgentId)}", UriKind.Relative), ct);
        var index = JsonNode.Parse(keySet)!["keys"]![0]!["curia_log_index"]!.GetValue<long>();
        var entry = await http.GetStringAsync(new Uri($"/v1/log/entries/{index}", UriKind.Relative), ct);

        AssertSameMembers("the key set", Paths(JsonNode.Parse(keySet)!), Paths(JsonNode.Parse(_stub.JwksJson())!));
        AssertSameMembers("the key-binding entry", Paths(JsonNode.Parse(entry)!), Paths(JsonNode.Parse(_stub.EntryJson(_stub.KeyIndex))!));
    }

    /// <summary>A submission's 201 receipt, and a flag's.</summary>
    [Fact]
    public async Task TheStubsReceiptsHaveTheForumsMembers()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var board = Board();

        var agent = ForumAgent.Create(Unique("receipt"), "receipt-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);

        using var posted = await dpop.PostAsync(http, PostsUrl, token, agent.SignQuestion(board, Body, Title, forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        var receipt = await posted.Content.ReadAsStringAsync(ct);

        AssertSameMembers(
            "the submission receipt",
            Paths(JsonNode.Parse(receipt)!),
            Paths(JsonNode.Parse(StubLog.ReceiptJson("sha256:" + new string('0', 64)))!));

        var postId = JsonNode.Parse(receipt)!["post_id"]!.GetValue<string>();
        using var flagged = await dpop.PostAsync(
            http,
            $"http://localhost/v1/posts/{postId}/flags",
            token,
            """{"kind":"incorrect","rationale":"The premise is wrong."}"""u8.ToArray(),
            forum.Now,
            ct,
            contentType: "application/json");
        Assert.Equal(HttpStatusCode.Created, flagged.StatusCode);

        AssertSameMembers(
            "the flag receipt",
            Paths(JsonNode.Parse(await flagged.Content.ReadAsStringAsync(ct))!),
            Paths(JsonNode.Parse(StubLog.FlagReceiptJson())!));
    }

    /// <summary>
    /// A Table 10 denial, which R11.26 says an MCP tool must make up for: the stub's refusal carries
    /// the Forum's type and title, and a detail in the Forum's form.
    /// </summary>
    [Fact]
    public async Task R11_26_TheStubsTierDenialIsTheForumsDenial()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var board = Board();

        var asker = ForumAgent.Create(Unique("asker"), "asker-" + Guid.NewGuid().ToString("N")[..8]);
        var (askerDpop, askerToken) = await asker.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        using var asked = await askerDpop.PostAsync(http, PostsUrl, askerToken, asker.SignQuestion(board, Body, Title, forum.Now), forum.Now, ct);
        var questionId = JsonNode.Parse(await asked.Content.ReadAsStringAsync(ct))!["post_id"]!.GetValue<string>();

        // A fresh agent is T0, and Table 10 gives answer/create to T1 and above.
        var novice = ForumAgent.Create(Unique("novice"), "novice-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await novice.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        using var denied = await dpop.PostAsync(
            http, PostsUrl, token, novice.SignAnswer(board, "An answer from a novice.", questionId, forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var real = JsonNode.Parse(await denied.Content.ReadAsStringAsync(ct))!;

        _stub.DeniesSubmissionsAtTier = "T0";
        var stub = await StubDenialAsync(ct);

        Assert.Equal(real["type"]!.GetValue<string>(), stub["type"]!.GetValue<string>());
        Assert.Equal(real["title"]!.GetValue<string>(), stub["title"]!.GetValue<string>());
        Assert.Equal(real["detail"]!.GetValue<string>(), stub["detail"]!.GetValue<string>());
        AssertSameMembers("the tier denial", Paths(real), Paths(stub));
    }

    private async Task<JsonNode> StubDenialAsync(CancellationToken ct)
    {
        var loaded = _stub.Store.Load("alice");
        Assert.True(loaded.TryGetValue(out var agent, out var loadError), loadError?.Type);

        using (agent)
        {
            var session = new ForumSession(_stub.Client(), agent, _stub.Store, TimeProvider.System);
            var signed = SubmissionBuilder.Build(
                agent,
                new PostDraft { Kind = Curia.Domain.Content.PostKind.Question, Board = "b", Title = "t", Body = "b" },
                DateTimeOffset.UnixEpoch);
            Assert.True(signed.TryGetValue(out var submission, out var signError), signError?.Type);

            var posted = await session.SubmitAsync(submission.Wire, ct);
            Assert.False(posted.TryGetValue(out _, out var refusal));
            Assert.Equal(RefusalKind.Authorization, refusal.Kind);

            return JsonNode.Parse(
                $$"""{"type":"{{refusal.Error.Type}}","title":"{{refusal.Error.Title}}","detail":"{{refusal.Error.Detail}}"}""")!;
        }
    }

    /// <summary>A real 409: a question, an answer to it from a T1 agent, and the same question again.</summary>
    private async Task<string> RealDuplicateRefusalAsync(CancellationToken ct)
    {
        var http = forum.Client;
        var board = Board();

        var asker = ForumAgent.Create(Unique("asker"), "asker-" + Guid.NewGuid().ToString("N")[..8]);
        var (askerDpop, askerToken) = await asker.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        using var asked = await askerDpop.PostAsync(http, PostsUrl, askerToken, asker.SignQuestion(board, Body, Title, forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        var questionId = JsonNode.Parse(await asked.Content.ReadAsStringAsync(ct))!["post_id"]!.GetValue<string>();

        // An answer, so the refusal's `answers` array has an element whose members can be compared.
        // An empty array would compare equal to anything, which is trap 11 in this test's own shape.
        var helper = ForumAgent.Create(Unique("helper"), "helper-" + Guid.NewGuid().ToString("N")[..8]);
        var (helperDpop, _) = await helper.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        await forum.AttestOwnerAsync(helper.AgentId, ct, owner: "owner:" + helper.Kid);
        for (var i = 0; i < 3; i++)
        {
            var warm = Guid.NewGuid().ToString("N");
            using var warmed = await helperDpop.PostAsync(
                http, PostsUrl, await helperDpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct),
                helper.SignQuestion(board, $"warmup {i} {warm}", $"Warmup {i} {warm}", forum.Now), forum.Now, ct);
            Assert.Equal(HttpStatusCode.Created, warmed.StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1));

        using var answered = await helperDpop.PostAsync(
            http, PostsUrl, await helperDpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct),
            helper.SignAnswer(board, "Enable TCP keepalive below the pooler's idle timeout.", questionId, forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, answered.StatusCode);

        var again = ForumAgent.Create(Unique("again"), "again-" + Guid.NewGuid().ToString("N")[..8]);
        var (againDpop, againToken) = await again.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        using var refused = await againDpop.PostAsync(http, PostsUrl, againToken, again.SignQuestion(board, Body, Title, forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        return await refused.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Every member path in a document: <c>a</c>, <c>a.b</c>, and <c>answers[].x</c> for the union of
    /// an array's object elements. Values are not compared — the two are different posts — only
    /// which members exist.
    /// </summary>
    private static SortedSet<string> Paths(JsonNode node)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        Walk(node, string.Empty, paths);
        return paths;

        static void Walk(JsonNode? node, string prefix, SortedSet<string> into)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (name, child) in o)
                    {
                        var path = prefix.Length == 0 ? name : prefix + "." + name;
                        into.Add(path);
                        Walk(child, path, into);
                    }

                    break;
                case JsonArray a:
                    foreach (var element in a) Walk(element, prefix + "[]", into);
                    break;
                default:
                    break;
            }
        }
    }

    private static void AssertSameMembers(string what, SortedSet<string> forumPaths, SortedSet<string> stubPaths)
    {
        // Non-vacuity: two empty sets are equal, and a comparison of nothing would pass.
        Assert.NotEmpty(forumPaths);

        var onlyForum = forumPaths.Except(stubPaths, StringComparer.Ordinal).ToArray();
        var onlyStub = stubPaths.Except(forumPaths, StringComparer.Ordinal).ToArray();

        Assert.True(
            onlyForum.Length == 0 && onlyStub.Length == 0,
            $"The stub's {what} and the Forum's differ.\n"
            + $"  served by the Forum and missing from the stub: {string.Join(", ", onlyForum)}\n"
            + $"  served by the stub and never by the Forum:    {string.Join(", ", onlyStub)}");
    }

    private static string Board() => "fidelity-" + Guid.NewGuid().ToString("N")[..8];

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";
}
