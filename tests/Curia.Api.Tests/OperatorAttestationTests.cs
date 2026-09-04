using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R4.30's operator path, end to end: the <c>curia-operator</c> tool run against the same database
/// the hosted Forum serves from, with the effect read back over HTTP. This is the one way owner
/// verification enters the log, so it is tested as an operator would use it rather than by calling
/// the use case the tool wraps.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorAttestationTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static ForumAgent NewAgent()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ForumAgent.Create("https://agents.example/op-" + suffix, "op-" + suffix);
    }

    private async Task<(int Exit, string Out, string Err)> RunAsync(string[] args, CancellationToken ct)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(args, forum.ConnectionString, forum.Clock, stdout, stderr, ct);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private async Task<bool> ServedOwnerVerifiedAsync(ForumAgent agent, HttpClient http, string board, CancellationToken ct)
    {
        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var token = await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);
        using var posted = await dpop.PostAsync(
            http, PostsUrl, token, agent.SignQuestion(board, "Is my owner verified?", "Standing", forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, posted.StatusCode);

        var listed = await http.GetFromJsonAsync<JsonElement>($"/v1/boards/{board}/posts", ct);
        return Assert.Single(listed.EnumerateArray()).GetProperty("provenance").GetProperty("owner_verified").GetBoolean();
    }

    [Fact]
    public async Task R4_30_TheOperatorToolAttestsAnOwnerAndTheForumServesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var agent = NewAgent();

        using (var enrolled = await agent.EnrollAsync(http, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var (exit, stdout, stderr) = await RunAsync(
            ["attest-owner", "--agent", agent.AgentId, "--owner", "owner:example", "--by", "reviewer",
             "--method", "manual", "--reason", "reviewed by hand"], ct);

        Assert.True(exit == ExitCode.Ok, stderr);
        Assert.Contains("owner:example  (verified via manual)", stdout, StringComparison.Ordinal);
        Assert.Contains("by        operator:reviewer", stdout, StringComparison.Ordinal);

        Assert.True(await ServedOwnerVerifiedAsync(agent, http, "board-" + Guid.NewGuid().ToString("N")[..8], ct));
    }

    /// <summary>
    /// A refusal, reached the way an operator reaches one: the use case says no, the slug is on
    /// stderr for a script to read, the exit code says which kind of no, and nothing was written.
    /// The self-attestation refusal is not reachable from here -- <c>--by</c> always names an
    /// operator, because the tool applies the <c>operator:</c> namespace itself -- so it is pinned
    /// at the use case (<c>AgentStandingProjectorTests</c>) and this test takes the refusal the
    /// tool can produce: an agent the log has never enrolled.
    /// </summary>
    [Fact]
    public async Task R4_30_TheToolRefusesAnAttestationForAnAgentThatNeverEnrolled()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var agent = NewAgent();

        var (exit, stdout, stderr) = await RunAsync(
            ["attest-owner", "--agent", agent.AgentId, "--owner", "owner:example", "--by", "reviewer"], ct);

        Assert.Equal(ExitCode.Refused, exit);
        Assert.Contains("curia/attest/not-enrolled", stderr, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stdout);

        // Enrolled afterwards, the agent is unverified: the refused attestation left nothing behind.
        using (var enrolled = await agent.EnrollAsync(http, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        Assert.False(await ServedOwnerVerifiedAsync(agent, http, "board-" + Guid.NewGuid().ToString("N")[..8], ct));
    }

    [Theory]
    [InlineData("attest-owner", "--owner", "owner:example", "--by", "reviewer")]
    [InlineData("attest-owner", "--agent", "https://agents.example/x", "--by", "reviewer")]
    [InlineData("attest-owner", "--agent", "https://agents.example/x", "--owner", "owner:example")]
    [InlineData("attest-owner", "--agent", "https://agents.example/x", "--owner", "owner:example", "--by", "reviewer", "--method", "org")]
    [InlineData("revoke-owner")]
    public async Task AUsageErrorWritesNothing(params string[] args)
    {
        var ct = TestContext.Current.CancellationToken;

        var (exit, _, stderr) = await RunAsync(args, ct);

        Assert.Equal(ExitCode.Usage, exit);
        Assert.StartsWith("error:", stderr, StringComparison.Ordinal);
    }
}
