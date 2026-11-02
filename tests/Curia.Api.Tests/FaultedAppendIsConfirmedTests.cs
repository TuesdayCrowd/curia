using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Curia.Application.Ingest;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// AFC1 to AFC9, AFC12 to AFC16, AFC29, AFC30 and AFC33 to AFC40 (errata G55, R8.84, R8.82 (rev.)): the confirm of a faulted append, on the production Forum over a real Postgres and the
/// real vector index, in process (TestServer). <b>The oracle is the events table, read by SQL on the fixture's own connection string by the digest the probe recomputes from the
/// bytes it signed</b> (<see cref="Afc"/>): never the status, never the Forum's projection (trap 97). The fixture's store clock reads 7 ms after the pipeline's
/// (<see cref="ConfirmFixture"/>), the catch-up's loop is removed and asserted absent, and a probe that moves time drives the tick itself. Every sentence, figure and event is
/// read from the errata (<see cref="PublishedConfirmWords"/>). A probe is written in its failing form and is red on the seam tree by assertion; the text each prints is
/// recorded next to its prediction in the Task 0c report.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FaultedAppendIsConfirmedTests(ConfirmFixture forum) : IClassFixture<ConfirmFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Unavailable = PublishedStoreFaults.StoreUnavailableType;

    private static void Said(string expected, string actual) => Probe.Said(expected, actual);

    private static string Answered(Sent sent) => sent.Threw is not null ? sent.Says : $"{sent.Status} {sent.Type}".TrimEnd();

    private static string Plural(long n, string noun) => $"{n} {noun}{(n == 1 ? string.Empty : "s")}";

    /// <summary>A fresh agent that may ask, its signed question, its token (minted before any fault is armed, so a fault arms only the append) and the digest recomputed from the wire.</summary>
    private async Task<(SceneActor Actor, byte[] Wire, string Token, string Digest)> AskerAsync(CheckHost host, string? body = null, string stem = "afc")
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, stem, Ct);
        var wire = actor.Agent.SignQuestion(Probe.Board(), body ?? Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now);
        var token = await actor.Dpop.GetTokenAsync(host.Client, Probe.TokenEndpoint, forum.Now, Ct);
        return (actor, wire, token, Afc.DigestOf(wire));
    }

    private async Task<Sent> SendUnderAsync(CheckHost host, EventStoreFault fault, SceneActor actor, byte[] wire, string token, Func<Exception>? exception = null)
    {
        host.Store.Arm(fault, exception);
        try
        {
            return await Probe.SendViaAsync(forum, host.Client, actor, wire, token, Ct);
        }
        finally
        {
            host.Store.Disarm();
        }
    }

    private static string Table(IReadOnlyList<StoredPost> rows) => Plural(rows.Count, "event");

    /// <summary>
    /// AFC1. The defect, with another agent's post in the way. An ask whose append commits and then throws, with a second agent's post appended between the commit and the
    /// throw, is answered 201; the receipt names the post the events table holds for the digest (SQL, no baseline) and not the other agent's, which was stored after it; the
    /// table holds exactly one event of the digest.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC1_ACommittedPostIsAnsweredFromTheLogWhateverElseWasAppendedAfterIt()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var other = await AcceptedAnswerScene.EnrolAsync(forum, "afc1-other", Ct);
        var otherWire = other.Agent.SignQuestion(Probe.Board(), Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now);
        var otherToken = await other.Dpop.GetTokenAsync(host.Client, Probe.TokenEndpoint, forum.Now, Ct);
        var interleaved = new List<Sent>();
        host.Store.OnInterleave = async ct => interleaved.Add(await Probe.SendViaAsync(forum, host.Client, other, otherWire, otherToken, ct));

        var sent = await SendUnderAsync(host, EventStoreFault.InterleaveOtherAgentsPost, actor, wire, token);
        host.Store.OnInterleave = null;
        var rows = await Afc.StoredAsync(forum, digest, Ct);
        var others = await Afc.StoredAsync(forum, Afc.DigestOf(otherWire), Ct);
        var mine = rows.Count > 0 ? rows[0] : null;
        var theirs = others.Count > 0 ? others[0] : null;
        Assert.True(interleaved.Count == 1 && interleaved[0].Status == 201 && mine is not null && theirs is not null, $"the fixture did not interleave: other agent {(interleaved.Count == 1 ? interleaved[0].Says : "never sent")}, act stored {rows.Count}, other stored {others.Count}");

        var expected = $"the ask is answered 201 naming {mine!.PostId}, not the other agent's {theirs!.PostId}, which was stored after it; the table holds 1 event of the digest";
        var good = sent.Status == 201 && sent.PostId == mine.PostId && theirs.Seq > mine.Seq && rows.Count == 1;
        Said(
            expected,
            good ? expected : $"the ask is answered {Answered(sent)} naming {sent.PostId ?? "no post"}{(sent.PostId == theirs.PostId ? ", the other agent's post" : string.Empty)}, " +
            $"the other agent's post {(theirs.Seq > mine.Seq ? "stored after it" : "stored before it")}; the table holds {Plural(rows.Count, "event")} of the digest");
    }

    /// <summary>
    /// AFC2, control. Refused before the commit. <c>ThrowOnAppend</c> is answered 503 <c>curia/store/unavailable</c>; the table holds none; for a discussion kind the
    /// obligation is owed (an instant); and the confirm made its one read after the fault (the counting reader) and found nothing. Red on the seam by the read count alone.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC2_ACommitThatNeverHappenedIsConfirmedAbsentWithOneReadAndStillOwesTheCheck()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var sent = await SendUnderAsync(host, EventStoreFault.ThrowOnAppend, actor, wire, token);
        var reads = (host.Store.ReadsAfterFault, host.Store.ForwardReadsAfterFault);
        var rows = await Afc.StoredAsync(forum, digest, Ct);
        var owed = (await host.StateAsync(Ct)).Owed;

        Said(
            $"503 {Unavailable}; the table holds 0 events of the digest; a check is owed; the confirm made 1 read after the fault, 1 of them forward",
            $"{Answered(sent)}; the table holds {Plural(rows.Count, "event")} of the digest; {(owed ? "a check is owed" : "no check is owed")}; the confirm made {Plural(reads.ReadsAfterFault, "read")} after the fault, {reads.ForwardReadsAfterFault} of them forward");
    }

    /// <summary>
    /// AFC3. The confirm's read fails and the answer is the append's. <c>AppendThenThrowThenFailReads</c>: 503 <c>curia/store/unavailable</c>, never <c>curia/log/unreadable</c>
    /// and never "Nothing was recorded"; the table holds the post (SQL); a check is owed; <c>AppendNotConfirmed</c> is logged once with the outcome <c>unreadable</c>.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC3_AConfirmWhoseReadFailsAnswersTheAppendsFaultAndLogsUnreadable()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var mark = forum.Log.Mark();
        var sent = await SendUnderAsync(host, EventStoreFault.AppendThenThrowThenFailReads, actor, wire, token);
        var rows = await Afc.StoredAsync(forum, digest, Ct);
        var owed = (await host.StateAsync(Ct)).Owed;
        var logged = Afc.Logged(forum.Log, mark, PublishedConfirmWords.NotConfirmedEvent().Id);
        var outcome = logged.Count == 1 ? Afc.OutcomeOf(logged[0]) : null;
        var nothingRecorded = sent.Body.Contains("Nothing was recorded", StringComparison.Ordinal);

        Said(
            $"503 {Unavailable} without \"Nothing was recorded\"; the table holds 1 event of the digest; a check is owed; AppendNotConfirmed logged once with the outcome unreadable",
            $"{Answered(sent)}{(nothingRecorded ? " with \"Nothing was recorded\"" : " without \"Nothing was recorded\"")}; the table holds {Plural(rows.Count, "event")} of the digest; {(owed ? "a check is owed" : "no check is owed")}; " +
            (logged.Count == 0 ? "no AppendNotConfirmed line" : $"AppendNotConfirmed logged {(logged.Count == 1 ? "once" : logged.Count + " times")} with the outcome {outcome ?? "(not the published message)"}"));
    }

    /// <summary>
    /// AFC4. The agent's hold is kept through the confirm. <c>HoldNextReadAfterFault</c>: while the confirm's read is held, a second request of the same agent is not admitted;
    /// released, the first is answered 201 and the second's receipt counts the first post (<c>budget.used</c> 2).
    /// </summary>
    [Fact]
    public async Task R8_84_AFC4_TheAgentsHoldIsKeptThroughTheConfirmsRead()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, _) = await AskerAsync(host);
        var wire2 = actor.Agent.SignQuestion(Probe.Board(), Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now);
        host.Store.Arm(EventStoreFault.HoldNextReadAfterFault);
        var first = Probe.SendViaAsync(forum, host.Client, actor, wire, token, Ct);
        var reached = await Task.WhenAny(host.Store.HeldReadStarted, first, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        var held = host.Store.HeldReadStarted.IsCompleted;
        if (!held)
        {
            host.Store.Disarm();
            var early = await first;
            Said("the confirm's read was reached and held", $"the confirm's read was never reached ({Answered(early)} was answered with no read after the fault)");
            return;
        }

        host.Store.Disarm();
        var second = Probe.SendViaAsync(forum, host.Client, actor, wire2, token, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        var admittedDuringHold = second.IsCompleted;
        host.Store.ReleaseHeldRead();
        var firstSent = await first;
        var secondSent = await second;
        var used = Afc.Member(Afc.Member(secondSent.Body, "budget") ?? "{}", "used");
        _ = reached;

        Said(
            "while the read is held the second request is not admitted; released, the first is answered 201 and the second 201 with budget.used 2",
            $"while the read is held the second request is {(admittedDuringHold ? "admitted" : "not admitted")}; released, the first is answered {Answered(firstSent)} and the second {Answered(secondSent)} with budget.used {used ?? "absent"}");
    }

    /// <summary>
    /// AFC4, the revision's gate. A revision whose append commits and throws is confirmed under both holds: a second revision of the same <c>prev</c> by the same agent is not
    /// answered while the confirm's read is held (the agent's gate is entered first and is held), and is answered <c>409 already-revised</c> once the first is.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC4_TheRevisionGateAndTheAgentsGateAreKeptThroughTheConfirmOfARevision()
    {
        await using var host = forum.NewHost();
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "afc4r", Ct);
        var question = await Probe.AskAsync(forum, actor, board, Ct);
        var r1 = actor.Agent.SignRevision(board, "First " + Probe.Unrelated(), question.PostId, question.Digest, forum.Now);
        var r2 = actor.Agent.SignRevision(board, "Second " + Probe.Unrelated(), question.PostId, question.Digest, forum.Now);
        var token = await actor.Dpop.GetTokenAsync(host.Client, Probe.TokenEndpoint, forum.Now, Ct);

        host.Store.Arm(EventStoreFault.HoldNextReadAfterFault);
        var first = Probe.SendViaAsync(forum, host.Client, actor, r1, token, Ct);
        await Task.WhenAny(host.Store.HeldReadStarted, first, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        if (!host.Store.HeldReadStarted.IsCompleted)
        {
            host.Store.Disarm();
            var early = await first;
            Said("the confirm's read was reached and held", $"the confirm's read was never reached ({Answered(early)} was answered with no read after the fault)");
            return;
        }

        host.Store.Disarm();
        var second = Probe.SendViaAsync(forum, host.Client, actor, r2, token, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        var admittedDuringHold = second.IsCompleted;
        host.Store.ReleaseHeldRead();
        var firstSent = await first;
        var secondSent = await second;
        var served = await Probe.RevisionsOfAsync(forum, question.Digest);

        Said(
            $"the second revision is not answered while the read is held; released, the first is answered 201 and the second 409 {IngestErrors.AlreadyRevisedType}; the table holds 1 revision",
            $"the second revision is {(admittedDuringHold ? "answered" : "not answered")} while the read is held; released, the first is answered {Answered(firstSent)} and the second {Answered(secondSent)}; the table holds {Plural(served, "revision")}");
    }

    /// <summary>
    /// AFC5, control. A late commit is not found yet, and is found by the check. <c>ThrowThenCommitWhenReleased</c>: 503 at the throw; the table holds none; the confirm said
    /// <c>absent</c> after one forward read; the commit lands later, the check finds the post (anti-join 0) and it is stored once.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC5_ALateCommitIsNotFoundByTheConfirmAndIsFoundByTheCheck()
    {
        var settle = (int)PublishedCheckWords.SweepSettle().TotalSeconds;
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var mark = forum.Log.Mark();
        var sent = await SendUnderAsync(host, EventStoreFault.ThrowThenCommitWhenReleased, actor, wire, token);
        var reads = host.Store.ForwardReadsAfterFault;
        var atOnce = (await Afc.StoredAsync(forum, digest, Ct)).Count;
        var logged = Afc.Logged(forum.Log, mark, PublishedConfirmWords.NotConfirmedEvent().Id);
        var outcome = logged.Count == 1 ? Afc.OutcomeOf(logged[0]) : null;

        host.Store.ReleaseLateCommit();
        await host.Store.LateCommitted.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await host.TicksAsync(settle + 2, Ct);
        var stored = (await Afc.StoredAsync(forum, digest, Ct)).Count;
        var antiJoin = await host.AntiJoinAsync(actor.Agent.AgentId, Ct);

        Said(
            $"503 {Unavailable}; the table held 0 at once; the confirm made 1 forward read and logged AppendNotConfirmed with the outcome absent; later the table holds 1 and the anti-join is 0",
            $"{Answered(sent)}; the table held {atOnce} at once; the confirm made {reads} forward read{(reads == 1 ? string.Empty : "s")} and " +
            (logged.Count == 0 ? "logged no AppendNotConfirmed line" : $"logged AppendNotConfirmed with the outcome {outcome ?? "(not the published message)"}") + $"; later the table holds {stored} and the anti-join is {antiJoin}");
    }

    private static readonly string[] FaultWords = ["fault", "confirm", "exception", "unavailable"];

    private static readonly string[] DiscussionKinds = ["question", "answer", "finding", "comment", "revision"];

    /// <summary>
    /// AFC6. Every kind. For each kind the model defines (derived from <see cref="PostKind"/>, not listed), a post whose append commits and then throws is answered 201
    /// naming the table's post, and one whose append throws before committing is answered 503 with nothing stored and the obligation owed for the five discussion kinds and not for
    /// a vote or a verification. Each leg takes a host and a question of its own.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC6_EveryKindOfPostIsConfirmedAndOnlyTheDiscussionKindsOweACheck()
    {
        var board = Probe.Board();
        var actor = await BudgetBursts.T2Async(forum, board, Ct);
        var expected = new List<string>();
        var actual = new List<string>();

        foreach (var kind in Enum.GetValues<PostKind>())
        {
            foreach (var committed in new[] { true, false })
            {
                var (want, got) = await KindLegAsync(kind, committed, board, actor);
                expected.Add(want);
                actual.Add(got);
            }
        }

        Said(string.Join(" | ", expected), string.Join(" | ", actual));
    }

    private async Task<(string Expected, string Actual)> KindLegAsync(PostKind kind, bool committed, string board, SceneActor actor)
    {
        var name = PostKinds.Wire(kind);
        var resultParent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var result = await BudgetBursts.StoreAsync(forum, ForumAgent.Create("https://agents.example/afc6-result-" + Probe.Suffix(), "result").SignAnswer(board, Probe.Unrelated(), resultParent.PostId, forum.Now), Ct);
        await using var host = forum.NewHost();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var own = await BudgetBursts.StoreAsync(forum, actor.Agent.SignQuestion(board, Probe.Unrelated(), "Own " + Probe.Unrelated(), forum.Now), Ct);
        var wire = kind switch
        {
            PostKind.Question => actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now),
            PostKind.Answer => actor.Agent.SignAnswer(board, Probe.Unrelated(), parent.PostId, forum.Now),
            PostKind.Comment => actor.Agent.Sign(PostKind.Comment, board, Probe.Unrelated(), null, parent.PostId, forum.Now),
            PostKind.Finding => actor.Agent.Sign(PostKind.Finding, board, Probe.Unrelated(), "Finding " + Probe.Unrelated(), null, forum.Now),
            PostKind.Revision => actor.Agent.SignRevision(board, "Corrected " + Probe.Unrelated(), own.PostId, own.Digest, forum.Now),
            PostKind.Vote => actor.Agent.SignVote(board, result.Digest, forum.Now),
            PostKind.Verification => actor.Agent.SignVerification(board, result.Digest, "reproduced", forum.Now),
            _ => throw new InvalidOperationException($"this fact does not know how to sign a {name}"),
        };
        var token = await actor.Dpop.GetTokenAsync(host.Client, Probe.TokenEndpoint, forum.Now, Ct);
        var digest = Afc.DigestOf(wire);
        var sent = await SendUnderAsync(host, committed ? EventStoreFault.AppendThenThrow : EventStoreFault.ThrowOnAppend, actor, wire, token);
        var rows = await Afc.StoredAsync(forum, digest, Ct);
        if (committed)
        {
            return (
                $"{name}: committed then thrown: 201 naming {(rows.Count == 1 ? rows[0].PostId : "the stored post")}",
                $"{name}: committed then thrown: {(sent.Status == 201 && rows.Count == 1 && sent.PostId == rows[0].PostId ? $"201 naming {rows[0].PostId}" : $"{Answered(sent)} naming {sent.PostId ?? "no post"}")}");
        }

        var owes = DiscussionKinds.Contains(name, StringComparer.Ordinal);
        var owed = (await host.StateAsync(Ct)).Owed;
        return (
            $"{name}: refused before the commit: 503 {Unavailable}, 0 stored, {(owes ? "a check is owed" : "no check is owed")}",
            $"{name}: refused before the commit: {Answered(sent)}, {rows.Count} stored, {(owed ? "a check is owed" : "no check is owed")}");
    }

    /// <summary>
    /// AFC7, controls. The acceptance and the flag are not confirmed. A resolve whose append commits and throws is answered 503 <c>curia/store/unavailable</c> with no forward
    /// read after the fault; its repeat is answered as the replay of the answer that already stands. A flag the same, answered as the raiser's repeat.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC7_AnAcceptanceAndAFlagAreNotConfirmedAndTheirRepeatIsSafe()
    {
        await using var host = forum.NewHost();
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var token = await scene.Asker.Dpop.GetTokenAsync(host.Client, Probe.TokenEndpoint, forum.Now, Ct);

        async Task<(int Status, string Body)> AcceptAsync()
        {
            using var response = await scene.Asker.Dpop.PostAsync(host.Client, $"http://localhost/v1/posts/{scene.Answer1.PostId}/accept", token, System.Text.Encoding.UTF8.GetBytes("{}"), forum.Now, Ct, contentType: "application/json");
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }

        host.Store.Arm(EventStoreFault.AppendThenThrow);
        var faulted = await AcceptAsync();
        var forwardReads = host.Store.ForwardReadsAfterFault;
        host.Store.Disarm();
        var repeat = await AcceptAsync();
        var replay = Afc.Member(repeat.Body, "already_accepted");

        var reporter = await AcceptedAnswerScene.EnrolAsync(forum, "afc7-flag", Ct);
        var flagToken = await reporter.Dpop.GetTokenAsync(host.Client, Probe.TokenEndpoint, forum.Now, Ct);

        async Task<(int Status, string Body)> FlagAsync()
        {
            using var response = await reporter.Dpop.PostAsync(host.Client, $"http://localhost/v1/posts/{scene.Answer2.PostId}/flags", flagToken, System.Text.Encoding.UTF8.GetBytes("{\"kind\":\"incorrect\",\"rationale\":\"It is wrong.\"}"), forum.Now, Ct, contentType: "application/json");
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }

        host.Store.Arm(EventStoreFault.AppendThenThrow);
        var flagFaulted = await FlagAsync();
        var flagForwardReads = host.Store.ForwardReadsAfterFault;
        host.Store.Disarm();
        var flagRepeat = await FlagAsync();

        string Type(string body) => Afc.Member(body, "type") ?? string.Empty;
        Said(
            $"the resolve: 503 {Unavailable}, 0 forward reads after the fault, the repeat answered 200 with already_accepted true; the flag: 503 {Unavailable}, 0 forward reads after the fault, the repeat refused",
            $"the resolve: {faulted.Status} {Type(faulted.Body)}, {forwardReads} forward reads after the fault, the repeat answered {repeat.Status} with already_accepted {replay ?? "absent"}; " +
            $"the flag: {flagFaulted.Status} {Type(flagFaulted.Body)}, {flagForwardReads} forward reads after the fault, the repeat {(flagRepeat.Status is 409 or 400 or 403 ? "refused" : "answered " + flagRepeat.Status)}");
    }

    /// <summary>
    /// AFC8. The cancellation door. <c>CommitThenCancel</c> with the client aborting after the commit: no status; the table holds the post; <b>with no tick</b> the anti-join is
    /// 0 and no check is owed; <c>AppendConfirmed</c> is logged (the confirm's token is the host's, so it read although the request's token was cancelled).
    /// </summary>
    [Fact]
    public async Task R8_84_AFC8_APostWhoseRequestWasAbortedAfterItsCommitIsConfirmedAndOwesNothing()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var mark = forum.Log.Mark();
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
            if (state.Owed || Afc.Logged(forum.Log, mark, PublishedConfirmWords.ConfirmedEvent().Id).Count > 0) break;
            await Task.Delay(50, Ct);
        }
        while (DateTime.UtcNow < deadline);

        var rows = await Afc.StoredAsync(forum, digest, Ct);
        var antiJoin = await host.AntiJoinAsync(actor.Agent.AgentId, Ct);
        var confirmed = Afc.Logged(forum.Log, mark, PublishedConfirmWords.ConfirmedEvent().Id);
        Said(
            "no status was answered; the post is committed; with no tick the anti-join is 0 and no check is owed; AppendConfirmed was logged once",
            $"{(sent.Threw == "cancelled" ? "no status was answered" : "the status was " + Answered(sent))}; the post is {(rows.Count == 1 ? "committed" : "not committed")}; with no tick the anti-join is {antiJoin} and " +
            $"{(state.Owed ? "a check is owed (vector_check_due_at is an instant after a committed post whose request was aborted)" : "no check is owed")}; AppendConfirmed was logged {(confirmed.Count == 1 ? "once" : confirmed.Count + " times")}");
    }

    /// <summary>
    /// AFC9. The index step follows a found post, once. After a confirmed 201 with no tick: <c>vector_index</c> is <c>indexed</c>; <c>post_embeddings</c> holds exactly one row
    /// for the digest and its sequence is the event's.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC9_AFoundPostIsIndexedByTheStepOfTheRequestOnceWithoutATick()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var sent = await SendUnderAsync(host, EventStoreFault.AppendThenThrow, actor, wire, token);
        var rows = await Afc.StoredAsync(forum, digest, Ct);
        var embeddings = await Afc.EmbeddingSequencesAsync(forum, digest, Ct);
        var sequence = rows.Count == 1 ? rows[0].Seq.ToString(CultureInfo.InvariantCulture) : "the event's";

        Said(
            $"201 with vector_index indexed; post_embeddings holds 1 row for the digest, with the sequence {sequence}",
            $"{Answered(sent)} with vector_index {Afc.Member(sent.Body, "vector_index") ?? "absent"}; post_embeddings holds {Plural(embeddings.Count, "row")} for the digest{(embeddings.Count == 1 ? ", with the sequence " + embeddings[0].ToString(CultureInfo.InvariantCulture) : string.Empty)}");
    }

    /// <summary>
    /// AFC10, in process. The negative properties, each with its positive: a found post is answered 201 and owes nothing (<c>vector_check_due_at</c> null, no
    /// <c>VectorIndexCheckOwed</c> line) and the confirm writes nothing (over the whole run the table's count and highest sequence are those of the posts committed, and the digest
    /// appears once); it read once, in one page.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC10_AFoundPostOwesNothingAndTheConfirmWritesNothing()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var shapeBefore = await host.LogShapeAsync(Ct);
        var mark = forum.Log.Mark();
        var sent = await SendUnderAsync(host, EventStoreFault.AppendThenThrow, actor, wire, token);
        var reads = host.Store.ForwardReadsAfterFault;
        var shapeAfter = await host.LogShapeAsync(Ct);
        var state = await host.StateAsync(Ct);
        var owedLines = forum.Log.Since(mark).Count(e => e.Message.Contains("check of the log against the vector index is owed", StringComparison.Ordinal));
        var rows = await Afc.StoredAsync(forum, digest, Ct);

        Said(
            "201; no check is owed and no owed line was logged; the table grew by 1 event and its highest sequence by 1; the digest appears once; the confirm made 1 forward read",
            $"{Answered(sent)}; {(state.Owed ? "a check is owed" : "no check is owed")} and {(owedLines == 0 ? "no owed line was logged" : owedLines + " owed line(s) were logged")}; " +
            $"the table grew by {shapeAfter.Count - shapeBefore.Count} event{(shapeAfter.Count - shapeBefore.Count == 1 ? string.Empty : "s")} and its highest sequence by {shapeAfter.MaxSeq - shapeBefore.MaxSeq}; the digest appears {(rows.Count == 1 ? "once" : rows.Count + " times")}; the confirm made {reads} forward read{(reads == 1 ? string.Empty : "s")}");
    }

    /// <summary>
    /// AFC12. The bound, on a timer-capable clock. <c>AppendThenThrowThenHangReads</c>: the confirm's read waits; the answer is the append's 503 once the clock has moved by
    /// <c>ConfirmBound</c> and not before; <c>AppendNotConfirmed</c> is logged with the outcome <c>bound</c>; a check is owed; the agent's hold is released (its next request is admitted).
    /// </summary>
    [Fact]
    public async Task R8_84_AFC12_AConfirmWhoseReadOutlastsItsBoundAnswersTheAppendsFaultWhenTheBoundIsReached()
    {
        var bound = TimeSpan.FromSeconds(PublishedConfirmWords.ConfirmBoundSeconds);
        var clock = new TimerManualTimeProvider(forum.Now);
        await using var host = forum.NewHost(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        var (actor, wire, token, _) = await AskerAsync(host);
        var next = actor.Agent.SignQuestion(Probe.Board(), Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now);
        var mark = forum.Log.Mark();

        host.Store.Arm(EventStoreFault.AppendThenThrowThenHangReads);
        var send = Probe.SendViaAsync(forum, host.Client, actor, wire, token, Ct);
        await Task.WhenAny(host.Store.HangStarted, send, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        if (!host.Store.HangStarted.IsCompleted)
        {
            host.Store.Disarm();
            var early = await send;
            Said("the confirm's read was reached and waited", $"the confirm's read was never reached ({Answered(early)} was answered with no read after the fault)");
            return;
        }

        clock.Advance(bound - TimeSpan.FromSeconds(1));
        forum.Clock.Advance(bound - TimeSpan.FromSeconds(1));
        await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
        var answeredEarly = send.IsCompleted;
        clock.Advance(TimeSpan.FromSeconds(1));
        forum.Clock.Advance(TimeSpan.FromSeconds(1));
        var sent = await send.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        host.Store.Disarm();
        var logged = Afc.Logged(forum.Log, mark, PublishedConfirmWords.NotConfirmedEvent().Id);
        var outcome = logged.Count == 1 ? Afc.OutcomeOf(logged[0]) : null;
        var owed = (await host.StateAsync(Ct)).Owed;
        var second = await Probe.SendViaAsync(forum, host.Client, actor, next, await actor.Dpop.GetTokenAsync(host.Client, Probe.TokenEndpoint, forum.Now, Ct), Ct);

        Said(
            $"not answered before the bound; then 503 {Unavailable}; AppendNotConfirmed logged with the outcome bound; a check is owed; the agent's next request is admitted (201)",
            $"{(answeredEarly ? "answered before the bound" : "not answered before the bound")}; then {Answered(sent)}; " +
            (logged.Count == 0 ? "no AppendNotConfirmed line" : $"AppendNotConfirmed logged with the outcome {outcome ?? "(not the published message)"}") + $"; {(owed ? "a check is owed" : "no check is owed")}; the agent's next request is {(second.Status == 201 ? "admitted (201)" : "answered " + Answered(second))}");
    }

    private static async Task<string?> ReceiptAsync(HttpResponseMessage response, CancellationToken ct) => response.StatusCode == System.Net.HttpStatusCode.Created ? await response.Content.ReadAsStringAsync(ct) : null;

    /// <summary>
    /// AFC13. Receipt parity, and the fault is not stated. A confirmed 201 has the members of an ordinary 201, in order; <c>Location</c> in the same form; <c>server_ts</c> is the
    /// stored column (what a read of the post serves) and is the pipeline's instant plus the 7 ms the fixture's clocks differ by; <c>budget</c> equals <c>GET /v1/budget</c> read
    /// right after; the body names no fault; and no log line holds a string the post carried.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC13_AConfirmedReceiptIsAnOrdinaryReceiptFromTheLogsOwnInstantAndStatesNoFault()
    {
        await using var host = forum.NewHost();
        var sentinel = "sentinel-" + Guid.NewGuid().ToString("N");
        var (actor, wire, token, digest) = await AskerAsync(host, Probe.Unrelated() + " " + sentinel);
        var (plainActor, plainWire, plainToken, _) = await AskerAsync(host);
        var mark = forum.Log.Mark();

        using var plainResponse = await plainActor.Dpop.PostAsync(host.Client, Probe.PostsUrl, plainToken, plainWire, forum.Now, Ct);
        var plainBody = await ReceiptAsync(plainResponse, Ct);

        host.Store.Arm(EventStoreFault.AppendThenThrow);
        using var response = await actor.Dpop.PostAsync(host.Client, Probe.PostsUrl, token, wire, forum.Now, Ct);
        host.Store.Disarm();
        var body = await response.Content.ReadAsStringAsync(Ct);
        var rows = await Afc.StoredAsync(forum, digest, Ct);

        var problems = new List<string>();
        if (response.StatusCode != System.Net.HttpStatusCode.Created) problems.Add($"answered {(int)response.StatusCode} {Afc.Member(body, "type")}");
        else
        {
            if (plainBody is not null && !Afc.MemberNames(body).SequenceEqual(Afc.MemberNames(plainBody))) problems.Add("the members differ from an ordinary receipt's");
            if (rows.Count != 1) problems.Add($"the table holds {rows.Count} events of the digest");
            else
            {
                var row = rows[0];
                if (response.Headers.Location?.ToString() != $"/v1/posts/{row.PostId}") problems.Add($"Location is {response.Headers.Location}");
                var served = DateTimeOffset.Parse(Afc.Member(body, "server_ts") ?? "0001-01-01T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                if (served != row.ColumnServerTs) problems.Add($"server_ts is {served:o}, not the stored column's {row.ColumnServerTs:o}");
                if (served - row.PayloadServerTs != ConfirmFixture.StoreAhead) problems.Add($"server_ts differs from the pipeline's instant by {(served - row.PayloadServerTs).TotalMilliseconds} ms, not {ConfirmFixture.StoreAhead.TotalMilliseconds}");
                var readout = await BudgetReadouts.GetAsync(forum, actor);
                var entry = readout.Entry("posting");
                var receipt = Afc.Member(body, "budget") ?? "{}";
                if (Afc.Member(receipt, "used") != entry.GetProperty("used").GetRawText() || Afc.Member(receipt, "remaining") != entry.GetProperty("remaining").GetRawText()) problems.Add("budget used/remaining differ from GET /v1/budget");
            }

            if (FaultWords.Any(w => body.Contains(w, StringComparison.OrdinalIgnoreCase))) problems.Add("the body names a fault");
        }

        if (forum.Log.Since(mark).Any(e => e.Message.Contains(sentinel, StringComparison.Ordinal) || e.Values.Any(v => v.Value?.ToString()?.Contains(sentinel, StringComparison.Ordinal) == true))) problems.Add("a log line holds text the post carried");

        const string ok = "201; the members of an ordinary receipt, in order; Location /v1/posts/<id>; server_ts the stored column and the pipeline's instant + 7 ms; budget equal to GET /v1/budget; no fault named; no log line holds the post's text";
        Said(ok, problems.Count == 0 ? ok : string.Join("; ", problems));
    }

    /// <summary>
    /// AFC14. A faulted revision. A revision whose append commits and throws is answered 201; a second revision of the same <c>prev</c> is answered 409 <c>already-revised</c>;
    /// the table holds one revision; and both holds are released (the agent's next post is admitted).
    /// </summary>
    [Fact]
    public async Task R8_84_AFC14_AFaultedRevisionIsConfirmedAndTheGatesAreReleased()
    {
        await using var host = forum.NewHost();
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "afc14", Ct);
        var question = await Probe.AskAsync(forum, actor, board, Ct);
        var r1 = actor.Agent.SignRevision(board, "First " + Probe.Unrelated(), question.PostId, question.Digest, forum.Now);
        var r2 = actor.Agent.SignRevision(board, "Second " + Probe.Unrelated(), question.PostId, question.Digest, forum.Now);
        var token = await actor.Dpop.GetTokenAsync(host.Client, Probe.TokenEndpoint, forum.Now, Ct);

        var first = await SendUnderAsync(host, EventStoreFault.AppendThenThrow, actor, r1, token);
        var second = await Probe.SendViaAsync(forum, host.Client, actor, r2, token, Ct);
        var revisions = await Probe.RevisionsOfAsync(forum, question.Digest);
        var next = await Probe.SendViaAsync(forum, host.Client, actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), token, Ct);

        Said(
            $"the faulted revision 201; the second revision 409 {IngestErrors.AlreadyRevisedType}; the table holds 1 revision; the agent's next post is admitted (201)",
            $"the faulted revision {Answered(first)}; the second revision {Answered(second)}; the table holds {Plural(revisions, "revision")}; the agent's next post is {(next.Status == 201 ? "admitted (201)" : "answered " + Answered(next))}");
    }

    /// <summary>
    /// AFC15. Any exception. An exception nothing names after the commit is answered 201; before the commit, <c>500 curia/server/fault</c> and the confirm ran (one forward read,
    /// nothing found); a <c>log/unreadable</c> before PERSIST (AUTHORIZE's read) is answered as nothing recorded (control for the answer; it makes no confirm, but the fault mode does not count reads for it, so the leg does not measure that).
    /// </summary>
    [Fact]
    public async Task R8_84_AFC15_AnyExceptionIsConfirmedAndAReadFaultBeforePersistIsNot()
    {
        Func<Exception> unnamed = () => new InvalidOperationException("an exception nothing names");
        await using var host = forum.NewHost();

        var (a1, w1, t1, d1) = await AskerAsync(host);
        var after = await SendUnderAsync(host, EventStoreFault.AppendThenThrow, a1, w1, t1, unnamed);
        var afterRows = (await Afc.StoredAsync(forum, d1, Ct)).Count;

        var (a2, w2, t2, d2) = await AskerAsync(host);
        var before = await SendUnderAsync(host, EventStoreFault.ThrowOnAppend, a2, w2, t2, unnamed);
        var beforeReads = host.Store.ForwardReadsAfterFault;
        var beforeRows = (await Afc.StoredAsync(forum, d2, Ct)).Count;

        var (a3, w3, t3, d3) = await AskerAsync(host);
        var read = await SendUnderAsync(host, EventStoreFault.ThrowUnreadable, a3, w3, t3);
        var readRows = (await Afc.StoredAsync(forum, d3, Ct)).Count;

        Said(
            $"after the commit, unnamed: 201 (1 stored); before it, unnamed: 500 {PublishedStoreFaults.FaultType} (0 stored, 1 forward read); a read fault before PERSIST: 503 {PublishedStoreFaults.LogUnreadableType} (0 stored)",
            $"after the commit, unnamed: {Answered(after)} ({afterRows} stored); before it, unnamed: {Answered(before)} ({beforeRows} stored, {beforeReads} forward read{(beforeReads == 1 ? string.Empty : "s")}); a read fault before PERSIST: {Answered(read)} ({readRows} stored)");
    }

    /// <summary>
    /// AFC16(b). The in-flight scope. While the confirm's read is held and a check is due, the check does not enrol the post (<c>vector_pending</c> stays 0); released, the post is
    /// indexed once; and the registration is gone after the request.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC16b_ACheckDoesNotEnrolAPostTheRouteIsConfirming()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        host.Store.Arm(EventStoreFault.HoldNextReadAfterFault);
        var send = Probe.SendViaAsync(forum, host.Client, actor, wire, token, Ct);
        await Task.WhenAny(host.Store.HeldReadStarted, send, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        if (!host.Store.HeldReadStarted.IsCompleted)
        {
            host.Store.Disarm();
            var early = await send;
            Said("the confirm's read was reached and held", $"the confirm's read was never reached ({Answered(early)} was answered with no read after the fault)");
            return;
        }

        host.Store.Disarm();
        host.Sweep.Owe(forum.Now);
        forum.Clock.Advance(TimeSpan.FromSeconds(1));
        await host.TickAsync(forum.Now, Ct);
        var pendingDuring = (await host.StateAsync(Ct)).Pending;
        var indexedDuring = (await Afc.EmbeddingSequencesAsync(forum, digest, Ct)).Count;
        host.Store.ReleaseHeldRead();
        var sent = await send;
        var embeddings = (await Afc.EmbeddingSequencesAsync(forum, digest, Ct)).Count;
        var inFlight = host.Sweep.IsIndexing(digest);

        Said(
            "during the hold the check enrolled nothing (vector_pending 0, 0 rows); released, 201 and the post indexed once; no registration after the request",
            $"during the hold the check {(pendingDuring == "0" && indexedDuring == 0 ? "enrolled nothing" : "enrolled the post")} (vector_pending {pendingDuring}, {indexedDuring} rows); released, {Answered(sent)} and the post indexed {(embeddings == 1 ? "once" : embeddings + " times")}; " +
            (inFlight ? "the digest is still registered after the request" : "no registration after the request"));
    }

    /// <summary>
    /// AFC29, controls. The neighbours stay as they are: an ordinary receipt has the members <c>post_id digest server_ts risk_flags vector_index budget</c>; the same bytes sent
    /// twice are refused 409 <c>already-recorded</c> naming the first post (R8.78); a second revision of a served <c>prev</c> is refused 409 <c>already-revised</c> (R8.77).
    /// </summary>
    [Fact]
    public async Task R8_84_AFC29_TheReceiptTheRepeatRefusalAndTheRevisionRefusalAreUnchanged()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        using var created = await actor.Dpop.PostAsync(host.Client, Probe.PostsUrl, token, wire, forum.Now, Ct);
        var body = await created.Content.ReadAsStringAsync(Ct);
        var again = await Probe.SendViaAsync(forum, host.Client, actor, wire, token, Ct);
        var stored = await Afc.StoredAsync(forum, digest, Ct);
        var first = stored.Count > 0 ? stored[0] : null;

        var board = Probe.Board();
        var reviser = await AcceptedAnswerScene.EnrolAsync(forum, "afc29", Ct);
        var question = await Probe.AskAsync(forum, reviser, board, Ct);
        var revised = await Probe.ReviseAsync(forum, reviser, board, question, null, null, Ct);
        var secondRevision = await Probe.ReviseAsync(forum, reviser, board, question, "Another " + Probe.Unrelated(), null, Ct);

        Said(
            $"members post_id digest server_ts risk_flags vector_index budget; the repeat 409 {IngestErrors.AlreadyRecordedType} naming {first?.PostId}; the second revision 409 {IngestErrors.AlreadyRevisedType}",
            $"members {string.Join(' ', Afc.MemberNames(body))}; the repeat {Answered(again)} naming {again.Detail?.Replace("post=", string.Empty, StringComparison.Ordinal)}; the second revision {Answered(secondRevision)}{(revised.Status == 201 ? string.Empty : $" (the first revision was {Answered(revised)})")}");
    }

    /// <summary>
    /// AFC30. The fixture sees the quantity (trap 90). The pipeline's instant (the payload's <c>server_ts</c>) is earlier than the store's column by at least 6 ms; the decorator's
    /// <c>Committed</c> counter moves when it commits and the table agrees; the read gate holds a read (a search made after a fault is held and then served); the host has no
    /// catch-up loop. A fixture that fails any of these fails this fact by name.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC30_TheFixtureSeesWhichInstantAReceiptCarries()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var committedBefore = host.Store.Committed;
        var tableBefore = (await host.LogShapeAsync(Ct)).Count;
        var sent = await Probe.SendViaAsync(forum, host.Client, actor, wire, token, Ct);
        var stored = await Afc.StoredAsync(forum, digest, Ct);
        var row = stored.Count > 0 ? stored[0] : null;
        var gap = row is null ? TimeSpan.Zero : row.ColumnServerTs - row.PayloadServerTs;
        var committedMoved = host.Store.Committed - committedBefore;
        var tableMoved = (await host.LogShapeAsync(Ct)).Count - tableBefore;

        var (second, secondWire, secondToken, _) = await AskerAsync(host);
        host.Store.Arm(EventStoreFault.HoldNextReadAfterFault);
        var ask = Probe.SendViaAsync(forum, host.Client, second, secondWire, secondToken, Ct);
        Task<CheckState>? search = null;
        await Task.WhenAny(host.Store.HeldReadStarted, ask, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        if (!host.Store.HeldReadStarted.IsCompleted)
        {
            // The route made no read after its fault (the seam): the probe makes the read, and the gate holds it.
            search = host.StateAsync(Ct);
            await Task.WhenAny(host.Store.HeldReadStarted, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        }

        var reached = host.Store.HeldReadStarted.IsCompleted;
        host.Store.ReleaseHeldRead();
        await ask;
        var served = search is null ? 200 : (await search).Status;
        host.Store.Disarm();

        const string ok = "the ask is 201; the column is at least 6 ms after the pipeline's instant; Committed and the table both moved by 1; the read gate held a read and the reads were then served; the host has no catch-up loop";
        var observed =
            $"the ask is {Answered(sent)}; the column is {gap.TotalMilliseconds:0.###} ms after the pipeline's instant; Committed moved by {committedMoved} and the table by {tableMoved}; " +
            $"the read gate {(reached ? "held a read" : "held no read")} and the reads were then {(served == 200 ? "served" : "answered " + served)}; the host has {(host.LoopIsAbsent ? "no" : "a")} catch-up loop";
        Said(ok, sent.Status == 201 && gap >= TimeSpan.FromMilliseconds(6) && committedMoved == 1 && tableMoved == 1 && reached && served == 200 && host.LoopIsAbsent ? ok : observed);
    }

    // ----- The fix round of Task 7 (the falsification's gaps): AFC33 to AFC40 -----

    private const string Released = "no registration after the request; the next check enrols the post (post_embeddings holds 1 row for the digest, the anti-join is 0)";

    /// <summary>What a check does with the post once the request is over: clock and ticks driven by the probe, the late commit (if any) landed first, a bounded number of ticks.</summary>
    private async Task<string> AfterTheRequestAsync(CheckHost host, SceneActor actor, string digest, Func<Task> tick)
    {
        var registered = host.Sweep.IsIndexing(digest);
        var settle = (int)PublishedCheckWords.SweepSettle().TotalSeconds;
        var embeddings = 0;
        for (var i = 0; i < (settle + 2) * 8 && embeddings == 0; i++)
        {
            await tick();
            embeddings = (await Afc.EmbeddingSequencesAsync(forum, digest, Ct)).Count;
        }

        var antiJoin = await host.AntiJoinAsync(actor.Agent.AgentId, Ct);
        return $"{(registered ? "the digest is still registered after the request" : "no registration after the request")}; " +
            $"the next check {(embeddings > 0 ? "enrols" : "does not enrol")} the post (post_embeddings holds {Plural(embeddings, "row")} for the digest, the anti-join is {antiJoin})";
    }

    /// <summary>
    /// AFC33, the exit <c>absent</c>. The confirm read the log and the act is not in it (a store that timed out and commits later: <c>ThrowThenCommitWhenReleased</c>): the
    /// answer is the append's 503, the digest is not registered as in flight after the request, and when the late commit lands the next check enrols the post. A registration
    /// that outlives the request hides the post from every check for as long as the process runs.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC33_TheRegistrationIsReleasedWhenTheConfirmFindsNothing()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var sent = await SendUnderAsync(host, EventStoreFault.ThrowThenCommitWhenReleased, actor, wire, token);
        host.Store.ReleaseLateCommit();
        await host.Store.LateCommitted.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var after = await AfterTheRequestAsync(host, actor, digest, () => host.TicksAsync(1, Ct));

        Said($"503 {Unavailable}; {Released}", $"{Answered(sent)}; {after}");
    }

    /// <summary>
    /// AFC34, the exit <c>unreadable</c>. The confirm's read fails over a post the log holds (<c>AppendThenThrowThenFailReads</c>): 503, the digest is not registered after
    /// the request, and the next check enrols the stored post.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC34_TheRegistrationIsReleasedWhenTheConfirmCouldNotRead()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var sent = await SendUnderAsync(host, EventStoreFault.AppendThenThrowThenFailReads, actor, wire, token);
        var after = await AfterTheRequestAsync(host, actor, digest, () => host.TicksAsync(1, Ct));

        Said($"503 {Unavailable}; {Released}", $"{Answered(sent)}; {after}");
    }

    /// <summary>
    /// AFC35, the exit <c>bound</c>. The confirm's read outlasts its bound (<c>AppendThenThrowThenHangReads</c> on a timer-capable clock, as AFC12): 503, the digest is not
    /// registered after the request, and the next check enrols the stored post.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC35_TheRegistrationIsReleasedWhenTheConfirmOutlastsItsBound()
    {
        var bound = TimeSpan.FromSeconds(PublishedConfirmWords.ConfirmBoundSeconds);
        var clock = new TimerManualTimeProvider(forum.Now);
        await using var host = forum.NewHost(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        });
        var (actor, wire, token, digest) = await AskerAsync(host);

        host.Store.Arm(EventStoreFault.AppendThenThrowThenHangReads);
        var send = Probe.SendViaAsync(forum, host.Client, actor, wire, token, Ct);
        await Task.WhenAny(host.Store.HangStarted, send, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        if (!host.Store.HangStarted.IsCompleted)
        {
            host.Store.Disarm();
            var early = await send;
            Said("the confirm's read was reached and waited", $"the confirm's read was never reached ({Answered(early)} was answered with no read after the fault)");
            return;
        }

        clock.Advance(bound);
        forum.Clock.Advance(bound);
        var sent = await send.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        host.Store.Disarm();
        var after = await AfterTheRequestAsync(host, actor, digest, () =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            forum.Clock.Advance(TimeSpan.FromSeconds(1));
            return host.TickAsync(forum.Now, Ct);
        });

        Said($"503 {Unavailable}; {Released}", $"{Answered(sent)}; {after}");
    }

    /// <summary>
    /// AFC36, the exit <c>stopped</c>. The host stops while the confirm's read is held (<c>HoldNextReadAfterFault</c>, then <c>StopApplication</c>, which ends the factory's
    /// host and disposes its container): the request ends (what a request answers while its host is being disposed is the host's, and is not asserted) and the digest is not registered after it. There is no next check in a process that has
    /// stopped, so the registration is read from the sweep the probe held before the stop.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC36_TheRegistrationIsReleasedWhenTheHostStopsTheConfirm()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);

        host.Store.Arm(EventStoreFault.HoldNextReadAfterFault);
        var send = Probe.SendViaAsync(forum, host.Client, actor, wire, token, Ct);
        await Task.WhenAny(host.Store.HeldReadStarted, send, Task.Delay(TimeSpan.FromSeconds(10), Ct));
        if (!host.Store.HeldReadStarted.IsCompleted)
        {
            host.Store.Disarm();
            var early = await send;
            Said("the confirm's read was reached and held", $"the confirm's read was never reached ({Answered(early)} was answered with no read after the fault)");
            return;
        }

        var store = host.Store;
        var sweep = host.Sweep;
        host.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();
        _ = await send.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        store.Disarm();

        Said("the request ended; no registration after the request", $"the request ended; {(sweep.IsIndexing(digest) ? "the digest is still registered after the request" : "no registration after the request")}");
    }

    /// <summary>
    /// AFC37, a found post whose index step did not end <c>Indexed</c>. The confirm finds the post and the vector index fails (<c>AppendThenThrow</c>, <c>FailUpserts</c>):
    /// 201 with <c>vector_index</c> pending, the digest is not registered after the request, and once the index heals the next check stores the post's vector.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC37_TheRegistrationIsReleasedWhenAFoundPostIsPending()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        host.Index.FailUpserts = true;
        var sent = await SendUnderAsync(host, EventStoreFault.AppendThenThrow, actor, wire, token);
        var vector = Afc.Member(sent.Body, "vector_index") ?? "absent";
        host.Index.FailUpserts = false;
        var after = await AfterTheRequestAsync(host, actor, digest, () => host.TicksAsync(1, Ct));

        Said($"201 with vector_index pending; {Released}", $"{Answered(sent)} with vector_index {vector}; {after}");
    }

    /// <summary>
    /// AFC38, a failed result of PERSIST that is not the conflict. The append returns a failed <c>Result</c> (<c>ReportAppend</c>), it does not throw: no confirm is made (no read
    /// after the fault, none that begins past the start of the log), nothing is stored and none is fabricated, and the answer is the route's own, <c>503 curia/store/unavailable</c> with the append's error.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC38_AFailedResultOfTheAppendIsNotConfirmedAndIsAnsweredAsBefore()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var sent = await SendUnderAsync(host, EventStoreFault.ReportAppend, actor, wire, token);
        var tail = host.Store.TailReadsAfterFault;
        var rows = await Afc.StoredAsync(forum, digest, Ct);

        Said(
            $"503 {Unavailable}; the table holds 0 events of the digest; the Forum made 0 reads of the log's tail after the failed result",
            $"{Answered(sent)}; the table holds {Plural(rows.Count, "event")} of the digest; the Forum made {Plural(tail, "read")} of the log's tail after the failed result");
    }

    /// <summary>
    /// AFC39, a failed result of PERSIST that is the conflict. Every append returns the condition conflict (<c>ReportConflictAppend</c>): the route decides again, as it did, three
    /// times, and answers <c>503</c> contended; no confirm is made, nothing is stored, none is fabricated, and the append was tried three times.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC39_AConflictOfTheAppendIsDecidedAgainAndNeverConfirmed()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var appendsBefore = host.Store.Appends;
        var sent = await SendUnderAsync(host, EventStoreFault.ReportConflictAppend, actor, wire, token);
        var appends = host.Store.Appends - appendsBefore;
        var tail = host.Store.TailReadsAfterFault;
        var rows = await Afc.StoredAsync(forum, digest, Ct);

        Said(
            $"503 {IngestErrors.Contended().Type}; the append was tried {IngestAttempts.Attempts} times; the table holds 0 events of the digest; the Forum made 0 reads of the log's tail after the failed result",
            $"{Answered(sent)}; the append was tried {appends} times; the table holds {Plural(rows.Count, "event")} of the digest; the Forum made {Plural(tail, "read")} of the log's tail after the failed result");
    }

    /// <summary>
    /// AFC40. The confirm finds the act by its digest and by nothing else: the same author's other post, stored after the act and before the confirm's read (appended straight to
    /// the store while the append is suspended: the agent's hold is the request's own), is not the post the receipt names. The ask is answered 201 naming the act's post.
    /// </summary>
    [Fact]
    public async Task R8_84_AFC40_ACommittedPostIsFoundByItsDigestWhateverTheSameAuthorStoredAfterIt()
    {
        await using var host = forum.NewHost();
        var (actor, wire, token, digest) = await AskerAsync(host);
        var laterWire = actor.Agent.SignQuestion(Probe.Board(), Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now);
        host.Store.OnInterleave = async ct => await BudgetBursts.StoreAsync(forum, laterWire, ct);

        var sent = await SendUnderAsync(host, EventStoreFault.InterleaveOtherAgentsPost, actor, wire, token);
        host.Store.OnInterleave = null;
        var rows = await Afc.StoredAsync(forum, digest, Ct);
        var later = await Afc.StoredAsync(forum, Afc.DigestOf(laterWire), Ct);
        var mine = rows.Count > 0 ? rows[0] : null;
        Assert.True(mine is not null && later.Count == 1 && later[0].Seq > mine.Seq && later[0].Author == mine.Author, $"the fixture did not store the author's later post after the act: act stored {rows.Count}, later stored {later.Count}");

        var expected = $"the ask is answered 201 naming {mine!.PostId}, not the same author's later post {later[0].PostId}; the table holds 1 event of the digest";
        var good = sent.Status == 201 && sent.PostId == mine.PostId && rows.Count == 1;
        Said(
            expected,
            good ? expected : $"the ask is answered {Answered(sent)} naming {sent.PostId ?? "no post"}{(sent.PostId == later[0].PostId ? ", the same author's later post" : string.Empty)}; the table holds {Plural(rows.Count, "event")} of the digest");
    }
}
