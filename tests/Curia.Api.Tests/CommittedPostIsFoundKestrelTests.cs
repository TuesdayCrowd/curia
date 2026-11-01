using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Curia.Tests.Shared;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// CPF1 to CPF8, CPF19, CPF19b, CPF20 and CPF31 (errata G54, R8.83, R9.53): the defect followed across the seam, on the production Forum over a real Postgres and the real
/// vector index. The oracle is a SQL anti-join over the events table and <c>post_embeddings</c> (<see cref="CheckHost.AntiJoinAsync"/>): the kind set is written there from §9
/// and read as <c>payload-&gt;&gt;'kind'</c>, and the check's own count, the backlog's, is never the oracle (trap 97). Time is driven by the probe, one tick per fake second, with
/// the catch-up's loop removed from the host and asserted absent (ruling 14). Every figure is read from the errata (<see cref="PublishedCheckWords"/>). A probe is
/// written in its failing form and is red on the seam tree by assertion; the text each prints is recorded next to its prediction in the Task 0c report.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CommittedPostIsFoundKestrelTests(CommittedPostFixture forum) : IClassFixture<CommittedPostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual) => Probe.Said(expected, actual);

    private static string Instant(DateTimeOffset at) => at.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static string Secs(TimeSpan span) => span.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static readonly string Unavailable = PublishedStoreFaults.StoreUnavailableType;

    /// <summary>A fresh agent that may ask, its signed question and its token (minted before any fault is armed, so a fault arms only the append).</summary>
    private async Task<(SceneActor Actor, byte[] Wire, string Token)> AskerAsync(CheckHost host, string? body = null)
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "cpf", Ct);
        var wire = actor.Agent.SignQuestion(Probe.Board(), body ?? Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now);
        var token = await actor.Dpop.GetTokenAsync(host.Client, Probe.TokenEndpoint, forum.Now, Ct);
        return (actor, wire, token);
    }

    private async Task<Sent> SendUnderAsync(CheckHost host, EventStoreFault fault, SceneActor actor, byte[] wire, string token)
    {
        host.Store.Arm(fault);
        try
        {
            return await Probe.SendViaAsync(forum, host.Client, actor, wire, token, Ct);
        }
        finally
        {
            host.Store.Disarm();
        }
    }

    private static string Answered(Sent sent) => sent.Threw is not null ? sent.Says : $"{sent.Status} {sent.Type}";

    /// <summary>
    /// CPF1. The defect across the seam. An <c>ask</c> whose append commits and then throws is answered <c>503 curia/store/unavailable</c>; the events table holds the
    /// post; the SQL anti-join holds one digest; the clock is advanced by ticks (one second each, driven by the probe) for six minutes, and the anti-join is zero within
    /// one tick, with no restart.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF1_ACommittedPostAnsweredWithAFaultIsInTheVectorIndexWithinOneTick()
    {
        await using var host = forum.NewHost();
        var loopAbsent = host.LoopIsAbsent;
        var (actor, wire, token) = await AskerAsync(host);
        var sent = await SendUnderAsync(host, EventStoreFault.AppendThenThrow, actor, wire, token);
        var author = actor.Agent.AgentId;
        var events = await host.EventsOfAsync(author, Ct);
        var before = await host.AntiJoinAsync(author, Ct);

        var zeroAt = -1;
        for (var second = 1; second <= 360 && zeroAt < 0; second++)
        {
            await host.TicksAsync(1, Ct);
            if (await host.AntiJoinAsync(author, Ct) == 0) zeroAt = second;
        }

        var after = await host.AntiJoinAsync(author, Ct);
        var state = await host.StateAsync(Ct);
        Said(
            $"the hosted loop is absent; the ask is answered 503 {Unavailable}; the events table holds 1 post; the anti-join holds 1 digest; the anti-join is 0 within 1 tick",
            $"the hosted loop is {(loopAbsent ? "absent" : "present")}; the ask is answered {Answered(sent)}; the events table holds {events} post{(events == 1 ? string.Empty : "s")}; the anti-join holds {before} digest{(before == 1 ? string.Empty : "s")}; " +
            (zeroAt == 1 ? "the anti-join is 0 within 1 tick" : zeroAt > 1 ? $"the anti-join is 0 after {zeroAt} ticks" : $"after 360 s of fake time the anti-join still holds {after} digest{(after == 1 ? string.Empty : "s")} ({state.Says})"));
    }

    /// <summary>
    /// CPF2. Owed at once. Directly after the <c>503</c>, with the clock not advanced and no tick run, a search states <c>vector_check_due_at</c> as an instant no later than
    /// the next tick (one second on), and <c>vector_checked_at</c> as an instant.
    /// </summary>
    [Fact]
    public async Task R9_53_CPF2_ACheckIsOwedTheInstantTheFaultIsAnswered()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token) = await AskerAsync(host);
        await SendUnderAsync(host, EventStoreFault.AppendThenThrow, actor, wire, token);
        var now = forum.Now;
        var state = await host.StateAsync(Ct);

        var expected = $"vector_check_due_at is an instant no earlier than {Instant(now)} and no later than {Instant(now + TimeSpan.FromSeconds(1))}";
        var actual = state.DueAt is { } due && due >= now && due <= now + TimeSpan.FromSeconds(1)
            ? expected
            : state.Owed ? $"vector_check_due_at is {state.Due}, outside the next tick of {Instant(now)}" : "vector_check_due_at is " + (state.DueRaw == "null" ? "null while a post committed under a 503 is unindexed" : state.DueRaw);
        Said(expected, actual);
    }

    /// <summary>
    /// CPF3. The first check is the next tick. One tick later the anti-join is empty, <c>vector_pending</c> is 0 (stored by the same pass), the obligation is still owed
    /// (the late-commit window is open) and <c>vector_checked_at</c> is the instant that tick began.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF3_TheFirstCheckIsTheNextTickAndTheObligationStaysOwed()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token) = await AskerAsync(host);
        await SendUnderAsync(host, EventStoreFault.AppendThenThrow, actor, wire, token);
        await host.TicksAsync(1, Ct);
        var tickAt = forum.Now;
        var antiJoin = await host.AntiJoinAsync(actor.Agent.AgentId, Ct);
        var state = await host.StateAsync(Ct);

        Said(
            $"after one tick the anti-join is 0, vector_pending is 0, a check is still owed and vector_checked_at is {Instant(tickAt)}",
            $"after one tick the anti-join is {antiJoin}, vector_pending is {state.Pending}, {(state.Owed ? "a check is still owed" : "no check is owed")} and vector_checked_at is {state.Checked}");
    }

    private async Task<string> LateCommitAsync(int releaseAt)
    {
        var settle = (int)PublishedCheckWords.SweepSettle().TotalSeconds;
        await using var host = forum.NewHost();
        var (actor, wire, token) = await AskerAsync(host);
        var author = actor.Agent.AgentId;
        var sent = await SendUnderAsync(host, EventStoreFault.ThrowThenCommitWhenReleased, actor, wire, token);
        var existedAtOnce = await host.EventsOfAsync(author, Ct);

        var owedMidway = false;
        for (var second = 1; second <= settle + 2; second++)
        {
            if (second == releaseAt)
            {
                host.Store.ReleaseLateCommit();
                await host.Store.LateCommitted.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            }

            await host.TicksAsync(1, Ct);
            if (second == settle / 2) owedMidway = (await host.StateAsync(Ct)).Owed;
        }

        var antiJoin = await host.AntiJoinAsync(author, Ct);
        var state = await host.StateAsync(Ct);
        return $"released at +{releaseAt} s: answered {Answered(sent)} with {existedAtOnce} post{(existedAtOnce == 1 ? string.Empty : "s")} stored; a check owed at +{settle / 2} s: {(owedMidway ? "yes" : "no")}; at +{settle + 2} s the anti-join holds {antiJoin} and {(state.Owed ? "a check is owed" : "no check is owed")}";
    }

    /// <summary>
    /// CPF4. The late commit. PR 1's gated mode throws the timeout-classified fault at once and commits when released. The commit lands at the settle window less 40 s and
    /// at the settle window less 1 s (the measured edge: a 90 s stall at the 30 s default lands at +60.0 s), both read from R8.83's words: the first check at +1 s finds
    /// nothing and the obligation stands, the repeat enrols and stores the post, the anti-join is zero, and the obligation clears only at the check that began at the
    /// settle window after the exit. A one-shot check would leave the post unindexed to the floor.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF4_ALateCommitIsFoundByTheRepeatBeforeTheObligationClears()
    {
        var settle = (int)PublishedCheckWords.SweepSettle().TotalSeconds;
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var release in new[] { settle - 40, settle - 1 })
        {
            expected.Add($"released at +{release} s: answered 503 {Unavailable} with 0 posts stored; a check owed at +{settle / 2} s: yes; at +{settle + 2} s the anti-join holds 0 and no check is owed");
            actual.Add(await LateCommitAsync(release));
        }

        Said(string.Join(" | ", expected), string.Join(" | ", actual));
    }

    /// <summary>
    /// CPF31. The hole, pinned (G54 non-property b). The commit is released at the settle window plus 15 s, after the obligation has cleared: the anti-join holds one digest,
    /// the search says no check is owed, and at the floor after the check that cleared the obligation began, the floor check enrols and stores the post. Read from the published
    /// figures, so a widened or narrowed window without the paper moves the probe and is red (FCPF32).
    /// </summary>
    [Fact]
    public async Task R8_83_CPF31_ACommitAfterTheWindowIsFoundOnlyByTheFloorAndTheLineSaysNothingIsOwedMeanwhile()
    {
        var settle = (int)PublishedCheckWords.SweepSettle().TotalSeconds;
        var floor = (int)PublishedCheckWords.SweepFloor().TotalSeconds;
        var release = settle + 15;
        await using var host = forum.NewHost();
        var (actor, wire, token) = await AskerAsync(host);
        var author = actor.Agent.AgentId;
        var exit = forum.Now;
        await SendUnderAsync(host, EventStoreFault.ThrowThenCommitWhenReleased, actor, wire, token);

        for (var second = 1; second <= release; second++)
        {
            if (second == release)
            {
                host.Store.ReleaseLateCommit();
                await host.Store.LateCommitted.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            }

            await host.TicksAsync(1, Ct);
        }

        var holeJoin = await host.AntiJoinAsync(author, Ct);
        var hole = await host.StateAsync(Ct);
        var clearedBy = hole.Checked;

        var atFloor = -1L;
        for (var second = release + 1; second <= settle + 1 + floor + 1; second++)
        {
            await host.TicksAsync(1, Ct);
            atFloor = await host.AntiJoinAsync(author, Ct);
        }

        var floorState = await host.StateAsync(Ct);
        var expectedClearing = Instant(exit + TimeSpan.FromSeconds(settle + 1));
        Said(
            $"after the window: the anti-join holds 1, no check is owed, last compared at {expectedClearing}; at the floor after that check the anti-join holds 0",
            $"after the window: the anti-join holds {holeJoin}, {(hole.Owed ? "a check is owed" : "no check is owed")}, last compared at {clearedBy}; at the floor after that check the anti-join holds {atFloor}{(atFloor == 0 ? string.Empty : " (" + floorState.Says + ")")}");
    }

    /// <summary>
    /// CPF5. The cancellation door (ruling 14). The append commits and then waits for the request's token, which the client cancels: nothing is answered, the post is
    /// committed, the next search states the obligation, and the post is found within a tick. The live variant (the proxy and a killed client) is Task 8's L7(b); this
    /// proves the mark, that proves the cancellation reaches <c>PersistAsync</c>.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF5_ARequestAbortedAfterItsCommitOwesACheck()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token) = await AskerAsync(host);
        var author = actor.Agent.AgentId;
        host.Store.Arm(EventStoreFault.CommitThenCancel);
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var send = Probe.SendViaAsync(forum, host.Client, actor, wire, token, abort.Token);
        await host.Store.CommittedAndWaiting.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await abort.CancelAsync();
        var sent = await send;
        host.Store.Disarm();

        CheckState state;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        do
        {
            state = await host.StateAsync(Ct);
            if (state.Owed) break;
            await Task.Delay(50, Ct);
        }
        while (DateTime.UtcNow < deadline);

        var events = await host.EventsOfAsync(author, Ct);
        await host.TicksAsync(1, Ct);
        var antiJoin = await host.AntiJoinAsync(author, Ct);
        Said(
            "no status was answered; the post is committed; the next search states an instant for the obligation; within one tick the anti-join is 0",
            $"{(sent.Threw == "cancelled" ? "no status was answered" : "the status was " + Answered(sent))}; the post is {(events == 1 ? "committed" : "not committed")}; the next search states {(state.Owed ? "an instant" : "no instant")} for the obligation" +
            (state.Owed ? string.Empty : " (vector_check_due_at is null after a committed post whose request was aborted)") +
            $"; within one tick the anti-join is {antiJoin}");
    }

    /// <summary>
    /// CPF6. Controls: what owes nothing. (a) a <c>log/unreadable</c> raised before PERSIST, (b) a refusal that is a failed result from the append and no exception, (c) a
    /// vote whose append throws (the index holds no vote) and (d) a <c>201</c> each leave the next search saying no check is owed. Red under a mark on every fault (FCPF8);
    /// its positive is CPF2.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF6_ARefusalBeforePersistAFailedResultAVoteAndASuccessOweNothing()
    {
        await using var host = forum.NewHost();
        var board = Probe.Board();
        var legs = new List<string>();

        async Task<string> OwedAsync() => (await host.StateAsync(Ct)).Owed ? "a check is owed" : "no check is owed";

        var (a, wireA, tokenA) = await AskerAsync(host);
        var sentA = await SendUnderAsync(host, EventStoreFault.ThrowUnreadable, a, wireA, tokenA);
        legs.Add($"a pre-PERSIST log/unreadable ({sentA.Status} {sentA.Type}): {await OwedAsync()}");

        var (b, wireB, tokenB) = await AskerAsync(host);
        var sentB = await SendUnderAsync(host, EventStoreFault.ReportAppend, b, wireB, tokenB);
        legs.Add($"a failed result from the append ({(sentB.Threw is null ? "answered" : "threw")}): {await OwedAsync()}");

        var voter = await BudgetBursts.T2Async(forum, board, Ct);
        var other = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var result = await BudgetBursts.StoreAsync(forum, ForumAgent.Create("https://agents.example/result-" + Probe.Suffix(), "result").SignAnswer(board, Probe.Unrelated(), other.PostId, forum.Now), Ct);
        var voteToken = await voter.Dpop.GetTokenAsync(host.Client, Probe.TokenEndpoint, forum.Now, Ct);
        var sentC = await SendUnderAsync(host, EventStoreFault.AppendThenThrow, voter, voter.Agent.SignVote(board, result.Digest, forum.Now), voteToken);
        legs.Add($"a vote whose append threw ({sentC.Status} {sentC.Type}): {await OwedAsync()}");

        var (d, wireD, tokenD) = await AskerAsync(host);
        var sentD = await Probe.SendViaAsync(forum, host.Client, d, wireD, tokenD, Ct);
        legs.Add($"a success ({sentD.Status}): {await OwedAsync()}");

        var expected = new[]
        {
            $"a pre-PERSIST log/unreadable (503 {StoreFaultsTypes.LogUnreadable}): no check is owed",
            $"a failed result from the append ({(sentB.Threw is null ? "answered" : "threw")}): no check is owed",
            $"a vote whose append threw (503 {Unavailable}): no check is owed",
            "a success (201): no check is owed",
        };
        Said(string.Join("; ", expected), string.Join("; ", legs));
    }

    /// <summary>
    /// CPF7. The floor, a post the Forum never saw: a post committed through the event-store port with no route and no exit, its vector absent. The anti-join holds it a
    /// second short of the floor after the host's last check began, and the floor check enrols and stores it at the floor, recording that instant.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF7_APostTheForumNeverSawIsFoundAtTheFloor()
    {
        var floor = PublishedCheckWords.SweepFloor();
        await using var host = forum.NewHost();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "cpf-floor", Ct);
        var author = actor.Agent.AgentId;
        var start = (await host.StateAsync(Ct)).Checked;
        var started = DateTimeOffset.Parse(start, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        await forum.AppendStoredPostAsync(actor.Agent.SignQuestion(Probe.Board(), Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), Ct);
        var planted = await host.AntiJoinAsync(author, Ct);

        while (forum.Now < started + floor - TimeSpan.FromSeconds(1)) await host.TicksAsync(1, Ct);
        var shortOf = await host.AntiJoinAsync(author, Ct);
        await host.TicksAsync(1, Ct);
        var atFloor = await host.AntiJoinAsync(author, Ct);
        var checkedAt = (await host.StateAsync(Ct)).Checked;
        while (forum.Now < started + floor * 2 && atFloor != 0)
        {
            await host.TicksAsync(1, Ct);
            atFloor = await host.AntiJoinAsync(author, Ct);
        }

        Said(
            $"the anti-join holds 1 digest, still 1 a second short of the floor, 0 at the floor, and vector_checked_at is {Instant(started + floor)}",
            $"the anti-join holds {planted} digest{(planted == 1 ? string.Empty : "s")}, still {shortOf} a second short of the floor, " +
            (atFloor == 0 ? "0 at the floor" : $"the anti-join still holds {atFloor} digest{(atFloor == 1 ? string.Empty : "s")} at +{Secs(floor * 2)} s") + $", and vector_checked_at is {checkedAt}");
    }

    /// <summary>
    /// CPF8. Control: start. A host started over a database that holds an unindexed post indexes it before it serves, and serves <c>vector_checked_at</c> as the instant
    /// the start's check began and <c>vector_check_due_at</c> as null.
    /// </summary>
    [Fact]
    public async Task R8_83_CPF8_AHostStartedOverAnUnindexedPostIndexesItBeforeServing()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "cpf-start", Ct);
        var author = actor.Agent.AgentId;
        await forum.AppendStoredPostAsync(actor.Agent.SignQuestion(Probe.Board(), Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), Ct);
        var startedAt = forum.Now;

        await using var restarted = forum.NewHost();
        var state = await restarted.StateAsync(Ct);
        Said(
            $"the anti-join is 0 before any tick; vector_checked_at is {Instant(startedAt)}; vector_check_due_at is null",
            $"the anti-join is {await restarted.AntiJoinAsync(author, Ct)} before any tick; vector_checked_at is {state.Checked}; vector_check_due_at is {state.Due}");
    }

    /// <summary>
    /// CPF19. Always present, typed. The raw JSON of every <c>200</c> search (plain, with a board filter, with a limit, and a filter that matches nothing) holds
    /// <c>vector_checked_at</c> as an instant string and <c>vector_check_due_at</c> as a string or JSON null, present and not omitted. A control on the seam; its owed legs are CPF2.
    /// </summary>
    [Fact]
    public async Task R9_53_CPF19_EverySearchStatesBothMembersTyped()
    {
        await using var host = forum.NewHost();
        var members = PublishedCheckWords.MemberNames();
        var legs = new[] { "q=quokka", "q=quokka&board=cpf19", "q=quokka&limit=1", "q=quokka&board=no-such-board-" + Probe.Suffix() };
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var leg in legs)
        {
            var state = await host.StateAsync(Ct, leg);
            using var doc = JsonDocument.Parse(state.Body);
            var checkedKind = doc.RootElement.TryGetProperty(members.CheckedAt, out var c) ? c.ValueKind.ToString() : "absent";
            var dueKind = doc.RootElement.TryGetProperty(members.CheckDueAt, out var d) ? d.ValueKind.ToString() : "absent";
            expected.Add($"{leg}: 200, {members.CheckedAt} String, {members.CheckDueAt} String or Null");
            actual.Add($"{leg}: {state.Status}, {members.CheckedAt} {checkedKind}, {members.CheckDueAt} {(dueKind is "String" or "Null" ? "String or Null" : dueKind)}");
        }

        Said(string.Join("; ", expected), string.Join("; ", actual));
    }

    /// <summary>
    /// CPF19b. Neither member is in a cursor's binding: a cursor minted while a check is owed answers 200 once no check is owed. Red on the seam because nothing is owed
    /// when it is minted.
    /// </summary>
    [Fact]
    public async Task R9_53_CPF19b_ACursorMintedWhileACheckIsOwedAnswersWhenNoneIs()
    {
        await using var host = forum.NewHost();
        var token = "tok" + Probe.Suffix();
        for (var i = 0; i < 2; i++)
        {
            var (asker, wire, accessToken) = await AskerAsync(host, token + " " + Probe.Unrelated());
            var sent = await Probe.SendViaAsync(forum, host.Client, asker, wire, accessToken, Ct);
            Assert.Equal(201, sent.Status);
        }

        var (actor, faulted, faultToken) = await AskerAsync(host, token + " " + Probe.Unrelated());
        await SendUnderAsync(host, EventStoreFault.AppendThenThrow, actor, faulted, faultToken);
        var owed = await host.StateAsync(Ct, $"q={token}&limit=1");
        using var page = JsonDocument.Parse(owed.Body);
        var cursor = page.RootElement.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString()! : string.Empty;

        await host.TicksAsync((int)PublishedCheckWords.SweepSettle().TotalSeconds + 2, Ct);
        var clear = await host.StateAsync(Ct, $"q={token}&limit=1&cursor={Uri.EscapeDataString(cursor)}");
        Said(
            "a check was owed when the cursor was minted; the cursor answered 200 once no check was owed",
            $"{(owed.Owed ? "a check was owed" : "no check was owed")} when the cursor was minted; the cursor answered {clear.Status}{(clear.Status == 200 ? string.Empty : " " + clear.Body[..Math.Min(120, clear.Body.Length)])} once {(clear.Owed ? "a check was still owed" : "no check was owed")}");
    }

    /// <summary>
    /// CPF20. A find is counted as soon as the scan ends: with the index held open, the tick that enrols the committed post leaves it pending, a search states
    /// <c>vector_pending</c> 1 beside an owed check; released, 0.
    /// </summary>
    [Fact]
    public async Task R9_51_CPF20_ACheckedPostIsCountedAsPendingUntilItsVectorIsStored()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token) = await AskerAsync(host);
        await SendUnderAsync(host, EventStoreFault.AppendThenThrow, actor, wire, token);
        host.Index.HoldUpserts();
        forum.Clock.Advance(TimeSpan.FromSeconds(1));
        var tick = host.TickAsync(forum.Now, Ct);
        var held = await host.Index.WaitForHeldAsync(TimeSpan.FromSeconds(5));
        var during = await host.StateAsync(Ct);
        host.Index.ReleaseUpserts();
        await tick;
        var after = await host.StateAsync(Ct);
        host.Index.ResetModes();

        Said(
            "an upsert was held by the check; during it vector_pending was 1 and a check was owed; released, vector_pending was 0",
            $"{(held ? "an upsert was held by the check" : "no upsert was held by the check")}; during it vector_pending was {during.Pending}{(held ? string.Empty : " (vector_pending is 0 while the check has enrolled a post)")} and {(during.Owed ? "a check was owed" : "no check was owed")}; released, vector_pending was {after.Pending}");
    }
}

/// <summary>The one type name the pre-PERSIST fault probe compares against, written where the probe says it so a change of the Forum's word is a red fact and not a silent re-agreement.</summary>
internal static class StoreFaultsTypes
{
    internal static string LogUnreadable => PublishedStoreFaults.LogUnreadableType;
}
