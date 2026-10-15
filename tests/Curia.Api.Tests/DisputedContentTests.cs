using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Domain.Authorization;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What a key's compromise does to the content signed under it, served (R6.57, R6.30 rev.): a post inside
/// the declaration's window is served marked and omitted from search, which counts it; a vote or report
/// from such a post carries no weight; and the declaration withholds nothing (R6.28). The declaration is
/// seeded through the operator tool itself, behind the permissive gate the fixture composes.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class DisputedContentTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<int> OperatorAsync(string pem, CancellationToken ct, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        return await OperatorCommands.RunAsync(
            args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, ct,
            readLogSigningKeyPem: () => pem);
    }

    /// <summary>Publishes the log key, then declares <paramref name="kid"/> compromised from <paramref name="fromIndex"/>; returns the declaration's log index.</summary>
    private async Task<long> DeclareAsync(string agent, string kid, long fromIndex, CancellationToken ct)
    {
        var pem = LogSigningKey.GeneratePem();
        Assert.Equal(0, await OperatorAsync(pem, ct, "sign-head", "--by", "ops"));

        // The declaration is the next entry of the log, so its log index is the count of entries before it.
        var declarationIndex = await CompromiseDeclarationTests.EventCountAsync(forum.ConnectionString, ct);
        Assert.Equal(0, await OperatorAsync(pem, ct, "compromise", "--agent", agent, "--kid", kid, "--from-index", Number(fromIndex),
            "--reason", "owner-reported", "--operator", "ops"));
        return declarationIndex;
    }

    private async Task<(string PostId, long LogIndex)> AskAsync(
        ForumAgent agent, DpopClient dpop, string token, string board, string title, string body, CancellationToken ct)
    {
        using var response = await dpop.PostAsync(forum.Client, PostsUrl, token, agent.SignQuestion(board, body, title, forum.Now), forum.Now, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        using var accepted = JsonDocument.Parse(text);
        var postId = accepted.RootElement.GetProperty("post_id").GetString()!;
        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        return (postId, served.GetProperty("log_index").GetInt64());
    }

    private async Task<JsonElement> ReadAsync(string postId, CancellationToken ct) =>
        await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);

    private async Task<JsonDocument> SearchAsync(string board, string term, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/search?q={term}&board={board}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string[] Ids(JsonDocument search) =>
        [.. search.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!)];

    private static string Describe(JsonElement provenance) =>
        $"{provenance.GetProperty("key_status").GetString()}; " +
        (provenance.TryGetProperty("disputed_by_log_index", out var by) ? by.GetInt64().ToString(CultureInfo.InvariantCulture) : "none");

    [Fact]
    public async Task R6_57_ADisputedPostIsServedMarked()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "board-" + Suffix();
        var term = "zdisp" + Suffix();
        var thief = ForumAgent.Create($"https://agents.example/disputed-{Suffix()}", "only-" + Suffix());
        var (dpop, token) = await thief.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var before = await AskAsync(thief, dpop, token, board, $"Before the window {term}", $"the first post mentions {term}", ct);
        var inside = await AskAsync(thief, dpop, token, board, $"Inside the window {term}", $"the second, different post mentions {term}", ct);

        var control = ForumAgent.Create($"https://agents.example/untouched-{Suffix()}", "only-" + Suffix());
        var (controlDpop, controlToken) = await control.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var untouched = await AskAsync(control, controlDpop, controlToken, board, $"Another author {term}", $"a third, distinct post mentions {term}", ct);

        var servedBefore = Describe((await ReadAsync(inside.PostId, ct)).GetProperty("provenance"));
        var declaredAt = await DeclareAsync(thief.AgentId, thief.Kid, inside.LogIndex, ct);

        // `before` lies below from_index, `inside` at it; both are signed under the compromised key.
        Assert.Equal(
            $"active; none | compromised; none | compromised; {declaredAt} | active; none",
            string.Join(" | ",
                servedBefore,
                Describe((await ReadAsync(before.PostId, ct)).GetProperty("provenance")),
                Describe((await ReadAsync(inside.PostId, ct)).GetProperty("provenance")),
                Describe((await ReadAsync(untouched.PostId, ct)).GetProperty("provenance"))));
    }

    [Fact]
    public async Task R6_57_SearchOmitsADisputedPostAndCountsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "board-" + Suffix();
        var term = "zsrch" + Suffix();
        var thief = ForumAgent.Create($"https://agents.example/searched-{Suffix()}", "only-" + Suffix());
        var (dpop, token) = await thief.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var disputed = await AskAsync(thief, dpop, token, board, $"Signed by the thief {term}", $"this post mentions {term}", ct);

        var control = ForumAgent.Create($"https://agents.example/kept-{Suffix()}", "only-" + Suffix());
        var (controlDpop, controlToken) = await control.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var kept = await AskAsync(control, controlDpop, controlToken, board, $"Signed by its owner {term}", $"another, different post mentions {term}", ct);

        string[] beforeIds;
        int beforeExcluded;
        using (var before = await SearchAsync(board, term, ct))
        {
            beforeIds = Ids(before);
            beforeExcluded = before.RootElement.GetProperty("floor").GetProperty("excluded_disputed").GetInt32();
        }

        await DeclareAsync(thief.AgentId, thief.Kid, disputed.LogIndex, ct);

        using var after = await SearchAsync(board, term, ct);
        var afterExcluded = after.RootElement.GetProperty("floor").GetProperty("excluded_disputed").GetInt32();

        // The disputed post is served by id, marked; search omits it and says how many it omitted.
        Assert.Equal(
            $"{string.Join(",", new[] { disputed.PostId, kept.PostId }.Order(StringComparer.Ordinal))}; 0 | {kept.PostId}; 1",
            $"{string.Join(",", beforeIds.Order(StringComparer.Ordinal))}; {beforeExcluded} | {string.Join(",", Ids(after))}; {afterExcluded}");
    }

    [Fact]
    public async Task R6_28_ADisputedPostIsNeverWithheldByTheDeclaration()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "board-" + Suffix();
        var thief = ForumAgent.Create($"https://agents.example/served-{Suffix()}", "only-" + Suffix());
        var (dpop, token) = await thief.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var post = await AskAsync(thief, dpop, token, board, "Disputed but served", "a question that must stay readable", ct);

        await DeclareAsync(thief.AgentId, thief.Kid, post.LogIndex, ct);

        using var byId = await forum.Client.GetAsync(new Uri($"/v1/posts/{post.PostId}", UriKind.Relative), ct);
        using var listing = await forum.Client.GetAsync(new Uri($"/v1/boards/{board}/posts", UriKind.Relative), ct);
        using var thread = await forum.Client.GetAsync(new Uri($"/v1/threads/{post.PostId}", UriKind.Relative), ct);
        var listed = (await listing.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray()
            .Select(p => p.GetProperty("post_id").GetString()).ToArray();

        Assert.Equal(
            $"{HttpStatusCode.OK} {HttpStatusCode.OK} {HttpStatusCode.OK} {post.PostId}",
            $"{byId.StatusCode} {listing.StatusCode} {thread.StatusCode} {string.Join(",", listed)}");
    }

    [Fact]
    public async Task R6_57_ADisputedVerificationCarriesNoWeight()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var board = "board-" + Suffix();

        async Task<(ForumAgent Agent, DpopClient Dpop, string Token)> T1Async(string stem)
        {
            var agent = ForumAgent.Create($"https://agents.example/{stem}-{Suffix()}", $"{stem}-{Suffix()}");
            var (dpop, token) = await agent.AuthenticateAsync(http, KeyActRequests.TokenEndpoint, forum.Now, ct);
            await forum.AttestOwnerAsync(agent.AgentId, ct, owner: $"owner:{stem}-{Suffix()}");
            for (var i = 0; i < 3; i++)
            {
                var nonce = Guid.NewGuid().ToString("N");
                using var warmup = await dpop.PostAsync(http, PostsUrl, token, agent.SignQuestion(board, $"warmup {i} {nonce}", $"Warmup {i} {nonce}", forum.Now), forum.Now, ct);
                Assert.Equal(HttpStatusCode.Created, warmup.StatusCode);
            }

            return (agent, dpop, token);
        }

        var asker = ForumAgent.Create($"https://agents.example/asker-{Suffix()}", "asker-" + Suffix());
        var (askerDpop, askerToken) = await asker.AuthenticateAsync(http, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var question = await AskAsync(asker, askerDpop, askerToken, board, "How?", "How does it work", ct);

        var author = await T1Async("author");
        var reproducer = await T1Async("reproducer");
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        using var answered = await author.Dpop.PostAsync(
            http, PostsUrl, await author.Dpop.GetTokenAsync(http, KeyActRequests.TokenEndpoint, forum.Now, ct), author.Agent.SignAnswer(board, "Like this.", question.PostId, forum.Now), forum.Now, ct);
        var answeredBody = await answered.Content.ReadAsStringAsync(ct);
        Assert.True(answered.StatusCode == HttpStatusCode.Created, answeredBody);
        using var answer = JsonDocument.Parse(answeredBody);
        var answerId = answer.RootElement.GetProperty("post_id").GetString()!;
        var answerDigest = answer.RootElement.GetProperty("digest").GetString()!;

        using var reported = await reproducer.Dpop.PostAsync(
            http, PostsUrl, await reproducer.Dpop.GetTokenAsync(http, KeyActRequests.TokenEndpoint, forum.Now, ct), reproducer.Agent.SignVerification(board, answerDigest, "reproduced", forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, reported.StatusCode);
        using var report = JsonDocument.Parse(await reported.Content.ReadAsStringAsync(ct));
        var reportId = report.RootElement.GetProperty("post_id").GetString()!;
        var reportAt = (await ReadAsync(reportId, ct)).GetProperty("log_index").GetInt64();

        var counted = (await ReadAsync(answerId, ct)).GetProperty("provenance");
        var levelBefore = counted.GetProperty("verification_level").GetString();

        await DeclareAsync(reproducer.Agent.AgentId, reproducer.Agent.Kid, reportAt, ct);

        var afterwards = await ReadAsync(answerId, ct);
        var provenance = afterwards.GetProperty("provenance");
        var reportServed = await http.GetAsync(new Uri($"/v1/posts/{reportId}", UriKind.Relative), ct);

        // The report is still served, and marked; it no longer lifts the answer's level.
        Assert.Equal(
            $"V2 | V0; 0 reproductions | {HttpStatusCode.OK}",
            $"{levelBefore} | {provenance.GetProperty("verification_level").GetString()}; {provenance.GetProperty("reproductions").GetArrayLength()} reproductions | {reportServed.StatusCode}");
    }
}
