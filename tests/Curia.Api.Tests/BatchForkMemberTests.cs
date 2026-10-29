using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.43 (errata G42): each item of R9.10's batch that carries a post carries <c>fork</c>, the other successors of the post the item's post
/// revises, and an item that carries no post carries none.
///
/// <para><b>Oracles (plan ruling 10).</b> The revisions a test expects are the digests and ids the Forum returned when the test posted them, and
/// the digests the test's own envelope builder computed for the ones it appended by hand, never the index's report of itself; the id a sibling
/// is served under is checked against the single read, which is a different route from the batch; and the member's name is parsed from the
/// errata (<see cref="PublishedForkWords"/>). A negative case always has a control that must pass.</para>
///
/// <para><b>Fixture.</b> The in-process host (<see cref="ForumFixture"/>), which serves the same routes and writes the same JSON as the
/// Kestrel one: the batch is a document, and no fact here is about a socket.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BatchForkMemberTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";
    private const string BatchUrl = "/v1/posts/batch";

    private static readonly string[] StatesInOrder = ["withheld", "unknown", "malformed", "current"];

    private sealed record Actor(ForumAgent Agent, DpopClient Dpop);

    private sealed record Posted(string PostId, string Digest);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static string Member => PublishedForkWords.MemberName();

    private async Task<Actor> EnrolAsync(string stem)
    {
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{Suffix()}", $"{stem}-{Suffix()}");
        var (dpop, _) = await agent.AuthenticateAsync(forum.Client, TokenEndpoint, forum.Now, Ct);
        return new Actor(agent, dpop);
    }

    /// <summary>Table 11's T1, which may answer (as <c>SupersededByEndpointTests</c>).</summary>
    private async Task<Actor> T1Async(string stem, string board)
    {
        var actor = await EnrolAsync(stem);
        await forum.AttestOwnerAsync(actor.Agent.AgentId, Ct, owner: $"owner:{stem}-{Suffix()}");
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            await PostAsync(actor, actor.Agent.SignQuestion(board, $"warmup {i} {nonce}", $"Warmup {i} {nonce}", forum.Now));
        }

        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1));
        return actor;
    }

    private async Task<Posted> PostAsync(Actor actor, byte[] wire)
    {
        using var response = await actor.Dpop.PostAsync(
            forum.Client, PostsUrl, await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct), wire, forum.Now, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);

        using var json = JsonDocument.Parse(body);
        return new Posted(json.RootElement.GetProperty("post_id").GetString()!, json.RootElement.GetProperty("digest").GetString()!);
    }

    private async Task<Posted> QuestionAsync(Actor author, string board, string title) =>
        await PostAsync(author, author.Agent.SignQuestion(board, "A body for " + title, title, forum.Now));

    private async Task<Posted> ReviseAsync(Actor author, string board, Posted of, string body) =>
        await PostAsync(author, author.Agent.SignRevision(board, body, of.PostId, of.Digest, forum.Now));

    /// <summary>A revision appended as a stored post: it takes no posting budget, which a three-way fork would otherwise exhaust (a T0 agent posts three a day).</summary>
    private async Task<Posted> ReviseStoredAsync(Actor author, string board, Posted of, string body)
    {
        var (postId, digest) = await forum.AppendStoredPostAsync(author.Agent.SignRevision(board, body, of.PostId, of.Digest, forum.Now), Ct);
        return new Posted(postId, digest);
    }

    private async Task<JsonElement[]> BatchAsync(params string[] digests)
    {
        using var response = await forum.Client.PostAsJsonAsync(BatchUrl, new { digests }, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var items = JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().Select(i => i.Clone()).ToArray();
        Assert.Equal(digests.Length, items.Length);
        return items;
    }

    /// <summary>The item's <c>fork</c> as (digest, post_id or null) pairs; fails by name when the member is absent or is not an array.</summary>
    private static (string Digest, string? PostId)[] ForkOf(JsonElement item)
    {
        Assert.True(item.TryGetProperty(Member, out var fork), $"the item for {item.GetProperty("digest").GetString()} carries no {Member} member (members: {string.Join(", ", item.EnumerateObject().Select(m => m.Name))})");
        Assert.Equal(JsonValueKind.Array, fork.ValueKind);
        return [.. fork.EnumerateArray().Select(e => (e.GetProperty("digest").GetString()!, e.TryGetProperty("post_id", out var id) ? id.GetString() : null))];
    }

    private static string[] SuccessorsOf(JsonElement item) =>
        [.. item.GetProperty("successors").EnumerateArray().Select(s => s.GetString()!)];

    [Fact]
    public async Task R9_43_ABranchNamesTheOtherBranch()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("fork");
        var question = await QuestionAsync(author, board, "A forked question");
        var r1 = await ReviseAsync(author, board, question, "Corrected one way.");
        var r2 = await ReviseStoredAsync(author, board, question, "Corrected another way.");

        var items = await BatchAsync(r1.Digest, r2.Digest);

        Assert.Equal("current", items[0].GetProperty("state").GetString());
        Assert.Equal("current", items[1].GetProperty("state").GetString());
        var first = ForkOf(items[0]);
        var second = ForkOf(items[1]);
        Assert.Equal((r2.Digest, (string?)r2.PostId), Assert.Single(first));
        Assert.Equal((r1.Digest, (string?)r1.PostId), Assert.Single(second));
    }

    [Fact]
    public async Task R9_43_ThreeBranchesNameTwoOthersInLogOrder()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("fork3");
        var question = await QuestionAsync(author, board, "A question forked three ways");
        var r1 = await ReviseAsync(author, board, question, "Branch one.");
        var r2 = await ReviseStoredAsync(author, board, question, "Branch two.");
        var r3 = await ReviseStoredAsync(author, board, question, "Branch three.");

        var items = await BatchAsync(r1.Digest, r2.Digest, r3.Digest);

        var all = new[] { r1, r2, r3 };
        for (var n = 0; n < all.Length; n++)
        {
            var listed = ForkOf(items[n]);
            Assert.Equal(2, listed.Length);
            Assert.Equal(all.Where((_, i) => i != n).Select(p => (p.Digest, (string?)p.PostId)).ToArray(), listed);
            Assert.DoesNotContain(listed, e => e.Digest == all[n].Digest);
        }
    }

    [Fact]
    public async Task R9_43_ALoneRevisionHasAnEmptyForkNotAnAbsentOne()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("lone");
        var question = await QuestionAsync(author, board, "A question revised once");
        var only = await ReviseAsync(author, board, question, "The one correction.");

        var items = await BatchAsync(only.Digest);

        // Control: the item has a post, so the member must exist; ForkOf fails by name when it does not.
        Assert.Equal(JsonValueKind.Object, items[0].GetProperty("post").ValueKind);
        Assert.Empty(ForkOf(items[0]));
    }

    [Fact]
    public async Task R9_43_AQuestionAnAnswerAndACommentCarryAnEmptyFork()
    {
        var board = "board-" + Suffix();
        var helper = await T1Async("helper", board);
        var question = await QuestionAsync(helper, board, "A plain question");
        var answer = await PostAsync(helper, helper.Agent.SignAnswer(board, "A plain answer.", question.PostId, forum.Now));
        var comment = await PostAsync(helper, helper.Agent.Sign(PostKind.Comment, board, "A plain comment.", null, question.PostId, forum.Now));

        var items = await BatchAsync(question.Digest, answer.Digest, comment.Digest);

        Assert.All(items, item => Assert.Equal("current", item.GetProperty("state").GetString()));
        Assert.All(items, item => Assert.Empty(ForkOf(item)));
    }

    [Fact]
    public async Task R9_43_OnlyTheAuthorsRevisionsAreSiblings()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("sib");
        var stranger = await EnrolAsync("stranger");
        var question = await QuestionAsync(author, board, "A question with hostile neighbours");
        var r1 = await ReviseAsync(author, board, question, "The author's first correction.");

        // A stranger's stored revision and a stored comment (the author's own) both naming the digest: neither corrects anything (R9.28).
        var (strangerId, strangerDigest) = await forum.AppendStoredPostAsync(
            stranger.Agent.Sign(PostKind.Revision, board, "not the author's", null, question.PostId, forum.Now, prev: question.Digest), Ct);
        var (commentId, commentDigest) = await forum.AppendStoredPostAsync(
            author.Agent.Sign(PostKind.Comment, board, "the author's own comment", null, question.PostId, forum.Now, prev: question.Digest), Ct);
        Assert.NotEqual(strangerId, commentId);

        // The exclusion rows: nothing but the author's revisions is a sibling, so r1 is alone.
        Assert.Empty(ForkOf(Assert.Single(await BatchAsync(r1.Digest))));

        // The control: the author's own second revision is a sibling, and the two strangers are still not.
        var r2 = await ReviseStoredAsync(author, board, question, "The author's second correction.");
        var listed = ForkOf(Assert.Single(await BatchAsync(r1.Digest)));
        Assert.Equal((r2.Digest, (string?)r2.PostId), Assert.Single(listed));
        Assert.DoesNotContain(listed, e => e.Digest == strangerDigest || e.Digest == commentDigest);
    }

    [Theory]
    [InlineData("withheld")]
    [InlineData("quarantined")]
    public async Task R9_43_AWithheldSiblingIsNamedByDigestAlone(string how)
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("hidden");
        var question = await QuestionAsync(author, board, "A question with a hidden branch");
        var r1 = await ReviseAsync(author, board, question, "The branch that stays.");
        var r2 = await ReviseStoredAsync(author, board, question, "The branch that goes.");
        if (how == "withheld") await forum.WithholdAsync(r2.PostId, Ct);
        else await forum.QuarantineAsync(r2.PostId, Ct);

        var items = await BatchAsync(r1.Digest, r2.Digest);

        var listed = ForkOf(items[0]);
        Assert.Equal((r2.Digest, (string?)null), Assert.Single(listed));

        // The hidden sibling's own item says withheld and carries no post, so no fork member either.
        Assert.Equal("withheld", items[1].GetProperty("state").GetString());
        Assert.False(items[1].TryGetProperty(Member, out _), "an item with no post carries a fork member");
    }

    [Fact]
    public async Task R9_43_ARevisionOfARevisionIsNotAFork()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("chain");
        var question = await QuestionAsync(author, board, "A question revised twice in a chain");
        var r1 = await ReviseAsync(author, board, question, "First correction.");
        var r2 = await ReviseAsync(author, board, r1, "Second correction, of the first.");

        var items = await BatchAsync(question.Digest, r1.Digest, r2.Digest);

        Assert.Empty(ForkOf(items[1]));
        Assert.Empty(ForkOf(items[2]));
        Assert.Equal(new[] { r1.Digest }, SuccessorsOf(items[0]));
        Assert.False(items[0].GetProperty("forked").GetBoolean());
    }

    [Fact]
    public async Task R9_43_ASupersededBranchStillNamesItsSibling()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("branch");
        var question = await QuestionAsync(author, board, "A question with a revised branch");
        var r1 = await ReviseAsync(author, board, question, "Branch one.");
        var r2 = await ReviseStoredAsync(author, board, question, "Branch two.");
        var r1b = await ReviseStoredAsync(author, board, r1, "Branch one, corrected again.");

        var items = await BatchAsync(r1.Digest, r2.Digest, r1b.Digest);

        Assert.Equal("superseded", items[0].GetProperty("state").GetString());
        Assert.Equal((r2.Digest, (string?)r2.PostId), Assert.Single(ForkOf(items[0])));

        // r2's siblings are r1 alone: r1's own descendant is not a sibling of anything.
        Assert.Equal((r1.Digest, (string?)r1.PostId), Assert.Single(ForkOf(items[1])));
        Assert.Empty(ForkOf(items[2]));
    }

    [Fact]
    public async Task R9_43_ItemsWithNoPostCarryNoForkMember()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("nopost");
        var live = await QuestionAsync(author, board, "A post that stays");
        var gone = await QuestionAsync(author, board, "A post that is withheld");
        await forum.WithholdAsync(gone.PostId, Ct);
        var nobody = "sha256:" + new string('0', 64);

        var items = await BatchAsync(gone.Digest, nobody, "nope", live.Digest);

        Assert.Equal(StatesInOrder, items.Select(i => i.GetProperty("state").GetString()!).ToArray());
        foreach (var item in items.Take(3))
            Assert.False(item.TryGetProperty(Member, out _), $"the {item.GetProperty("state").GetString()} item carries a fork member");

        // The control: the item that does carry a post carries the member.
        Assert.Empty(ForkOf(items[3]));
    }

    [Fact]
    public async Task R9_43_TheOriginalsItemIsUnchanged()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("orig");
        var question = await QuestionAsync(author, board, "A question forked two ways");
        var r1 = await ReviseAsync(author, board, question, "Branch one.");
        var r2 = await ReviseStoredAsync(author, board, question, "Branch two.");

        var item = Assert.Single(await BatchAsync(question.Digest));

        Assert.True(item.GetProperty("forked").GetBoolean());
        Assert.Equal(new[] { r1.Digest, r2.Digest }, SuccessorsOf(item));
        Assert.Empty(ForkOf(item));
    }

    [Fact]
    public async Task R9_43_TheSingleReadCarriesNoForkMember()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("single");
        var question = await QuestionAsync(author, board, "A question forked two ways, read singly");
        var r1 = await ReviseAsync(author, board, question, "Branch one.");
        await ReviseStoredAsync(author, board, question, "Branch two.");

        // Control: the batch item for the same post carries the member (the call fails by name if it does not), so the name is real
        // and the absence below is the single read's own.
        _ = ForkOf(Assert.Single(await BatchAsync(r1.Digest)));

        var text = await forum.Client.GetStringAsync(new Uri($"/v1/posts/{r1.PostId}", UriKind.Relative), Ct);
        var post = JsonDocument.Parse(text).RootElement;
        Assert.False(post.TryGetProperty(Member, out _), "a recorded non-property (decision 9b): the single read carries no fork member");
        Assert.False(post.GetProperty("provenance").TryGetProperty(Member, out _), "a recorded non-property (decision 9b): the single read's provenance carries no fork member");
    }

    [Fact]
    public async Task R9_43_TheForkFollowsTheLog()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("follow");
        var question = await QuestionAsync(author, board, "A question that forks later");
        var r1 = await ReviseAsync(author, board, question, "Branch one.");

        Assert.Empty(ForkOf(Assert.Single(await BatchAsync(r1.Digest))));

        var r2 = await ReviseStoredAsync(author, board, question, "Branch two, arriving later.");

        var listed = ForkOf(Assert.Single(await BatchAsync(r1.Digest)));
        Assert.Equal((r2.Digest, (string?)r2.PostId), Assert.Single(listed));
    }

    [Fact]
    public async Task R9_43_ASiblingsPostIdIsTheOneTheSingleReadServesForThatDigest()
    {
        var board = "board-" + Suffix();
        var author = await EnrolAsync("ident");
        var question = await QuestionAsync(author, board, "A question forked two ways, identified");
        var r1 = await ReviseAsync(author, board, question, "Branch one.");
        var r2 = await ReviseStoredAsync(author, board, question, "Branch two.");

        var listed = ForkOf(Assert.Single(await BatchAsync(r1.Digest)));
        Assert.Single(listed);

        // A different route from the batch: the id the fork names, read singly, serves the digest the fork names.
        var text = await forum.Client.GetStringAsync(new Uri($"/v1/posts/{listed[0].PostId}", UriKind.Relative), Ct);
        var served = JsonDocument.Parse(text).RootElement;
        Assert.Equal(listed[0].PostId, served.GetProperty("post_id").GetString());
        Assert.Equal(listed[0].Digest, served.GetProperty("digest").GetString());
        Assert.Equal(r2.Digest, served.GetProperty("digest").GetString());
    }
}
