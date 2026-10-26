using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.OperatorTool;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

using Posted = Curia.Api.Tests.ScenePost;
using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// R9.36 (errata G36): every post of kind <c>question</c> the Forum serves carries <c>provenance.accepted_answer</c>, which is never absent on a
/// question: the standing acceptance's answer by digest (and post id where the Forum serves it), or <c>null</c>; no other kind carries it.
///
/// <para><b>Oracles.</b> The answer a test expects is the one it asked the asker to accept, and its digest is computed here from the canonical text
/// the Forum served for it with <c>System.Security.Cryptography</c> and compared with the receipt's; its post id is the <c>event_id</c> the events table
/// holds for the digest, read by SQL; the <c>answer.accepted</c> count is read from the table; the member names are parsed from the errata. The null
/// and absent states are told apart on the <b>raw response text</b>, since a deserializer collapses them. A negative case always has a control.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptedAnswerEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string BatchUrl = "/v1/posts/batch";

    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();

    private sealed record Served(Dictionary<string, JsonElement> Posts, string Raw);

    public static TheoryData<string> Routes() => ["post", "thread", "board", "search", "batch"];

    private Task<Scene> SceneAsync(CancellationToken ct) => Scene.BuildAsync(forum, ct);

    private Task<HttpStatusCode> AcceptAsync(Scene scene, Posted answer, CancellationToken ct) => scene.AcceptAsync(forum, answer, ct);

    private static Dictionary<string, JsonElement> ById(IEnumerable<JsonElement> posts) =>
        posts.ToDictionary(p => p.GetProperty("post_id").GetString()!, p => p, StringComparer.Ordinal);

    /// <summary>What a route serves about the scene's posts (the thread and board of the scene, a search for its term, a batch of the digests).</summary>
    private async Task<Served> ServedAsync(string route, Scene scene, CancellationToken ct)
    {
        var http = forum.Client;
        Posted[] all = [scene.Question, scene.Answer1, scene.Answer2, scene.Comment, scene.Revision];
        switch (route)
        {
            case "post":
            {
                var raw = new List<string>();
                foreach (var post in all) raw.Add(await http.GetStringAsync(new Uri($"/v1/posts/{post.PostId}", UriKind.Relative), ct));
                return new Served(ById(raw.Select(r => JsonDocument.Parse(r).RootElement.Clone())), string.Join('\n', raw));
            }

            case "thread":
            {
                var text = await http.GetStringAsync(new Uri($"/v1/threads/{scene.Question.PostId}", UriKind.Relative), ct);
                return new Served(ById(JsonDocument.Parse(text).RootElement.EnumerateArray().Select(e => e.Clone())), text);
            }

            case "board":
            {
                var text = await http.GetStringAsync(new Uri($"/v1/boards/{scene.Board}/posts", UriKind.Relative), ct);
                return new Served(ById(JsonDocument.Parse(text).RootElement.EnumerateArray().Select(e => e.Clone())), text);
            }

            case "search":
            {
                var text = await http.GetStringAsync(new Uri($"/v1/search?q={scene.Term}&board={scene.Board}&limit=50", UriKind.Relative), ct);
                var results = JsonDocument.Parse(text).RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").Clone());
                return new Served(ById(results), text);
            }

            case "batch":
            {
                using var response = await http.PostAsJsonAsync(BatchUrl, new { digests = all.Select(p => p.Digest).ToArray() }, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var items = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray()
                    .Select(i => i.GetProperty("post")).Where(p => p.ValueKind == JsonValueKind.Object).Select(p => p.Clone());
                return new Served(ById(items), text);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(route), route, "not one of the routes the theory names");
        }
    }

    /// <summary>The raw text of a single read of a post, for the facts that tell <c>null</c> from absent.</summary>
    private async Task<string> RawAsync(string postId, CancellationToken ct) =>
        await forum.Client.GetStringAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);

    /// <summary>The member of a served question as (present, element); fails by name when the post is not served.</summary>
    private static (bool Present, JsonElement Value) MemberOf(JsonElement post) =>
        post.GetProperty("provenance").TryGetProperty("accepted_answer", out var member) ? (true, member) : (false, default);

    /// <summary>The question's member from a raw read, failing by name when it is absent (an exception from a missing key would be a compile-shaped failure, not an assertion).</summary>
    private static JsonElement RequireMember(string raw)
    {
        Assert.True(JsonDocument.Parse(raw).RootElement.GetProperty("provenance").TryGetProperty("accepted_answer", out var member), "provenance.accepted_answer is absent on a question");
        return member.Clone();
    }

    private static string Sha256Of(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>The post id the events table holds for the digest: the event the Forum appended when it accepted the post.</summary>
    private async Task<string> EventIdOfAsync(string digest, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var query = new NpgsqlCommand("SELECT event_id FROM events WHERE event_type = 'post.accepted' AND payload->>'digest' = @digest;", connection);
        query.Parameters.AddWithValue("digest", digest);
        return (string)(await query.ExecuteScalarAsync(ct))!;
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task R9_36_EveryQuestionOnTheRoutesCarriesTheMemberAndNoOtherKindDoes(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await SceneAsync(ct);

        // Before any acceptance: the member is present and null on the question.
        var before = await ServedAsync(route, scene, ct);
        Assert.True(before.Posts.ContainsKey(scene.Question.PostId), $"{route} did not serve the question:\n{before.Raw}");
        var (present, none) = MemberOf(before.Posts[scene.Question.PostId]);
        Assert.True(present, $"provenance.accepted_answer is absent on a question (route: {route})");
        Assert.Equal(JsonValueKind.Null, none.ValueKind);

        await AcceptAsync(scene, scene.Answer1, ct);

        // After: the member is an object naming the answer the test asked the asker to accept.
        var after = await ServedAsync(route, scene, ct);
        var (presentAfter, standing) = MemberOf(after.Posts[scene.Question.PostId]);
        Assert.True(presentAfter, $"provenance.accepted_answer is absent on a question after an acceptance (route: {route})");
        Assert.Equal(JsonValueKind.Object, standing.ValueKind);
        Assert.Equal(scene.Answer1.Digest, standing.GetProperty("digest").GetString());

        // No other kind the test signed carries it, on any route that serves it.
        foreach (var other in new[] { scene.Answer1, scene.Answer2, scene.Comment, scene.Revision })
        {
            if (!after.Posts.TryGetValue(other.PostId, out var served)) continue;
            Assert.False(MemberOf(served).Present, $"{route}: a post that is not a question carries provenance.accepted_answer");
        }
    }

    [Fact]
    public async Task R9_36_TheInboxRowOfAnOpenQuestionCarriesNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await SceneAsync(ct);
        var reader = await Scene.EnrolAsync(forum, "inbox", ct);

        var token = await reader.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/inbox?board={scene.Board}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("DPoP", token);

        // RFC 9449 section 4.2: the proof's htu is the target without its query.
        request.Headers.Add("DPoP", reader.Dpop.Proof("GET", "http://localhost/v1/inbox", forum.Now, token, nonce: null));
        using var response = await forum.Client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);

        var rows = JsonDocument.Parse(text).RootElement.GetProperty("results").EnumerateArray().Select(r => r.Clone()).ToArray();
        var row = rows.SingleOrDefault(r => r.GetProperty("post_id").GetString() == scene.Question.PostId);
        Assert.True(row.ValueKind == JsonValueKind.Object, $"the inbox did not serve the open question:\n{text}");

        var (present, member) = MemberOf(row);
        Assert.True(present, "provenance.accepted_answer is absent on a question (route: GET /v1/inbox)");
        Assert.Equal(JsonValueKind.Null, member.ValueKind);
    }

    [Fact]
    public async Task R9_36_NullBeforeAnAcceptanceTheAnswerAfterAndTheLatestAfterAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await SceneAsync(ct);

        // Raw text, since a deserializer collapses null and absent: present and null before any acceptance.
        Assert.Contains("\"accepted_answer\":null", await RawAsync(scene.Question.PostId, ct), StringComparison.Ordinal);

        await AcceptAsync(scene, scene.Answer1, ct);
        var servedAnswer = JsonDocument.Parse(await RawAsync(scene.Answer1.PostId, ct)).RootElement;
        var independentDigest = Sha256Of(servedAnswer.GetProperty("canonical").GetString()!);
        Assert.Equal(scene.Answer1.Digest, independentDigest);

        var raw = await RawAsync(scene.Question.PostId, ct);
        Assert.DoesNotContain("\"accepted_answer\":null", raw, StringComparison.Ordinal);
        var member = JsonDocument.Parse(raw).RootElement.GetProperty("provenance").GetProperty("accepted_answer");
        Assert.Equal(independentDigest, member.GetProperty("digest").GetString());
        Assert.Equal(await EventIdOfAsync(independentDigest, ct), member.GetProperty("post_id").GetString());
        Assert.Equal(scene.Answer1.PostId, member.GetProperty("post_id").GetString());

        // A second acceptance replaces it.
        await AcceptAsync(scene, scene.Answer2, ct);
        var replaced = RequireMember(await RawAsync(scene.Question.PostId, ct));
        Assert.Equal(scene.Answer2.Digest, replaced.GetProperty("digest").GetString());

        // Resolving the same answer twice leaves one event and the same member (R8.68).
        var events = await forum.CountEventsAsync("answer.accepted", scene.Question.PostId, ct);
        Assert.Equal(HttpStatusCode.OK, await AcceptAsync(scene, scene.Answer2, ct));
        Assert.Equal(events, await forum.CountEventsAsync("answer.accepted", scene.Question.PostId, ct));
        var again = RequireMember(await RawAsync(scene.Question.PostId, ct));
        Assert.Equal(replaced.GetRawText(), again.GetRawText());

        // The answer is not a question: its read carries no such key at all.
        Assert.DoesNotContain("accepted_answer", await RawAsync(scene.Answer2.PostId, ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_36_AWithheldAcceptedAnswerIsNamedByDigestAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await SceneAsync(ct);
        await AcceptAsync(scene, scene.Answer1, ct);

        // Control: before the withholding, the member names the answer by id as well as by digest.
        var before = RequireMember(await RawAsync(scene.Question.PostId, ct));
        Assert.Equal(scene.Answer1.PostId, before.GetProperty("post_id").GetString());

        await forum.WithholdAsync(scene.Answer1.PostId, ct);

        var raw = await RawAsync(scene.Question.PostId, ct);
        Assert.True(JsonDocument.Parse(raw).RootElement.GetProperty("provenance").TryGetProperty("accepted_answer", out var member), "accepted_answer is absent");
        Assert.Equal(JsonValueKind.Object, member.ValueKind);
        Assert.Equal(scene.Answer1.Digest, member.GetProperty("digest").GetString());
        Assert.False(member.TryGetProperty("post_id", out _), "the withheld answer's post id is served");
        Assert.DoesNotContain(scene.Answer1.PostId, raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_36_TheValidatorOfAQuestionChangesWhenItsAnswerIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await SceneAsync(ct);

        // The representation includes the inclusion proof, taken against the latest signed head when it covers the leaf and against the whole
        // log otherwise, so a head is signed first and the only thing that can move the question's tag is the acceptance.
        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            Assert.Equal(0, await OperatorCommands.RunAsync(
                ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, ct, readLogSigningKeyPem: () => LogKeyPem));
        }

        async Task<HttpResponseMessage> GetAsync(string? ifNoneMatch)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/posts/{scene.Question.PostId}");
            if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            return await forum.Client.SendAsync(request, ct);
        }

        using var first = await GetAsync(null);
        var tag = first.Headers.ETag!.ToString();

        // Control: with the head signed and nothing changed, the tag is current.
        using (var current = await GetAsync(tag))
            Assert.Equal(HttpStatusCode.NotModified, current.StatusCode);

        await AcceptAsync(scene, scene.Answer1, ct);

        using var after = await GetAsync(tag);
        Assert.True(after.StatusCode == HttpStatusCode.OK, $"expected 200 for the question's old validator after an acceptance, got {(int)after.StatusCode}");
        Assert.NotEqual(tag, after.Headers.ETag!.ToString());
    }

    [Fact]
    public async Task R9_36_TheMemberNamesAreTheOnesTheErratumPublishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await SceneAsync(ct);
        await AcceptAsync(scene, scene.Answer1, ct);

        // R9.36's three names, and R9.41's one: the object gained `superseded_by`, which errata G40 publishes beside them.
        var published = PublishedSearchPage.AcceptedAnswerNames().Append(PublishedReadersWords.R941Member()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(4, published.Length);

        var provenance = JsonDocument.Parse(await RawAsync(scene.Question.PostId, ct)).RootElement.GetProperty("provenance");
        Assert.True(provenance.TryGetProperty("accepted_answer", out var member), "accepted_answer is absent");
        var served = provenance.EnumerateObject().Select(m => m.Name).Where(n => n == "accepted_answer")
            .Concat(member.EnumerateObject().Select(m => m.Name))
            .Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(published, served);
    }

    /// <summary>
    /// <b>Weak by construction:</b> both sides read one fold, so a bug in the fold moves both. R9.36's independent oracle is the digest and event id the
    /// test computed in <c>R9_36_NullBeforeAnAcceptanceTheAnswerAfterAndTheLatestAfterAnother</c>. This fact holds only that the two members never disagree.
    /// </summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task R9_36_AnAnswersAcceptedFlagAndItsQuestionsMemberNeverDisagree(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await SceneAsync(ct);

        foreach (var accept in new Posted?[] { null, scene.Answer1, scene.Answer2 })
        {
            if (accept is not null) await AcceptAsync(scene, accept, ct);
            var served = await ServedAsync(route, scene, ct);
            var (present, member) = MemberOf(served.Posts[scene.Question.PostId]);
            var named = present && member.ValueKind == JsonValueKind.Object && member.TryGetProperty("post_id", out var id) ? id.GetString() : null;

            foreach (var answer in new[] { scene.Answer1, scene.Answer2 })
            {
                if (!served.Posts.TryGetValue(answer.PostId, out var post)) continue;
                Assert.Equal(named == answer.PostId, post.GetProperty("accepted").GetBoolean());
            }
        }
    }

    /// <summary>Control for FPC23: an answer, a comment and a revision the test signed carry no member on any route that serves them.</summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task R9_36_ThePostsAnAcceptanceDoesNotNameCarryNoMember(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await SceneAsync(ct);
        await AcceptAsync(scene, scene.Answer1, ct);

        var served = await ServedAsync(route, scene, ct);
        var checkedCount = 0;
        foreach (var other in new[] { scene.Answer1, scene.Answer2, scene.Comment, scene.Revision })
        {
            if (!served.Posts.TryGetValue(other.PostId, out var post)) continue;
            checkedCount++;
            Assert.False(MemberOf(post).Present, $"{route}: post {other.PostId} (not a question) carries provenance.accepted_answer");
        }

        // Non-vacuity: the route served at least the accepted answer, so the loop above looked at a post.
        Assert.True(checkedCount > 0, $"{route} served none of the answer, comment or revision, so the check looked at nothing");
    }

    /// <summary>
    /// Control for the facts above: the oracles they compute are sound on this fixture, whatever the Forum says of the member. The digest recomputed from the
    /// canonical text the Forum served is the receipt's, the events table names the answer's post by the digest, and accepting one answer twice records one
    /// event (201, then 200).
    /// </summary>
    [Fact]
    public async Task R9_36_TheOraclesTheOtherFactsComputeAreSoundOnThisFixture()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await SceneAsync(ct);

        var served = JsonDocument.Parse(await RawAsync(scene.Answer1.PostId, ct)).RootElement;
        Assert.Equal(scene.Answer1.Digest, Sha256Of(served.GetProperty("canonical").GetString()!));
        Assert.Equal(scene.Answer1.PostId, await EventIdOfAsync(scene.Answer1.Digest, ct));

        Assert.Equal(HttpStatusCode.Created, await AcceptAsync(scene, scene.Answer1, ct));
        var events = await forum.CountEventsAsync("answer.accepted", scene.Question.PostId, ct);
        Assert.Equal(1, events);
        Assert.Equal(HttpStatusCode.OK, await AcceptAsync(scene, scene.Answer1, ct));
        Assert.Equal(events, await forum.CountEventsAsync("answer.accepted", scene.Question.PostId, ct));
        Assert.Equal(HttpStatusCode.Created, await AcceptAsync(scene, scene.Answer2, ct));
        Assert.Equal(2, await forum.CountEventsAsync("answer.accepted", scene.Question.PostId, ct));
    }
}
