using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Table 10's <c>revision</c>/<c>create</c> row is <c>✗ | ✓ | ✓ | ✓ | ✓</c> with the parenthetical
/// <b>(own)</b> — only an author may revise their own post.
///
/// <para><b>Nothing enforced that, and nothing tested it.</b> <c>ResourceActionModel</c> carried
/// <see cref="Curia.Domain.Authorization.GrantQualifier.OwnResourceOnly"/> on the row,
/// <c>AuthorizationDecision</c> carried it out of the PDP and documented that such a decision "is
/// not yet a permission to act", and <c>SubmitAsync</c> checked <c>IsAllowed</c> and submitted. A
/// grep for <c>PostKind.Revision</c> across the test projects returned nothing at all, so the
/// omission had no probe to fail.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class RevisionOwnershipTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    private async Task<(ForumAgent Agent, DpopClient Dpop, string Token)> AuthenticatedAsync(
        HttpClient client, string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create(Unique(stem), stem + "-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return (agent, dpop, token);
    }

    /// <summary>Posts a question and returns its id and digest — the digest is what <c>prev</c> chains to.</summary>
    private async Task<(string PostId, string Digest)> AskAsync(
        HttpClient client, ForumAgent agent, DpopClient dpop, string token, string board, CancellationToken ct)
    {
        using var response = await dpop.PostAsync(
            client, PostsUrl, token,
            agent.SignQuestion(board, "the original body", "Original title", forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return (json.RootElement.GetProperty("post_id").GetString()!,
            json.RootElement.GetProperty("digest").GetString()!);
    }

    /// <summary>An author may revise their own post: the "(own)" qualifier is satisfied.</summary>
    [Fact]
    public async Task AnAuthorMayReviseTheirOwnPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (author, dpop, token) = await AuthenticatedAsync(client, "author", ct);
        var (postId, digest) = await AskAsync(client, author, dpop, token, board, ct);

        using var revised = await dpop.PostAsync(
            client, PostsUrl, token,
            author.SignRevision(board, "a corrected body", postId, digest, forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, revised.StatusCode);
    }

    /// <summary>
    /// <b>The defect this file exists for.</b> Another agent may not revise a post it did not write,
    /// and until the grant qualifier was discharged it could: the PDP returned an allow carrying
    /// "(own)", and the caller read past the parenthetical.
    ///
    /// <para>The denial's reason names the parenthetical rather than reporting a bare refusal, so an
    /// operator reading R7.16's log can tell "wrong agent" from "wrong tier" — two conditions that
    /// want very different responses.</para>
    /// </summary>
    [Fact]
    public async Task Table10_AnotherAgentMayNotReviseAPostItDidNotWrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (author, authorDpop, authorToken) = await AuthenticatedAsync(client, "author", ct);
        var (postId, digest) = await AskAsync(client, author, authorDpop, authorToken, board, ct);

        var (stranger, strangerDpop, strangerToken) = await AuthenticatedAsync(client, "stranger", ct);

        using var response = await strangerDpop.PostAsync(
            client, PostsUrl, strangerToken,
            stranger.SignRevision(board, "a hostile body", postId, digest, forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "own-resource-only",
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A revision that chains to nothing cannot establish ownership of anything, so it is refused.
    /// R6.7 makes <c>prev</c> the link that "commits to its predecessor's digest"; without it there
    /// is no predecessor to own, and permitting the write would be permitting an unowned revision.
    /// </summary>
    [Fact]
    public async Task R6_7_ARevisionThatChainsToNothingIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (author, dpop, token) = await AuthenticatedAsync(client, "author", ct);
        var (postId, _) = await AskAsync(client, author, dpop, token, board, ct);

        using var response = await dpop.PostAsync(
            client, PostsUrl, token,
            author.SignRevision(board, "a body", postId, prev: null, forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>A <c>prev</c> naming a digest the log has never seen is refused, not treated as unowned-but-fine.</summary>
    [Fact]
    public async Task ARevisionChainingToAnUnknownDigestIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (author, dpop, token) = await AuthenticatedAsync(client, "author", ct);
        var (postId, _) = await AskAsync(client, author, dpop, token, board, ct);

        using var response = await dpop.PostAsync(
            client, PostsUrl, token,
            author.SignRevision(board, "a body", postId, "sha-256:" + new string('a', 43), forum.Now),
            forum.Now, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
