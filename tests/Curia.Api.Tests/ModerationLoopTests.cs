using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.OperatorTool;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G13's two findings, closed where a user meets them. Table 11's T1 row — "≥ 3 questions with
/// no upheld flags" — was vacuous because nothing could uphold a flag; here an upheld flag demotes its
/// author, an unadjudicated one does not, and a restore reinstates. And R10.39's figures are computed
/// from nothing but what an anonymous reader can fetch.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ModerationLoopTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private sealed record Party(ForumAgent Agent, DpopClient Dpop, string Token);

    private async Task<Party> PartyAsync(HttpClient client, ForumAgent agent, CancellationToken ct)
    {
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return new Party(agent, dpop, token);
    }

    private static ForumAgent NewAgent(string stem)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
    }

    private async Task<(string PostId, string Digest)> AskAsync(HttpClient client, Party party, string board, string body, string title, CancellationToken ct)
    {
        using var asked = await party.Dpop.PostAsync(client, PostsUrl, party.Token, party.Agent.SignQuestion(board, body, title, forum.Now), forum.Now, ct);
        var text = await asked.Content.ReadAsStringAsync(ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, text);

        using var receipt = JsonDocument.Parse(text);
        return (receipt.RootElement.GetProperty("post_id").GetString()!, receipt.RootElement.GetProperty("digest").GetString()!);
    }

    private async Task<(HttpStatusCode Status, string Body)> AnswerAsync(HttpClient client, Party party, string board, string question, string body, CancellationToken ct)
    {
        using var answered = await party.Dpop.PostAsync(client, PostsUrl, party.Token, party.Agent.SignAnswer(board, body, question, forum.Now), forum.Now, ct);
        return (answered.StatusCode, await answered.Content.ReadAsStringAsync(ct));
    }

    private async Task FlagAsync(HttpClient client, Party raiser, string postId, string kind, string rationale, CancellationToken ct)
    {
        using var raised = await raiser.Dpop.PostAsync(
            client, $"http://localhost/v1/posts/{postId}/flags", raiser.Token,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind, rationale })),
            forum.Now, ct, contentType: "application/json");
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
    }

    private async Task OperatorAsync(CancellationToken ct, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(args, forum.ConnectionString, forum.Clock, stdout, stderr, ct);
        Assert.True(exit == ExitCode.Ok, $"curia-operator {string.Join(' ', args)} exited {exit}: {stderr}");
    }

    private async Task<string> InboxAsync(HttpClient client, Party party, string board, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"/v1/inbox?board={board}", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", party.Token);
        request.Headers.Add("DPoP", party.Dpop.Proof("GET", "http://localhost/v1/inbox", forum.Now, party.Token, nonce: null));

        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Every log entry an anonymous reader can fetch, as the <c>entry</c> object R6.51 serves.</summary>
    private static async Task<List<JsonElement>> PublicLogAsync(HttpClient client, CancellationToken ct)
    {
        var entries = new List<JsonElement>();
        for (long i = 0; i < 100_000; i++)
        {
            using var response = await client.GetAsync(new Uri($"/v1/log/entries/{i}", UriKind.Relative), ct);
            if (response.StatusCode == HttpStatusCode.NotFound) return entries;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            entries.Add(document.RootElement.GetProperty("entry").Clone());
        }

        throw new InvalidOperationException("the log did not end within 100,000 entries");
    }

    private static string TypeOf(JsonElement entry) => entry.GetProperty("event_type").GetString()!;

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    /// <summary>
    /// Appends an automated dismissal naming <paramref name="post"/>'s flag, straight to the event store.
    ///
    /// <para><b>R10.61's anticipated out-of-rule input, not a stand-in for the writer</b> (trap 16).
    /// R10.60 has an automated record name no flag, and R10.59's writer writes only human records, so
    /// nothing in this Forum writes one. But the log is append-only and can hold it, and R10.61 binds
    /// every fold that reads the log, R10.39's alike, to let a record that is not reviewing change
    /// nothing, "whatever it names". An auditor cannot assume the rule was kept, so this test puts the
    /// breach in the log and holds both derivations to ignoring it.</para>
    /// </summary>
    private async Task AppendAnAutomatedDismissalAsync((string PostId, string Digest) post, CancellationToken ct)
    {
        var store = forum.Services.GetRequiredService<IEventStore>();
        var events = Require(await store.ReadAllAsync(ct));
        var details = Require(await forum.Services.GetRequiredService<IFlagDetailStore>().ReadAllAsync(ct));
        var flag = FlagDirectory.Join(events, details).Flags.Single(f => f.PostId == post.PostId);

        var aggregate = Require(AggregateId.Create(post.PostId));
        var actor = Require(ActorId.Create("automated:detector"));
        var payload = new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(post.PostId)),
            new(FlagProjector.DigestField, new JsonValue.String(post.Digest)),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(ModeratorKind.Automated))),
            new(FlagProjector.ActorIdField, new JsonValue.String(actor.Value)),
            new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(ModerationEffect.Dismiss))),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(flag.Kind))),
            new(FlagProjector.RationaleField, new JsonValue.String("Detector score below threshold.")),
            new(FlagProjector.AdjudicatesField, new JsonValue.Array([new JsonValue.String(flag.FlagId)])),
        ]);

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(events.Count(e => e.AggregateId == aggregate))),
            [new DomainEvent(
                Require(EventId.Create(Require(new UlidGenerator(forum.Clock).Next()).ToString())),
                Require(EventType.Create(FlagProjector.ModerationAppliedType)),
                actor,
                payload)],
            ct));
    }

    /// <summary>Every read path agrees, in both directions — the second direction is what stops the first passing on an empty page.</summary>
    private async Task AssertServedAsync(HttpClient client, Party reader, string board, string postId, string digest, bool served, CancellationToken ct)
    {
        using (var single = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct))
            Assert.Equal(served ? HttpStatusCode.OK : HttpStatusCode.NotFound, single.StatusCode);

        using (var thread = await client.GetAsync(new Uri($"/v1/threads/{postId}", UriKind.Relative), ct))
            Assert.Equal(served ? HttpStatusCode.OK : HttpStatusCode.NotFound, thread.StatusCode);

        using (var listing = await client.GetAsync(new Uri($"/v1/boards/{board}/posts", UriKind.Relative), ct))
            Assert.Equal(served, (await listing.Content.ReadAsStringAsync(ct)).Contains(postId, StringComparison.Ordinal));

        using (var search = await client.GetAsync(new Uri($"/v1/search?q=canonical&board={board}&kind=question", UriKind.Relative), ct))
            Assert.Equal(served, (await search.Content.ReadAsStringAsync(ct)).Contains(postId, StringComparison.Ordinal));

        Assert.Equal(served, (await InboxAsync(client, reader, board, ct)).Contains(postId, StringComparison.Ordinal));

        using var batch = await client.PostAsJsonAsync(new Uri("/v1/posts/batch", UriKind.Relative), new { digests = new[] { digest } }, ct);
        using var items = JsonDocument.Parse(await batch.Content.ReadAsStringAsync(ct));
        Assert.Equal(served ? "current" : "withheld", items.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
    }

    /// <summary>
    /// R10.61 and Table 11's T1 row, end to end. An author reaches T1; an unadjudicated flag against
    /// one of its three questions leaves it there (no unilateral demotion primitive); the operator
    /// upholds the flag and the question disappears from every read path and the author drops to T0;
    /// a restore reverses both.
    /// </summary>
    [Fact]
    public async Task R10_61_AnUpheldFlagDemotesItsAuthorAndARestoreReinstatesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "loop-" + Guid.NewGuid().ToString("N")[..8];

        ForumAgent authorAgent = NewAgent("loop-author"), askerAgent = NewAgent("loop-asker"), reporterAgent = NewAgent("loop-reporter");
        var author = await PartyAsync(client, authorAgent, ct);
        var asker = await PartyAsync(client, askerAgent, ct);
        await forum.AttestOwnerAsync(authorAgent.AgentId, ct);

        var questions = new List<(string PostId, string Digest)>();
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            questions.Add(await AskAsync(client, author, board, $"Question {i} about canonical form ({nonce}).", $"Author's question {i} {nonce}", ct));
        }

        // One question per answer attempt, so no step depends on answering the same question twice.
        var open = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            open.Add((await AskAsync(client, asker, board, $"Which canonical form does the log hash ({nonce})?", $"Asker's question {i} {nonce}", ct)).PostId);
        }

        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));
        author = await PartyAsync(client, authorAgent, ct);
        asker = await PartyAsync(client, askerAgent, ct);
        var reporter = await PartyAsync(client, reporterAgent, ct);

        var (status, body) = await AnswerAsync(client, author, board, open[0], "The pure RFC 8785 form (R6.46).", ct);
        Assert.True(status == HttpStatusCode.Created, "the author did not reach T1, so this test proves nothing: " + body);

        var (flagged, flaggedDigest) = questions[0];
        await FlagAsync(client, reporter, flagged, "spam", "Advertising, not a question.", ct);

        (status, body) = await AnswerAsync(client, author, board, open[1], "Still T1: an open flag decides nothing.", ct);
        Assert.True(status == HttpStatusCode.Created, "an unadjudicated flag demoted its target: " + body);

        await OperatorAsync(ct, "moderate", "--post", flagged, "--category", "spam", "--effect", "withhold", "--reason", "Reviewed: advertising.", "--by", "reviewer");
        await AssertServedAsync(client, asker, board, flagged, flaggedDigest, served: false, ct);

        (status, body) = await AnswerAsync(client, author, board, open[2], "Now T0: two clean questions, not three.", ct);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("curia/authz/denied", body, StringComparison.Ordinal);

        // The record is public (R6.25) and names the flag it upheld (R10.60) — and still no raiser.
        var record = (await PublicLogAsync(client, ct)).Single(e =>
            TypeOf(e) == "moderation.applied" && e.GetProperty("payload").GetProperty("post_id").GetString() == flagged);
        Assert.Single(record.GetProperty("payload").GetProperty("adjudicates").EnumerateArray());
        Assert.DoesNotContain(reporterAgent.AgentId, record.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Advertising", record.GetRawText(), StringComparison.Ordinal);

        await OperatorAsync(ct, "moderate", "--post", flagged, "--category", "spam", "--effect", "restore", "--reason", "Appeal upheld on review.", "--by", "reviewer");
        await AssertServedAsync(client, asker, board, flagged, flaggedDigest, served: true, ct);

        (status, body) = await AnswerAsync(client, author, board, open[2], "T1 again: the restore released the flag.", ct);
        Assert.True(status == HttpStatusCode.Created, "the restore did not reinstate the author: " + body);
    }

    /// <summary>
    /// R10.39 and R10.60: each flag's time to action and the upheld rate are computable from the public
    /// log alone. Flag entries give the instants, records give the adjudications, and each record's
    /// digest ties it to the envelope the log accepted for its post (R6.25), so an auditor counts no
    /// record for content the log never held. The figures must equal the same figures computed through
    /// the private join, which knows each flag's post without any record (spec Increment 4). The test's
    /// own clock is kept only as the non-vacuity guard: two flags, 90 and 120 minutes, one upheld. Two
    /// automated records naming those flags sit in the log beside the reviews, as R10.61's out-of-rule
    /// input, and both derivations must ignore them.
    /// </summary>
    [Fact]
    public async Task R10_39_TimeToActionAndTheUpheldRateAreComputableFromThePublicLogAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "r1039-" + Guid.NewGuid().ToString("N")[..8];

        var author = await PartyAsync(client, NewAgent("r1039-author"), ct);
        var first = await AskAsync(client, author, board, "Is canonical form unique?", "First " + Guid.NewGuid().ToString("N")[..8], ct);
        var second = await AskAsync(client, author, board, "Is canonical form stable?", "Second " + Guid.NewGuid().ToString("N")[..8], ct);

        var reporterA = await PartyAsync(client, NewAgent("r1039-a"), ct);
        await FlagAsync(client, reporterA, first.PostId, "spam", "Advertising.", ct);

        forum.Clock.Advance(TimeSpan.FromMinutes(30));
        var reporterB = await PartyAsync(client, NewAgent("r1039-b"), ct);
        await FlagAsync(client, reporterB, second.PostId, "incorrect", "The premise is wrong.", ct);

        // R10.61's anticipated out-of-rule input, not a stand-in for the writer (trap 16): an automated
        // record naming B before anyone reviewed it. A fold that counted it would time B's action at
        // 30 minutes, not 90.
        forum.Clock.Advance(TimeSpan.FromMinutes(30));
        await AppendAnAutomatedDismissalAsync(second, ct);

        forum.Clock.Advance(TimeSpan.FromMinutes(60));
        await OperatorAsync(ct, "moderate", "--post", first.PostId, "--category", "spam", "--effect", "withhold", "--reason", "Reviewed: advertising.", "--by", "reviewer");

        // R10.61's anticipated out-of-rule input again, not a stand-in for the writer (trap 16): an
        // automated dismissal naming A after a human upheld it. A fold that honoured it would release
        // A and count no flag upheld.
        await AppendAnAutomatedDismissalAsync(first, ct);
        await OperatorAsync(ct, "moderate", "--post", second.PostId, "--category", "incorrect", "--effect", "dismiss", "--reason", "Reviewed: the premise holds.", "--by", "reviewer");

        // Only what an anonymous reader can fetch.
        var log = await PublicLogAsync(client, ct);
        static DateTimeOffset At(JsonElement e) => DateTimeOffset.Parse(e.GetProperty("server_ts").GetString()!, CultureInfo.InvariantCulture);

        var raisedAt = log.Where(e => TypeOf(e) == "flag.committed")
            .ToDictionary(e => e.GetProperty("event_id").GetString()!, At, StringComparer.Ordinal);

        string AcceptedDigest(string postId) => log
            .Single(e => TypeOf(e) == "post.accepted" && e.GetProperty("payload").GetProperty("post_id").GetString() == postId)
            .GetProperty("payload").GetProperty("digest").GetString()!;

        var publicTimeToAction = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var publicUpheld = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in log.Where(e => TypeOf(e) == "moderation.applied"))
        {
            var payload = record.GetProperty("payload");
            var postId = payload.GetProperty("post_id").GetString()!;
            if (postId != first.PostId && postId != second.PostId) continue;

            // R6.25: the record names the bytes it acted on, and they are the bytes the log accepted.
            Assert.Equal(AcceptedDigest(postId), payload.GetProperty("digest").GetString());

            // R10.61: only a reviewing record adjudicates; any other changes nothing, whatever it names. The
            // table permits every cell of these two rows, so the moderator kind alone decides it here.
            if (payload.GetProperty("moderator").GetString() is not ("human" or "delegated_agent")) continue;

            var upholds = payload.GetProperty("effect").GetString() is "withhold" or "quarantine";
            foreach (var flag in payload.GetProperty("adjudicates").EnumerateArray().Select(f => f.GetString()!))
            {
                publicTimeToAction.TryAdd(flag, At(record) - raisedAt[flag]);
                if (upholds) publicUpheld.Add(flag); else publicUpheld.Remove(flag);
            }
        }

        // The same figures through the private join: the directory knows each flag's post from the
        // private store, and the fold decides upholding exactly as posture does.
        var events = Require(await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(ct));
        var details = Require(await forum.Services.GetRequiredService<IFlagDetailStore>().ReadAllAsync(ct));
        var moderation = FlagProjector.Fold(events);

        var privateTimeToAction = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var privateUpheld = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in FlagDirectory.Join(events, details).Flags.Where(f => f.PostId == first.PostId || f.PostId == second.PostId))
        {
            var history = moderation[flag.PostId].History;

            // R10.61: a flag is acted on by the first record after which it is adjudicated -- the first
            // reviewing record that names it, never an automated one that names it sooner.
            var actedOn = history.Where((_, i) => ModerationPolicy.AdjudicatedFlags(history[..(i + 1)]).Contains(flag.FlagId)).First();
            privateTimeToAction[flag.FlagId] = actedOn.At.Value - flag.At.Value;

            if (ModerationPolicy.UpheldFlags(history).Contains(flag.FlagId)) privateUpheld.Add(flag.FlagId);
        }

        Assert.Equal(
            privateTimeToAction.OrderBy(p => p.Key, StringComparer.Ordinal),
            publicTimeToAction.OrderBy(p => p.Key, StringComparer.Ordinal));
        Assert.Equal(privateUpheld.Order(StringComparer.Ordinal), publicUpheld.Order(StringComparer.Ordinal));

        // Non-vacuity: two flags, acted on 90 and 120 minutes after they were raised, one of them upheld.
        Assert.Equal([TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(120)], publicTimeToAction.Values.Order());
        Assert.Single(publicUpheld);

        // And the public log carries neither raiser.
        foreach (var entry in log.Where(e => TypeOf(e) is "flag.committed" or "moderation.applied"))
        {
            Assert.DoesNotContain(reporterA.Agent.AgentId, entry.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain(reporterB.Agent.AgentId, entry.GetRawText(), StringComparison.Ordinal);
        }
    }
}
