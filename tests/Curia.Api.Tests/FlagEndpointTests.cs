using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.35's flag path, over the running Forum — the endpoint that gives a beta tester who finds bad
/// content somewhere to report it, and gives Table 11's "no upheld flags" something to be true of.
///
/// <para><b>Requires a reachable Postgres</b>, and fails loudly rather than skipping, for the
/// reason <see cref="ForumFixture"/> records.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class FlagEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    private static byte[] Json(string kind, string rationale) => Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { kind, rationale }));

    /// <summary>Enrols an agent, gets it a DPoP-bound token, and has it post one question.</summary>
    private async Task<(ForumAgent Agent, DpopClient Dpop, string Token, string PostId)> PostedQuestionAsync(
        HttpClient client, string board, CancellationToken ct)
    {
        var agent = ForumAgent.Create(Unique("author"), "author-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var wire = agent.SignQuestion(board, "How does JCS order object members?", "Member ordering", forum.Now);
        using var response = await dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return (agent, dpop, token, json.RootElement.GetProperty("post_id").GetString()!);
    }

    private async Task<HttpResponseMessage> RaiseAsync(
        HttpClient client, DpopClient dpop, string token, string postId, string kind, string rationale,
        CancellationToken ct) =>
        await dpop.PostAsync(
            client,
            $"http://localhost/v1/posts/{postId}/flags",
            token,
            Json(kind, rationale),
            forum.Now,
            ct,
            contentType: "application/json");

    /// <summary>
    /// Table 10's <c>flag</c>/<c>raise</c> row is <c>✗</c> in the Anonymous column, and R10.35 says
    /// so in words: "Any <b>credentialed</b> agent MAY flag content." PEP-1 refuses before the PDP
    /// is ever consulted, because there is no principal to decide about.
    /// </summary>
    [Fact]
    public async Task R10_35_AnAnonymousCallerMayNotRaiseAFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        using var content = new ByteArrayContent(Json("spam", "this is spam"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync(new Uri($"/v1/posts/{postId}/flags", UriKind.Relative), content, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// R10.35: "Any credentialed agent MAY flag content." Table 10 gives <c>flag</c>/<c>raise</c> to
    /// T0, so a freshly enrolled agent — which may not answer and may not vote — may still report.
    /// That asymmetry is the requirement: reporting bad content is the one capability that must not
    /// wait on standing, because the agents most likely to encounter it first are the newest.
    /// </summary>
    [Fact]
    public async Task R10_35_AFreshlyEnrolledT0AgentMayRaiseAFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var reporter = ForumAgent.Create(Unique("reporter"), "reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await RaiseAsync(client, dpop, token, postId, "incorrect", "the JCS claim is wrong", ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// R10.35 fixes seven types. An eighth is a client error, not a new category — accepting it
    /// would make R10.39's per-category statistics count something nobody defined.
    /// </summary>
    [Fact]
    public async Task R10_35_AnUnknownFlagTypeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var reporter = ForumAgent.Create(Unique("reporter"), "reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await RaiseAsync(client, dpop, token, postId, "vibes", "I don't like it", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("curia/flag/unknown-kind", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.35 requires a rationale: a flag nobody can review is not reviewable, cannot be appealed
    /// against (R10.38), and cannot be counted honestly in R10.39's upheld rate.
    /// </summary>
    [Fact]
    public async Task R10_35_AFlagWithoutARationaleIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var reporter = ForumAgent.Create(Unique("reporter"), "reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await RaiseAsync(client, dpop, token, postId, "spam", "   ", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// <b>A flag's rationale is an ingest path, and it goes through SCREEN like any other.</b>
    ///
    /// <para>The rationale is attacker-controlled text that lands in an append-only log with no
    /// redaction primitive — forever. R10.28's reasoning ("a scanner that logs what it finds is a
    /// credential aggregator") applies exactly: a rationale reading "this post leaks AKIA…" would
    /// republish the credential the flag was reporting, and R10.26 makes that a hard rejection
    /// rather than an annotation because there is no way to take it back afterwards.</para>
    ///
    /// <para>The response is checked for the credential's own bytes, because a refusal that quoted
    /// what it refused would leak it into every error log on the path — which is the same defect
    /// one layer up, and the reason <c>RiskFlag</c> records an offset and never the matched text.</para>
    /// </summary>
    [Fact]
    public async Task R10_26_ACredentialInTheRationaleIsRejectedAndNotEchoed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var reporter = ForumAgent.Create(Unique("reporter"), "reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        const string Secret = "AKIAIOSFODNN7EXAMPLE";
        using var response = await RaiseAsync(
            client, dpop, token, postId, "credential_leak", $"this post contains {Secret} in a code block", ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// A flag names a post, so a flag against nothing is a client error rather than a fact recorded
    /// about an identifier the log has never seen. An append-only store cannot take back a flag
    /// raised against a typo.
    /// </summary>
    [Fact]
    public async Task AFlagAgainstAPostThatDoesNotExistIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        var reporter = ForumAgent.Create(Unique("reporter"), "reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await RaiseAsync(
            client, dpop, token, "01JNOSUCHPOST00000000000001", "spam", "nothing is here", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The problem type is asserted, not merely the status. An absent route returns 404 too, so
        // a bare status check here passes identically whether the refusal is deliberate or the
        // endpoint was never mapped -- and it did, before this endpoint existed.
        Assert.Contains(
            "curia/flag/no-such-post",
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.36: withheld content stops being served. The remedy is <b>withholding plus a moderation
    /// event</b>, never deletion — the post stays in the log exactly as signed, and the read path
    /// declines to serve it.
    ///
    /// <para>The moderation action is appended directly to the store because no HTTP route creates
    /// one: Table 10 gates <c>moderation:apply</c> to "T3 (delegated)" and Table 22 puts delegated
    /// moderation in Phase 4. Asserting the filter through the log rather than through a route is
    /// the whole reason <c>moderation.applied</c> ships without a writer — a <c>MayServe</c> folded
    /// over a history that could only ever be empty is a filter whose silence carries no
    /// information.</para>
    /// </summary>
    [Fact]
    public async Task R10_36_AWithheldPostStopsBeingServedAndIsNotDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        using (var before = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await WithholdAsync(postId, ct);

        using var after = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);

        // The board listing must agree. A post withheld on one read path and served on another is
        // the withholding not having happened.
        using var listing = await client.GetAsync(new Uri($"/v1/boards/{board}/posts", UriKind.Relative), ct);
        Assert.DoesNotContain(postId, await listing.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// Appends a <c>moderation.applied</c> event withholding a post, through the host's own event
    /// store — the same append-only Postgres table everything else writes to.
    /// </summary>
    private async Task WithholdAsync(string postId, CancellationToken ct)
    {
        using var scope = forum.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

        var aggregate = Require(AggregateId.Create(postId));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create($"{postId}-mod")),
                Require(EventType.Create(FlagProjector.ModerationAppliedType)),
                Require(ActorId.Create("https://agents.example/moderator")),
                new JsonValue.Object(
                [
                    new(FlagProjector.PostIdField, new JsonValue.String(postId)),
                    new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(ModeratorKind.Human))),
                    new(FlagProjector.ActorIdField, new JsonValue.String("https://agents.example/moderator")),
                    new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(ModerationEffect.Withhold))),
                    new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(FlagKind.Injection))),
                    new(FlagProjector.RationaleField, new JsonValue.String("reviewed and confirmed")),
                ]))],
            ct));
    }
}
