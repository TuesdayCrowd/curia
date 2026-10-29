using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Application.Ingest;
using Curia.Application.Projections;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R8.77 (errata G45) at the route, probes RVO1 to RVO18, RVO53 and RVO55: the Forum refuses the second revision of a served <c>prev</c>, under a gate, whatever the
/// order requests arrive in. The host is <see cref="RevisionOnceForumFixture"/>: the production Forum, its store, revision gate and policy decision
/// point wrapped by decorators that forward to the real objects.
///
/// <para><b>Oracles (plan ruling 13).</b> The <c>post.accepted</c> events the table holds with a <c>prev</c> (parsed from each event's canonical form),
/// the moderation records the test wrote, and the post id the winning request's own receipt returned; never the response of the request under test and
/// never <c>CorrectionIndex</c>. A control (green on the unmodified tree) says so and carries information only under the falsification row that names it.</para>
///
/// <para><b>Boards.</b> Every fact takes a board of its own. A fact that stores two posts of one digest takes the board it will not reuse, because a
/// stored duplicate disables a post kind on its board until R9.47 is built (D87).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class RevisionOnceEndpointTests : IClassFixture<RevisionOnceForumFixture>, IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private readonly RevisionOnceForumFixture _forum;

    public RevisionOnceEndpointTests(RevisionOnceForumFixture forum)
    {
        _forum = forum;
        _forum.ResetSeams();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _forum.ResetSeams();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<SceneActor> NewAsync(string stem) => AcceptedAnswerScene.EnrolAsync(_forum, stem, Ct);

    private Task<SceneActor> T1Async(string board) => AcceptedAnswerScene.T1Async(_forum, board, Ct);

    private Task<ScenePost> AskAsync(SceneActor actor, string board) => RevisionProbe.AskAsync(_forum, actor, board, Ct);

    private Task<Sent> ReviseAsync(SceneActor actor, string board, ScenePost of, string? body = null, string? token = null) =>
        RevisionProbe.ReviseAsync(_forum, actor, board, of, body, token, Ct);

    private Task<long> RevisionsOfAsync(ScenePost of) => RevisionProbe.RevisionsOfAsync(_forum, of.Digest);

    private static string Slug(Sent sent) => sent.Threw is null ? sent.Type : $"threw {sent.Threw}";

    /// <summary>The wire of a revision that names a parent the author chose (the `parent` is the author's word, not the Forum's).</summary>
    private byte[] RevisionNamingParent(SceneActor actor, string board, ScenePost of, string parent, string body) =>
        actor.Agent.SignRevision(board, body, parent, of.Digest, _forum.Now);

    // ---------------------------------------------------------------- RVO1

    /// <summary>
    /// RVO1. Forced interleave. The store parks every revision append: A is sent and parked; B (the same author's second revision of the same post) is sent
    /// while A is parked and is answered within 5 s by <c>revision-in-flight</c> whose detail names no post and no digest; A is released and answered 201;
    /// the table holds one revision of the post. Cannot be passed by a jitter-lucky run: it asserts what B is told while A is held at a named seam.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO1_ASecondRevisionWhileTheFirstIsBeingRecordedIsRefusedInFlight()
    {
        var board = Board();
        var author = await NewAsync("rvo1");
        var post = await AskAsync(author, board);

        _forum.Store.ParkRevisionAppends();
        var sendA = Task.Run(() => ReviseAsync(author, board, post, "First " + Unrelated()), Ct);
        var parked = await _forum.Store.WaitForParkedAsync(1, Wait);
        var sendB = Task.Run(() => ReviseAsync(author, board, post, "Second " + Unrelated()), Ct);
        var bFirst = await Task.WhenAny(sendB, Task.Delay(Wait, Ct)) == sendB ? await sendB : null;
        _forum.Store.Release();
        var a = await sendA;
        var b = await sendB;

        var bSaid = bFirst is null
            ? "B parked at the append after 5000 ms"
            : $"B {bFirst.Says} ({(bFirst.Detail is { } d && !d.Contains("successor=", StringComparison.Ordinal) && !d.Contains("sha256:", StringComparison.Ordinal) && !d.Contains(a.PostId ?? "-", StringComparison.Ordinal) ? "detail names no post or digest" : "detail: " + bFirst.Detail)})";
        Said(
            $"A parked; B 409 {IngestErrors.RevisionInFlightType} (detail names no post or digest); A 201; stored 1",
            $"{(parked ? "A parked" : "A never reached the append")}; {bSaid}; A {a.Status}; {(bFirst is null ? $"B {b.Status}; " : string.Empty)}stored {await RevisionsOfAsync(post)}");
    }

    // ---------------------------------------------------------------- RVO2

    /// <summary>One repetition: a question, then <paramref name="n"/> revisions of it sent together. What the table holds and how the responses went.</summary>
    private sealed record Round(ScenePost Question, long Stored, int Created, int Conflicts, int Other);

    /// <summary>
    /// <paramref name="n"/> hand-signed revisions of one fresh question, released together, <paramref name="reps"/> times; the clock moves past Table 11's
    /// window after each repetition so a T0 author's budget is not the thing under test (M1: the baseline red is 20 of 20 at N = 2 and N = 8).
    /// <paramref name="parentOf"/> is the `parent` member each revision names.
    /// </summary>
    private async Task<List<Round>> RaceAsync(string board, int n, int reps, Func<ScenePost, int, string>? parentOf = null)
    {
        var author = await NewAsync("race");
        var rounds = new List<Round>();
        for (var rep = 0; rep < reps; rep++)
        {
            var question = await AskAsync(author, board);
            var token = await author.Dpop.GetTokenAsync(_forum.Client, TokenEndpoint, _forum.Now, Ct);
            var sends = Enumerable.Range(0, n).Select(i =>
            {
                var wire = author.Agent.SignRevision(board, $"Correction {i} " + Unrelated(), parentOf?.Invoke(question, i) ?? question.PostId, question.Digest, _forum.Now);
                return (Func<Task<Sent>>)(() => SendAsync(_forum, author, wire, token, Ct));
            });
            var results = await TogetherAsync(sends);
            rounds.Add(new Round(
                question,
                await RevisionsOfAsync(question),
                results.Count(r => r.Status == 201),
                results.Count(r => r.Status == 409 && r.Type is IngestErrors.AlreadyRevisedType or IngestErrors.RevisionInFlightType),
                results.Count(r => r.Status is not 201 and not 409 || r.Status == 409 && r.Type is not (IngestErrors.AlreadyRevisedType or IngestErrors.RevisionInFlightType))));
            _forum.Clock.Advance(TimeSpan.FromHours(25));
        }

        return rounds;
    }

    private static string Summarise(int n, List<Round> rounds) =>
        $"N={n}: {rounds.Count(r => r.Stored > 1)} forked of {rounds.Count}, " +
        $"{rounds.Count(r => r.Created != 1 || r.Conflicts != n - 1 || r.Other != 0)} badly answered (largest stored for one prev: {rounds.Max(r => r.Stored)})";

    /// <summary>
    /// RVO2. The race, 2 and 8 together, 50 repetitions each (property P6's first probe): every repetition holds exactly one revision of the question and
    /// answers the other N-1 with 409 of either revision slug, never 201.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO2_SimultaneousRevisionsOfOnePrevStoreExactlyOne()
    {
        var two = await RaceAsync(Board(), 2, 50);
        var eight = await RaceAsync(Board(), 8, 50);

        Said(
            "N=2: 0 forked of 50, 0 badly answered (largest stored for one prev: 1); N=8: 0 forked of 50, 0 badly answered (largest stored for one prev: 1)",
            $"{Summarise(2, two)}; {Summarise(8, eight)}");
    }

    // ---------------------------------------------------------------- RVO3

    /// <summary>RVO3. A sequential second revision by the same author is <c>already-revised</c>, naming the first revision's post id, and stores nothing.</summary>
    [Fact]
    public async Task R8_77_RVO3_ASequentialSecondRevisionIsRefusedNamingTheFirst()
    {
        var board = Board();
        var author = await NewAsync("rvo3");
        var post = await AskAsync(author, board);
        var first = await ReviseAsync(author, board, post);
        Assert.Equal(201, first.Status);

        var second = await ReviseAsync(author, board, post);

        Said(
            $"409 {IngestErrors.AlreadyRevisedType}; successor={first.PostId}; stored 1",
            $"{second.Says}; {second.Detail}; stored {await RevisionsOfAsync(post)}");
    }

    // ---------------------------------------------------------------- RVO4

    /// <summary>RVO4 (a) and (b), controls: a revision the Forum does not serve does not count (served, not stored). Green now; carries information only under FRVO8.</summary>
    [Theory]
    [InlineData("withheld")]
    [InlineData("quarantined")]
    public async Task R8_77_RVO4_ARevisionTheForumDoesNotServeDoesNotCountAsTheSuccessor(string how)
    {
        ArgumentNullException.ThrowIfNull(how);
        var board = Board();
        var author = await NewAsync("rvo4");
        var post = await AskAsync(author, board);
        var first = await ReviseAsync(author, board, post);
        Assert.Equal(201, first.Status);
        if (how == "withheld") await _forum.WithholdAsync(first.PostId!, Ct);
        else await _forum.QuarantineAsync(first.PostId!, Ct);

        var second = await ReviseAsync(author, board, post);
        var served = await SupersededByAsync(_forum, post.PostId, Ct);

        Said(
            $"201; stored 2; superseded_by serves the new revision {second.PostId}",
            $"{second.Says.Split(' ')[0]}; stored {await RevisionsOfAsync(post)}; superseded_by serves {(served.Any(s => s.PostId == second.PostId) ? "the new revision " + second.PostId : "[" + string.Join(", ", served.Select(s => s.PostId ?? "(digest only)")) + "]")}");
    }

    /// <summary>
    /// RVO4 (c). A moderator's restore of the withheld first revision while the second stands makes two served successors (a fork by a moderator's act,
    /// which FORKED still tells); a third revision of the post is refused naming the first served in log order, and the post serves both.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO4_ARestoredFirstRevisionBesideASecondRefusesAThird()
    {
        var board = Board();
        var author = await T1Async(board);
        var post = await AskAsync(author, board);
        var first = await ReviseAsync(author, board, post);
        await _forum.WithholdAsync(first.PostId!, Ct);
        var second = await ReviseAsync(author, board, post);
        Assert.Equal(201, second.Status);
        await _forum.RestoreAsync(first.PostId!, Ct);

        var third = await ReviseAsync(author, board, post);
        var served = await SupersededByAsync(_forum, post.PostId, Ct);

        Said(
            $"409 {IngestErrors.AlreadyRevisedType}; successor={first.PostId}; stored 2; the post serves 2 successors",
            $"{third.Says}; {third.Detail}; stored {await RevisionsOfAsync(post)}; the post serves {served.Length} successors");
    }

    // ---------------------------------------------------------------- RVO5

    /// <summary>RVO5, control. A stranger revising an already-revised post is told Table 10's ownership refusal and learns no successor; nothing is stored. Green now; red under FRVO11.</summary>
    [Fact]
    public async Task R8_77_RVO5_AStrangerIsToldOwnershipAndLearnsNoSuccessor()
    {
        var board = Board();
        var author = await NewAsync("rvo5-author");
        var stranger = await NewAsync("rvo5-stranger");
        var post = await AskAsync(author, board);
        Assert.Equal(201, (await ReviseAsync(author, board, post)).Status);

        var refused = await ReviseAsync(stranger, board, post);

        Said(
            "403; the detail names table-10/own-resource-only; no successor= and no already-revised; stored 1",
            $"{refused.Status}; the detail {(refused.Detail?.Contains("table-10/own-resource-only", StringComparison.Ordinal) == true ? "names table-10/own-resource-only" : "is " + refused.Detail)}; " +
            $"{(refused.Body.Contains("successor=", StringComparison.Ordinal) || refused.Body.Contains("already-revised", StringComparison.Ordinal) ? "the body names a successor" : "no successor= and no already-revised")}; stored {await RevisionsOfAsync(post)}");
    }

    // ---------------------------------------------------------------- RVO6

    /// <summary>RVO6. No unilateral hold: a stranger's revision, answered 403, never reaches the gate; the author's does, once.</summary>
    [Fact]
    public async Task R8_77_RVO6_OnlyTheAuthorsRevisionReachesTheGate()
    {
        var board = Board();
        var author = await NewAsync("rvo6-author");
        var stranger = await NewAsync("rvo6-stranger");
        var post = await AskAsync(author, board);
        _forum.Gate.Reset();

        var refused = await ReviseAsync(stranger, board, post);
        var accepted = await ReviseAsync(author, board, post);

        Said(
            "stranger 403; author 201; the gate was entered for the post 1 time",
            $"stranger {refused.Status}; author {accepted.Status}; the gate was entered for the post {_forum.Gate.Entered.Count(d => d == post.Digest)} time");
    }

    // ---------------------------------------------------------------- RVO7

    /// <summary>
    /// RVO7. The hold is released on every path out of the submission, and after the commit. Each path takes a post of its own; the gate's counters are read
    /// from a clean start; at the stored revision's first release the table already holds its event (a release before the commit sees none).
    /// </summary>
    [Fact]
    public async Task R8_77_RVO7_TheHoldIsReleasedOnEveryPathAndAfterTheCommit()
    {
        var board = Board();
        var author = await T1Async(board);
        var lines = new List<string>();

        async Task<string> NextIsNotInFlightAsync(ScenePost post)
        {
            var next = await ReviseAsync(author, board, post);
            return $"next {next.Says}";
        }

        string Counters() => $"entered {_forum.Gate.Entered.Count} released {_forum.Gate.ReleasedCount}";

        // A stored revision: the table holds its event when the hold is released.
        var p0 = await AskAsync(author, board);
        _forum.Gate.Reset();
        var stored = await ReviseAsync(author, board, p0);
        lines.Add($"stored {stored.Status}: {Counters()}; events at release {string.Join(",", _forum.Gate.Releases.Select(r => r.RevisionEvents))}");

        // 409 already-revised.
        _forum.Gate.Reset();
        var again = await ReviseAsync(author, board, p0);
        lines.Add($"second {again.Status}: {Counters()}");

        // A SCREEN rejection: a credential in the body (422).
        var p1 = await AskAsync(author, board);
        _forum.Gate.Reset();
        var leaked = await ReviseAsync(author, board, p1, "My token is ghp_A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3gI5o");
        var leakedCounters = Counters();
        var afterLeak = await NextIsNotInFlightAsync(p1);
        lines.Add($"screened {leaked.Status}: {leakedCounters}; {afterLeak}");

        // A store that throws at the append.
        var p2 = await AskAsync(author, board);
        _forum.Gate.Reset();
        _forum.Store.ThrowOnRevisionAppend = true;
        var thrown = await ReviseAsync(author, board, p2);
        _forum.Store.ThrowOnRevisionAppend = false;
        var thrownCounters = Counters();
        var afterThrow = await NextIsNotInFlightAsync(p2);
        lines.Add($"append throws {thrown.Status}: {thrownCounters}; {afterThrow}");

        // A client that cancels while the append is parked.
        var p3 = await AskAsync(author, board);
        _forum.Gate.Reset();
        _forum.Store.ParkRevisionAppends();
        using (var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            var inFlight = Task.Run(() => RevisionProbe.ReviseAsync(_forum, author, board, p3, null, null, cancel.Token), Ct);
            Assert.True(await _forum.Store.WaitForParkedAsync(1, Wait), "the revision never reached the append");
            await cancel.CancelAsync();
            _ = await inFlight;
        }

        _forum.Store.Release();
        await Task.Delay(300, Ct);
        var cancelledCounters = Counters();
        var afterCancel = await NextIsNotInFlightAsync(p3);
        lines.Add($"cancelled: {cancelledCounters}; {afterCancel}");

        // One lost race on the key stream (R6.56): the attempt enters again.
        var racer = await NewAsync("rvo7-racer");
        var p4 = await AskAsync(racer, board);
        _forum.Gate.Reset();
        _forum.Contention.Arm(
            Curia.Domain.AggregateId.Create(racer.Agent.AgentId).Match(v => v, e => throw new InvalidOperationException(e.Type)),
            1,
            token => _forum.AttestOwnerAsync(racer.Agent.AgentId, token, verified: false));
        var raced = await ReviseAsync(racer, board, p4);
        _forum.Contention.Disarm();
        lines.Add($"lost race {raced.Status}: {Counters()}");

        Said(
            [
                "stored 201: entered 1 released 1; events at release 1",
                "second 409: entered 1 released 1",
                "screened 422: entered 1 released 1; next 201",
                "append throws 500: entered 1 released 1; next 201",
                "cancelled: entered 1 released 1; next 201",
                "lost race 201: entered 2 released 2",
            ],
            lines);
    }

    // ---------------------------------------------------------------- RVO8

    /// <summary>RVO8 (a). Fail closed: a gate whose store cannot be reached answers 503 <c>revision-gate-unavailable</c>, without the store's words, and stores nothing.</summary>
    [Fact]
    public async Task R8_77_RVO8_AGateThatCannotBeReachedRefusesAndStoresNothing()
    {
        var board = Board();
        var author = await NewAsync("rvo8a");
        var post = await AskAsync(author, board);
        _forum.Gate.FailEntries = true;

        var refused = await ReviseAsync(author, board, post);

        Said(
            $"503 {IngestErrors.RevisionGateUnavailableType}; no detail; stored 0",
            $"{refused.Says}; {(refused.Detail is null ? "no detail" : "detail " + refused.Detail)}; stored {await RevisionsOfAsync(post)}");
    }

    /// <summary>RVO8 (b). Fail closed: a log that cannot be read under the hold answers 503 and stores nothing; it is never read as empty.</summary>
    [Fact]
    public async Task R8_77_RVO8_ALogThatCannotBeReadUnderTheHoldRefusesAndStoresNothing()
    {
        var board = Board();
        var author = await NewAsync("rvo8b");
        var post = await AskAsync(author, board);
        _forum.Store.FailReadsWhile = () => _forum.Gate.HeldCount > 0;

        var refused = await ReviseAsync(author, board, post);

        Said("503; stored 0", $"{refused.Status}; stored {await RevisionsOfAsync(post)}");
    }

    // ---------------------------------------------------------------- RVO9

    /// <summary>
    /// RVO9, sequential, on a board of its own (a stored duplicate disables the board's revisions until R9.47). The same signed bytes sent twice: the second
    /// is <c>already-recorded</c> naming the first's post id (ruling 17; M2 found no existing refusal to keep); the table holds one event of the digest; a
    /// different revision on that board is accepted and is not a 500.
    /// </summary>
    [Fact]
    public async Task R8_78_RVO9_TheSameRevisionBytesTwiceAreRefusedTheSecondTime()
    {
        var board = Board();
        var author = await T1Async(board);
        var post = await AskAsync(author, board);
        var other = await AskAsync(author, board);
        var wire = author.Agent.SignRevision(board, "Correction " + Unrelated(), post.PostId, post.Digest, _forum.Now);

        var first = await SendAsync(_forum, author, wire, cancellationToken: Ct);
        var second = await SendAsync(_forum, author, wire, cancellationToken: Ct);
        var events = await EventsWithDigestAsync(_forum, first.Digest ?? "none");
        var next = await ReviseAsync(author, board, other);

        Said(
            $"{first.Status}; 409 {IngestErrors.AlreadyRecordedType} post={first.PostId}; 1 event with the digest; the next revision on the board 201",
            $"{first.Status}; {second.Says} {second.Detail}; {events} event with the digest; the next revision on the board {next.Says}");
    }

    /// <summary>
    /// RVO9, concurrent, on a board of its own. The same bytes released together: one 201 and one 409 of either revision slug (<c>already-recorded</c> if the
    /// other has committed, <c>revision-in-flight</c> if it holds the gate); one event of the digest; a different revision on the board is 201.
    /// </summary>
    [Fact]
    public async Task R8_78_RVO9_TheSameRevisionBytesTogetherStoreOnce()
    {
        var board = Board();
        var author = await T1Async(board);
        var post = await AskAsync(author, board);
        var other = await AskAsync(author, board);
        var wire = author.Agent.SignRevision(board, "Correction " + Unrelated(), post.PostId, post.Digest, _forum.Now);
        var token = await author.Dpop.GetTokenAsync(_forum.Client, TokenEndpoint, _forum.Now, Ct);

        var pair = await TogetherAsync([() => SendAsync(_forum, author, wire, token, Ct), () => SendAsync(_forum, author, wire, token, Ct)]);
        var digest = pair.Select(s => s.Digest).FirstOrDefault(d => d is not null);
        var events = digest is null ? -1 : await EventsWithDigestAsync(_forum, digest);
        var next = await ReviseAsync(author, board, other);

        var refusedSlugs = new[] { IngestErrors.AlreadyRecordedType, IngestErrors.RevisionInFlightType };
        var shape = pair.Count(s => s.Status == 201) == 1 && pair.Count(s => s.Status == 409 && refusedSlugs.Contains(s.Type)) == 1
            ? "one 201 and one 409 of a revision slug"
            : string.Join(" and ", pair.Select(s => s.Says));
        Said(
            "one 201 and one 409 of a revision slug; 1 event with the digest; the next revision on the board 201",
            $"{shape}; {events} event with the digest; the next revision on the board {next.Says}");
    }

    // ---------------------------------------------------------------- RVO10

    /// <summary>
    /// RVO10. A stored fork replays and is read as before (the three readings are controls): two revisions of a post stored with ingest bypassed are both
    /// listed by the post, named as a fork by the batch and by each branch. A third revision through the route is refused naming the first in log order; a
    /// revision of one branch is accepted.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO10_AStoredForkIsReadAsBeforeAndRefusesAThirdRevision()
    {
        var board = Board();
        var author = await T1Async(board);
        var post = await AskAsync(author, board);
        var (id1, digest1) = await _forum.AppendStoredPostAsync(author.Agent.SignRevision(board, "Branch one " + Unrelated(), post.PostId, post.Digest, _forum.Now), Ct);
        var (id2, digest2) = await _forum.AppendStoredPostAsync(author.Agent.SignRevision(board, "Branch two " + Unrelated(), post.PostId, post.Digest, _forum.Now), Ct);

        var served = await SupersededByAsync(_forum, post.PostId, Ct);
        using var batch = await _forum.Client.PostAsJsonAsync("/v1/posts/batch", new { digests = new[] { post.Digest, digest1 } }, Ct);
        using var items = JsonDocument.Parse(await batch.Content.ReadAsStringAsync(Ct));
        var all = items.RootElement.GetProperty("items").EnumerateArray().ToArray();
        var readings =
            $"the post serves {string.Join("+", served.Select(s => s.PostId == id1 ? "R1" : s.PostId == id2 ? "R2" : "?"))}; " +
            $"the batch says {(all[0].GetProperty("forked").GetBoolean() ? "forked" : "not forked")} with {all[0].GetProperty("successors").GetArrayLength()} successors; " +
            $"R1 names {string.Join("+", all[1].GetProperty("fork").EnumerateArray().Select(f => f.GetProperty("digest").GetString() == digest2 ? "R2" : "?"))}";

        var third = await ReviseAsync(author, board, post);
        var branch = await RevisionProbe.SendAsync(_forum, author, author.Agent.SignRevision(board, "Of branch one " + Unrelated(), id1, digest1, _forum.Now), cancellationToken: Ct);

        Said(
            $"the post serves R1+R2; the batch says forked with 2 successors; R1 names R2; third 409 {IngestErrors.AlreadyRevisedType} successor={id1}; branch 201",
            $"{readings}; third {third.Says} {third.Detail}; branch {branch.Status}");
    }

    // ---------------------------------------------------------------- RVO11

    /// <summary>
    /// RVO11. The `parent` is the author's word, not the gate's key: two concurrent revisions of one `prev`, each naming a different `parent` (a post and an
    /// id no post bears), 50 repetitions; exactly one is stored in each.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO11_TheGateIsKeyedByPrevAndNotByTheAuthorsParent()
    {
        var rounds = await RaceAsync(Board(), 2, 50, (question, i) => i == 0 ? question.PostId : "01NOPOST" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant());

        Said(
            "50 repetitions of 50 stored exactly one revision (largest stored for one prev: 1)",
            $"{rounds.Count(r => r.Stored == 1)} repetitions of {rounds.Count} stored exactly one revision (largest stored for one prev: {rounds.Max(r => r.Stored)})");
    }

    // ---------------------------------------------------------------- RVO12

    /// <summary>
    /// RVO12. The log AUTHORIZE read is stale: B is parked inside AUTHORIZE (its log read before A exists); A, the same author's revision of the same post,
    /// runs to 201; B is released and is refused <c>already-revised</c>.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO12_ARequestParkedInAuthorizeIsRefusedOnTheLogAsItThenStands()
    {
        var board = Board();
        var author = await NewAsync("rvo12");
        var post = await AskAsync(author, board);

        _forum.Decisions.ParkNextRevisionDecision();
        var sendB = Task.Run(() => ReviseAsync(author, board, post, "B " + Unrelated()), Ct);
        var parked = await _forum.Decisions.WaitForParkedAsync(Wait);
        var a = await ReviseAsync(author, board, post, "A " + Unrelated());
        _forum.Decisions.Release();
        var b = await sendB;

        Said(
            $"B parked in AUTHORIZE; A 201; B 409 {IngestErrors.AlreadyRevisedType}; stored 1",
            $"{(parked ? "B parked in AUTHORIZE" : "no decision parked")}; A {a.Status}; B {b.Says}; stored {await RevisionsOfAsync(post)}");
    }

    // ---------------------------------------------------------------- RVO13

    /// <summary>RVO13, control. Other work is not gated: two concurrent comments by one author on a question, two concurrent answers by one T1 author, and two concurrent revisions by one author of two posts are all accepted. Green now; red under FRVO5 and FRVO18.</summary>
    [Fact]
    public async Task R8_77_RVO13_OtherConcurrentWorkIsNotGated()
    {
        var board = Board();
        var asker = await T1Async(board);
        var answerer = await T1Async(board);
        var question = await AskAsync(asker, board);
        var other = await AskAsync(asker, board);
        var askerToken = await asker.Dpop.GetTokenAsync(_forum.Client, TokenEndpoint, _forum.Now, Ct);
        var answererToken = await answerer.Dpop.GetTokenAsync(_forum.Client, TokenEndpoint, _forum.Now, Ct);

        var comments = await TogetherAsync(Enumerable.Range(0, 2).Select(i =>
        {
            var wire = asker.Agent.Sign(PostKind.Comment, board, $"Comment {i} " + Unrelated(), null, question.PostId, _forum.Now);
            return (Func<Task<Sent>>)(() => SendAsync(_forum, asker, wire, askerToken, Ct));
        }));
        var answers = await TogetherAsync(Enumerable.Range(0, 2).Select(i =>
        {
            var wire = answerer.Agent.SignAnswer(board, $"Answer {i} " + Unrelated(), question.PostId, _forum.Now);
            return (Func<Task<Sent>>)(() => SendAsync(_forum, answerer, wire, answererToken, Ct));
        }));
        var revisions = await TogetherAsync(new[] { question, other }.Select(post =>
        {
            var wire = asker.Agent.SignRevision(board, "Correction " + Unrelated(), post.PostId, post.Digest, _forum.Now);
            return (Func<Task<Sent>>)(() => SendAsync(_forum, asker, wire, askerToken, Ct));
        }));

        string Statuses(Sent[] sent) => string.Join(",", sent.Select(s => s.Status));
        Said(
            "comments 201,201; answers 201,201; revisions of two posts 201,201",
            $"comments {Statuses(comments)}; answers {Statuses(answers)}; revisions of two posts {Statuses(revisions)}");
    }

    // ---------------------------------------------------------------- RVO14

    /// <summary>
    /// RVO14. The refusals' shape: both 409s are <c>application/problem+json</c> with type, title, status and exactly the factory's detail; a sentinel the author
    /// wrote into the revision's body, title and `parent` appears nowhere in either response; <c>revision-in-flight</c> names no post and no digest.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO14_TheTwoRefusalsAreProblemDocumentsThatEchoNothingAnAuthorWrote()
    {
        var board = Board();
        var author = await T1Async(board);
        var post = await AskAsync(author, board);
        var sentinel = "ZQSENTINEL" + Suffix();
        byte[] Wire(string body) => author.Agent.Sign(PostKind.Revision, board, $"{body} {sentinel} " + Unrelated(), sentinel + " title", sentinel + "-parent", _forum.Now, prev: post.Digest);

        _forum.Store.ParkRevisionAppends();
        var sendA = Task.Run(() => SendAsync(_forum, author, Wire("A"), cancellationToken: Ct), Ct);
        await _forum.Store.WaitForParkedAsync(1, Wait);
        var sendB = Task.Run(() => SendAsync(_forum, author, Wire("B"), cancellationToken: Ct), Ct);
        var inFlight = await Task.WhenAny(sendB, Task.Delay(Wait, Ct)) == sendB ? await sendB : null;
        _forum.Store.Release();
        var a = await sendA;
        _ = await sendB;
        var sequential = await SendAsync(_forum, author, Wire("C"), cancellationToken: Ct);

        var inFlightProblems = inFlight is null ? "B parked at the append" : RevisionProbe.ShapeProblems(inFlight, IngestErrors.RevisionInFlight(), 409, sentinel);
        var revisedProblems = RevisionProbe.ShapeProblems(sequential, IngestErrors.AlreadyRevised(a.PostId ?? "none"), 409, sentinel);
        Said(
            "in-flight: none; already-revised: none",
            $"in-flight: {(inFlightProblems.Length == 0 ? "none" : inFlightProblems)}; already-revised: {(revisedProblems.Length == 0 ? "none" : revisedProblems)}");
    }

    // ---------------------------------------------------------------- RVO15

    /// <summary>RVO15. A refused revision spends nothing (a sequential refusal; simultaneous requests are not capped by Table 11 at all, D88): a T0 author at two posts, refused a second revision, may still post a comment.</summary>
    [Fact]
    public async Task R8_77_RVO15_ARefusedRevisionSpendsNoBudget()
    {
        var board = Board();
        var author = await NewAsync("rvo15");
        var post = await AskAsync(author, board);
        Assert.Equal(201, (await ReviseAsync(author, board, post)).Status);

        var refused = await ReviseAsync(author, board, post);
        var comment = await SendAsync(_forum, author, author.Agent.Sign(PostKind.Comment, board, "A comment " + Unrelated(), null, post.PostId, _forum.Now), cancellationToken: Ct);

        Said(
            $"second revision 409 {IngestErrors.AlreadyRevisedType}; comment 201",
            $"second revision {refused.Says}; comment {comment.Says}");
    }

    // ---------------------------------------------------------------- RVO16

    /// <summary>RVO16, control. The chain continues: the author revises the revision; the post lists the first revision only, the first lists the second only. Green now.</summary>
    [Fact]
    public async Task R8_77_RVO16_TheChainContinuesFromTheRevision()
    {
        var board = Board();
        var author = await NewAsync("rvo16");
        var post = await AskAsync(author, board);
        var first = await ReviseAsync(author, board, post);
        var next = await RevisionProbe.SendAsync(_forum, author, author.Agent.SignRevision(board, "Second correction " + Unrelated(), first.PostId!, first.Digest!, _forum.Now), cancellationToken: Ct);

        var ofPost = await SupersededByAsync(_forum, post.PostId, Ct);
        var ofFirst = await SupersededByAsync(_forum, first.PostId!, Ct);

        Said(
            $"revision of the revision 201; the post lists {first.PostId}; the revision lists {next.PostId}",
            $"revision of the revision {next.Status}; the post lists {string.Join("+", ofPost.Select(s => s.PostId))}; the revision lists {string.Join("+", ofFirst.Select(s => s.PostId))}");
    }

    // ---------------------------------------------------------------- RVO17

    /// <summary>
    /// RVO17. Replay drill (R11.9): after a 4-way race, repeated, the read models rebuilt from the events table list for every original the successors the
    /// live read lists, and no original has more than one. Shares RVO2's oracle by design.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO17_ARebuildFromTheLogAgreesWithTheLiveReadAndHoldsOneHeadPerPost()
    {
        var rounds = await RaceAsync(Board(), 4, 10);

        var log = await ReadLogAsync(_forum, Ct);
        var posts = PostProjector.Fold(log);
        var rebuilt = CorrectionIndex.Fold(posts, static _ => true);
        var lines = new List<string>();
        foreach (var round in rounds)
        {
            var original = posts.Single(p => p.PostId == round.Question.PostId);
            var live = (await SupersededByAsync(_forum, round.Question.PostId, Ct)).Length;
            var replayed = rebuilt.For(original).Length;
            lines.Add($"live {live} rebuilt {replayed}");
        }

        Said(
            Enumerable.Repeat("live 1 rebuilt 1", rounds.Count).ToArray(),
            lines.ToArray());
    }

    // ---------------------------------------------------------------- RVO18

    /// <summary>RVO18, control. Not every post naming the digest is a correction: a stranger's revision and a comment naming it (seeded, as ingest refuses both, R8.64) do not block the author's revision. Green now; red under FRVO10.</summary>
    [Fact]
    public async Task R8_77_RVO18_PostsThatAreNotCorrectionsDoNotBlockTheAuthor()
    {
        var board = Board();
        var author = await T1Async(board);
        var stranger = await NewAsync("rvo18-stranger");
        var post = await AskAsync(author, board);
        await _forum.AppendStoredPostAsync(stranger.Agent.Sign(PostKind.Revision, board, "Not the author's " + Unrelated(), null, post.PostId, _forum.Now, prev: post.Digest), Ct);
        await _forum.AppendStoredPostAsync(author.Agent.Sign(PostKind.Comment, board, "The author's own comment " + Unrelated(), null, post.PostId, _forum.Now, prev: post.Digest), Ct);

        var revised = await ReviseAsync(author, board, post);

        Said("201", revised.Says);
    }

    // ---------------------------------------------------------------- RVO53

    /// <summary>
    /// RVO53. Other work is not held up by a request parked in its own gate, at a named seam and not by timing. Twice, one request is parked at the store's
    /// append (first a comment, then a revision of a post), and while it is parked the same author's comment on a question, a T1 author's answer to it and a
    /// revision of a different post are each answered <c>201</c> within 5 s; then the parked request is released and answered <c>201</c>. RVO13 releases its
    /// requests together and can pass a gate on every kind when they do not overlap; this one holds a request in its gate. Green now; red under FRVO18's first form.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO53_WorkIsNotHeldUpByARequestParkedInItsOwnGate()
    {
        var board = Board();
        var asker = await T1Async(board);
        var answerer = await T1Async(board);
        var question = await AskAsync(asker, board);
        var firstOther = await AskAsync(asker, board);
        var secondOther = await AskAsync(asker, board);
        var askerToken = await asker.Dpop.GetTokenAsync(_forum.Client, TokenEndpoint, _forum.Now, Ct);
        var answererToken = await answerer.Dpop.GetTokenAsync(_forum.Client, TokenEndpoint, _forum.Now, Ct);

        Task<Sent> Comment() => SendAsync(_forum, asker, asker.Agent.Sign(PostKind.Comment, board, "Comment " + Unrelated(), null, question.PostId, _forum.Now), askerToken, Ct);
        Task<Sent> Answer() => SendAsync(_forum, answerer, answerer.Agent.SignAnswer(board, "Answer " + Unrelated(), question.PostId, _forum.Now), answererToken, Ct);
        Task<Sent> Revise(ScenePost of) => SendAsync(_forum, asker, asker.Agent.SignRevision(board, "Correction " + Unrelated(), of.PostId, of.Digest, _forum.Now), askerToken, Ct);

        async Task<string> WhileParkedAsync(Task<Sent> parkedSend, ScenePost revised)
        {
            var parkedAtSeam = await _forum.Store.WaitForParkedAsync(1, Wait);
            var all = Task.WhenAll(Task.Run(Comment, Ct), Task.Run(Answer, Ct), Task.Run(() => Revise(revised), Ct));
            var answered = await Task.WhenAny(all, Task.Delay(Wait, Ct)) == all ? await all : null;
            _forum.Store.Release();
            var parked = await parkedSend;
            var said = answered is null
                ? "no answer within 5 s"
                : $"comment {answered[0].Says}, answer {answered[1].Says}, revision of another post {answered[2].Says}";
            if (answered is null) _ = await all;
            return $"{(parkedAtSeam ? "parked" : "never parked")}; {said}; parked request {parked.Says}";
        }

        _forum.Store.ParkNextAppendOf("comment");
        var parkedComment = Task.Run(Comment, Ct);
        var whileCommentParked = await WhileParkedAsync(parkedComment, firstOther);

        _forum.Store.ParkNextAppendOf("revision");
        var parkedRevision = Task.Run(() => Revise(secondOther), Ct);
        var whileRevisionParked = await WhileParkedAsync(parkedRevision, question);

        const string Fine = "parked; comment 201, answer 201, revision of another post 201; parked request 201";
        Said(
            $"comment parked: {Fine}; revision parked: {Fine}",
            $"comment parked: {whileCommentParked}; revision parked: {whileRevisionParked}");
    }

    // ---------------------------------------------------------------- RVO55

    /// <summary>
    /// RVO55. The three refusals' types are the spellings the errata publishes. The slugs are read from R8.77's and R8.78's paragraphs at test time
    /// (<see cref="PublishedRevisionSlugs"/>), not from <see cref="IngestErrors"/>, and each Forum response's <c>type</c> is compared with them: a revision
    /// sent while another is parked (in flight), one sent after it was recorded (already revised) and the first one's bytes sent again (already recorded).
    /// RVO14 and RVO9 compare with the production constants, so a constant whose value changes moves both sides; this does not. Green now; red under
    /// FRVO22's first form and under a change of a constant's value.
    /// </summary>
    [Fact]
    public async Task R8_77_RVO55_TheThreeRefusalsAnswerTheSlugsTheErrataPublishes()
    {
        var board = Board();
        var author = await T1Async(board);
        var post = await AskAsync(author, board);
        var wireA = author.Agent.SignRevision(board, "A " + Unrelated(), post.PostId, post.Digest, _forum.Now);

        _forum.Store.ParkRevisionAppends();
        var sendA = Task.Run(() => SendAsync(_forum, author, wireA, cancellationToken: Ct), Ct);
        await _forum.Store.WaitForParkedAsync(1, Wait);
        var sendB = Task.Run(() => ReviseAsync(author, board, post, "B " + Unrelated()), Ct);
        var inFlight = await Task.WhenAny(sendB, Task.Delay(Wait, Ct)) == sendB ? await sendB : null;
        _forum.Store.Release();
        _ = await sendA;
        _ = await sendB;
        var revised = await ReviseAsync(author, board, post, "C " + Unrelated());
        var recorded = await SendAsync(_forum, author, wireA, cancellationToken: Ct);

        Said(
            $"in flight {PublishedRevisionSlugs.RevisionInFlight()}; already revised {PublishedRevisionSlugs.AlreadyRevised()}; already recorded {PublishedRevisionSlugs.AlreadyRecorded()}",
            $"in flight {(inFlight is null ? "B parked at the append" : Slug(inFlight))}; already revised {Slug(revised)}; already recorded {Slug(recorded)}");
    }
}
