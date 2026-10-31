using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// ULG20 to ULG24, ULG41 (errata G52, R8.82, R7.31): the six writes a route accepts, each sent while the event store fails in a way the test chooses, on the
/// in-process host (the plan's table says Kestrel; nothing here needs a socket, and the Task 0 record sent the same acts through this host). The oracle for what
/// was stored and spent is the database, counted by SQL on <c>events</c> (and, for the orphan row, on <c>flag_details</c>), never the response of the request
/// under test. Every act is built and every token minted before the fault is armed; the fault is armed for the send and disarmed after it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class StoreFaultWriteTests(FaultableForumFixture forum) : IClassFixture<FaultableForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One act of one kind: who sends it, which budget it spends, and how to send it again exactly as before.</summary>
    private sealed record WriteCase(string Name, SceneActor Actor, string Budget, Func<Task<Reply>> Send, string RepeatShape);

    private static Reply Of(Sent sent) => new(sent.Status, sent.ContentType, sent.Body, sent.Threw);

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A request the host threw out of is a reply that says what it threw; the deployed host answers 500.")]
    private async Task<Reply> JsonPostAsync(SceneActor actor, string url, string json, string token)
    {
        try
        {
            using var response = await actor.Dpop.PostAsync(forum.Client, url, token, Encoding.UTF8.GetBytes(json), forum.Now, Ct, contentType: "application/json");
            return new Reply((int)response.StatusCode, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsStringAsync(Ct), null);
        }
        catch (Exception e) when (!Ct.IsCancellationRequested)
        {
            return new Reply(500, null, string.Empty, $"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>The six writes of a scene, each with its token minted and its bytes signed, none sent.</summary>
    private async Task<WriteCase[]> CasesAsync()
    {
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var flagger = await AcceptedAnswerScene.EnrolAsync(forum, "flagger", Ct);
        var board = scene.Board;
        var now = forum.Now;
        var askerToken = await scene.Asker.Dpop.GetTokenAsync(forum.Client, Probe.TokenEndpoint, now, Ct);
        var answererToken = await scene.Answerer.Dpop.GetTokenAsync(forum.Client, Probe.TokenEndpoint, now, Ct);
        var flaggerToken = await flagger.Dpop.GetTokenAsync(forum.Client, Probe.TokenEndpoint, now, Ct);

        // The posts are the T1 answerer's: the scene's asker (T0) has spent its three posts building the thread, and a spent budget is not what these facts are about.
        var question = scene.Answerer.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), now);
        var answer = scene.Answerer.Agent.SignAnswer(board, Probe.Unrelated(), scene.Question.PostId, now);
        var comment = scene.Answerer.Agent.Sign(PostKind.Comment, board, Probe.Unrelated(), null, scene.Question.PostId, now);
        var revision = scene.Answerer.Agent.SignRevision(board, "Corrected " + Probe.Unrelated(), scene.Answer2.PostId, scene.Answer2.Digest, now);
        var flag = JsonSerializer.Serialize(new { kind = "spam", rationale = "this reads as spam" });

        async Task<Reply> Post(SceneActor actor, string token, byte[] wire) => Of(await Probe.SendViaAsync(forum, null, actor, wire, token, Ct));

        return
        [
            new("question", scene.Answerer, "posting", () => Post(scene.Answerer, answererToken, question), "409 curia/ingest/already-recorded"),
            new("answer", scene.Answerer, "posting", () => Post(scene.Answerer, answererToken, answer), "409 curia/ingest/already-recorded"),
            new("comment", scene.Answerer, "posting", () => Post(scene.Answerer, answererToken, comment), "409 curia/ingest/already-recorded"),
            new("revision", scene.Answerer, "posting", () => Post(scene.Answerer, answererToken, revision), "409 curia/ingest/already-revised or already-recorded"),
            new("acceptance", scene.Asker, "acceptance", () => JsonPostAsync(scene.Asker, $"http://localhost/v1/posts/{scene.Answer1.PostId}/accept", "{}", askerToken), "200 already_accepted"),
            new("flag", flagger, "flag", () => JsonPostAsync(flagger, $"http://localhost/v1/posts/{scene.Question.PostId}/flags", flag, flaggerToken), "refused as a second flag of a type"),
        ];
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every caller passes a constant statement; nothing is interpolated.")]
    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var admin = new NpgsqlConnection(connectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, admin);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private Task<long> EventsAsync() => CountAsync(forum.ConnectionString, "SELECT count(*) FROM events;");

    private Task<long> FlagRowsAsync() => CountAsync(forum.ConnectionString, "SELECT count(*) FROM flag_details;");

    private async Task<int> UsedAsync(WriteCase c) =>
        (await BudgetReadouts.OracleAsync(forum, "T1", c.Budget, c.Actor.Agent.AgentId)).Used;

    private static bool IsStoreUnavailable(Reply reply) =>
        FaultSweep.IsProblem(reply, PublishedStoreFaults.UnavailableStatus, PublishedStoreFaults.StoreUnavailableType);

    private static bool IsLogUnreadable(Reply reply) =>
        FaultSweep.IsProblem(reply, PublishedStoreFaults.UnavailableStatus, PublishedStoreFaults.LogUnreadableType);

    private async Task<Reply> ArmedAsync(EventStoreFault fault, WriteCase c, Func<Exception>? exception = null)
    {
        forum.Store.Arm(fault, exception);
        try
        {
            return await c.Send();
        }
        finally
        {
            forum.Store.Disarm();
        }
    }

    /// <summary>
    /// ULG20. The append throws and commits nothing: <c>503</c> <c>curia/store/unavailable</c>; the log is unchanged and the budget unspent; the same agent's next
    /// write, still armed, is refused by the fault again and not by <c>agent-in-flight</c> (the hold was released); disarmed, the act is recorded once. Counted on
    /// <c>events</c> alone: a failed flag leaves a <c>flag_details</c> row (D97, ULG41).
    /// </summary>
    [Fact]
    public async Task R8_82_ULG20_AnAppendThatThrowsIs503StoreUnavailableAndRecordsNothingAndReleasesTheHold()
    {
        var problems = new List<string>();
        foreach (var c in await CasesAsync())
        {
            var events = await EventsAsync();
            var used = await UsedAsync(c);

            var first = await ArmedAsync(EventStoreFault.ThrowOnAppend, c);
            if (!IsStoreUnavailable(first)) problems.Add($"{c.Name}: expected 503 {PublishedStoreFaults.StoreUnavailableType}, actual {first.Says}");

            var again = await ArmedAsync(EventStoreFault.ThrowOnAppend, c);
            if (!IsStoreUnavailable(again)) problems.Add($"{c.Name}: the next write, still armed, expected 503 {PublishedStoreFaults.StoreUnavailableType}, actual {again.Says}");

            var stored = await EventsAsync() - events;
            if (stored != 0) problems.Add($"{c.Name}: {stored} events were stored by a refused append");
            var spent = await UsedAsync(c) - used;
            if (spent != 0) problems.Add($"{c.Name}: the {c.Budget} budget used moved by {spent}");

            var recorded = await c.Send();
            if (recorded.Status is not (200 or 201)) problems.Add($"{c.Name}: disarmed, expected the act recorded, actual {recorded.Says}");
            var after = await EventsAsync() - events;
            if (after != 1) problems.Add($"{c.Name}: disarmed, {after} events were stored, not one");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} problems:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// ULG21. The append commits and then throws: <c>503</c> <c>curia/store/unavailable</c>; exactly one event is stored; the repeat of the same bytes is the
    /// refusal its kind has and records nothing; <c>used</c> is 1. Each step is recorded, so the red text shows what the repeat answered on the seam tree too.
    /// </summary>
    [Fact]
    public async Task R8_82_ULG21_AnAppendThatCommitsAndThenThrowsIs503AndAnyRepeatRecordsNothingTwice()
    {
        var problems = new List<string>();
        foreach (var c in await CasesAsync())
        {
            var events = await EventsAsync();
            var usedBefore = await UsedAsync(c);
            var first = await ArmedAsync(EventStoreFault.AppendThenThrow, c);
            if (!IsStoreUnavailable(first)) problems.Add($"{c.Name}: expected 503 {PublishedStoreFaults.StoreUnavailableType}, actual {first.Says}");

            var stored = await EventsAsync() - events;
            if (stored != 1) problems.Add($"{c.Name}: {stored} events were stored, not one");
            if (await UsedAsync(c) - usedBefore != 1) problems.Add($"{c.Name}: the {c.Budget} budget used moved by {await UsedAsync(c) - usedBefore}, not 1");

            var repeat = await c.Send();
            var repeated = await EventsAsync() - events;
            if (repeated != 1) problems.Add($"{c.Name}: the repeat left {repeated} events, not one (repeat answered {repeat.Says})");
            var shape = c.RepeatShape switch
            {
                "200 already_accepted" => repeat.Status == 200 && repeat.Body.Contains("\"already_accepted\":true", StringComparison.Ordinal),
                "409 curia/ingest/already-recorded" => repeat.Status == 409 && repeat.Type == "curia/ingest/already-recorded",
                "409 curia/ingest/already-revised or already-recorded" => repeat.Status == 409 && repeat.Type is "curia/ingest/already-revised" or "curia/ingest/already-recorded",
                _ => repeat.Status is >= 400 and < 500,
            };
            if (!shape) problems.Add($"{c.Name}: the repeat expected {c.RepeatShape}, actual {repeat.Says}");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} problems:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// ULG22, a control (Task 0's M5: no read after the commit, measured live for all six kinds and here for each). The store reads healthily until the first
    /// append after arming commits and fails every read after it; each of the six acts is still a <c>2xx</c> composed from what the act holds. Held by FULG13.
    /// </summary>
    [Fact]
    public async Task R8_82_ULG22_ACommittedActIsAnsweredFromWhatItHoldsWhenEveryReadAfterItFails()
    {
        var problems = new List<string>();
        foreach (var c in await CasesAsync())
        {
            var events = await EventsAsync();
            var reply = await ArmedAsync(EventStoreFault.FailReadsAfterFirstAppend, c);
            if (reply.Status is not (200 or 201)) problems.Add($"{c.Name}: expected 2xx, actual {reply.Says}");
            if (await EventsAsync() - events != 1) problems.Add($"{c.Name}: the act was not recorded once");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} problems:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// ULG23. The log is unreadable before the append: <c>503</c> <c>curia/log/unreadable</c> (a read of the log, so nothing was recorded), nothing stored,
    /// <c>used</c> unchanged.
    /// </summary>
    [Fact]
    public async Task R8_82_ULG23_AFaultOfTheLogReadBeforeTheAppendIs503LogUnreadableAndRecordsNothing()
    {
        var problems = new List<string>();
        foreach (var c in await CasesAsync())
        {
            var events = await EventsAsync();
            var used = await UsedAsync(c);

            var reply = await ArmedAsync(EventStoreFault.ThrowUnreadable, c);
            if (!IsLogUnreadable(reply)) problems.Add($"{c.Name}: expected 503 {PublishedStoreFaults.LogUnreadableType}, actual {reply.Says}");
            if (await EventsAsync() != events) problems.Add($"{c.Name}: events were stored by a request that could not read the log");
            if (await UsedAsync(c) != used) problems.Add($"{c.Name}: the {c.Budget} budget used moved");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} problems:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// ULG24, a control (Task 0's M11, held by FULG12). The revision gate and the flag raiser's gate are released by the fault: the next revision of the same
    /// <c>prev</c> is not <c>revision-in-flight</c>, and the next flag is not refused for the first; disarmed, each act is recorded.
    /// </summary>
    [Fact]
    public async Task R8_82_ULG24_TheRevisionGateAndTheFlagRaisersGateAreReleasedByTheFault()
    {
        var problems = new List<string>();
        foreach (var c in (await CasesAsync()).Where(c => c.Name is "revision" or "flag"))
        {
            var events = await EventsAsync();
            _ = await ArmedAsync(EventStoreFault.ThrowOnAppend, c);
            var again = await ArmedAsync(EventStoreFault.ThrowOnAppend, c);
            if (again.Type == "curia/ingest/revision-in-flight") problems.Add($"{c.Name}: the next attempt was refused as revision-in-flight: the gate was not released");
            if (again.Status == 409 && again.Type.EndsWith("in-flight", StringComparison.Ordinal)) problems.Add($"{c.Name}: the next attempt was refused as {again.Type}");

            var recorded = await c.Send();
            if (recorded.Status is not (200 or 201)) problems.Add($"{c.Name}: disarmed, expected the act recorded, actual {recorded.Says}");
            if (await EventsAsync() - events != 1) problems.Add($"{c.Name}: disarmed, the act was not recorded once");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} problems:\n" + string.Join('\n', problems));
    }

    /// <summary>
    /// ULG41 (D97). The orphan is inert and pinned: the flag's append throws; <c>events</c> is unchanged and <c>flag_details</c> gains one row (R10.62 writes the
    /// private row first; the pin is flipped by whichever option closes D97); the flag listing for the raiser shows nothing for it; the flag budget's <c>used</c>
    /// is unchanged; disarmed, the repeat is recorded once.
    /// </summary>
    [Fact]
    public async Task R8_82_ULG41_AFlagWhoseAppendThrowsLeavesOneInertPrivateRowAndIsRecordedOnceOnItsRepeat()
    {
        var flag = (await CasesAsync()).Single(c => c.Name == "flag");
        var events = await EventsAsync();
        var rows = await FlagRowsAsync();

        var first = await ArmedAsync(EventStoreFault.ThrowOnAppend, flag);

        var problems = new List<string>();
        if (!IsStoreUnavailable(first)) problems.Add($"expected 503 {PublishedStoreFaults.StoreUnavailableType}, actual {first.Says}");
        if (await EventsAsync() != events) problems.Add("the events table changed under a refused append");
        if (await FlagRowsAsync() - rows != 1) problems.Add($"D97's pin: flag_details gained {await FlagRowsAsync() - rows} rows, not one");
        if (await UsedAsync(flag) != 0) problems.Add($"the flag budget used moved to {await UsedAsync(flag)}");
        var listing = await FaultActs.GetAsync(forum, flag.Actor, "/v1/flags", Ct);
        if (listing.Status != 200 || listing.Body.Contains("post_id", StringComparison.Ordinal))
            problems.Add($"the raiser's flag listing names a flag for the failed act: {listing.Says}");

        var repeat = await flag.Send();
        if (repeat.Status != (int)HttpStatusCode.Created) problems.Add($"disarmed, expected the flag recorded, actual {repeat.Says}");
        if (await EventsAsync() - events != 1) problems.Add("the repeat did not record exactly one event");

        Assert.True(problems.Count == 0, $"{problems.Count} problems:\n" + string.Join('\n', problems));
    }
}

/// <summary>A GET as an enrolled agent that never throws for the host's failure.</summary>
internal static class FaultActs
{
    internal static async Task<Reply> GetAsync(ForumFixture forum, SceneActor actor, string path, CancellationToken ct, string? token = null)
    {
        token ??= await actor.Dpop.GetTokenAsync(forum.Client, Probe.TokenEndpoint, forum.Now, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", actor.Dpop.Proof("GET", "http://localhost" + path.Split('?')[0], forum.Now, token, nonce: null));
        return await Reply.SendAsync(forum.Client, request, ct);
    }
}
