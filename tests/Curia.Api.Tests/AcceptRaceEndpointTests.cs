using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// The fixture for R8.79's route facts: the production host whose event store is wrapped by a <see cref="ConflictingEventStore"/>, which gives a rival a turn
/// before each of the use case's appends on one stream, or fails every read of it. Disarmed it is a pass-through, so the burst fact runs through it too.
/// </summary>
public sealed class AcceptRaceForumFixture : ForumFixture
{
    private ConflictingEventStore? _conflicting;
    private IEventStore? _inner;

    internal ConflictingEventStore Conflicting =>
        _conflicting ?? throw new InvalidOperationException("the host has not composed its event store yet");

    /// <summary>The store beneath the decorator: what a rival writes through, so the real store raises the conflict.</summary>
    internal IEventStore Inner =>
        _inner ?? throw new InvalidOperationException("the host has not composed its event store yet");

    protected override void ConfigureStore(IServiceCollection services)
    {
        var registered = services.Last(d => d.ServiceType == typeof(IEventStore));
        if (registered.ImplementationFactory is null)
            throw new InvalidOperationException("the event store is registered by a factory; this decorator wraps what it builds");

        services.Remove(registered);
        services.AddSingleton<IEventStore>(sp =>
        {
            _inner = (IEventStore)registered.ImplementationFactory(sp);
            _conflicting = new ConflictingEventStore(_inner);
            return _conflicting;
        });
    }
}

