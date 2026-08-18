using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Domain.Authorization;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Table 11's posting budget, over HTTP.
///
/// <para><b>These tests exist because the budget was decoration.</b> `AuthorizationRequest.PostsToday`
/// defaulted to zero and nothing supplied it, so the branch in `AccessPolicy` was unreachable from
/// the outside — a client could post without limit at any tier. A reference client exercising the
/// Forum from outside is what surfaced it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RateBudgetTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>
    /// R7.15/Table 11: T0's budget is three posts per day, and the fourth is refused — with the
    /// budget's own reason, not a tier denial. R7.16 wants those distinguishable: one means "wait",
    /// the other means "you will never be allowed this".
    /// </summary>
    [Fact]
    public async Task Table11_TheFourthPostAtT0IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/budget-" + suffix, "bk-" + suffix);
        var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        var board = "board-" + suffix;

        var budget = TierPolicy.PostsPerDay(PrincipalTier.T0);

        for (var i = 0; i < budget; i++)
        {
            using var ok = await dpop.PostAsync(
                http, PostsUrl, token, agent.SignQuestion(board, $"Question {i}?", $"Q{i}", forum.Now), forum.Now, ct);
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var refused = await dpop.PostAsync(
            http, PostsUrl, token, agent.SignQuestion(board, "One too many?", "Over", forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains(
            "table-11/rate-budget-exhausted",
            await refused.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The window is a trailing 24 hours, so budget returns rather than resetting at a boundary
    /// nobody specified. Advancing the clock past the window lets the agent post again.
    /// </summary>
    [Fact]
    public async Task Table11_BudgetReturnsAfterTheWindowPasses()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/window-" + suffix, "wk-" + suffix);
        var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        var board = "board-" + suffix;

        for (var i = 0; i < TierPolicy.PostsPerDay(PrincipalTier.T0); i++)
        {
            using var ok = await dpop.PostAsync(
                http, PostsUrl, token, agent.SignQuestion(board, $"Q{i}?", $"Q{i}", forum.Now), forum.Now, ct);
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromHours(25));

        // The token expired long ago -- R5 caps it at 300 seconds -- so a fresh one is obtained,
        // which is what any real agent does after a gap.
        var (freshDpop, freshToken) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);

        using var allowed = await freshDpop.PostAsync(
            http, PostsUrl, freshToken, agent.SignQuestion(board, "New day?", "New", forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }
}
