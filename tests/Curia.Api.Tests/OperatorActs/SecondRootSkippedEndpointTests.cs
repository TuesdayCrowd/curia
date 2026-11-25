#pragma warning disable CA1305, CA1308, CA1707, CA1822, CA1031, CA2007, CA1861, CA1859, CA1062, CA1307, CA1310, CA1002, CA1819
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Curia.OperatorTool;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// SRS12 to SRS17 and SRS19 (errata G80, R4.48, R6.64 (rev.); register item 299), over a real Forum and a throwaway database per test: a row only the application role's own <c>INSERT</c> can write is planted, and
/// a genuine agent (or an identity that has not yet enrolled) is observed by the commands any agent runs, beside a control observed by the same commands. Each observation is read as text and compared whole.
/// The skipped row's type and predicate come from the errata (R6.64 (rev.): a later enrolment of the stream is <c>broken-link</c>; a row that names another agent is <c>statement-mismatch</c>), not from a run.
/// </summary>
public sealed class SecondRootSkippedEndpointTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Row(string agent, string kid, bool reason = true, string extra = "") =>
        $"{{\"agent_id\":\"{agent}\",\"kid\":\"{kid}\"{(reason ? ",\"reason\":\"Enrollment accepted: agent key registered with the Registrar\"" : string.Empty)}{extra}}}";

    /// <summary>A status and the problem type when it is a refusal: no detail, which names agents and posts that differ between an agent and its control.</summary>
    private static string Said(HttpStatusCode status, string body)
    {
        var reply = OperatorActsApi.Read(status, body);
        return $"{(int)status} {((int)status is >= 200 and < 300 ? "ok" : reply.Type)}";
    }

    private static async Task<string> NewTokenAsync(ForumFixture f, OperatorActsApi.Actor a)
    {
        var (st, body) = await a.Dpop.RequestTokenAsync(f.Client, OperatorActsApi.Token, f.Now, a.Agent.AgentId, Ct);
        return st == HttpStatusCode.OK ? JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()! : Said(st, body);
    }

    private static async Task<string> GetAs(ForumFixture f, OperatorActsApi.Actor a, string token, string path)
    {
        var htu = "http://localhost" + path.Split('?')[0];
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", a.Dpop.Proof("GET", htu, f.Now, token, nonce: null));
        using var r = await f.Client.SendAsync(request, Ct);
        return Said(r.StatusCode, await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> PostAs(ForumFixture f, OperatorActsApi.Actor a, string token, string path, string json)
    {
        using var r = await a.Dpop.PostAsync(f.Client, "http://localhost" + path, token, Encoding.UTF8.GetBytes(json), f.Now, Ct, contentType: "application/json");
        return Said(r.StatusCode, await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> AskWith(ForumFixture f, OperatorActsApi.Actor a, string token, string board)
    {
        var wire = a.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "Q " + RevisionProbe.Unrelated(), f.Now);
        using var r = await a.Dpop.PostAsync(f.Client, OperatorActsApi.Posts, token, wire, f.Now, Ct);
        return Said(r.StatusCode, await r.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<string> GetPublic(ForumFixture f, string path)
    {
        using var r = await f.Client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        return Said(r.StatusCode, await r.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>One request of each family that reads the posture, made with a token the agent held before the row, and the public reads that do not: the answer to each by status and type.</summary>
    private static async Task<string> RequestsAsync(ForumFixture f, OperatorActsApi.Actor who, string token, string board, string otherPost)
    {
        var lines = new List<string>
        {
            "GET /v1/budget       " + await GetAs(f, who, token, "/v1/budget"),
            "GET /v1/inbox        " + await GetAs(f, who, token, "/v1/inbox?board=" + board),
            "GET /v1/flags        " + await GetAs(f, who, token, "/v1/flags"),
            "POST /v1/posts       " + await AskWith(f, who, token, board),
            "POST post flags      " + await PostAs(f, who, token, $"/v1/posts/{otherPost}/flags", "{\"kind\":\"spam\",\"rationale\":\"this is a flag raised by a probe\"}"),
            "POST post accept     " + await PostAs(f, who, token, $"/v1/posts/{otherPost}/accept", "{}"),
            "POST posts/batch     " + await PostAs(f, who, token, "/v1/posts/batch", "{\"digests\":[]}"),
            "POST agents/id/keys  " + await PostAs(f, who, token, new Uri(KeyActRequests.RotateUrl(who.Agent.AgentId)).AbsolutePath, "{}"),
            "GET /v1/posts/id     " + await GetPublic(f, "/v1/posts/" + otherPost),
        };
        return string.Join("\n", lines);
    }

    /// <summary>The key set's <c>curia_skipped</c> as the agent's own client reads it: each row as <c>index type predicate</c>, and the total.</summary>
    private static async Task<string> KeySetSkippedAsync(ForumFixture f, string agentId)
    {
        using var r = await f.Client.GetAsync(new Uri("/v1/jwks?agent=" + Uri.EscapeDataString(agentId), UriKind.Relative), Ct);
        var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync(Ct)).RootElement;
        var rows = j.TryGetProperty("curia_skipped", out var s) && s.ValueKind == JsonValueKind.Array
            ? string.Join("; ", s.EnumerateArray().Select(x => $"{x.GetProperty("log_index").GetInt64()} {x.GetProperty("type").GetString()} {x.GetProperty("predicate").GetString()}"))
            : "absent";
        return $"{(int)r.StatusCode} [{(rows.Length == 0 ? "none" : rows)}] total {(j.TryGetProperty("curia_skipped_total", out var t) ? t.GetRawText() : "absent")}";
    }

    /// <summary>What a genuine agent observes of the Forum after a row was planted: a new token, the tier it stamps, whether the re-announcement is unchanged, and one request of each family made with the token it held before.</summary>
    private static async Task<string> ObservedAsync(ForumFixture f, OperatorActsApi api, OperatorActsApi.Actor who, string preToken, string enrolledAtBefore, string board, string otherPost)
    {
        var token = await api.TokenAsync(who, Ct);
        var enrolledAtAfter = await api.EnrollBodyMemberAsync(who.Agent, "enrolled_at", Ct);
        return $"token {token.Reply.Status} {token.Reply.Type}; tier {token.Tier ?? "-"}; re-announce unchanged {enrolledAtBefore == enrolledAtAfter} ({enrolledAtAfter[..3]})\n"
            + await RequestsAsync(f, who, preToken, board, otherPost);
    }

    private static async Task<(OperatorActsApi.Actor Agent, string PreToken, string EnrolledAt)> EnrolledAsync(ForumFixture f, OperatorActsApi api, string stem)
    {
        var a = await api.GenuineAsync(stem, Ct);
        return (a, await NewTokenAsync(f, a), await api.EnrollBodyMemberAsync(a.Agent, "enrolled_at", Ct));
    }

    private static async Task<(string Board, string Post)> QuestionAsync(ForumFixture f, OperatorActsApi api, string stem)
    {
        var board = OperatorActsApi.Board();
        var author = await api.GenuineAsync(stem, Ct);
        var q = await api.PostRawAsync(author, author.Agent.SignQuestion(board, $"How does zq{stem} pelican behave under load and what is its failure mode in production?", $"About zq{stem} pelican", f.Now), Ct);
        return (board, q.PostId);
    }

    /// <summary>SRS12 (OAS39 clause (i), reversed): a well-formed second root in the genuine agent's own stream leaves its token request, its re-announcement and every authenticated request as a control's, and is skipped, counted and listed.</summary>
    [Fact]
    public async Task R4_48_SRS12_ASecondRootInTheAgentsOwnStreamLeavesItServedAndIsListedAsSkipped()
    {
        await using var f = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(f);
        var (board, post) = await QuestionAsync(f, api, "srs12o");
        var victim = await EnrolledAsync(f, api, "srs12v");
        var control = await EnrolledAsync(f, api, "srs12c");
        var index = await api.EventCountAsync(Ct);
        await api.InsertRowAsync("agent.enrolled", victim.Agent.Agent.AgentId, Row(victim.Agent.Agent.AgentId, victim.Agent.Agent.Kid), victim.Agent.Agent.AgentId, f.Now - TimeSpan.FromHours(72), Ct);

        var victimSaw = await ObservedAsync(f, api, victim.Agent, victim.PreToken, victim.EnrolledAt, board, post);
        var controlSaw = await ObservedAsync(f, api, control.Agent, control.PreToken, control.EnrolledAt, board, post);

        WholeText.Same(controlSaw, victimSaw);
        Assert.StartsWith("token 200", victimSaw, StringComparison.Ordinal);
        WholeText.Same(
            $"200 [{index} agent.enrolled broken-link] total 1; control 200 [none] total absent",
            $"{await KeySetSkippedAsync(f, victim.Agent.Agent.AgentId)}; control {await KeySetSkippedAsync(f, control.Agent.Agent.AgentId)}");
    }

    /// <summary>SRS12b: a real key rotation, the one family the matrix of SRS12 can only send a bad body to, with a token held before the row: served, and so is the rotation of a control no row names (a key act appends through the use case and not only through the route's authorization).</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R4_48_SRS12b_AKeyRotationWithATokenHeldBeforeTheRowIsServed(bool planted)
    {
        await using var f = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(f);
        var victim = await EnrolledAsync(f, api, "srs12bv");
        var control = await EnrolledAsync(f, api, "srs12bc");
        if (planted) await api.InsertRowAsync("agent.enrolled", victim.Agent.Agent.AgentId, Row(victim.Agent.Agent.AgentId, victim.Agent.Agent.Kid), victim.Agent.Agent.AgentId, f.Now - TimeSpan.FromHours(72), Ct);

        async Task<string> RotateAsync((OperatorActsApi.Actor Agent, string PreToken, string EnrolledAt) who)
        {
            var prev = await KeyActRequests.LatestLifecycleAsync(f.Client, who.Agent.Agent.AgentId, Ct);
            using var r = await who.Agent.Dpop.PostAsync(
                f.Client, KeyActRequests.RotateUrl(who.Agent.Agent.AgentId), who.PreToken,
                KeyActRequests.RotationBody(who.Agent.Agent, KeyActRequests.KeyPair.New("next-" + OperatorActsApi.Suffix()), prev), f.Now, Ct, contentType: "application/json");
            return Said(r.StatusCode, await r.Content.ReadAsStringAsync(Ct));
        }

        // Both rotations are made before either is read: with a row planted for the victim, the control (an identity no row names) is rotated beside it.
        var controlSaw = await RotateAsync(control);
        var victimSaw = await RotateAsync(victim);
        WholeText.Same("control 201 ok; victim 201 ok", $"control {controlSaw}; victim {victimSaw}");
    }

    /// <summary>SRS13: a well-formed enrolment in X's stream naming Y (after Y's own root) leaves Y served and Y's tenure its own; the row is listed on X's key set (statement-mismatch) and Y's carries none. Y before the row is the positive control.</summary>
    [Fact]
    public async Task R4_48_SRS13_ARowInAnotherStreamNamingYLeavesYServedAndIsListedOnXs()
    {
        await using var f = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(f);
        var (board, post) = await QuestionAsync(f, api, "srs13o");
        var x = await EnrolledAsync(f, api, "srs13x");
        var y = await EnrolledAsync(f, api, "srs13y");
        var twin = await EnrolledAsync(f, api, "srs13t");
        var before = await ObservedAsync(f, api, y.Agent, y.PreToken, y.EnrolledAt, board, post);
        var index = await api.EventCountAsync(Ct);
        await api.InsertRowAsync("agent.enrolled", x.Agent.Agent.AgentId, Row(y.Agent.Agent.AgentId, y.Agent.Agent.Kid), x.Agent.Agent.AgentId, f.Now - TimeSpan.FromHours(72), Ct);

        var after = await ObservedAsync(f, api, y.Agent, y.PreToken, y.EnrolledAt, board, post);
        var twinSaw = await ObservedAsync(f, api, twin.Agent, twin.PreToken, twin.EnrolledAt, board, post);

        WholeText.Same(before, after);
        WholeText.Same(twinSaw, after);
        WholeText.Same(
            $"X 200 [{index} agent.enrolled statement-mismatch] total 1; Y 200 [none] total absent",
            $"X {await KeySetSkippedAsync(f, x.Agent.Agent.AgentId)}; Y {await KeySetSkippedAsync(f, y.Agent.Agent.AgentId)}");
    }

    /// <summary>SRS13b: a row in X's stream naming Z before Z has enrolled: Z's enrolment is 201 with its own instant, Z's token is 200 and a request it authorises is served; X's key set carries the row and Z's none. Z's twin, enrolling with no row planted, is the control.</summary>
    [Fact]
    public async Task R4_48_SRS13b_ARowNamingAnIdentityThatHasNotEnrolledDoesNotLockItOutWhenItDoes()
    {
        await using var f = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(f);
        var (board, post) = await QuestionAsync(f, api, "srs13bo");
        var x = await EnrolledAsync(f, api, "srs13bx");
        var z = ForumAgent.Create($"https://agents.example/srs13bz-{OperatorActsApi.Suffix()}", "z-" + OperatorActsApi.Suffix());
        var twin = ForumAgent.Create($"https://agents.example/srs13bt-{OperatorActsApi.Suffix()}", "t-" + OperatorActsApi.Suffix());
        var index = await api.EventCountAsync(Ct);
        await api.InsertRowAsync("agent.enrolled", x.Agent.Agent.AgentId, Row(z.AgentId, z.Kid), x.Agent.Agent.AgentId, f.Now - TimeSpan.FromHours(72), Ct);

        async Task<string> EnrolThenObserveAsync(ForumAgent agent)
        {
            using var enrol = await agent.EnrollAsync(f.Client, f.Now, Ct);
            var enrolBody = await enrol.Content.ReadAsStringAsync(Ct);
            var enrolledAt = JsonDocument.Parse(enrolBody).RootElement.TryGetProperty("enrolled_at", out var v) ? DateTimeOffset.Parse(v.GetString()!, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime : (DateTime?)null;
            var actor = new OperatorActsApi.Actor(agent, DpopClient.For(agent, agent.AssertionKey));
            var token = await NewTokenAsync(f, actor);
            var served = token.Contains(' ', StringComparison.Ordinal) ? "no token: " + token : await GetAs(f, actor, token, "/v1/budget");
            return $"enrol {(int)enrol.StatusCode}; enrolled_at is the planted instant {(enrolledAt is { } e && e == (f.Now - TimeSpan.FromHours(72)).UtcDateTime)}; budget {served}";
        }

        var zSaw = await EnrolThenObserveAsync(z);
        var twinSaw = await EnrolThenObserveAsync(twin);

        WholeText.Same("enrol 201; enrolled_at is the planted instant False; budget 200 ok", twinSaw);
        WholeText.Same(twinSaw, zSaw);
        WholeText.Same(
            $"X 200 [{index} agent.enrolled statement-mismatch] total 1; Z 200 [none] total absent",
            $"X {await KeySetSkippedAsync(f, x.Agent.Agent.AgentId)}; Z {await KeySetSkippedAsync(f, z.AgentId)}");
        _ = (board, post);
    }

    /// <summary>SRS14: every shape of an <c>agent.enrolled</c> row that is not a stream's honoured root leaves the genuine agent served as its control is; the sweep item 299's single scene was not.</summary>
    [Fact]
    public async Task R4_48_SRS14_EveryShapeOfARowThatIsNotAnHonouredRootLeavesTheAgentServed()
    {
        await using var f = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(f);
        var (board, post) = await QuestionAsync(f, api, "srs14o");
        var control = await EnrolledAsync(f, api, "srs14c");
        var x = await EnrolledAsync(f, api, "srs14x");
        var controlSaw = await ObservedAsync(f, api, control.Agent, control.PreToken, control.EnrolledAt, board, post);
        var at = f.Now - TimeSpan.FromHours(72);
        var failures = new List<string>();
        var named = await EnrolledAsync(f, api, "srs14n");
        var shapes = new (string Name, Func<OperatorActsApi.Actor, Task> Plant, bool ObserveNamed)[]
        {
            ("same-kid second root", v => api.InsertRowAsync("agent.enrolled", v.Agent.AgentId, Row(v.Agent.AgentId, v.Agent.Kid), v.Agent.AgentId, at, Ct), false),
            ("other-kid second root", v => api.InsertRowAsync("agent.enrolled", v.Agent.AgentId, Row(v.Agent.AgentId, "another-kid"), v.Agent.AgentId, at, Ct), false),
            ("a later row in the agent's own stream naming another enrolled agent (observed on the named one)", v => api.InsertRowAsync("agent.enrolled", v.Agent.AgentId, Row(named.Agent.Agent.AgentId, named.Agent.Agent.Kid), v.Agent.AgentId, at, Ct), true),
            ("a first root of a fresh stream naming the agent", v => api.InsertRowAsync("agent.enrolled", "https://agents.example/fresh-" + OperatorActsApi.Suffix(), Row(v.Agent.AgentId, v.Agent.Kid), "https://agents.example/fresh", at, Ct), false),
            ("a row in another enrolled agent's stream naming the agent", v => api.InsertRowAsync("agent.enrolled", x.Agent.Agent.AgentId, Row(v.Agent.AgentId, v.Agent.Kid), x.Agent.Agent.AgentId, at, Ct), false),
            ("a second root without a reason (inert today: the standing reads no row without one)", v => api.InsertRowAsync("agent.enrolled", v.Agent.AgentId, Row(v.Agent.AgentId, v.Agent.Kid, reason: false), v.Agent.AgentId, at, Ct), false),
            ("a second root with an extra member", v => api.InsertRowAsync("agent.enrolled", v.Agent.AgentId, Row(v.Agent.AgentId, v.Agent.Kid, extra: ",\"note\":\"x\""), v.Agent.AgentId, at, Ct), false),
        };
        var n = 0;
        foreach (var (name, plant, observeNamed) in shapes)
        {
            var victim = await EnrolledAsync(f, api, "srs14v" + n++);
            await plant(victim.Agent);
            var saw = observeNamed
                ? await ObservedAsync(f, api, named.Agent, named.PreToken, named.EnrolledAt, board, post)
                : await ObservedAsync(f, api, victim.Agent, victim.PreToken, victim.EnrolledAt, board, post);
            if (saw != controlSaw) failures.Add($"{name}: Expected: {controlSaw.Split('\n')[0]}  Actual: {saw.Split('\n')[0]}");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} of {shapes.Length} shapes leave the agent unserved: {string.Join(" | ", failures)}");
    }

    /// <summary>SRS15 (OAS39's clauses (ii) and (iii), kept; green on the tree and after): the squat is still 409 and the backdated root still buys its planted instant. The closure narrowed nothing it did not mean to.</summary>
    [Fact]
    public async Task R4_48_SRS15_TheSquatAndTheBackdatedRootAreAsFiled_Control()
    {
        await using var f = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(f);
        var squatted = ForumAgent.Create($"https://agents.example/planted-{OperatorActsApi.Suffix()}", "k-" + OperatorActsApi.Suffix());
        await api.InsertRowAsync("agent.enrolled", squatted.AgentId, Row(squatted.AgentId, "other"), squatted.AgentId, f.Now - TimeSpan.FromHours(72), Ct);
        var backdated = await api.ForgeAsync("backdated", TimeSpan.FromHours(72), attest: false, "owner:none", Ct);
        var served = await api.EnrollBodyMemberAsync(backdated.Agent, "enrolled_at", Ct);

        WholeText.Same(
            "409 curia/enroll/already-enrolled; 201 " + (f.Now - TimeSpan.FromHours(72)).UtcDateTime.ToString("o"),
            $"{await api.EnrollBodyMemberAsync(squatted, "none", Ct)}; {served[..4]}{(served.Length > 4 ? DateTimeOffset.Parse(served[4..], System.Globalization.CultureInfo.InvariantCulture).UtcDateTime.ToString("o") : string.Empty)}");
    }

    /// <summary>SRS16: <c>curia-operator lifecycle-audit</c> and <c>sign-head --if-grown</c> over a later <c>agent.enrolled</c> row: exit 7 and the row's line, exit 6 and the withheld line while it follows the latest head, and a head signed once a plain head covers it. Both shapes (the agent's own stream; an enrolled agent's stream naming another).</summary>
    [Theory]
    [InlineData("own-stream", "broken-link")]
    [InlineData("naming-another", "statement-mismatch")]
    public async Task R4_48_SRS16_TheOperatorSeesALaterEnrolmentRowAndTheHeadSignerStops(string shape, string predicate)
    {
        await using var f = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(f);
        var y = await api.GenuineAsync("srs16y", Ct);
        var x = await api.GenuineAsync("srs16x", Ct);
        var pem = LogSigningKey.GeneratePem();
        Assert.Equal(0, (await VerifiedStopSupport.OperatorAsync(f, Ct, ["sign-head", "--by", "ops"], () => pem)).Exit);
        var index = await api.EventCountAsync(Ct);
        var (aggregate, names) = shape == "own-stream" ? (y.Agent.AgentId, y.Agent) : (x.Agent.AgentId, y.Agent);
        await api.InsertRowAsync("agent.enrolled", aggregate, Row(names.AgentId, names.Kid), aggregate, f.Now, Ct);

        var audit = await api.OperatorAsync(Ct, "lifecycle-audit");
        var line = $"skipped  log_index={index}  type=agent.enrolled  aggregate={Curia.Canon.Json.DisplayLiteral.Of(aggregate)}  predicate={predicate}";
        var before = await VerifiedStopSupport.CountAsync(f, Ct);
        var withheld = await VerifiedStopSupport.OperatorAsync(f, Ct, ["sign-head", "--by", "ops", "--if-grown"], () => pem);
        var wantWithheld = $"withheld  log_index={index}  type=agent.enrolled  predicate={predicate}";
        WholeText.Same(
            "audit exit 7, the line; --if-grown exit 6, stdout empty, the withheld line, 0 rows appended",
            $"audit exit {audit.Exit}, {(audit.Out.Contains(line, StringComparison.Ordinal) ? "the line" : "no line [" + line + "] in [" + audit.Out.Trim() + "]")}; "
            + $"--if-grown exit {withheld.Exit}, stdout {(withheld.Out.Length == 0 ? "empty" : "[" + withheld.Out.Trim() + "]")}, {(withheld.Err.Contains(wantWithheld, StringComparison.Ordinal) ? "the withheld line" : "no withheld line in [" + withheld.Err.Trim() + "]")}, {await VerifiedStopSupport.CountAsync(f, Ct) - before} rows appended");

        var covered = await VerifiedStopSupport.OperatorAsync(f, Ct, ["sign-head", "--by", "ops"], () => pem);
        await VerifiedStopSupport.EnrolAsync(f, "srs16n", Ct);
        var resumed = await VerifiedStopSupport.OperatorAsync(f, Ct, ["sign-head", "--by", "ops", "--if-grown"], () => pem);
        WholeText.Same(
            "plain 0; resumed 0 signed head",
            $"plain {covered.Exit}; resumed {resumed.Exit} {(resumed.Out.Contains("signed head", StringComparison.Ordinal) ? "signed head" : "no head: " + resumed.Err.Split('\n')[0])}");
    }

    /// <summary>SRS17: the Forum's start-up scan (EventId 7007) names a later <c>agent.enrolled</c> row, Error level, with the count and the predicate.</summary>
    [Theory]
    [InlineData("own-stream", "broken-link")]
    [InlineData("naming-another", "statement-mismatch")]
    public async Task R4_48_SRS17_TheStartUpScanNamesALaterEnrolmentRow(string shape, string predicate)
    {
        await using var f = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(f);
        var y = await api.GenuineAsync("srs17y", Ct);
        var x = await api.GenuineAsync("srs17x", Ct);
        var aggregate = shape == "own-stream" ? y.Agent.AgentId : x.Agent.AgentId;
        await api.InsertRowAsync("agent.enrolled", aggregate, Row(y.Agent.AgentId, y.Agent.Kid), aggregate, f.Now, Ct);

        await using var captured = new CapturedForum(f);
        var logged = captured.Log.All.Where(e => e.EventId == 7007).ToArray();
        var count = logged.Length == 1 ? logged[0].Values.FirstOrDefault(v => v.Key == "Count").Value?.ToString() : null;
        WholeText.Same(
            "one event 7007, Error, with Count 1 and the predicate",
            logged.Length == 1
                ? $"one event 7007, {(logged[0].Level == LogLevel.Error ? "Error" : logged[0].Level.ToString())}, with Count {count ?? "none"} and {(logged[0].Message.Contains(predicate, StringComparison.Ordinal) ? "the predicate" : "no predicate: " + logged[0].Message)}"
                : $"{logged.Length} events 7007: a log entry with EventId 7007 was expected, none");
    }

    /// <summary>SRS19 (a control, green on the tree): <c>curia-operator attest-owner</c> for an identity whose only enrolment-looking row is one in another stream answers exactly as it does for an unknown agent. It is the one reader of the standing's enrolment instant whose answer would move if a cross-stream standing were left in place.</summary>
    [Fact]
    public async Task R4_48_SRS19_AttestOwnerForAnIdentityNamedOnlyFromAnotherStreamAnswersAsForAnUnknownAgent_Control()
    {
        await using var f = await VerifiedStopSupport.NewAsync();
        var api = new OperatorActsApi(f);
        var x = await api.GenuineAsync("srs19x", Ct);
        var named = $"https://agents.example/srs19n-{OperatorActsApi.Suffix()}";
        var unknown = $"https://agents.example/srs19u-{OperatorActsApi.Suffix()}";
        await api.InsertRowAsync("agent.enrolled", x.Agent.AgentId, Row(named, "k"), x.Agent.AgentId, f.Now - TimeSpan.FromHours(72), Ct);
        await f.OperatorActsKeyAsync(Ct);
        var size = await api.EventCountAsync(Ct);

        string Words((int Exit, string Out, string Err) run, string agent) => $"exit {run.Exit}; out [{run.Out.Replace(agent, "<agent>", StringComparison.Ordinal).Trim()}]; err [{run.Err.Replace(agent, "<agent>", StringComparison.Ordinal).Trim()}]";
        var forNamed = await api.OperatorAsync(Ct, "attest-owner", "--agent", named, "--owner", "owner:example", "--by", "reviewer");
        var forUnknown = await api.OperatorAsync(Ct, "attest-owner", "--agent", unknown, "--owner", "owner:example", "--by", "reviewer");

        WholeText.Same(Words(forUnknown, unknown), Words(forNamed, named));
        Assert.Contains("curia/attest/not-enrolled", forNamed.Err, StringComparison.Ordinal);
        Assert.Equal(size, await api.EventCountAsync(Ct));
    }
}
