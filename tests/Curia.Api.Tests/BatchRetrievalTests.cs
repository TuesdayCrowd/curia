using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.10 over HTTP (errata G7, R9.18–R9.20): <c>POST /v1/posts/batch</c>, an agent's re-check of
/// the posts it cited, one item per requested digest, in order, with nothing omitted.
///
/// <para><b>Omission is the failure mode.</b> An agent re-checking fifty citations cannot tell a
/// filtered array from a short one, so a withheld post is reported as withheld, an unknown digest as
/// unknown, and an element that is not a digest as malformed -- each in its own position.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BatchRetrievalTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";
    private const string BatchUrl = "/v1/posts/batch";

    private static string Unknown => "sha256:" + new string('0', 64);

    private async Task<(ForumAgent Agent, DpopClient Dpop, string Board)> AgentAsync(HttpClient http, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/batch-" + suffix, "bk-" + suffix);
        var (dpop, _) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        return (agent, dpop, "board-" + suffix);
    }

    private async Task<(string PostId, string Digest)> PostAsync(
        HttpClient http, ForumAgent agent, DpopClient dpop, byte[] wire, CancellationToken ct)
    {
        using var response = await dpop.PostAsync(
            http, PostsUrl, await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct), wire, forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);

        using var json = JsonDocument.Parse(body);
        return (json.RootElement.GetProperty("post_id").GetString()!, json.RootElement.GetProperty("digest").GetString()!);
    }

    private static async Task<(HttpStatusCode Status, string Body)> BatchAsync(HttpClient http, object body, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync(BatchUrl, body, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    [Fact]
    public async Task R9_18_OneItemPerElementInOrderAndNothingOmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, dpop, board) = await AgentAsync(http, ct);

        var (_, first) = await PostAsync(http, agent, dpop, agent.SignQuestion(board, "First?", "One", forum.Now), ct);
        var (withheldId, second) = await PostAsync(http, agent, dpop, agent.SignQuestion(board, "Second?", "Two", forum.Now), ct);
        var (_, third) = await PostAsync(http, agent, dpop, agent.SignQuestion(board, "Third?", "Three", forum.Now), ct);
        await forum.WithholdAsync(withheldId, ct);

        const string junk = "not-a-digest <script>alert(1)</script>";
        var (status, body) = await BatchAsync(http, new { digests = new[] { first, second, third, Unknown, junk } }, ct);

        Assert.Equal(HttpStatusCode.OK, status);
        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal(5, items.Length);
        Assert.Equal(["current", "withheld", "current", "unknown", "malformed"], items.Select(i => i.GetProperty("state").GetString()));

        Assert.Equal(first, items[0].GetProperty("digest").GetString());
        Assert.Equal(first, items[0].GetProperty("post").GetProperty("digest").GetString());
        Assert.False(items[0].GetProperty("post").GetProperty("accepted").GetBoolean());

        // Withheld: reported, with the digest, and with no content.
        Assert.Equal(second, items[1].GetProperty("digest").GetString());
        Assert.Equal(JsonValueKind.Null, items[1].GetProperty("post").ValueKind);

        Assert.Equal(Unknown, items[3].GetProperty("digest").GetString());
        Assert.Equal(JsonValueKind.Null, items[3].GetProperty("post").ValueKind);

        // Malformed: identified by position, never echoed. The request is caller-controlled text on
        // a path that hands it back to a reader, with no provenance envelope behind it.
        Assert.Equal(JsonValueKind.Null, items[4].GetProperty("digest").ValueKind);
        Assert.DoesNotContain("script", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>R6.7: a revision chains by <c>prev</c>; the cited original is superseded, still served, and names its successor.</summary>
    [Fact]
    public async Task R9_19_ARevisionMarksItsPredecessorSuperseded()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, dpop, board) = await AgentAsync(http, ct);

        var (questionId, original) = await PostAsync(http, agent, dpop, agent.SignQuestion(board, "Draft?", "Draft", forum.Now), ct);
        var (_, revision) = await PostAsync(http, agent, dpop, agent.SignRevision(board, "Corrected.", questionId, original, forum.Now), ct);

        var (status, body) = await BatchAsync(http, new { digests = new[] { original, revision } }, ct);

        Assert.Equal(HttpStatusCode.OK, status);
        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Equal("superseded", items[0].GetProperty("state").GetString());
        Assert.Equal([revision], items[0].GetProperty("successors").EnumerateArray().Select(s => s.GetString()));
        Assert.False(items[0].GetProperty("forked").GetBoolean());
        Assert.Equal(questionId, items[0].GetProperty("post").GetProperty("post_id").GetString());

        Assert.Equal("current", items[1].GetProperty("state").GetString());
        Assert.Empty(items[1].GetProperty("successors").EnumerateArray());
    }

    /// <summary>
    /// A batch route that is more permissive, or differently shaped, than the route it batches is a
    /// bypass. The item's post is byte-for-byte what <c>GET /v1/posts/{id}</c> serves anonymously.
    /// </summary>
    [Fact]
    public async Task R9_19_AnItemIsExactlyWhatTheSingleReadServes()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, dpop, board) = await AgentAsync(http, ct);
        var (postId, digest) = await PostAsync(http, agent, dpop, agent.SignQuestion(board, "Same?", "Parity", forum.Now), ct);

        var single = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        var (_, body) = await BatchAsync(http, new { digests = new[] { digest } }, ct);
        using var json = JsonDocument.Parse(body);
        var item = json.RootElement.GetProperty("items")[0].GetProperty("post");

        Assert.True(JsonElement.DeepEquals(single, item), "batch item differs from the single read");
    }

    /// <summary>R9.20: over the cap, the whole request is refused and the problem names the cap and the count. Never truncated.</summary>
    [Fact]
    public async Task R9_20_ABatchOverTheCapIsRefusedWholeNotTruncated()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;

        var digests = Enumerable.Range(0, 65).Select(i => "sha256:" + i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var (status, body) = await BatchAsync(http, new { digests }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("curia/posts/batch-too-large", json.RootElement.GetProperty("type").GetString());
        Assert.Contains("cap=64", json.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.Contains("received=65", json.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.False(json.RootElement.TryGetProperty("items", out _));
    }

    [Fact]
    public async Task R9_20_ExactlyTheCapIsAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;

        var digests = Enumerable.Range(0, 64).Select(i => "sha256:" + i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var (status, body) = await BatchAsync(http, new { digests }, ct);

        Assert.Equal(HttpStatusCode.OK, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(64, json.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task AnEmptyBatchIsAnEmptyAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var (status, body) = await BatchAsync(forum.Client, new { digests = Array.Empty<string>() }, ct);

        Assert.Equal(HttpStatusCode.OK, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(0, json.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task ABodyWithoutADigestsArrayIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (status, body) = await BatchAsync(forum.Client, new { post_ids = "01ABC" }, ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("curia/posts/batch-malformed", json.RootElement.GetProperty("type").GetString());
    }
}
