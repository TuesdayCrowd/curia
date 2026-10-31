using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G51 (R8.81) at the surface an agent uses: probes PIL1 to PIL10 (the D92 PR). A post the log holds is answered <c>201</c> whatever the vector index then
/// does, and the receipt says whether its vector was stored. The oracles are the events table and <c>post_embeddings</c> counted from the fixture's own
/// connection string (<see cref="IndexLag"/>), never a response's claim about itself; the values, the bound and the event name are the entry's
/// (<see cref="PublishedIndexLag"/>). The faults are the host's own vector index decorated by <see cref="FailingVectorIndex"/>: a returned error, an exception,
/// a hold inside the upsert. Each fact states what it saw as one sentence and compares it with the sentence the entry gives, so a red text says the whole of what
/// differed. PIL6 runs on a host whose clock has timers (ruling 15), the others on the fixture's.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CommittedPostEndpointTests(AgentWriteGateForumFixture forum) : IClassFixture<AgentWriteGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Indexed => PublishedIndexLag.Values().Indexed;

    private static string Pending => PublishedIndexLag.Values().Pending;

    private static string NotApplicable => PublishedIndexLag.Values().NotApplicable;

    public static TheoryData<string> DiscussionKinds => [.. IndexLag.Kinds];

    /// <summary>
    /// PIL1. The index fails (a returned error) after the append, for one question, one answer, one comment and one revision: each is <c>201</c> with the
    /// receipt's <c>vector_index</c> <c>pending</c>, the post is in the events table once, and the receipt's <c>budget.used</c> is the count of the agent's
    /// posts the table holds.
    /// </summary>
    [Theory]
    [MemberData(nameof(DiscussionKinds))]
    public async Task R8_81_PIL1_AFailedResultAfterTheCommitIsStoredAndPending(string kind)
    {
        forum.ResetSeams();
        var fresh = await IndexLag.FreshAsync(forum, kind, Ct);
        var id = fresh.Actor.Agent.AgentId;
        var before = await BudgetOracle.PostsInWindowAsync(forum, id);

        forum.Index.FailUpserts = true;
        var sent = await Probe.SendAsync(forum, fresh.Actor, fresh.Wire, null, Ct);
        forum.Index.FailUpserts = false;
        var after = await BudgetOracle.PostsInWindowAsync(forum, id);

        Said(
            $"{kind}: 201 {Pending}; stored 1; budget used {after}",
            $"{kind}: {IndexLag.Says(sent)}; stored {after - before}; budget used {IndexLag.BudgetMember(sent.Body, "used")}");
    }

    /// <summary>
    /// PIL2. The index throws where a result would be returned (an <c>NpgsqlException</c>, a <c>TimeoutException</c>, an <c>InvalidOperationException</c>): each
    /// is <c>201</c> and <c>pending</c> with the post stored once, never an unhandled exception out of a request whose post the log holds.
    /// </summary>
    [Theory]
    [InlineData("NpgsqlException")]
    [InlineData("TimeoutException")]
    [InlineData("InvalidOperationException")]
    public async Task R8_81_PIL2_AnExceptionAfterTheCommitIsStoredAndPending(string exception)
    {
        forum.ResetSeams();
        var fresh = await IndexLag.FreshAsync(forum, "question", Ct);
        var id = fresh.Actor.Agent.AgentId;
        var before = await BudgetOracle.PostsInWindowAsync(forum, id);

        forum.Index.ThrowWith = () => exception switch
        {
            "NpgsqlException" => new NpgsqlException("The vector index failed with an exception that is not a returned result"),
            "TimeoutException" => new TimeoutException("The vector index timed out"),
            _ => new InvalidOperationException("The vector index failed with an exception"),
        };
        var sent = await Probe.SendAsync(forum, fresh.Actor, fresh.Wire, null, Ct);
        forum.Index.ThrowWith = null;
        var after = await BudgetOracle.PostsInWindowAsync(forum, id);

        Said(
            $"{exception}: 201 {Pending}; stored 1",
            $"{exception}: {IndexLag.Says(sent)}; stored {after - before}");
    }

    /// <summary>PIL3. A healthy index: each discussion kind is <c>201</c> and <c>indexed</c>, and the vector is in <c>post_embeddings</c> by the digest the post was stored under.</summary>
    [Theory]
    [MemberData(nameof(DiscussionKinds))]
    public async Task R8_81_PIL3_AHealthyIndexIsIndexedAndTheRowIsThere(string kind)
    {
        forum.ResetSeams();
        var fresh = await IndexLag.FreshAsync(forum, kind, Ct);

        var sent = await Probe.SendAsync(forum, fresh.Actor, fresh.Wire, null, Ct);
        var digest = (await IndexLag.DigestsOfAsync(forum, fresh.Actor.Agent.AgentId))[^1];

        Said(
            $"{kind}: 201 {Indexed}; 1 row",
            $"{kind}: {IndexLag.Says(sent)}; {await IndexLag.RowsAsync(forum, digest)} row");
    }

    /// <summary>PIL4. A vote, a verification and a question whose text has no letter or digit (D24's shape) are <c>201</c> and <c>not_applicable</c>, and no row is stored for them.</summary>
    [Theory]
    [InlineData("vote")]
    [InlineData("verification")]
    [InlineData("no-features")]
    public async Task R8_81_PIL4_ANotApplicablePostIsSaidSoAndStoresNoRow(string what)
    {
        forum.ResetSeams();
        var board = Probe.Board();
        SceneActor actor;
        byte[] wire;
        if (what == "no-features")
        {
            actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-nf", Ct);
            wire = actor.Agent.SignQuestion(board, "!!!", "???", forum.Now, tags: []);
        }
        else
        {
            actor = await BudgetBursts.T2Async(forum, board, Ct);
            var other = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
            var result = await BudgetBursts.StoreAsync(forum, ForumAgent.Create("https://agents.example/result-" + Probe.Suffix(), "result").SignAnswer(board, Probe.Unrelated(), other.PostId, forum.Now), Ct);
            wire = what == "vote" ? actor.Agent.SignVote(board, result.Digest, forum.Now) : actor.Agent.SignVerification(board, result.Digest, "reproduced", forum.Now);
        }

        var sent = await Probe.SendAsync(forum, actor, wire, null, Ct);
        var digest = (await IndexLag.DigestsOfAsync(forum, actor.Agent.AgentId))[^1];

        Said(
            $"{what}: 201 {NotApplicable}; 0 rows",
            $"{what}: {IndexLag.Says(sent)}; {await IndexLag.RowsAsync(forum, digest)} rows");
    }

    /// <summary>
    /// PIL5. The caller's disconnection does not reach the step: the index holds inside the upsert, the client's request is cancelled after the append, the hold is
    /// released, and the upsert completes without having seen a cancellation, so the row exists.
    /// </summary>
    [Fact]
    public async Task R8_81_PIL5_TheCallersDisconnectionDoesNotReachTheStep()
    {
        forum.ResetSeams();
        var fresh = await IndexLag.FreshAsync(forum, "question", Ct);
        var id = fresh.Actor.Agent.AgentId;

        forum.Index.HoldUpserts();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var send = Task.Run(() => Probe.SendAsync(forum, fresh.Actor, fresh.Wire, null, cts.Token), Ct);
        var held = await forum.Index.WaitForHeldAsync(TimeSpan.FromSeconds(15));
        await cts.CancelAsync();
        await Task.Delay(400, Ct);
        forum.Index.ReleaseUpserts();
        await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(10), Ct));

        var digest = (await IndexLag.DigestsOfAsync(forum, id)).SingleOrDefault();
        var rows = 0L;
        for (var i = 0; i < 60 && digest is not null && rows == 0; i++)
        {
            rows = await IndexLag.RowsAsync(forum, digest);
            if (rows == 0) await Task.Delay(50, Ct);
        }

        Said(
            "the post stored (1); the index reached and held; the upsert saw 0 cancellations; 1 row",
            $"the post {(digest is null ? "not stored (0)" : "stored (1)")}; the index {(held ? "reached and held" : "never reached")}; the upsert saw {forum.Index.CancelledWhileHeld} cancellations; {rows} row");
    }

    /// <summary>
    /// PIL6. The bound, on a clock that has timers (ruling 15): a host whose <see cref="TimeProvider"/> is a <see cref="TimerManualTimeProvider"/>, an index that
    /// never returns. At 250 ms of real time the request has not completed and a timer is armed beyond those the host held before the send; the clock is advanced by the
    /// published bound and the <c>201</c> <c>pending</c> arrives within 1.5 s of real time of the send, which a bound on the system clock (2 s of real time) does not.
    /// </summary>
    [Fact]
    public async Task R8_81_PIL6_TheBoundIsMadeFromTheClockPortAndEndsAtItsAdvance()
    {
        var bound = PublishedIndexLag.StepBound();
        var clock = TimerClockHost.At(forum);
        await using var host = TimerClockHost.Of(forum, clock);
        using var client = host.CreateClient();
        var index = AgentWriteGateForumFixture.IndexOf(host.Services);
        var fresh = await IndexLag.FreshAsync(forum, "question", Ct);

        index.HoldUpserts();
        var armedBefore = clock.Armed;
        var watch = Stopwatch.StartNew();
#pragma warning disable CA2025 // The request is awaited on every path below (or abandoned for five seconds after the hold is released) before the client and the host are disposed.
        var send = Task.Run(() => Probe.SendViaAsync(forum, client, fresh.Actor, fresh.Wire, null, Ct), Ct);
#pragma warning restore CA2025
        var held = await index.WaitForHeldAsync(TimeSpan.FromSeconds(15));
        var armed = clock.Armed - armedBefore;
        await Task.Delay(250, Ct);
        var completedBeforeAdvance = send.IsCompleted;

        clock.Advance(bound);
        var answered = await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(10), Ct)) == send;
        var elapsed = watch.Elapsed;
        index.ReleaseUpserts();
        var sent = answered ? await send : null;
        if (!answered) await Task.WhenAny(send, Task.Delay(TimeSpan.FromSeconds(5), Ct));

        var armedWord = armed >= 1 ? "a timer armed (at least 1)" : "no timer armed";
        Said(
            $"the index held; a timer armed (at least 1); not complete at 250 ms; after the bound is advanced 201 {Pending} within 1.5 s of the send",
            $"the index {(held ? "held" : "never reached")}; {armedWord}; {(completedBeforeAdvance ? "complete before the advance" : "not complete at 250 ms")}; after the bound is advanced "
            + (sent is null ? "no response within 10 s" : $"{IndexLag.Says(sent)} {(elapsed < TimeSpan.FromSeconds(1.5) ? "within 1.5 s of the send" : $"after {elapsed.TotalSeconds:0.0} s of the send")}"));
    }

    /// <summary>
    /// PIL7. A repeat of a pending post. The test signs once and keeps the body: the first send is <c>201</c> <c>pending</c>; the same bytes again, with a fresh
    /// DPoP proof, are refused <c>409</c> <c>already-recorded</c> naming the first post, and the table holds one. A second leg re-signs the same text with a new nonce (what
    /// the CLI's retry does, each <c>curia ask</c> building a fresh nonce) and it is stored as a second post, the first still the only one pending.
    /// </summary>
    [Fact]
    public async Task R8_81_PIL7_ARepeatOfAPendingPostIsRefusedAndAReSignedOneIsASecondPost()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-repeat", Ct);
        var id = actor.Agent.AgentId;
        var (body, title) = (Probe.Unrelated(), "About " + Probe.Unrelated());
        var wire = actor.Agent.SignQuestion(board, body, title, forum.Now);

        forum.Index.FailUpserts = true;
        var first = await Probe.SendAsync(forum, actor, wire, null, Ct);
        forum.Index.FailUpserts = false;
        var firstId = (await IndexLag.PostIdsOfAsync(forum, id)).FirstOrDefault() ?? "none";
        var repeat = await Probe.SendAsync(forum, actor, wire, null, Ct);
        var storedAfterRepeat = (await IndexLag.DigestsOfAsync(forum, id)).Length;
        var reSigned = await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, body, title, forum.Now), null, Ct);
        var storedAfterReSign = (await IndexLag.DigestsOfAsync(forum, id)).Length;
        using var search = await IndexLag.SearchAsync(forum.Client, $"board={board}", Ct);

        Said(
            $"first 201 {Pending}; repeat 409 curia/ingest/already-recorded post={firstId}; stored 1; re-signed 201 {Indexed} (stored 2); pending 1",
            $"first {IndexLag.Says(first)}; repeat {repeat.Says}{(repeat.Detail is { } detail ? " " + detail : string.Empty)}; stored {storedAfterRepeat}; re-signed {IndexLag.Says(reSigned)} (stored {storedAfterReSign}); pending {IndexLag.Pending(search)}");
    }

    /// <summary>
    /// PIL8. Before the commit nothing moved (a control): the near-duplicate check's read failing is <c>503</c> with nothing stored and no budget spent, a screening
    /// rejection is <c>422</c> with nothing stored, and the positive in the same fact, a healthy question, is <c>201</c> and stored once.
    /// </summary>
    [Fact]
    public async Task R8_81_PIL8_TheRefusalsBeforeThePersistAreAsTheyWere()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-before", Ct);
        var id = actor.Agent.AgentId;

        // The near-duplicate check reads the index only when the board already holds a question to compare with, so one is stored first.
        await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), null, Ct);
        var seeded = await BudgetOracle.PostsInWindowAsync(forum, id);

        forum.Index.FailNearest = true;
        var nearest = await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), null, Ct);
        forum.Index.FailNearest = false;
        var afterNearest = await BudgetOracle.PostsInWindowAsync(forum, id);

        var screened = await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, "the key is AKIAIOSFODNN7EXAMPLE and it is live " + Probe.Unrelated(), "A key " + Probe.Unrelated(), forum.Now), null, Ct);
        var afterScreen = await BudgetOracle.PostsInWindowAsync(forum, id);

        var healthy = await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), null, Ct);
        var afterHealthy = await BudgetOracle.PostsInWindowAsync(forum, id);

        Said(
            "the near-duplicate read failing: 503, stored 0; a screening rejection: 422, stored 0; a healthy question: 201, stored 1",
            $"the near-duplicate read failing: {nearest.Status}, stored {afterNearest - seeded}; a screening rejection: {screened.Status}, stored {afterScreen - afterNearest}; a healthy question: {healthy.Status}, stored {afterHealthy - afterScreen}");
    }

    /// <summary>
    /// PIL9. The log line: one <c>VectorIndexPending</c> event per pending post, naming the post and the error's type, and no captured event holds the text the post
    /// carried (a canary in its title and body) or, since the post is stored, any other of its words.
    /// </summary>
    [Fact]
    public async Task R8_81_PIL9_ThePendingPostIsLoggedOnceByIdAndTypeAndNeverByText()
    {
        using var logged = new LogCapture();
        var clock = TimerClockHost.At(forum);
        await using var host = TimerClockHost.Of(forum, clock, logged);
        using var client = host.CreateClient();
        var index = AgentWriteGateForumFixture.IndexOf(host.Services);
        var canary = "CANARY" + Guid.NewGuid().ToString("N");
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-log", Ct);
        var wire = actor.Agent.SignQuestion(board, canary + " " + Probe.Unrelated(), canary + " title", forum.Now);

        index.FailUpserts = true;
        var mark = logged.Mark();
        var sent = await Probe.SendViaAsync(forum, client, actor, wire, null, Ct);
        index.FailUpserts = false;
        var postId = (await IndexLag.PostIdsOfAsync(forum, actor.Agent.AgentId)).FirstOrDefault() ?? "none";

        var events = logged.Since(mark);
        var pendingEvents = events.Where(e => e.Name == PublishedIndexLag.PendingEvent()).ToArray();
        var named = pendingEvents.Count(e => e.Message.Contains(postId, StringComparison.Ordinal) && e.Message.Contains("curia/retrieval/index-unavailable", StringComparison.Ordinal));
        var holdingText = events.Count(e => e.Message.Contains(canary, StringComparison.Ordinal) || e.Values.Any(v => v.Value?.ToString()?.Contains(canary, StringComparison.Ordinal) == true));

        Said(
            $"201 {Pending}; 1 {PublishedIndexLag.PendingEvent()} event, naming the post id and the error type; 0 events holding the text",
            $"{IndexLag.Says(sent)}; {pendingEvents.Length} {PublishedIndexLag.PendingEvent()} events, {(pendingEvents.Length == named && named > 0 ? "naming" : "not naming")} the post id and the error type; {holdingText} events holding the text");
    }

    /// <summary>
    /// PIL10 (BRB60 flipped). A committed post whose index fails releases the gate and counts: <c>201</c> <c>pending</c> with the post stored once, the gate entered once
    /// and nothing held, the agent's next request <c>201</c>, and a request beyond the limit refused as the budget. No wall time is read.
    /// </summary>
    [Fact]
    public async Task R7_27_PIL10_ACommittedPostWhoseIndexFailsStillReleasesTheGateAndCounts()
    {
        forum.ResetSeams();
        var limit = BudgetBursts.PostLimit("T0");
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-gate", Ct);
        var id = actor.Agent.AgentId;

        forum.Index.FailUpserts = true;
        var failed = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        forum.Index.FailUpserts = false;
        var stored = await BudgetOracle.PostsInWindowAsync(forum, id);
        var held = forum.Gate.HeldCount;
        var entries = forum.Gate.Entered.Count(e => e == id);
        var next = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var third = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var fourth = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var (posting, _) = PublishedBudgetGate.BudgetReasons();

        Said(
            $"the failed index answered 201 {Pending} with the post stored (1), the gate entered once and nothing held; the next request 201 (and the third 201); the request beyond the limit of {limit} 403 {posting}",
            $"the failed index answered {IndexLag.Says(failed)} with the post {(stored == 1 ? "stored (1)" : $"counted {stored}")}, the gate entered {AgentWriteGateEndpointTests.Times(entries)} and {(held == 0 ? "nothing held" : held + " held")}; "
            + $"the next request {BudgetBursts.Kind(next)} (and the third {BudgetBursts.Kind(third)}); the request beyond the limit of {limit} {BudgetBursts.Kind(fourth)}");
    }
}
