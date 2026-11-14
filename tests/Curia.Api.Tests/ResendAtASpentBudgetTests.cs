using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// ASA30 to ASA34, ASA36 and ASA37 (errata G58, R7.32, R11.79): the route, in the Production composition on a real Postgres connecting as the application role
/// (<see cref="LateCommitForumFixture"/>), with its store counting the reads it is asked for. <b>Oracles</b>: the events table by SQL on the fixture's own connection string (the one
/// that carries the password: trap 134), the budget readout (<c>GET /v1/budget</c>) and the store's own read counters, never the response of the request under test. The status, type and detail
/// of <c>already-recorded</c> are read from G57's <c>R8.86 words</c> (<see cref="PublishedLateCommitWords"/>). A probe green on the seam is a control and says so: the gap R7.32 closes is at a
/// spent posting budget, and everything around it must not move.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every statement is constant text and every value is bound as a parameter.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each holder and kit is a using declaration; the analyzer loses the ownership across the awaits.")]
[Collection("forum")]
public sealed class ResendAtASpentBudgetTests : IClassFixture<LateCommitForumFixture>, IAsyncLifetime
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private readonly LateCommitForumFixture _forum;
    private readonly List<string> _homes = [];

    public ResendAtASpentBudgetTests(LateCommitForumFixture forum)
    {
        _forum = forum;
        _forum.ResetSeams();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _forum.ResetSeams();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual) => Probe.Said(expected, actual);

    private static (string Type, int Status) AlreadyRecorded()
    {
        var refusal = PublishedLateCommitWords.Refusals().First(r => r.Type.EndsWith("already-recorded", StringComparison.Ordinal));
        return (refusal.Type, refusal.Status);
    }

    private Task<SceneActor> T0Async() => AcceptedAnswerScene.EnrolAsync(_forum, "t0-resend", Ct);

    private Task<Sent> SendAsync(SceneActor actor, byte[] wire) => Probe.SendAsync(_forum, actor, wire, cancellationToken: Ct);

    private async Task<int> PostedAsync(SceneActor actor) =>
        (await BudgetReadouts.ReadAsync(_forum, actor)).Entry("posting").GetProperty("used").GetInt32();

    /// <summary>A T0 agent that has spent its posting budget: the three questions it posted, the last of which is returned with its bytes.</summary>
    private async Task<(SceneActor Actor, byte[] Wire, ScenePost Post, string Board)> SpentAsync()
    {
        var actor = await T0Async();
        var board = Probe.Board();
        var limit = BudgetBursts.PostLimit("T0");
        byte[] last = [];
        ScenePost post = new(string.Empty, string.Empty);
        for (var i = 0; i < limit; i++)
        {
            last = actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), _forum.Now);
            post = await Probe.PostAsync(_forum, actor, last, Ct);
        }

        Assert.Equal(limit, await PostedAsync(actor));
        return (actor, last, post, board);
    }

    /// <summary>
    /// ASA30. R7.32: the agent at a spent posting budget whose third act was recorded sends the same bytes again and is answered <c>409</c> <c>curia/ingest/already-recorded</c> with detail
    /// exactly <c>post=&lt;the first id&gt;</c>; SQL: one <c>post.accepted</c> for the digest; <c>GET /v1/budget</c> <c>used</c> unchanged. Predicted red:
    /// <c>expected 409, was 403 table-11/rate-budget-exhausted</c> (Task 0 M1 measured exactly that on the unmodified tree).
    /// </summary>
    [Fact]
    public async Task R7_32_ASA30_TheSameBytesAtASpentBudgetAreAlreadyRecordedAndNotTheBudget()
    {
        var (actor, wire, post, _) = await SpentAsync();
        var (type, status) = AlreadyRecorded();

        var resent = await SendAsync(actor, wire);

        var expected = $"{status} {type} post={post.PostId}; one post.accepted for the digest; used {BudgetBursts.PostLimit("T0")}";
        var problems = new List<string>();
        if (resent.Status != status || resent.Type != type) problems.Add($"expected {status}, was {resent.Says}" + (resent.Detail is { } d ? $" {d.Split(' ')[0]}" : string.Empty));
        else if (resent.Detail != $"post={post.PostId}") problems.Add($"detail was \"{resent.Detail}\"");
        var rows = await Probe.EventsWithDigestAsync(_forum, post.Digest);
        if (rows != 1) problems.Add($"{rows} post.accepted events carry the digest");
        var used = await PostedAsync(actor);
        if (used != BudgetBursts.PostLimit("T0")) problems.Add($"used moved to {used}");
        Said(expected, problems.Count == 0 ? expected : string.Join("; ", problems));
    }

    /// <summary>
    /// ASA31. The gap does not widen, a control (green on the seam: Task 0 M1 measured both): at the spent budget a <b>new</b> nonce is still refused <c>403</c> for the budget, and an agent with budget
    /// to spare and a new nonce is <c>201</c>.
    /// </summary>
    [Fact]
    public async Task R7_32_ASA31_ANewActAtASpentBudgetIsStillRefusedAndOneWithBudgetToSpareIsStored()
    {
        var (actor, _, _, board) = await SpentAsync();
        var fresh = await SendAsync(actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), _forum.Now));

        var spare = await T0Async();
        await Probe.AskAsync(_forum, spare, board, Ct);
        var stored = await SendAsync(spare, spare.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), _forum.Now));

        var actual = $"{fresh.Says} ({(fresh.Detail ?? string.Empty).Split(' ')[0]}); {stored.Says}";
        Said("403 curia/authz/denied (table-11/rate-budget-exhausted); 201", actual);
    }

    /// <summary>
    /// ASA32. Only the posting budget gives way, a control (green on the seam; red under FASA14): a tier denial (a T0 agent's own recorded answer, sent again) stays <c>403</c> with its own reason,
    /// and an own-resource denial (an agent's revision of another's post, recorded, sent again) stays <c>403</c> with its own. (The quarantined-credential leg cannot be built in this fixture, and Task 2 found why: no log event produces a quarantined
    /// credential (the standing fold reads <c>agent.enrolled</c> alone), so seeding one is a production change. R7.32's <c>Active</c> clause is therefore implemented and not discharged by a probe
    /// at the route; the policy's order (the posting budget before the quarantine intersection) is what makes the clause necessary.)
    /// </summary>
    [Fact]
    public async Task R7_32_ASA32_NoOtherDenialGivesWayToTheDigest()
    {
        var board = Probe.Board();
        var other = await T0Async();
        var question = await Probe.AskAsync(_forum, other, board, Ct);

        var novice = await T0Async();
        var answer = novice.Agent.SignAnswer(board, Probe.Unrelated(), question.PostId, _forum.Now);
        await _forum.AppendStoredPostAsync(answer, Ct);
        var tier = await SendAsync(novice, answer);

        var stranger = await T0Async();
        var revision = stranger.Agent.SignRevision(board, "Corrected " + Probe.Unrelated(), question.PostId, question.Digest, _forum.Now);
        await _forum.AppendStoredPostAsync(revision, Ct);
        var own = await SendAsync(stranger, revision);

        var actual = $"{tier.Says} ({(tier.Detail ?? string.Empty).Split(' ')[0]}); {own.Says} ({(own.Detail ?? string.Empty).Split(' ')[0]})";
        Said("403 curia/authz/denied (table-10/denied); 403 curia/authz/denied (table-10/own-resource-only)", actual);
    }

    /// <summary>
    /// ASA33. A refusal costs nothing: the request of ASA30 leaves the events table, the budget's <c>used</c> and the flag rows exactly as they were. A control (green on the seam: a refusal
    /// costs nothing today); red under FASA15, where the <c>409</c> stores a post.
    /// </summary>
    [Fact]
    public async Task R7_32_ASA33_TheAnsweredResendStoresNothingAndSpendsNothing()
    {
        var (actor, wire, _, _) = await SpentAsync();
        var before = (await BudgetReadouts.TableSizesAsync(_forum), await PostedAsync(actor));

        await SendAsync(actor, wire);

        var after = (await BudgetReadouts.TableSizesAsync(_forum), await PostedAsync(actor));
        Said($"events {before.Item1.Events}, flag rows {before.Item1.FlagRows}, used {before.Item2}", $"events {after.Item1.Events}, flag rows {after.Item1.FlagRows}, used {after.Item2}");
    }

    /// <summary>
    /// ASA34. The order on a resend (Task 0 M2), a control (green on the seam): a revision, a question whose first is indexed and a vote, each sent again after it landed with budget to spare, are
    /// answered <c>409 already-recorded</c>, never <c>already-revised</c>, <c>duplicate-question</c> or <c>already-voted</c>.
    /// </summary>
    [Fact]
    public async Task R8_78_ASA34_AResendOfEveryKindIsAlreadyRecordedBeforeTheKindsOwnCheck()
    {
        var (_, recordedStatus) = AlreadyRecorded();
        var board = Probe.Board();
        var author = await T0Async();
        var question = await Probe.AskAsync(_forum, author, board, Ct);
        var revisionWire = author.Agent.SignRevision(board, "Corrected " + Probe.Unrelated(), question.PostId, question.Digest, _forum.Now);
        await Probe.PostAsync(_forum, author, revisionWire, Ct);
        var revision = await SendAsync(author, revisionWire);

        var asker = await T0Async();
        var questionWire = asker.Agent.SignQuestion(board, Probe.Unrelated() + " indexed", "About " + Probe.Unrelated(), _forum.Now);
        var first = await Probe.PostAsync(_forum, asker, questionWire, Ct);
        await WaitIndexedAsync(first.Digest);
        var repeated = await SendAsync(asker, questionWire);

        var voter = await AcceptedAnswerScene.T1Async(_forum, board, Ct);
        var answer = await Probe.PostAsync(_forum, voter, voter.Agent.SignAnswer(board, Probe.Unrelated(), question.PostId, _forum.Now), Ct);
        var second = await AcceptedAnswerScene.T1Async(_forum, board, Ct);
        var voteWire = second.Agent.SignVote(board, answer.Digest, _forum.Now);
        await Probe.PostAsync(_forum, second, voteWire, Ct);
        var vote = await SendAsync(second, voteWire);

        string Slug(Sent s) => s.Status == recordedStatus ? s.Type : s.Says;
        var expected = string.Join("; ", Enumerable.Repeat(AlreadyRecorded().Type, 3));
        Said(expected, string.Join("; ", new[] { revision, repeated, vote }.Select(Slug)));
    }

    private async Task WaitIndexedAsync(string digest)
    {
        await using var connection = new NpgsqlConnection(_forum.ConnectionString);
        await connection.OpenAsync(Ct);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            await using var count = new NpgsqlCommand("SELECT count(*) FROM post_embeddings WHERE digest = @digest;", connection);
            count.Parameters.AddWithValue("digest", digest);
            if ((long)(await count.ExecuteScalarAsync(Ct))! > 0) return;
            await Task.Delay(50, Ct);
        }
    }

    /// <summary>
    /// ASA37. The cost of R7.32 is one call (plan ruling 27): a counting log reader. The log reads a request makes are counted by differences of the store's own counters; the reads of a resend
    /// at a spent budget (a duplicate) equal the reads of a new act at the same spent budget (the AUTHORIZE read and nothing more), so the digest check reuses the read and adds none. Green on the
    /// seam (both are refused for the budget after the one read); red under FASA16, where the denied path reads again.
    /// </summary>
    [Fact]
    public async Task R7_32_ASA37_TheAnsweredResendReadsTheLogNoMoreThanTheRefusedNewActDoes()
    {
        var (actor, wire, _, board) = await SpentAsync();
        var token = await actor.Dpop.GetTokenAsync(_forum.Client, TokenEndpoint, _forum.Now, Ct);

        // The store's counters are the process's: EmbeddingCatchUp's check reads the same store on its own tick, so one send can count a read the request did not make (seen once, in a
        // whole run). A background read only adds, and a refused send changes nothing, so the fewest reads over three sends is what the request itself reads; a route that reads again
        // does so on every send and is still counted (FASA16).
        async Task<(int Forward, int ByAggregate)> ReadsOfAsync(byte[] bytes)
        {
            var fewest = (Forward: int.MaxValue, ByAggregate: int.MaxValue);
            for (var send = 0; send < 3; send++)
            {
                var (forward, byAggregate) = (_forum.Store.ReadsForward, _forum.Store.ReadsByAggregate);
                await Probe.SendAsync(_forum, actor, bytes, token, Ct);
                fewest = (Math.Min(fewest.Forward, _forum.Store.ReadsForward - forward), Math.Min(fewest.ByAggregate, _forum.Store.ReadsByAggregate - byAggregate));
            }

            return fewest;
        }

        var fresh = await ReadsOfAsync(actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), _forum.Now));
        var resent = await ReadsOfAsync(wire);
        // Absolute, not relative to the refused new act: a re-read shared by every denied path moves both counts together. The route's cost at a spent budget is the AUTHORIZE read of
        // the whole log and the two key reads the request makes in any case (measured on the unmodified route; R7.32 adds none). Until errata G68 the two key reads (the token's key
        // at authentication, R5.22, and the author's key at VERIFY) were reads of the agent's stream and this was "1 whole-log reads and 2 stream reads"; R6.64 decides the lifecycle
        // from the whole log, so they are whole-log reads: three reads before and three now.
        const string Route = "3 whole-log reads and 0 stream reads";
        Said($"{Route} for a refused new act and {Route} for the answered resend", $"{fresh.Forward} whole-log reads and {fresh.ByAggregate} stream reads for a refused new act and {resent.Forward} whole-log reads and {resent.ByAggregate} stream reads for the answered resend");
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------
    // ASA36: the resend through the real Forum, inside a stall and beyond the client's bound
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------

    internal sealed record Late(string PostId, string Digest, string Json);

    private static T Require<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private Late LatePost(byte[] wire)
    {
        var submission = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(wire));
        JsonValue Member(JsonValue.Object o, string name) => o.Members.First(m => m.Key == name).Value;
        var envelope = (JsonValue.Object)Member(submission, "envelope");
        var signature = ((JsonValue.String)Member(submission, "signature")).Value;
        string Text(string name) => ((JsonValue.String)Member(envelope, name)).Value;

        var canonicalBytes = Require(CanonicalJson.CanonicalizeWithNfc(envelope));
        var digest = Digests.Sha256(canonicalBytes).ToPrefixed();
        var postId = Require(new UlidGenerator(_forum.Clock).Next()).ToString();
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["post_id"] = postId,
            ["canonical"] = Encoding.UTF8.GetString(canonicalBytes.ToArray()),
            ["signature"] = signature,
            ["digest"] = digest,
            ["author"] = Text("author"),
            ["board"] = Text("board"),
            ["kind"] = Text("kind"),
            ["parent"] = null,
            ["server_ts"] = _forum.Now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["risk_flags"] = Array.Empty<object>(),
        });
        return new Late(postId, digest, json);
    }

    private async Task WaitQueuedAsync(Task request)
    {
        await using var probe = new NpgsqlConnection(_forum.ConnectionString);
        await probe.OpenAsync(Ct);
        for (var attempt = 0; attempt < 400; attempt++)
        {
            if (request.IsCompleted) return;
            if (await LateCommitHolder.WaitersAsync(probe, Ct) > 0) return;
            await Task.Delay(25, Ct);
        }
    }

    /// <summary>An enrolled agent in a profile store of its own, a session over the host's client, and a signed question (the act).</summary>
    private async Task<(ForumSession Session, EnrolledAgent Agent, SignedSubmission Act, ProfileStore Store, string Slug)> KeptAsync(TimeSpan? bound)
    {
        var home = Directory.CreateTempSubdirectory("curia-resend-k-").FullName;
        _homes.Add(home);
        var slug = "resend-" + Guid.NewGuid().ToString("N")[..8];
        var store = new ProfileStore(home);
        Assert.True(store.Create(slug, $"https://agents.example/{slug}", $"{slug}-k", _forum.Client.BaseAddress!).TryGetValue(out var agent, out var error), error?.Detail);
        var client = new ForumClient(_forum.Client, _forum.Client.BaseAddress!);
        var enrolled = await client.EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var refusal), refusal?.Summary);

        var draft = new PostDraft { Kind = PostKind.Question, Board = Probe.Board(), Title = "About " + Probe.Unrelated(), Body = Probe.Unrelated() };
        Assert.True(SubmissionBuilder.Build(agent!, draft, _forum.Now).TryGetValue(out var act, out var buildError), buildError?.Type);
        return (new ForumSession(client, agent!, store, _forum.Clock, bound), agent!, act!, store, slug);
    }

    /// <summary>
    /// ASA36. The resend through the real Forum (plan ruling 22): a kept act sent through <c>ForumSession.ResendAsync</c> to the host while the first send's event is held uncommitted (the late-commit
    /// seam). (i) A hold shorter than the client's bound: the resend waits out the stall and is answered <c>409 already-recorded</c>, the kept file is removed and the digest is stored once; (ii) a hold
    /// longer than the bound (the bound injected small): the resend ends unheard at the bound with the file kept, and a resend after the landing is <c>already-recorded</c> with one stored post.
    /// Predicted red: <c>NOT SENT: not built</c> (on the seam the resend is the old path: the file is not removed, and the bound is applied nowhere).
    /// </summary>
    [Fact]
    public async Task R11_79_ASA36_ARecordedActResentInsideAndBeyondAStallIsAnsweredOrUnheardAndKept()
    {
        var problems = new List<string>();

        // (i) inside the stall: the hold is 1.5 s, the bound 20 s
        var (inside, _, act, store, slug) = await KeptAsync(TimeSpan.FromSeconds(20));
        var path = KeptActFiles.Plant(Path.Combine(store.DirectoryFor(slug), "acts"), act, forum: _forum.Client.BaseAddress!.ToString().TrimEnd('/'), author: $"https://agents.example/{slug}");
        var late = LatePost(act.Wire.ToArray());
        await using (var holder = await LateCommitHolder.BeginAsync(_forum.AppRole, "public", Ct))
        {
            await holder.InsertAsync(late.PostId, "post.accepted", late.PostId, $"https://agents.example/{slug}", late.Json, _forum.Now, Ct);
            var kept = new KeptAct(new KeptActHeader(act.PrefixedDigest, "question", "b", _forum.Client.BaseAddress.ToString(), $"https://agents.example/{slug}", DateTimeOffset.UtcNow, 0, null, null), act.Wire);
            var started = Stopwatch.StartNew();
            var resend = inside.ResendAsync(kept, MarkingMode.None, Ct);
            await WaitQueuedAsync(resend);
            await Task.Delay(1500, Ct);
            await holder.CommitAsync(Ct);
            var answered = await resend;
            if (answered.Refusal?.Error.Type != AlreadyRecorded().Type) problems.Add($"(i) NOT SENT: not built; the resend ended as {answered.Refusal?.Error.Type ?? "a success"} after {started.ElapsedMilliseconds} ms, expected already-recorded");
        }

        if (File.Exists(path)) problems.Add("(i) the kept file was not removed");
        if (await Probe.EventsWithDigestAsync(_forum, act.PrefixedDigest) != 1) problems.Add("(i) the digest is not stored once");

        // (ii) beyond the bound: the hold is 4 s, the bound 700 ms
        var (beyond, _, act2, store2, slug2) = await KeptAsync(TimeSpan.FromMilliseconds(700));
        var path2 = KeptActFiles.Plant(Path.Combine(store2.DirectoryFor(slug2), "acts"), act2, forum: _forum.Client.BaseAddress!.ToString().TrimEnd('/'), author: $"https://agents.example/{slug2}");
        var late2 = LatePost(act2.Wire.ToArray());
        await using (var holder = await LateCommitHolder.BeginAsync(_forum.AppRole, "public", Ct))
        {
            await holder.InsertAsync(late2.PostId, "post.accepted", late2.PostId, $"https://agents.example/{slug2}", late2.Json, _forum.Now, Ct);
            var kept = new KeptAct(new KeptActHeader(act2.PrefixedDigest, "question", "b", _forum.Client.BaseAddress.ToString(), $"https://agents.example/{slug2}", DateTimeOffset.UtcNow, 0, null, null), act2.Wire);
            var started = Stopwatch.StartNew();
            var resend = beyond.ResendAsync(kept, MarkingMode.None, Ct);
            await WaitQueuedAsync(resend);
            await Task.Delay(4000, Ct);
            var endedAtBound = resend.IsCompleted;
            await holder.CommitAsync(Ct);
            var answered = await resend;
            if (!endedAtBound || answered.Refusal?.Unheard != true)
                problems.Add($"(ii) NOT SENT: not built; the resend was not ended unheard at the bound (it ended as {answered.Refusal?.Error.Type ?? "a success"} after {started.ElapsedMilliseconds} ms)");
        }

        if (!File.Exists(path2)) problems.Add("(ii) the kept file was removed although the resend ended unheard");
        if (await Probe.EventsWithDigestAsync(_forum, act2.PrefixedDigest) != 1) problems.Add("(ii) the digest is not stored once");

        if (problems.Count > 0) throw new Xunit.Sdk.XunitException($"Expected: (i) already-recorded after the stall, file removed, 1 row; (ii) unheard at the bound, file kept, 1 row{Environment.NewLine}Actual:   {string.Join("; ", problems)}");
    }
}
