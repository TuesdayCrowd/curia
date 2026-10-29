using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.32 (errata G32): every post the Forum serves carries <c>provenance.superseded_by</c>, the revisions R9.28 counts, in log order.
///
/// <para><b>Oracles.</b> The revision a test expects is the digest and id the Forum returned when the test posted it, never anything the
/// serving code computed; the set a test expects for a hostile world is written from R9.28's sentence (a revision, by the original's own
/// author, chained by <c>prev</c>); the batch route's <c>successors</c> is the second witness (R9.10); and the member names are parsed from
/// the errata. A negative case always has a control that must pass.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SupersededByEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";
    private const string BatchUrl = "/v1/posts/batch";

    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();

    private sealed record Actor(ForumAgent Agent, DpopClient Dpop);

    private sealed record Posted(string PostId, string Digest);

    /// <summary>One route's answer, as the posts it served keyed by post id.</summary>
    private sealed record Served(Dictionary<string, JsonElement> Posts, string Raw);

    private static readonly string[] CoveredMembers = ["canonical", "signature", "digest"];

    private static readonly string[] ServingRoutes = ["post", "thread", "board", "batch"];

    public static TheoryData<string> Routes() => ["post", "thread", "board", "search", "batch"];

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<Actor> EnrolAsync(HttpClient http, string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{Suffix()}", $"{stem}-{Suffix()}");
        var (dpop, _) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        return new Actor(agent, dpop);
    }

    /// <summary>Table 11's T1, the tier that may answer: an attested owner, three questions and the tenure clock (as <c>DedupeEndpointTests</c>).</summary>
    private async Task<Actor> T1Async(HttpClient http, string stem, string board, CancellationToken ct)
    {
        var actor = await EnrolAsync(http, stem, ct);
        await forum.AttestOwnerAsync(actor.Agent.AgentId, ct, owner: $"owner:{stem}-{Suffix()}");
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            await PostAsync(http, actor, actor.Agent.SignQuestion(board, $"warmup {i} {nonce}", $"Warmup {i} {nonce}", forum.Now), ct);
        }

        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1));
        return actor;
    }

    private async Task<Posted> PostAsync(HttpClient http, Actor actor, byte[] wire, CancellationToken ct)
    {
        using var response = await actor.Dpop.PostAsync(
            http, PostsUrl, await actor.Dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct), wire, forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);

        using var json = JsonDocument.Parse(body);
        return new Posted(json.RootElement.GetProperty("post_id").GetString()!, json.RootElement.GetProperty("digest").GetString()!);
    }

    /// <summary>A question and its author's revision, both posted through the API; the term is in both, for the search route.</summary>
    private async Task<(Actor Author, string Board, string Term, Posted Original, Posted Revision)> RevisedQuestionAsync(HttpClient http, CancellationToken ct)
    {
        var author = await EnrolAsync(http, "sup", ct);
        var board = "board-" + Suffix();
        var term = "zsup" + Suffix();
        var original = await PostAsync(http, author, author.Agent.SignQuestion(board, $"The first draft mentions {term}.", $"A draft about {term}", forum.Now), ct);
        var revision = await PostAsync(http, author, author.Agent.SignRevision(board, $"The corrected text still mentions {term}.", original.PostId, original.Digest, forum.Now), ct);
        return (author, board, term, original, revision);
    }

    private async Task<Posted> UnrevisedQuestionAsync(HttpClient http, string board, string term, CancellationToken ct)
    {
        var author = await EnrolAsync(http, "plain", ct);
        return await PostAsync(http, author, author.Agent.SignQuestion(board, $"A question nobody has corrected, about {term}.", $"A current post about {term}", forum.Now), ct);
    }

    private static Dictionary<string, JsonElement> ById(IEnumerable<JsonElement> posts) =>
        posts.ToDictionary(p => p.GetProperty("post_id").GetString()!, p => p, StringComparer.Ordinal);

    /// <summary>What each of the five routes the theory names serves about the given posts (the thread and board of <paramref name="board"/>, the post ids, a search for <paramref name="term"/>, a batch of the digests).</summary>
    private async Task<Served> ServedAsync(string route, string board, string term, IReadOnlyList<Posted> posts, CancellationToken ct)
    {
        var http = forum.Client;
        switch (route)
        {
            case "post":
            {
                var served = new List<JsonElement>();
                var raw = new List<string>();
                foreach (var post in posts)
                {
                    var text = await http.GetStringAsync(new Uri($"/v1/posts/{post.PostId}", UriKind.Relative), ct);
                    raw.Add(text);
                    served.Add(JsonDocument.Parse(text).RootElement.Clone());
                }

                return new Served(ById(served), string.Join('\n', raw));
            }

            case "thread":
            {
                var text = await http.GetStringAsync(new Uri($"/v1/threads/{posts[0].PostId}", UriKind.Relative), ct);
                return new Served(ById(JsonDocument.Parse(text).RootElement.EnumerateArray().Select(e => e.Clone())), text);
            }

            case "board":
            {
                var text = await http.GetStringAsync(new Uri($"/v1/boards/{board}/posts", UriKind.Relative), ct);
                return new Served(ById(JsonDocument.Parse(text).RootElement.EnumerateArray().Select(e => e.Clone())), text);
            }

            case "search":
            {
                var text = await http.GetStringAsync(new Uri($"/v1/search?q={term}&board={board}", UriKind.Relative), ct);
                var results = JsonDocument.Parse(text).RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").Clone());
                return new Served(ById(results), text);
            }

            case "batch":
            {
                using var response = await http.PostAsJsonAsync(BatchUrl, new { digests = posts.Select(p => p.Digest).ToArray() }, ct);
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

    /// <summary>The served list for a post, as (digest, post_id or null) pairs; fails by name when the member is absent or is not an array.</summary>
    private static (string Digest, string? PostId)[] ListOf(string route, JsonElement post)
    {
        var provenance = post.GetProperty("provenance");
        Assert.True(
            provenance.TryGetProperty("superseded_by", out var list),
            $"{route}: post {post.GetProperty("post_id").GetString()} serves no provenance.superseded_by (members: {string.Join(", ", provenance.EnumerateObject().Select(m => m.Name))})");
        Assert.True(list.ValueKind == JsonValueKind.Array, $"{route}: provenance.superseded_by is {list.ValueKind}, not an array");

        return [.. list.EnumerateArray().Select(e => (
            e.GetProperty("digest").GetString()!,
            e.TryGetProperty("post_id", out var id) ? id.GetString() : null))];
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task R9_32_EveryRouteThatServesAPostServesSupersededBy(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, board, term, original, revision) = await RevisedQuestionAsync(forum.Client, ct);

        var served = await ServedAsync(route, board, term, [original, revision], ct);

        Assert.NotEmpty(served.Posts);
        Assert.True(served.Posts.ContainsKey(original.PostId), $"{route} did not serve the original at all:\n{served.Raw}");
        Assert.Equal([(revision.Digest, (string?)revision.PostId)], ListOf(route, served.Posts[original.PostId]));

        // Control: the revision itself is corrected by nobody, wherever the route serves it.
        if (served.Posts.TryGetValue(revision.PostId, out var served2))
            Assert.Empty(ListOf(route, served2));
        else
            Assert.True(route == "search", $"{route} did not serve the revision, so the control did not run");
    }

    [Fact]
    public async Task R9_32_TheMemberIsTheSetTheBatchReportsAndNamesNoStrangersRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var board = "board-" + Suffix();
        var stranger = await EnrolAsync(http, "stranger", ct);

        // One author per original: a T0 agent may post three times a day, and the fork below takes all three.
        async Task<(Actor Author, Posted Original)> OriginalAsync(string title)
        {
            var author = await EnrolAsync(http, "author", ct);
            return (author, await PostAsync(http, author, author.Agent.SignQuestion(board, "A body for " + title, title, forum.Now), ct));
        }

        // (i) the author's revision: one successor.
        var i = await OriginalAsync("Draft one");
        var iRevision = await PostAsync(http, i.Author, i.Author.Agent.SignRevision(board, "Corrected one.", i.Original.PostId, i.Original.Digest, forum.Now), ct);

        // (ii) a stranger's stored comment naming the digest, (iii) a stranger's stored revision naming it, (iv) the author's own stored
        // comment naming it: all admitted before R8.64, so appended by hand as BatchRetrievalTests does; none corrects anything (R9.28).
        var ii = await OriginalAsync("Draft two");
        await forum.AppendStoredPostAsync(stranger.Agent.Sign(PostKind.Comment, board, "a comment", null, ii.Original.PostId, forum.Now, prev: ii.Original.Digest), ct);
        var iii = await OriginalAsync("Draft three");
        await forum.AppendStoredPostAsync(stranger.Agent.Sign(PostKind.Revision, board, "not the author's", null, iii.Original.PostId, forum.Now, prev: iii.Original.Digest), ct);
        var iv = await OriginalAsync("Draft four");
        await forum.AppendStoredPostAsync(iv.Author.Agent.Sign(PostKind.Comment, board, "the author's own comment", null, iv.Original.PostId, forum.Now, prev: iv.Original.Digest), ct);

        // (v) two revisions of one original: a fork, both in log order.
        var v = await OriginalAsync("Draft five");
        var vFirst = await PostAsync(http, v.Author, v.Author.Agent.SignRevision(board, "Corrected five, one way.", v.Original.PostId, v.Original.Digest, forum.Now), ct);
        // Stored, not posted: the Forum refuses a second revision of a served prev (R8.77), and a log can still hold the fork.
        var (vSecondId, vSecondDigest) = await forum.AppendStoredPostAsync(v.Author.Agent.SignRevision(board, "Corrected five, another way.", v.Original.PostId, v.Original.Digest, forum.Now), ct);
        var vSecond = new Posted(vSecondId, vSecondDigest);

        // Written from R9.28 by hand: what the test posted, by the rule.
        var expected = new (Posted Original, string[] Digests)[]
        {
            (i.Original, [iRevision.Digest]),
            (ii.Original, []),
            (iii.Original, []),
            (iv.Original, []),
            (v.Original, [vFirst.Digest, vSecond.Digest]),
        };

        var served = await ServedAsync("post", board, "", [.. expected.Select(e => e.Original)], ct);
        var batch = await ServedBatchSuccessorsAsync([.. expected.Select(e => e.Original.Digest)], ct);

        Assert.Equal(expected.Length, served.Posts.Count);
        for (var n = 0; n < expected.Length; n++)
        {
            var listed = ListOf("post", served.Posts[expected[n].Original.PostId]).Select(e => e.Digest).ToArray();
            Assert.True(expected[n].Digests.SequenceEqual(listed), $"original ({n + 1}): the test posted [{string.Join(", ", expected[n].Digests)}] by R9.28, the Forum serves [{string.Join(", ", listed)}]");
            Assert.True(batch[n].SequenceEqual(listed), $"original ({n + 1}): the batch's successors are [{string.Join(", ", batch[n])}], the served member [{string.Join(", ", listed)}]");
        }

        Assert.Contains(expected, e => e.Digests.Length == 2);
    }

    private async Task<string[][]> ServedBatchSuccessorsAsync(string[] digests, CancellationToken ct)
    {
        using var response = await forum.Client.PostAsJsonAsync(BatchUrl, new { digests }, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return [.. JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray()
            .Select(i => i.GetProperty("successors").EnumerateArray().Select(s => s.GetString()!).ToArray())];
    }

    [Fact]
    public async Task R9_32_AWithheldRevisionIsListedByDigestAloneAndItsIdAppearsNowhere()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, board, term, original, revision) = await RevisedQuestionAsync(forum.Client, ct);

        // Control: before withholding, the element carries the id.
        var before = await ServedAsync("post", board, term, [original], ct);
        Assert.Equal([(revision.Digest, (string?)revision.PostId)], ListOf("post", before.Posts[original.PostId]));

        await forum.WithholdAsync(revision.PostId, ct);

        foreach (var route in ServingRoutes)
        {
            var served = await ServedAsync(route, board, term, [original], ct);
            Assert.True(served.Posts.ContainsKey(original.PostId), $"{route} no longer serves the original");
            var listed = ListOf(route, served.Posts[original.PostId]);
            Assert.Equal([(revision.Digest, (string?)null)], listed);
            Assert.DoesNotContain(revision.PostId, served.Raw, StringComparison.Ordinal);
            Assert.False(served.Posts.ContainsKey(revision.PostId), $"{route} still serves the withheld revision");
        }
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task R9_32_ACurrentPostStatesAnEmptyListAndNeverOmitsTheMember(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "board-" + Suffix();
        var term = "zcur" + Suffix();
        var post = await UnrevisedQuestionAsync(forum.Client, board, term, ct);

        var served = await ServedAsync(route, board, term, [post], ct);

        Assert.True(served.Posts.ContainsKey(post.PostId), $"{route} did not serve the post:\n{served.Raw}");
        Assert.Empty(ListOf(route, served.Posts[post.PostId]));
    }

    /// <summary>
    /// A post's representation includes its inclusion proof, taken against the latest signed head when that head covers the leaf and against
    /// the whole log otherwise (<c>ActaEndpoints.ProofFor</c>), so before any head is signed every append changes every tag. This fact signs
    /// one first, so the only thing that can change the original's tag is the revision, and the control's tag is stable.
    /// </summary>
    [Fact]
    public async Task R9_32_TheValidatorOfTheOriginalChangesWhenARevisionLands()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var author = await EnrolAsync(http, "etag", ct);
        var board = "board-" + Suffix();
        var original = await PostAsync(http, author, author.Agent.SignQuestion(board, "Does this change?", "Validator", forum.Now), ct);
        var untouched = await UnrevisedQuestionAsync(http, board, "zetag", ct);

        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            Assert.Equal(0, await OperatorCommands.RunAsync(
                ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, ct, readLogSigningKeyPem: () => LogKeyPem));
        }

        async Task<HttpResponseMessage> GetAsync(string postId, string? ifNoneMatch)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/posts/{postId}");
            if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            return await http.SendAsync(request, ct);
        }

        using var first = await GetAsync(original.PostId, null);
        var tag = first.Headers.ETag!.ToString();
        using var untouchedFirst = await GetAsync(untouched.PostId, null);
        var untouchedTag = untouchedFirst.Headers.ETag!.ToString();

        // Non-vacuity: with the head signed and nothing yet changed, both tags are current.
        using (var current = await GetAsync(original.PostId, tag))
            Assert.Equal(HttpStatusCode.NotModified, current.StatusCode);

        await PostAsync(http, author, author.Agent.SignRevision(board, "Now it does.", original.PostId, original.Digest, forum.Now), ct);

        using var after = await GetAsync(original.PostId, tag);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.NotEqual(tag, after.Headers.ETag!.ToString());

        // Control: a post nothing has changed about still answers 304 after the same append.
        using var control = await GetAsync(untouched.PostId, untouchedTag);
        Assert.Equal(HttpStatusCode.NotModified, control.StatusCode);
    }

    [Fact]
    public async Task R9_32_TheMemberIsOutsideEverythingTheSignatureAndTheLogCover()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var author = await EnrolAsync(http, "outside", ct);
        var board = "board-" + Suffix();
        var original = await PostAsync(http, author, author.Agent.SignQuestion(board, "Is this covered?", "Coverage", forum.Now), ct);

        static string[] Covered(JsonElement post) =>
            [.. CoveredMembers.Select(n => post.GetProperty(n).GetString()!),
                post.GetProperty("log_index").GetInt64().ToString(CultureInfo.InvariantCulture)];

        var before = JsonDocument.Parse(await http.GetStringAsync(new Uri($"/v1/posts/{original.PostId}", UriKind.Relative), ct)).RootElement.Clone();
        await PostAsync(http, author, author.Agent.SignRevision(board, "Corrected.", original.PostId, original.Digest, forum.Now), ct);
        var after = JsonDocument.Parse(await http.GetStringAsync(new Uri($"/v1/posts/{original.PostId}", UriKind.Relative), ct)).RootElement.Clone();

        Assert.Equal(Covered(before), Covered(after));
        Assert.All(Covered(after), c => Assert.NotEmpty(c));

        // The client's own signature check of the original still holds once the revision has landed.
        var client = new ForumClient(http, http.BaseAddress!);
        Assert.True((await client.GetPostAsync(original.PostId, MarkingMode.None, ct)).TryGetValue(out var post, out var refusal), refusal?.Summary);
        Assert.True((await client.GetJwksAsync(author.Agent.AgentId, ct)).TryGetValue(out var keys, out var keysRefusal), keysRefusal?.Summary);
        Assert.True(SignatureCheck.Verify(post!, keys).Verified);
    }

    [Fact]
    public async Task R9_32_TheInboxAndTheDuplicateRefusalServeTheMemberToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;

        // The inbox: another agent's open question that its author has since revised, read by an agent that did not ask it.
        var (_, inboxBoard, _, question, questionRevision) = await RevisedQuestionAsync(http, ct);
        var reader = await EnrolAsync(http, "reader", ct);
        var token = await reader.Dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);
        using var inboxRequest = new HttpRequestMessage(HttpMethod.Get, $"/v1/inbox?board={inboxBoard}");
        inboxRequest.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        inboxRequest.Headers.Add("DPoP", reader.Dpop.Proof("GET", "http://localhost/v1/inbox", forum.Now, token, nonce: null));
        using var inboxResponse = await http.SendAsync(inboxRequest, ct);
        var inboxText = await inboxResponse.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, inboxResponse.StatusCode);
        var inbox = ById(JsonDocument.Parse(inboxText).RootElement.GetProperty("results").EnumerateArray().Select(r => r.Clone()));
        Assert.True(inbox.ContainsKey(question.PostId), $"the inbox did not serve the open question:\n{inboxText}");
        Assert.Equal([(questionRevision.Digest, (string?)questionRevision.PostId)], ListOf("inbox", inbox[question.PostId]));

        // The duplicate refusal's `answers` (R8.61): an answer its T1 author has since revised.
        const string Title = "ECONNRESET from npgsql after the pool idles";
        const string Body = "npgsql throws ECONNRESET after the pooler idles the connection; keepalive is off";
        var board = "board-" + Suffix();
        var asker = await EnrolAsync(http, "asker", ct);
        var helper = await T1Async(http, "helper", board, ct);
        var canonical = await PostAsync(http, asker, asker.Agent.SignQuestion(board, Body, Title, forum.Now), ct);
        var answer = await PostAsync(http, helper, helper.Agent.SignAnswer(board, "Set Keepalive=30 in the connection string.", canonical.PostId, forum.Now), ct);
        var answerRevision = await PostAsync(http, helper, helper.Agent.SignRevision(board, "Set Keepalive=30 and Tcp Keepalive Time=60.", answer.PostId, answer.Digest, forum.Now), ct);

        var again = await EnrolAsync(http, "again", ct);
        using var refused = await again.Dpop.PostAsync(
            http, PostsUrl, await again.Dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct),
            again.Agent.SignQuestion(board, Body + " Does anyone have a fix?", Title, forum.Now), forum.Now, ct);
        var problem = await refused.Content.ReadAsStringAsync(ct);
        Assert.True(refused.StatusCode == HttpStatusCode.Conflict, problem);

        var answers = ById(JsonDocument.Parse(problem).RootElement.GetProperty("answers").EnumerateArray().Select(a => a.Clone()));
        Assert.True(answers.ContainsKey(answer.PostId), $"the refusal did not serve the answer:\n{problem}");
        Assert.Equal([(answerRevision.Digest, (string?)answerRevision.PostId)], ListOf("duplicate refusal", answers[answer.PostId]));
    }

    [Fact]
    public async Task R9_32_TheMemberTheErrataNamesIsTheMemberTheForumServes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, board, term, original, revision) = await RevisedQuestionAsync(forum.Client, ct);

        var published = PublishedSupersession.Names();
        Assert.Equal(3, published.Length);

        var served = await ServedAsync("post", board, term, [original, revision], ct);
        var provenance = served.Posts[original.PostId].GetProperty("provenance");

        // Found by walking the served document: the provenance members that speak of supersession, and the keys of the element they hold.
        var members = provenance.EnumerateObject().Where(m => m.Name.Contains("supersed", StringComparison.Ordinal)).ToArray();
        var element = members
            .Where(m => m.Value.ValueKind == JsonValueKind.Array && m.Value.GetArrayLength() > 0)
            .SelectMany(m => m.Value[0].EnumerateObject().Select(k => k.Name));
        var found = members.Select(m => m.Name).Concat(element).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(published, found);
    }
}
