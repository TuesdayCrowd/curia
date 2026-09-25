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