/// <summary>
/// R8.79 (errata G46) at the surface an agent uses, TRW49 to TRW51. TRW49 is a detector and not a proof: eight simultaneous accepts of one thread were
/// <c>500</c> <c>curia/domain/concurrency-conflict</c> for two or three of the eight in 19 of 20 repetitions on the unmodified tree, and a green run of it
/// alone says nothing (it carries information beside the deterministic facts TRW45 to TRW48). TRW50 and TRW51 are deterministic: the store is made to lose
/// (a rival appends to the thread's stream, so the real store raises the conflict) or to fail its read. The oracle is the <c>answer.accepted</c> events in
/// the events table, never a response; the slug and the bound are the errata's (<see cref="PublishedAcceptBudget"/>).
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptRaceEndpointTests(AcceptRaceForumFixture forum) : IClassFixture<AcceptRaceForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const int Simultaneous = 8;

    private static readonly string[] StoreWords = ["aggregate", "expected", "actual"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Reply(HttpStatusCode Status, string Raw);

    private async Task<Reply> AcceptRawAsync(SceneActor actor, string postId, string? token = null)
    {
        using var response = await actor.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{postId}/accept",
            token ?? await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            Encoding.UTF8.GetBytes("{}"), forum.Now, Ct, contentType: "application/json");
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>The <c>answer.accepted</c> events of one thread, in log order, as the answer each names.</summary>
    private async Task<string[]> EventsOfAsync(string threadRoot)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand(
            "SELECT payload->>'answer_id' FROM events WHERE event_type = 'answer.accepted' AND aggregate_id = @root ORDER BY seq;", connection);
        query.Parameters.AddWithValue("root", threadRoot);
        var answers = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) answers.Add(reader.GetString(0));
        return [.. answers];
    }

    private static AggregateId Aggregate(string id) =>
        AggregateId.Create(id).Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Detail}"));

    /// <summary>
    /// TRW49 (a detector). Eight simultaneous accepts alternating two answers of one thread, ten repetitions, each repetition its own T1 asker (so that a
    /// budget of acceptances is not what this fact finds): no response is 5xx, each is <c>200</c> or <c>201</c>, every event names one of the two, no two
    /// adjacent events name one answer (a replay appended nothing), and the last event is what a following read serves as accepted.
    /// </summary>
    [Fact]
    public async Task R8_79_TRW49_EightSimultaneousAcceptsOfOneThreadAreNeverAServerFault()
    {
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var answerer = await Scene.T1Async(forum, board, Ct);
        var failures = new List<string>();

        for (var rep = 1; rep <= 10; rep++)
        {
            var asker = await Scene.T1Async(forum, board, Ct);
            var question = await Scene.PostAsync(forum, asker, asker.Agent.SignQuestion(board, $"Which settles burst {rep}?", $"Burst {rep}", forum.Now), Ct);
            ScenePost[] answers =
            [
                await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, $"First way, burst {rep}.", question.PostId, forum.Now), Ct),
                await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, $"Second way, burst {rep}.", question.PostId, forum.Now), Ct),
            ];

            var token = await asker.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct);
            using var gate = new ManualResetEventSlim(false);
            var sent = Enumerable.Range(0, Simultaneous)
                .Select(i => Task.Run(
                    async () =>
                    {
                        gate.Wait(Ct);
                        return await AcceptRawAsync(asker, answers[i % 2].PostId, token);
                    },
                    Ct))
                .ToArray();
            gate.Set();
            var replies = await Task.WhenAll(sent);

            foreach (var reply in replies.Where(r => r.Status is not (HttpStatusCode.OK or HttpStatusCode.Created)))
                failures.Add($"rep {rep}: {(int)reply.Status} {(JsonDocument.Parse(reply.Raw).RootElement.TryGetProperty("type", out var t) ? t.GetString() : reply.Raw)}");

            var events = await EventsOfAsync(question.PostId);
            var names = answers.Select(a => a.PostId).ToArray();
            if (events.Any(e => !names.Contains(e, StringComparer.Ordinal))) failures.Add($"rep {rep}: an event names neither answer");
            for (var i = 1; i < events.Length; i++)
                if (events[i] == events[i - 1]) failures.Add($"rep {rep}: events {i} and {i + 1} name one answer");

            var served = JsonDocument.Parse(await forum.Client.GetStringAsync(new Uri($"/v1/posts/{question.PostId}", UriKind.Relative), Ct)).RootElement;
            var accepted = served.GetProperty("provenance").GetProperty("accepted_answer");
            if (events.Length > 0 && accepted.ValueKind is JsonValueKind.Object && accepted.GetProperty("post_id").GetString() != events[^1])
                failures.Add($"rep {rep}: the read serves another answer than the last event names");
        }

        Assert.True(failures.Count == 0, $"Expected: no 5xx and a consistent log. Actual: {failures.Count} problems: {string.Join("; ", failures)}");
    }

    private async Task<(Scene Scene, AggregateId Root)> SceneAsync()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        return (scene, Aggregate(scene.Question.PostId));
    }

    /// <summary>
    /// TRW50. The route's mapping, past the bound: with the store made to lose every append of the thread's stream (a rival appends an event of another
    /// type first, so the real store raises its own conflict), the answer is <c>503</c> with the published type, a body holding neither <c>aggregate</c>,
    /// <c>expected</c>, <c>actual</c> nor a version number, no <c>answer.accepted</c> event stored and the published number of appends tried; with the store
    /// honest again the same accept is <c>201</c>.
    /// </summary>
    [Fact]
    public async Task R8_79_TRW50_PastTheBoundTheRouteAnswers503ContendedAndStoresNothing()
    {
        var (scene, root) = await SceneAsync();
        forum.Conflicting.Arm(root, async (_, token) => await InterfereAsync(root, token));
        Reply lost;
        try
        {
            lost = await AcceptRawAsync(scene.Asker, scene.Answer1.PostId);
        }
        finally
        {
            forum.Conflicting.Disarm();
        }

        var type = lost.Raw.StartsWith('{') && JsonDocument.Parse(lost.Raw).RootElement.TryGetProperty("type", out var t) ? t.GetString() : lost.Raw;
        var stored = await EventsOfAsync(scene.Question.PostId);
        Assert.True(
            lost.Status == HttpStatusCode.ServiceUnavailable && type == PublishedAcceptBudget.ContendedType(),
            $"Expected: 503 {PublishedAcceptBudget.ContendedType()} Actual: {(int)lost.Status} {type}");
        Assert.Equal(
            (true, 0, PublishedAcceptBudget.Attempts()),
            (StoreWords.All(w => !lost.Raw.Contains(w, StringComparison.OrdinalIgnoreCase)) && !System.Text.RegularExpressions.Regex.IsMatch(lost.Raw, @"\d+\s*(?:->|to)\s*\d+"),
             stored.Length,
             forum.Conflicting.AppendsTried));

        // G17: a 5xx is served through ServerFault, which carries type and title and a null detail (no value), so the thread's id is not on the wire.
        using var body = JsonDocument.Parse(lost.Raw);
        Assert.Equal(
            (true, true, false, false),
            (body.RootElement.TryGetProperty("type", out _),
             body.RootElement.TryGetProperty("title", out _),
             body.RootElement.TryGetProperty("detail", out var detail) && detail.ValueKind != JsonValueKind.Null,
             lost.Raw.Contains(scene.Question.PostId, StringComparison.Ordinal)));

        var honest = await AcceptRawAsync(scene.Asker, scene.Answer1.PostId);
        Assert.Equal(HttpStatusCode.Created, honest.Status);
    }

    /// <summary>Appends an event of another type to the stream beneath the decorator, so the caller's own append meets a moved stream.</summary>
    private async Task<bool> InterfereAsync(AggregateId root, CancellationToken token)
    {
        var history = await forum.Inner.ReadByAggregateAsync(root, token);
        var count = history.Match(h => h.Count, e => throw new InvalidOperationException($"{e.Type}: {e.Detail}"));
        var appended = await forum.Inner.AppendAsync(
            root,
            AggregateVersion.From(count).Match(v => v, e => throw new InvalidOperationException(e.Type)),
            [new DomainEvent(
                EventId.Create(Guid.NewGuid().ToString("N")).Match(v => v, e => throw new InvalidOperationException(e.Type)),
                EventType.Create("test.interference").Match(v => v, e => throw new InvalidOperationException(e.Type)),
                ActorId.Create("operator:fixture").Match(v => v, e => throw new InvalidOperationException(e.Type)),
                new Curia.Canon.Json.JsonValue.Object([]))],
            token);
        _ = appended.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Detail}"));
        return true;
    }

    /// <summary>
    /// TRW51 (green first, held by FTRW60 and FTRW62). Only a conflict is retried and only a conflict is a <c>503</c>: with reading the thread root's stream
    /// failing for another reason the answer is <c>500</c> (not <c>503</c>), the read was tried once, and nothing is stored.
    /// </summary>
    [Fact]
    public async Task R8_79_TRW51_AnotherFailureIsNotRetriedAndStaysA500()
    {
        var (scene, root) = await SceneAsync();
        forum.Conflicting.FailReads(root, new Error("curia/test/log-unreadable", "The log could not be read"));
        Reply failed;
        try
        {
            failed = await AcceptRawAsync(scene.Asker, scene.Answer1.PostId);
        }
        finally
        {
            forum.Conflicting.Disarm();
        }

        Assert.Equal(
            (HttpStatusCode.InternalServerError, 1, 0),
            (failed.Status, forum.Conflicting.ReadsTried, (await EventsOfAsync(scene.Question.PostId)).Length));
    }
}
