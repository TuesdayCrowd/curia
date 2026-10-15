using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json.Nodes;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// The fixture for R6.56's route facts: the production host whose event store is wrapped by a
/// <see cref="ContendedEventStore"/>, which provokes the store's own condition conflict on demand.
/// </summary>
public sealed class ContendedForumFixture : ForumFixture
{
    private ContendedEventStore? _contention;

    /// <summary>The decorator the host appends through; the host is built by the time a fact asks.</summary>
    internal ContendedEventStore Contention =>
        _contention ?? throw new InvalidOperationException("the host has not composed its event store yet");

    protected override void ConfigureStore(IServiceCollection services)
    {
        var registered = services.Last(d => d.ServiceType == typeof(IEventStore));
        if (registered.ImplementationFactory is null)
            throw new InvalidOperationException("the event store is registered by a factory; this decorator wraps what it builds");

        services.Remove(registered);
        services.AddSingleton<IEventStore>(sp =>
        {
            var inner = (IEventStore)registered.ImplementationFactory(sp);
            _contention = new ContendedEventStore(inner);
            return _contention;
        });
    }
}

/// <summary>
/// R6.56 at the surface an agent uses. The Application-level facts pin <see cref="IngestAttempts"/>;
/// these pin the route that wraps it: a lost race (the real store refusing a conditioned append because
/// the author's stream moved) is decided again, three lost races answer 503 <c>curia/ingest/contended</c>
/// with nothing stored, and every other refusal is the response it always was and never reaches PERSIST.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class IngestContentionTests(ContendedForumFixture forum) : IClassFixture<ContendedForumFixture>
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

    /// <summary>
    /// Arms the decorator on the agent's stream, runs <paramref name="send"/>, and disarms in a
    /// <c>finally</c>: the host is shared by every fact in the class. The unrelated entry is an
    /// unverified owner attestation (an owner binds once, so every interference names the same one), so the agent's standing does not change under the request.
    /// </summary>
    private async Task<T> ArmedAsync<T>(ForumAgent agent, int conflicts, Func<Task<T>> send, CancellationToken ct)
    {
        forum.Contention.Arm(
            AuthorStream(agent),
            conflicts,
            token => forum.AttestOwnerAsync(agent.AgentId, token, verified: false));
        try
        {
            return await send();
        }
        finally
        {
            forum.Contention.Disarm();
        }
    }

    private static AggregateId AuthorStream(ForumAgent agent) =>
        AggregateId.Create(agent.AgentId).Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Detail}"));

    private async Task<long> AcceptedOnBoardAsync(string board, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'board' = @board;", admin);
        command.Parameters.AddWithValue("board", board);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    [Fact]
    public async Task R6_56_OneLostRaceOverHttpIsDecidedAgainAndLands()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await AuthenticatedAsync(client, "one-lost", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var wire = agent.SignQuestion(board, "How is one lost race handled?", "One lost race " + board, forum.Now);

        using var posted = await ArmedAsync(agent, 1, () => dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct), ct);

        Assert.Equal(
            "201; injected 1, conditioned appends 2, remaining 0; accepted 1",
            $"{(int)posted.StatusCode}; injected {forum.Contention.ConflictsInjected}, conditioned appends {forum.Contention.ConditionedAppendCount}, " +
            $"remaining {forum.Contention.ConflictsRemaining}; accepted {await AcceptedOnBoardAsync(board, ct)}");
    }

    [Fact]
    public async Task R6_56_ThreeLostRacesOverHttpAnswer503Contended()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await AuthenticatedAsync(client, "three-lost", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var wire = agent.SignQuestion(board, "How are three lost races handled?", "Three lost races " + board, forum.Now);

        // The literal 3 beside the constant: a change to either alone fails here, where a change to both
        // would have moved a fact written only in terms of the constant.
        Assert.Equal(3, IngestAttempts.Attempts);

        using var posted = await ArmedAsync(agent, 3, () => dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct), ct);
        var raw = await posted.Content.ReadAsStringAsync(ct);
        var problem = JsonNode.Parse(raw)!.AsObject();

        Assert.Equal(
            "503 curia/ingest/contended; detail absent; injected 3, conditioned appends 3, remaining 0; accepted 0",
            $"{(int)posted.StatusCode} {problem["type"]?.GetValue<string>()}; " +
            $"detail {(problem["detail"] is null ? "absent" : "present")}; " +
            $"injected {forum.Contention.ConflictsInjected}, conditioned appends {forum.Contention.ConditionedAppendCount}, " +
            $"remaining {forum.Contention.ConflictsRemaining}; accepted {await AcceptedOnBoardAsync(board, ct)}");
    }

    [Fact]
    public async Task R6_56_AFourthRaceIsNotNeededToLand()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await AuthenticatedAsync(client, "two-lost", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var wire = agent.SignQuestion(board, "Do two lost races still land?", "Two lost races " + board, forum.Now);

        using var posted = await ArmedAsync(agent, 2, () => dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct), ct);

        Assert.Equal(
            "201; injected 2, conditioned appends 3, remaining 0; accepted 1",
            $"{(int)posted.StatusCode}; injected {forum.Contention.ConflictsInjected}, conditioned appends {forum.Contention.ConditionedAppendCount}, " +
            $"remaining {forum.Contention.ConflictsRemaining}; accepted {await AcceptedOnBoardAsync(board, ct)}");
    }

    [Fact]
    public async Task R6_56_ARefusalOtherThanAConflictIsNotRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await AuthenticatedAsync(client, "refused", ct);
        var (other, otherDpop, otherToken) = await AuthenticatedAsync(client, "other", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        // A question by another agent for the T0 agent to answer; sent before the decorator is armed.
        using var asked = await otherDpop.PostAsync(
            client, PostsUrl, otherToken,
            other.SignQuestion(board, "What can a new agent answer?", "Refusals " + board, forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        var question = JsonNode.Parse(await asked.Content.ReadAsStringAsync(ct))!["post_id"]!.GetValue<string>();

        // Refused by VERIFY, with no lifecycle kind: the envelope names another enrolled agent than the
        // token's subject.
        var mismatch = other.SignQuestion(board, "Whose post is this?", "Mismatch " + board, forum.Now);
        // Refused after VERIFY and before PERSIST: Table 10 gives a T0 agent no answer.
        var answer = agent.SignAnswer(board, "A tier-zero answer", question, forum.Now);

        var refusals = new List<string>();
        foreach (var (name, wire) in new[] { ("mismatch", mismatch), ("tier", answer) })
        {
            using var response = await ArmedAsync(agent, 0, () => dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct), ct);
            var type = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))?["type"]?.GetValue<string>();
            refusals.Add($"{name} {(int)response.StatusCode} {type} conditioned appends {forum.Contention.ConditionedAppendCount}");
        }

        Assert.Equal(
            ["mismatch 401 curia/content/author-principal-mismatch conditioned appends 0", "tier 403 curia/authz/denied conditioned appends 0"],
            refusals);
    }
}
