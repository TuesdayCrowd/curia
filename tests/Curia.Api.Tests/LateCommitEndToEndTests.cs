using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R6.60, R8.86, R10.94 (errata G57) at the route, probes LCB11 to LCB23, LCB26 and LCB29: the Forum's uniqueness checks hold against an append that lands late. The
/// host is <see cref="LateCommitForumFixture"/>: the production composition in the Production environment, connecting as the application role (traps 84 and 86), its store
/// wrapped by decorators that forward to the real one.
///
/// <para><b>The late commit.</b> A <see cref="LateCommitHolder"/> (a second session as the application role) holds the store's whole-log lock with an uncommitted event; the
/// request under test reads the log without it, reaches its append and queues behind the holder; the holder commits; the append then runs on a log that holds the
/// event. That is the live drill's mechanism with no proxy and no sleep (trap 90). <b>Oracles</b> (plan ruling 13): the events table and <c>flag_details</c> on the owner's
/// connection, read by SQL, and the post ids the holder wrote; never the response of the request under test and never <c>CorrectionIndex</c>. Every type, status, detail form,
/// event id and figure is read from the errata by <see cref="PublishedLateCommitWords"/>. <b>On the seam the store ignores the guard</b>, which is the defect these probes name:
/// a probe is red by its assertion, and a probe green on the seam says so (a control, held by the falsification row that names it).</para>
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every statement is constant text or built from a GUID's hex digits; every value is bound as a parameter.")]
[Collection("forum")]
public sealed class LateCommitEndToEndTests : IClassFixture<LateCommitForumFixture>, IAsyncLifetime
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private readonly LateCommitForumFixture _forum;

    public LateCommitEndToEndTests(LateCommitForumFixture forum)
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

    private static void Said(string expected, string actual) => RevisionProbe.Said(expected, actual);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------
    // The seam: a late commit placed between a request's read and its append
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>An event the holder will hold: a post as <c>IngestPipeline</c> would have stored it (the shape <c>ForumFixture.AppendStoredPostAsync</c> writes).</summary>
    private sealed record LateEvent(string PostId, string Digest, string Author, string EventType, string AggregateId, string Json);

    private LateEvent LatePost(byte[] wire)
    {
        var submission = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(wire));
        JsonValue Member(JsonValue.Object o, string name) => o.Members.First(m => m.Key == name).Value;
        var envelope = (JsonValue.Object)Member(submission, "envelope");
        var signature = ((JsonValue.String)Member(submission, "signature")).Value;
        string Text(string name) => ((JsonValue.String)Member(envelope, name)).Value;

        var canonicalBytes = Require(CanonicalJson.CanonicalizeWithNfc(envelope));
        var digest = Digests.Sha256(canonicalBytes).ToPrefixed();
        var postId = Require(new UlidGenerator(_forum.Clock).Next()).ToString();
        var author = Text("author");
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["post_id"] = postId,
            ["canonical"] = Encoding.UTF8.GetString(canonicalBytes.ToArray()),
            ["signature"] = signature,
            ["digest"] = digest,
            ["author"] = author,
            ["board"] = Text("board"),
            ["kind"] = Text("kind"),
            ["parent"] = envelope.Members.Any(m => m.Key == "parent") ? Text("parent") : null,
            ["server_ts"] = _forum.Now.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["risk_flags"] = Array.Empty<object>(),
        });
        return new LateEvent(postId, digest, author, "post.accepted", postId, json);
    }

    private LateEvent LateModeration(string postId, string effect)
    {
        var id = Require(new UlidGenerator(_forum.Clock).Next()).ToString();
        var json = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["post_id"] = postId,
            ["actor_id"] = "operator:fixture",
            ["rationale"] = "moderated by the late-commit probe",
            ["moderator"] = ModeratorKinds.Wire(ModeratorKind.Human),
            ["effect"] = effect,
            ["category"] = FlagKinds.Wire(FlagKind.Spam),
            ["adjudicates"] = Array.Empty<string>(),
        });
        return new LateEvent(id, string.Empty, "operator:fixture", FlagProjector.ModerationAppliedType, postId, json);
    }

    private Task<long> InsertAsync(LateCommitHolder holder, LateEvent late) =>
        holder.InsertAsync(late.PostId, late.EventType, late.AggregateId, late.Author, late.Json, _forum.Now, Ct);

    /// <summary>Waits until a session is queued on the log's lock, or the request has answered (then it never reached its append).</summary>
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

        throw new Xunit.Sdk.XunitException("the request never queued behind the holder's lock and never answered");
    }

    /// <summary>
    /// Sends <paramref name="wire"/> as <paramref name="sender"/> while a holder holds the log's lock and the events <paramref name="stage"/> inserts; commits the holder once the
    /// request is queued on the lock (or has answered); returns the answer. The token is minted before the lock is taken.
    /// </summary>
    private async Task<Sent> SendBehindAsync(SceneActor sender, byte[] wire, Func<LateCommitHolder, Task> stage, bool commit = true) =>
        await BehindAsync(sender, token => RevisionProbe.SendAsync(_forum, sender, wire, token, Ct), stage, commit);

    /// <summary>As <see cref="SendBehindAsync"/> for any request <paramref name="request"/> makes with the token already minted.</summary>
    private async Task<T> BehindAsync<T>(SceneActor sender, Func<string, Task<T>> request, Func<LateCommitHolder, Task> stage, bool commit = true)
    {
        var token = await sender.Dpop.GetTokenAsync(_forum.Client, TokenEndpoint, _forum.Now, Ct);
        await using var holder = await LateCommitHolder.BeginAsync(_forum.AppRole, "public", Ct);
        await stage(holder);
        var send = request(token);
        await WaitQueuedAsync(send);
        if (commit) await holder.CommitAsync(Ct); else await holder.RollbackAsync(Ct);
        return await send;
    }

    /// <summary>A flag raised by <paramref name="actor"/> against <paramref name="postId"/> (the route's JSON body), answered as a <see cref="Sent"/>.</summary>
    private async Task<Sent> RaiseAsync(SceneActor actor, string postId, string kind, string? token = null)
    {
        token ??= await actor.Dpop.GetTokenAsync(_forum.Client, TokenEndpoint, _forum.Now, Ct);
        var body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind, rationale = "late-commit probe " + Guid.NewGuid().ToString("N") }));
        using var response = await actor.Dpop.PostAsync(_forum.Client, $"http://localhost/v1/posts/{postId}/flags", token, body, _forum.Now, Ct, contentType: "application/json");
        var text = await response.Content.ReadAsStringAsync(Ct);
        string Member(string name)
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                return json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
            }
            catch (JsonException)
            {
                return string.Empty;
            }
        }

        return new Sent((int)response.StatusCode, Member("type"), Member("title"), Member("detail") is { Length: > 0 } d ? d : null, text, response.Content.Headers.ContentType?.MediaType, null, null, null);
    }

    /// <summary>The events and the private row of a flag as <c>RaiseFlag</c> writes them, held by <paramref name="holder"/> (the private row first, as the use case writes it).</summary>
    private async Task LateFlagAsync(LateCommitHolder holder, string postId, string raiser, string kind)
    {
        var id = Require(new UlidGenerator(_forum.Clock).Next()).ToString();
        var salt = FlagSalt.New();
        var rationale = "held by the late-commit probe";
        var commitment = Require(FlagCommitment.Of(postId, raiser, rationale, salt));
        await holder.ExecuteAsync(
            "INSERT INTO flag_details (event_id, post_id, raised_by, rationale, salt) VALUES (@id, @post, @raiser, @rationale, @salt);",
            Ct,
            ("id", id), ("post", postId), ("raiser", raiser), ("rationale", rationale), ("salt", salt));
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?> { ["commitment"] = commitment, ["kind"] = kind });
        await holder.InsertAsync(id, FlagProjector.FlagCommittedType, "flag:" + id, null, payload, _forum.Now, Ct);
    }

    /// <summary>The oracle for a flag recorded twice: private rows joined to their <c>flag.committed</c> entries on the event id, grouped by post, raiser and kind.</summary>
    private Task<List<string>> FlagTripleRowsAsync(string postId) => RowsAsync(
        "SELECT d.post_id, d.raised_by, e.payload->>'kind', count(*) FROM flag_details d JOIN events e ON e.event_id = d.event_id WHERE e.event_type = 'flag.committed' AND d.post_id = @post GROUP BY 1, 2, 3 HAVING count(*) > 1;",
        ("post", postId));

    private async Task<long> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var admin = new NpgsqlConnection(_forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, admin);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private async Task<List<string>> RowsAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var admin = new NpgsqlConnection(_forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, admin);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct))
            rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        return rows;
    }

    private Task<long> EventsAsync() => ScalarAsync("SELECT count(*) FROM events;");

    private Task<long> HighestAsync() => ScalarAsync("SELECT coalesce(max(seq), 0) FROM events;");

    private Task<long> WithDigestAsync(string digest) => RevisionProbe.EventsWithDigestAsync(_forum, digest);

    /// <summary>The oracle for a revision fork (plan ruling 13): revisions on a board sharing one <c>prev</c>, the <c>prev</c> read from the signed canonical text by a pattern and not by a jsonb cast.</summary>
    private Task<List<string>> ForkRowsAsync(string board) => RowsAsync(
        "SELECT substring(payload->>'canonical' from '\"prev\":\"(sha256:[0-9a-f]{64})\"'), count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'revision' AND payload->>'board' = @board GROUP BY 1 HAVING count(*) > 1;",
        ("board", board));

    private Task<List<string>> VoteRowsAsync(string board) => RowsAsync(
        "SELECT payload->>'author', substring(payload->>'canonical' from '\"target\":\"(sha256:[0-9a-f]{64})\"'), count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'vote' AND payload->>'board' = @board GROUP BY 1, 2 HAVING count(*) > 1;",
        ("board", board));

    private static string Slug(Sent sent) => sent.Threw is null ? sent.Says : $"threw {sent.Threw}";

    private Task<SceneActor> T2Async(string board) => BudgetBursts.T2Async(_forum, board, Ct);

    private Task<ScenePost> AskAsync(SceneActor actor, string board) => RevisionProbe.AskAsync(_forum, actor, board, Ct);

    private async Task<ScenePost> AnswerAsync(SceneActor actor, string board, ScenePost question) =>
        await RevisionProbe.PostAsync(_forum, actor, actor.Agent.SignAnswer(board, RevisionProbe.Unrelated(), question.PostId, _forum.Now), Ct);

    private (string Type, int Count)[] RefusalLines(int mark)
    {
        var refused = PublishedLateCommitWords.RefusedEvent();
        return _forum.Log.Since(mark).Where(e => e.EventId == refused.Id).Select(e => (e.Message, 1)).ToArray();
    }

    private int LinesOf(int mark, int eventId) => _forum.Log.Since(mark).Count(e => e.EventId == eventId);

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------
    // LCB11 to LCB14: the submission legs
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// LCB11. The digest leg, every kind. For each of the seven kinds (the list is <see cref="PostKind"/>'s, and the count is asserted: trap 81): the holder holds the post
    /// W's event uncommitted; the same signed bytes are sent; the holder commits; the answer is <c>409 curia/ingest/already-recorded</c> with detail exactly
    /// <c>post=&lt;the holder's post id&gt;</c>; SQL: one <c>post.accepted</c> with the digest, and the author's posts unchanged by the refused request. Control: the same words
    /// signed with a new nonce are stored <c>201</c> (R8.78's reason: a different digest).
    /// </summary>
    [Fact]
    public async Task R8_86_LCB11_TheDigestLegRefusesTheSameBytesOfEveryKind()
    {
        var board = RevisionProbe.Board();
        var sender = await T2Async(board);
        var other = await T2Async(board);
        var question = await AskAsync(other, board);
        var result = await AnswerAsync(other, board, question);
        var own = await AskAsync(sender, board);
        var now = _forum.Now;

        var wires = new (PostKind Kind, byte[] Wire)[]
        {
            (PostKind.Question, sender.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), now)),
            (PostKind.Answer, sender.Agent.SignAnswer(board, RevisionProbe.Unrelated(), question.PostId, now)),
            (PostKind.Comment, sender.Agent.Sign(PostKind.Comment, board, RevisionProbe.Unrelated(), null, question.PostId, now)),
            (PostKind.Finding, sender.Agent.Sign(PostKind.Finding, board, RevisionProbe.Unrelated(), "Finding " + RevisionProbe.Unrelated(), null, now)),
            (PostKind.Revision, sender.Agent.SignRevision(board, "Corrected " + RevisionProbe.Unrelated(), own.PostId, own.Digest, now)),
            (PostKind.Vote, sender.Agent.SignVote(board, result.Digest, now)),
            (PostKind.Verification, sender.Agent.SignVerification(board, result.Digest, "reproduced", now)),
        };
        Assert.Equal(Enum.GetValues<PostKind>().Length, wires.Length);

        var refusal = PublishedLateCommitWords.Refusals().First(r => r.Type.EndsWith("already-recorded", StringComparison.Ordinal));
        var expected = new List<string>();
        var said = new List<string>();
        foreach (var (kind, wire) in wires)
        {
            var late = LatePost(wire);
            var before = await RevisionProbe.PostsByAsync(_forum, sender.Agent.AgentId);
            var sent = await SendBehindAsync(sender, wire, holder => InsertAsync(holder, late));
            var stored = await WithDigestAsync(late.Digest);
            var authored = await RevisionProbe.PostsByAsync(_forum, sender.Agent.AgentId) - before;
            expected.Add($"{kind}: {refusal.Status} {refusal.Type} post={late.PostId}, {1} event of the digest, {1} new post by the author");
            said.Add($"{kind}: {Slug(sent)}{(sent.Detail is null ? string.Empty : " " + sent.Detail)}, {stored} event of the digest, {authored} new post by the author");
        }

        // The author's count of new posts includes the holder's committed event in both columns; the refused request adds none.
        Said(string.Join(" | ", expected), string.Join(" | ", said));

        var control = await RevisionProbe.SendAsync(
            _forum, sender, sender.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "Again " + RevisionProbe.Unrelated(), now), cancellationToken: Ct);
        Said("control, a new nonce: 201", $"control, a new nonce: {control.Says}");
    }

    /// <summary>
    /// LCB12. The revision leg, both timings. (a) The holder holds R1, a revision of P by the author; R2 (the same <c>prev</c>, different text) is sent while R1 is held; the holder
    /// commits: <c>409 curia/ingest/already-revised</c>, detail exactly <c>successor=&lt;R1's post id&gt;</c>; the SQL oracle (revisions sharing a <c>prev</c>) returns no row; the Forum's log carries
    /// <c>AppendGuardRefused</c>. (b) A hand-built wire with a stale <c>prev</c> (ruling 24: the CLI would say NOT SENT ... superseded) is sent after R1 has committed: the same
    /// refusal, from the route's own check, and no <c>AppendGuardRefused</c>. Controls: a revision of a different post at the same moment is <c>201</c>; a revision of R1 (a different <c>prev</c>) is <c>201</c>.
    /// </summary>
    [Fact]
    public async Task R8_86_LCB12_TheRevisionLegRefusesASecondRevisionOfOnePrev()
    {
        var board = RevisionProbe.Board();
        var author = await T2Async(board);
        var now = _forum.Now;
        var refused = PublishedLateCommitWords.RefusedEvent();
        var revised = PublishedLateCommitWords.Refusals().First(r => r.Type.EndsWith("already-revised", StringComparison.Ordinal));
        var said = new List<string>();
        var expected = new List<string>();

        // (a)
        var p = await AskAsync(author, board);
        var r1 = LatePost(author.Agent.SignRevision(board, "First " + RevisionProbe.Unrelated(), p.PostId, p.Digest, now));
        var mark = _forum.Log.Mark();
        var a = await SendBehindAsync(author, author.Agent.SignRevision(board, "Second " + RevisionProbe.Unrelated(), p.PostId, p.Digest, now), holder => InsertAsync(holder, r1));
        var forks = await ForkRowsAsync(board);
        expected.Add($"(a) {revised.Status} {revised.Type} successor={r1.PostId}, oracle rows 0, {refused.Name} logged 1");
        said.Add($"(a) {Slug(a)}{(a.Detail is null ? string.Empty : " " + a.Detail)}, oracle rows {forks.Count}, {refused.Name} logged {LinesOf(mark, refused.Id)}");

        // (b)
        var p2 = await AskAsync(author, board);
        var first = await RevisionProbe.ReviseAsync(_forum, author, board, p2, "First " + RevisionProbe.Unrelated(), cancellationToken: Ct);
        Assert.Equal(201, first.Status);
        var mark2 = _forum.Log.Mark();
        var b = await RevisionProbe.ReviseAsync(_forum, author, board, p2, "Second " + RevisionProbe.Unrelated(), cancellationToken: Ct);
        expected.Add($"(b) {revised.Status} {revised.Type} successor={first.PostId}, {refused.Name} logged 0");
        said.Add($"(b) {Slug(b)}{(b.Detail is null ? string.Empty : " " + b.Detail)}, {refused.Name} logged {LinesOf(mark2, refused.Id)}");

        // controls
        var p3 = await AskAsync(author, board);
        var held = LatePost(author.Agent.SignRevision(board, "Held " + RevisionProbe.Unrelated(), p3.PostId, p3.Digest, now));
        var other = await AskAsync(author, board);
        var differentPost = await SendBehindAsync(author, author.Agent.SignRevision(board, "Other " + RevisionProbe.Unrelated(), other.PostId, other.Digest, now), holder => InsertAsync(holder, held));
        expected.Add("control, a revision of a different post at the same moment: 201");
        said.Add($"control, a revision of a different post at the same moment: {differentPost.Says}");

        var p4 = await AskAsync(author, board);
        var held4 = LatePost(author.Agent.SignRevision(board, "Held " + RevisionProbe.Unrelated(), p4.PostId, p4.Digest, now));
        var chained = await SendBehindAsync(author, author.Agent.SignRevision(board, "Of the revision " + RevisionProbe.Unrelated(), p.PostId, first.Digest, now), holder => InsertAsync(holder, held4));
        expected.Add("control, a revision of a revision (another prev): 201");
        said.Add($"control, a revision of a revision (another prev): {chained.Says}");

        Said(string.Join(" | ", expected), string.Join(" | ", said));
    }

    /// <summary>
    /// LCB13. A successor the Forum does not serve does not count; one it serves again does. (i) The holder commits R1 and a record withholding it together: R2 is <c>201</c> (R8.77's last
    /// sentence; the successor leg folds the read followed by the delta, moderation included). (ii) R1 was withheld in the route's read and the holder commits its restore: R2 is
    /// <c>409 already-revised</c> (a delta-only successor leg would miss it). (iii) R1 withheld in the read and nothing in the delta: R2 <c>201</c> (the route's own check: a control). (iv) The delta holds only an
    /// unrelated moderation record: R2 <c>201</c> (a control).
    /// </summary>
    [Fact]
    public async Task R8_86_LCB13_AWithheldSuccessorDoesNotCountAndARestoredOneDoes()
    {
        var board = RevisionProbe.Board();
        var author = await T2Async(board);
        var now = _forum.Now;
        var said = new List<string>();
        var expected = new List<string>();

        // (i)
        var p = await AskAsync(author, board);
        var r1 = LatePost(author.Agent.SignRevision(board, "First " + RevisionProbe.Unrelated(), p.PostId, p.Digest, now));
        var withhold = LateModeration(r1.PostId, ModerationEffects.Wire(ModerationEffect.Withhold));
        var i = await SendBehindAsync(author, author.Agent.SignRevision(board, "Second " + RevisionProbe.Unrelated(), p.PostId, p.Digest, now), async holder =>
        {
            await InsertAsync(holder, r1);
            await InsertAsync(holder, withhold);
        });
        expected.Add("(i) the successor and its withholding together: 201");
        said.Add($"(i) the successor and its withholding together: {i.Says}");

        // (ii)
        var p2 = await AskAsync(author, board);
        var s1 = await RevisionProbe.ReviseAsync(_forum, author, board, p2, "First " + RevisionProbe.Unrelated(), cancellationToken: Ct);
        await _forum.WithholdAsync(s1.PostId!, Ct);
        var restore = LateModeration(s1.PostId!, ModerationEffects.Wire(ModerationEffect.Restore));
        var ii = await SendBehindAsync(author, author.Agent.SignRevision(board, "Second " + RevisionProbe.Unrelated(), p2.PostId, p2.Digest, now), holder => InsertAsync(holder, restore));
        expected.Add("(ii) the successor withheld in the read, restored in the delta: 409 curia/ingest/already-revised");
        said.Add($"(ii) the successor withheld in the read, restored in the delta: {Slug(ii)}");

        // (iii)
        var p3 = await AskAsync(author, board);
        var t1 = await RevisionProbe.ReviseAsync(_forum, author, board, p3, "First " + RevisionProbe.Unrelated(), cancellationToken: Ct);
        await _forum.WithholdAsync(t1.PostId!, Ct);
        var iii = await RevisionProbe.ReviseAsync(_forum, author, board, p3, "Second " + RevisionProbe.Unrelated(), cancellationToken: Ct);
        expected.Add("(iii) the successor withheld in the read, nothing in the delta: 201");
        said.Add($"(iii) the successor withheld in the read, nothing in the delta: {iii.Says}");

        // (iv)
        var p4 = await AskAsync(author, board);
        var unrelated = LateModeration((await AskAsync(author, board)).PostId, ModerationEffects.Wire(ModerationEffect.Withhold));
        var iv = await SendBehindAsync(author, author.Agent.SignRevision(board, "Only " + RevisionProbe.Unrelated(), p4.PostId, p4.Digest, now), holder => InsertAsync(holder, unrelated));
        expected.Add("(iv) only an unrelated moderation record in the delta: 201");
        said.Add($"(iv) only an unrelated moderation record in the delta: {iv.Says}");

        Said(string.Join(" | ", expected), string.Join(" | ", said));
    }

    /// <summary>
    /// LCB14. The vote leg. The holder holds X's vote V1 for target T; X sends V2 (a new nonce) for T; the holder commits: <c>409 curia/verification/already-voted</c>, detail the target
    /// digest; SQL: one vote by X for T. Controls: another agent's vote for T is <c>201</c>; X's vote for another target is <c>201</c>. The leg is built whether or not the control
    /// reproduced (ruling 8); Task 0 reproduced it, 3 of 3.
    /// </summary>
    [Fact]
    public async Task R8_86_LCB14_TheVoteLegRefusesASecondVoteForOneTarget()
    {
        var board = RevisionProbe.Board();
        var voter = await T2Async(board);
        var otherVoter = await T2Async(board);
        var author = await T2Async(board);
        var question = await AskAsync(author, board);
        var target = await AnswerAsync(author, board, question);
        var target2 = await AnswerAsync(author, board, question);
        var voted = PublishedLateCommitWords.Refusals().First(r => r.Type.EndsWith("already-voted", StringComparison.Ordinal));
        var now = _forum.Now;
        var said = new List<string>();
        var expected = new List<string>();

        var v1 = LatePost(voter.Agent.SignVote(board, target.Digest, now));
        var v2 = await SendBehindAsync(voter, voter.Agent.SignVote(board, target.Digest, now), holder => InsertAsync(holder, v1));
        var rows = await VoteRowsAsync(board);
        expected.Add($"{voted.Status} {voted.Type} {target.Digest}, oracle rows 0");
        said.Add($"{Slug(v2)}{(v2.Detail is null ? string.Empty : " " + v2.Detail)}, oracle rows {rows.Count}");

        var held = LatePost(voter.Agent.SignVote(board, target2.Digest, now));
        var another = await SendBehindAsync(otherVoter, otherVoter.Agent.SignVote(board, target2.Digest, now), holder => InsertAsync(holder, held));
        expected.Add("control, another agent's vote for the target: 201");
        said.Add($"control, another agent's vote for the target: {another.Says}");

        var target3 = await AnswerAsync(author, board, question);
        var target4 = await AnswerAsync(author, board, question);
        var held3 = LatePost(voter.Agent.SignVote(board, target3.Digest, now));
        var forAnother = await SendBehindAsync(voter, voter.Agent.SignVote(board, target4.Digest, now), holder => InsertAsync(holder, held3));
        expected.Add("control, the same agent's vote for another target: 201");
        said.Add($"control, the same agent's vote for another target: {forAnother.Says}");

        Said(string.Join(" | ", expected), string.Join(" | ", said));
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------
    // LCB15 to LCB17: the flag
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// LCB15. The flag leg and its false positive. The holder holds a flag F1 (the private row written, the entry uncommitted) by raiser R against post P of kind k; R sends the same flag;
    /// the holder commits: <c>409 curia/flag/already-raised</c>; the Forum's log carries exactly one <c>AppendGuardRedecided</c> (pass 1 of N, N read from the errata); the oracle (private rows
    /// joined to their entries, grouped by post, raiser and kind) returns no row. Controls: R's flag of another kind against P is <c>201</c> with no redecide; another raiser's flag of kind k is
    /// <c>201</c> with one redecide (the false positive costs a pass and never a refusal).
    /// </summary>
    [Fact]
    public async Task R10_94_LCB15_TheFlagLegRefusesTheSameFlagTwiceAndRedecidesForAnotherRaisersFlag()
    {
        var board = RevisionProbe.Board();
        var asker = await AcceptedAnswerScene.EnrolAsync(_forum, "asker", Ct);
        var raiser = await AcceptedAnswerScene.EnrolAsync(_forum, "raiser", Ct);
        var other = await AcceptedAnswerScene.EnrolAsync(_forum, "other", Ct);
        var redecided = PublishedLateCommitWords.RedecidedEvent();
        var passes = PublishedLateCommitWords.Passes;
        var said = new List<string>();
        var expected = new List<string>();

        var p = await AskAsync(asker, board);
        var mark = _forum.Log.Mark();
        var again = await BehindAsync(raiser, token => RaiseAsync(raiser, p.PostId, "spam", token), holder => LateFlagAsync(holder, p.PostId, raiser.Agent.AgentId, "spam"));
        var rows = await FlagTripleRowsAsync(p.PostId);
        var lines = _forum.Log.Since(mark).Where(e => e.EventId == redecided.Id).Select(e => e.Message).ToArray();
        expected.Add($"409 curia/flag/already-raised, {redecided.Name} logged 1 (pass 1 of {passes}), oracle rows 0");
        said.Add($"{Slug(again)}, {redecided.Name} logged {lines.Length}{(lines.Length == 1 ? $" ({lines[0]})" : string.Empty)}, oracle rows {rows.Count}");

        var p2 = await AskAsync(asker, board);
        var mark2 = _forum.Log.Mark();
        var otherKind = await BehindAsync(raiser, token => RaiseAsync(raiser, p2.PostId, "incorrect", token), holder => LateFlagAsync(holder, p2.PostId, raiser.Agent.AgentId, "spam"));
        expected.Add($"control, R's flag of another kind: 201, {redecided.Name} logged 0");
        said.Add($"control, R's flag of another kind: {otherKind.Says}, {redecided.Name} logged {LinesOf(mark2, redecided.Id)}");

        var p3 = await AskAsync(asker, board);
        var mark3 = _forum.Log.Mark();
        var otherRaiser = await BehindAsync(other, token => RaiseAsync(other, p3.PostId, "spam", token), holder => LateFlagAsync(holder, p3.PostId, raiser.Agent.AgentId, "spam"));
        expected.Add($"control, another raiser's flag of the kind: 201, {redecided.Name} logged 1");
        said.Add($"control, another raiser's flag of the kind: {otherRaiser.Says}, {redecided.Name} logged {LinesOf(mark3, redecided.Id)}");

        Said(string.Join(" | ", expected), string.Join(" | ", said));
    }

    /// <summary>
    /// LCB16. The loop is bounded and its end is not a <c>500</c>. The decorator appends a <c>flag.committed</c> of the flag's kind before every one of its append attempts: after N passes
    /// (N from the errata) <c>503 curia/flag/contended</c>, the type and the title from the words, <c>detail</c> null in the served body, nothing recorded for the flag, exactly N
    /// <c>AppendGuardRedecided</c> lines, the private row written once; never <c>500</c>; the same request sent again with the injection off is <c>201</c>.
    /// </summary>
    [Fact]
    public async Task R10_94_LCB16_TheFlagLoopIsBoundedAndItsEndIsNotA500()
    {
        var board = RevisionProbe.Board();
        var asker = await AcceptedAnswerScene.EnrolAsync(_forum, "asker", Ct);
        var raiser = await AcceptedAnswerScene.EnrolAsync(_forum, "raiser", Ct);
        var p = await AskAsync(asker, board);
        var contended = PublishedLateCommitWords.Contended();
        var redecided = PublishedLateCommitWords.RedecidedEvent();
        var passes = PublishedLateCommitWords.Passes;
        var token = await raiser.Dpop.GetTokenAsync(_forum.Client, TokenEndpoint, _forum.Now, Ct);

        _forum.Store.Arm(
            id => id.Value.StartsWith("flag:", StringComparison.Ordinal),
            async (attempt, ct) =>
            {
                var id = Require(new UlidGenerator(_forum.Clock).Next()).ToString();
                var payload = new JsonValue.Object([new("commitment", new JsonValue.String("injected-" + attempt)), new("kind", new JsonValue.String("spam"))]);
                Require(await _forum.Store.Inner.AppendAsync(
                    Require(AggregateId.Create("flag:" + id)), AggregateVersion.New,
                    [new DomainEvent(Require(EventId.Create(id)), Require(EventType.Create(FlagProjector.FlagCommittedType)), null, payload)], ct));
            });

        var mark = _forum.Log.Mark();
        var flagEventsBefore = await ScalarAsync("SELECT count(*) FROM events WHERE event_type = 'flag.committed';");
        var sent = await RaiseAsync(raiser, p.PostId, "spam", token);
        _forum.Store.Reset();
        var flagEventsAfter = await ScalarAsync("SELECT count(*) FROM events WHERE event_type = 'flag.committed';");
        var privateRows = await ScalarAsync("SELECT count(*) FROM flag_details WHERE post_id = @post AND raised_by = @raiser;", ("post", p.PostId), ("raiser", raiser.Agent.AgentId));
        var bodyDetailNull = sent.Body.Contains("\"detail\":null", StringComparison.Ordinal) || !sent.Body.Contains("\"detail\"", StringComparison.Ordinal);

        Said(
            $"{contended.Status} {contended.Type}, title \"{contended.Title}\", detail null, {redecided.Name} logged {passes}, the private row written 1, flag entries added {passes} (the injected ones)",
            $"{Slug(sent)}, title \"{sent.Title}\", detail {(bodyDetailNull ? "null" : "served")}, {redecided.Name} logged {LinesOf(mark, redecided.Id)}, the private row written {privateRows}, flag entries added {flagEventsAfter - flagEventsBefore}");

        var again = await RaiseAsync(raiser, p.PostId, "spam", token);
        Said("the same flag sent again with the injection off: 201 or 409 curia/flag/already-raised (never 500)", "the same flag sent again with the injection off: " + (again.Status is 201 || again.Type == "curia/flag/already-raised" ? "201 or 409 curia/flag/already-raised (never 500)" : again.Says));
    }

    /// <summary>
    /// LCB17. An orphan row is read by nothing. A private row with no entry (committed alone) is not counted by the raiser's flag budget readout, not joined by <c>FlagDirectory.Join</c> and
    /// not in the raiser's or the owner's listing. Green on the seam if the join already ignores an orphan (Task 0 read it: it does; a control); red if it does not, and the plan stops.
    /// </summary>
    [Fact]
    public async Task R10_94_LCB17_AnOrphanPrivateRowIsReadByNothing()
    {
        var board = RevisionProbe.Board();
        var asker = await AcceptedAnswerScene.EnrolAsync(_forum, "asker", Ct);
        var raiser = await AcceptedAnswerScene.EnrolAsync(_forum, "raiser", Ct);
        var p = await AskAsync(asker, board);
        var used0 = (await BudgetReadouts.ReadAsync(_forum, raiser)).Entry("flag").GetProperty("used").GetInt32();

        await using (var holder = await LateCommitHolder.BeginAsync(_forum.AppRole, "public", Ct))
        {
            await holder.ExecuteAsync(
                "INSERT INTO flag_details (event_id, post_id, raised_by, rationale, salt) VALUES (@id, @post, @raiser, 'orphan', 'salt');",
                Ct, ("id", "ORPHAN" + Guid.NewGuid().ToString("N")[..20]), ("post", p.PostId), ("raiser", raiser.Agent.AgentId));
            await holder.CommitAsync(Ct);
        }

        var used1 = (await BudgetReadouts.ReadAsync(_forum, raiser)).Entry("flag").GetProperty("used").GetInt32();
        var log = await RevisionProbe.ReadLogAsync(_forum, Ct);
        var details = (await RowsAsync("SELECT event_id, post_id, raised_by, rationale, salt FROM flag_details;"))
            .Select(r => r.Split('|')).Select(f => new FlagDetail(f[0], f[1], f[2], f[3], f[4])).ToList();
        var joined = FlagDirectory.Join(log, details).Flags.Count(f => f.PostId == p.PostId);

        Said("flag budget used unchanged by the orphan, joined flags 0", $"flag budget used {(used1 == used0 ? "unchanged by the orphan" : $"{used0} then {used1}")}, joined flags {joined}");
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------
    // LCB18 to LCB22: what must not change, the mapping, every exit
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// LCB18. A refusal changes nothing it should not, and logs once. A digest refusal (the shape the other two share): the events count and the highest sequence are those of the holder's
    /// commit and nothing more (SQL), <c>budget.used</c> is unchanged by the refused request, neither <c>AppendConfirmed</c> nor <c>AppendNotConfirmed</c> is logged, and exactly one
    /// <c>AppendGuardRefused</c> is logged with the kind and the type from the words and no digest, no post id and none of the post's text (a sentinel in the body is in no captured byte).
    /// </summary>
    [Fact]
    public async Task R8_86_LCB18_AGuardRefusalChangesNothingAndLogsOnceWithoutADigest()
    {
        var board = RevisionProbe.Board();
        var sender = await T2Async(board);
        var refused = PublishedLateCommitWords.RefusedEvent();
        var canary = "CANARY" + Guid.NewGuid().ToString("N");
        var wire = sender.Agent.SignQuestion(board, canary + " " + RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), _forum.Now);
        var late = LatePost(wire);
        var usedBefore = (await BudgetReadouts.ReadAsync(_forum, sender)).Entry("posting").GetProperty("used").GetInt32();
        var mark = _forum.Log.Mark();

        var sent = await SendBehindAsync(sender, wire, holder => InsertAsync(holder, late));
        var eventsAfterCommit = await EventsAsync();
        var highest = await HighestAsync();
        var usedAfter = (await BudgetReadouts.ReadAsync(_forum, sender)).Entry("posting").GetProperty("used").GetInt32();
        var logged = _forum.Log.Since(mark);
        var guardLines = logged.Where(e => e.EventId == refused.Id).ToList();
        var confirmLines = logged.Count(e => e.EventId is 5004 or 5005);
        var leak = logged.Any(e => e.Message.Contains(canary, StringComparison.Ordinal) || e.Message.Contains(late.Digest, StringComparison.Ordinal) || e.Message.Contains(late.PostId, StringComparison.Ordinal)
            || e.Values.Any(v => (v.Value?.ToString() ?? string.Empty).Contains(canary, StringComparison.Ordinal)));

        Said(
            $"{refused.Name} logged 1 ({refused.Message.Replace("<Kind>", "question", StringComparison.Ordinal).Replace("<RefusalType>", PublishedLateCommitWords.Refusals()[0].Type, StringComparison.Ordinal)}), no digest or post id or text in the log, 0 confirm lines, the table holds 1 of the digest (409 {PublishedLateCommitWords.Refusals()[0].Type}), budget used moved by 0",
            $"{refused.Name} logged {guardLines.Count}{(guardLines.Count == 1 ? $" ({guardLines[0].Message})" : string.Empty)}, {(leak ? "the log carries a digest, post id or text" : "no digest or post id or text in the log")}, {confirmLines} confirm lines, the table holds {await WithDigestAsync(late.Digest)} of the digest ({Slug(sent)}), budget used moved by {usedAfter - usedBefore - 1}");
        Assert.True(eventsAfterCommit > 0 && highest > 0);
    }

    /// <summary>
    /// LCB19. The healthy path and the false-refusal direction (traps 100 and 110). Every kind <c>201</c> with the receipt's members unchanged by an unrelated delta; a revision whose delta holds unrelated events
    /// (other agents' posts committed between its read and its append) is <c>201</c>; a post whose delta holds a post of a different digest is <c>201</c>; over a run of mixed writes by eight agents the number of refusals
    /// of a legitimate act is 0. Green on the seam (controls); red under the falsification rows that over-refuse.
    /// </summary>
    [Fact]
    public async Task R8_86_LCB19_AnUnrelatedLateCommitRefusesNothingLegitimate()
    {
        var board = RevisionProbe.Board();
        var sender = await T2Async(board);
        var bystander = await T2Async(board);
        var now = _forum.Now;
        var said = new List<string>();
        var expected = new List<string>();

        var p = await AskAsync(sender, board);
        var unrelated = LatePost(bystander.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), now));
        var revised = await SendBehindAsync(sender, sender.Agent.SignRevision(board, "Corrected " + RevisionProbe.Unrelated(), p.PostId, p.Digest, now), holder => InsertAsync(holder, unrelated));
        expected.Add("a revision with an unrelated post in its delta: 201");
        said.Add($"a revision with an unrelated post in its delta: {revised.Says}");

        var unrelated2 = LatePost(bystander.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), now));
        var asked = await SendBehindAsync(sender, sender.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), now), holder => InsertAsync(holder, unrelated2));
        expected.Add("a question with a post of a different digest in its delta: 201");
        said.Add($"a question with a post of a different digest in its delta: {asked.Says}");

        var agents = new List<SceneActor>();
        for (var i = 0; i < 8; i++) agents.Add(await AcceptedAnswerScene.T1Async(_forum, board, Ct));
        var limit = BudgetBursts.PostLimit("T1") - 3;
        var refusals = new List<string>();
        await Task.WhenAll(agents.Select(agent => Task.Run(async () =>
        {
            for (var i = 0; i < limit; i++)
            {
                var sent = await RevisionProbe.SendAsync(
                    _forum, agent, i % 2 == 0 ? agent.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), _forum.Now) : agent.Agent.Sign(PostKind.Comment, board, RevisionProbe.Unrelated(), null, p.PostId, _forum.Now),
                    cancellationToken: Ct);
                if (sent.Status != 201) lock (refusals) refusals.Add(sent.Says);
            }
        }, Ct)));
        expected.Add($"{agents.Count * limit} mixed writes by {agents.Count} agents: 0 refused");
        said.Add($"{agents.Count * limit} mixed writes by {agents.Count} agents: {refusals.Count} refused{(refusals.Count > 0 ? " (" + string.Join(", ", refusals.Distinct()) + ")" : string.Empty)}");

        Said(string.Join(" | ", expected), string.Join(" | ", said));
    }

    /// <summary>
    /// LCB20 (route half). The route's own answer to the three sequential refusals: status, type, title and detail as <c>f4f8d899</c> answered them (the titles are G45's, recorded from that tree). Green on the seam: the
    /// extraction of the checks changes no answer (a control; red under FLCB27). The table half is <c>SubmissionChecksTests</c>.
    /// </summary>
    [Fact]
    public async Task R8_86_LCB20_TheRoutesOwnAnswersAreUnchangedByTheExtraction()
    {
        var board = RevisionProbe.Board();
        var sender = await T2Async(board);
        var other = await T2Async(board);
        var question = await AskAsync(other, board);
        var target = await AnswerAsync(other, board, question);
        var now = _forum.Now;
        var said = new List<string>();

        var wire = sender.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), now);
        var first = await RevisionProbe.SendAsync(_forum, sender, wire, cancellationToken: Ct);
        var second = await RevisionProbe.SendAsync(_forum, sender, wire, cancellationToken: Ct);
        said.Add($"{second.Status} {second.Type} \"{second.Title}\" {second.Detail}".Replace(first.PostId!, "<post id>", StringComparison.Ordinal));

        var p = await AskAsync(sender, board);
        var r1 = await RevisionProbe.ReviseAsync(_forum, sender, board, p, "First " + RevisionProbe.Unrelated(), cancellationToken: Ct);
        var r2 = await RevisionProbe.ReviseAsync(_forum, sender, board, p, "Second " + RevisionProbe.Unrelated(), cancellationToken: Ct);
        said.Add($"{r2.Status} {r2.Type} \"{r2.Title}\" {r2.Detail}".Replace(r1.PostId!, "<post id>", StringComparison.Ordinal));

        Assert.Equal(201, (await RevisionProbe.SendAsync(_forum, sender, sender.Agent.SignVote(board, target.Digest, now), cancellationToken: Ct)).Status);
        var v2 = await RevisionProbe.SendAsync(_forum, sender, sender.Agent.SignVote(board, target.Digest, now), cancellationToken: Ct);
        said.Add($"{v2.Status} {v2.Type} \"{v2.Title}\" {v2.Detail}".Replace(target.Digest, "<target>", StringComparison.Ordinal));

        Said(
            string.Join(" | ",
            [
                "409 curia/ingest/already-recorded \"This post is already recorded\" post=<post id>",
                "409 curia/ingest/already-revised \"This post already has a revision\" successor=<post id>",
                "409 curia/verification/already-voted \"This agent has already voted on that digest; a vote cannot be recast\" <target>",
            ]),
            string.Join(" | ", said));
    }

    /// <summary>
    /// LCB21. The mapping. A failed append with each of the three guard types is <c>409</c> (the status the errata gives it), with <c>curia/append/delta-too-large</c> is <c>503</c> (type, title and a null
    /// <c>detail</c> from the words), and one with another type is <c>500</c> as before; <c>curia/append/redecide</c> returned to a submission's append is <c>500</c> and is not in the body.
    /// The decorator answers the append with the failure; nothing else is fabricated.
    /// </summary>
    [Fact]
    public async Task R8_86_LCB21_AFailedAppendIsMappedToItsStatus()
    {
        var board = RevisionProbe.Board();
        var sender = await T2Async(board);
        var tooLarge = PublishedLateCommitWords.DeltaTooLarge();
        var cases = new List<(string Name, Error Error, int Status, bool BodyHasDetailNull)>();
        foreach (var (type, status, detail) in PublishedLateCommitWords.Refusals())
            cases.Add((type, new Error(type, "A guard refusal", detail.Contains('<', StringComparison.Ordinal) ? "post=PROBE" : "target"), status, false));
        cases.Add((tooLarge.Type, new Error(tooLarge.Type, tooLarge.Title), tooLarge.Status, true));
        cases.Add(("probe/other", new Error("probe/other", "Another failure", "detail"), 500, false));
        cases.Add((PublishedLateCommitWords.RedecideType, new Error(PublishedLateCommitWords.RedecideType, "Redecide", null), 500, false));

        var expected = new List<string>();
        var said = new List<string>();
        foreach (var (name, error, status, detailNull) in cases)
        {
            _forum.Store.FailAppends(id => !id.Value.StartsWith("urn:", StringComparison.Ordinal), error);
            var sent = await RevisionProbe.SendAsync(_forum, sender, sender.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), _forum.Now), cancellationToken: Ct);
            _forum.Store.Reset();
            var bodyDetailNull = !sent.Body.Contains("\"detail\":\"", StringComparison.Ordinal);
            expected.Add($"{name}: {status}{(status == 500 ? string.Empty : " " + name)}{(detailNull ? ", detail null" : string.Empty)}{(name == PublishedLateCommitWords.RedecideType ? ", not in the body" : string.Empty)}");
            said.Add($"{name}: {sent.Status}{(sent.Status == 500 ? string.Empty : " " + sent.Type)}{(detailNull ? (bodyDetailNull ? ", detail null" : ", detail served") : string.Empty)}{(name == PublishedLateCommitWords.RedecideType ? (sent.Body.Contains("redecide", StringComparison.Ordinal) ? ", in the body" : ", not in the body") : string.Empty)}");
        }

        Said(string.Join(" | ", expected), string.Join(" | ", said));
    }

    /// <summary>
    /// LCB22 (route half). Every exit releases. After an append that failed with a guard type, and after one that threw, the same agent's next request is admitted (not <c>agent-in-flight</c>) and a
    /// revision of the same <c>prev</c> is not <c>revision-in-flight</c> (trap 107). Green on the seam (the failure paths release today: controls); red under FLCB8's leaks. The store half is in the
    /// Infrastructure suite.
    /// </summary>
    [Fact]
    public async Task R8_86_LCB22_EveryExitOfTheRouteReleasesTheAgentsHoldAndTheRevisionGate()
    {
        var board = RevisionProbe.Board();
        var sender = await T2Async(board);
        var p = await AskAsync(sender, board);
        var said = new List<string>();

        _forum.Store.FailAppends(id => !id.Value.StartsWith("urn:", StringComparison.Ordinal), new Error(PublishedLateCommitWords.Refusals()[1].Type, "A guard refusal", "successor=PROBE"));
        var failed = await RevisionProbe.ReviseAsync(_forum, sender, board, p, "First " + RevisionProbe.Unrelated(), cancellationToken: Ct);
        _forum.Store.FailAppends(id => !id.Value.StartsWith("urn:", StringComparison.Ordinal), null, new InvalidOperationException("the probe's store threw"));
        var threw = await RevisionProbe.ReviseAsync(_forum, sender, board, p, "Second " + RevisionProbe.Unrelated(), cancellationToken: Ct);
        _forum.Store.Reset();
        var next = await RevisionProbe.ReviseAsync(_forum, sender, board, p, "Third " + RevisionProbe.Unrelated(), cancellationToken: Ct);
        var after = await AskAsync(sender, board);

        Assert.NotNull(failed);
        Assert.NotNull(threw);
        Said("the next revision of the same prev: 201, the agent's next question stored", $"the next revision of the same prev: {next.Says}, the agent's next question {(after.PostId.Length > 0 ? "stored" : "not stored")}");
    }

    // ------------------------------------------------------------------------------------------------------------------------------------------------------------
    // LCB23, LCB26, LCB29: the oracle's control, what is already stored, the cap
    // ------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>The SQL of the section of <c>tools/fault-harness/forks.sh</c> whose heading begins with <paramref name="heading"/>: the line after the <c>section</c> line, unquoted, <c>\"</c> unescaped.</summary>
    private static string OracleQuery(string heading)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "fault-harness", "forks.sh"))) dir = dir.Parent;
        var lines = File.ReadAllLines(Path.Combine(dir!.FullName, "tools", "fault-harness", "forks.sh"));
        var at = Enumerable.Range(0, lines.Length).Where(i => lines[i].StartsWith("section \"" + heading, StringComparison.Ordinal)).ToArray();
        Assert.Single(at);
        var sql = lines[at[0] + 1].Trim();
        Assert.StartsWith("\"", sql, StringComparison.Ordinal);
        Assert.EndsWith("\"", sql, StringComparison.Ordinal);
        return sql[1..^1].Replace("\\\"", "\"", StringComparison.Ordinal);
    }

    /// <summary>
    /// LCB23. The oracle has a positive control (vacuity question 1: a passing and an absent probe must differ). The queries of <c>tools/fault-harness/forks.sh</c>, read from the script itself and run on this database
    /// after a seed made through the direct-append seam (two revisions of one <c>prev</c>, one digest stored twice, a flag recorded twice, a vote cast twice), return exactly the seeded rows; on the same
    /// database with the mark taken after the seed they return none; and the fork the SQL finds is the fork <c>CorrectionIndex</c> finds on the same log (two artifacts, one input).
    /// </summary>
    [Fact]
    public async Task R6_60_LCB23_TheOracleReturnsTheSeededRowsAndNoOthers()
    {
        var board = RevisionProbe.Board();
        var sender = await T2Async(board);
        var other = await T2Async(board);
        var now = _forum.Now;
        var mark = await HighestAsync();

        var p = await AskAsync(sender, board);
        var fork1 = await _forum.AppendStoredPostAsync(sender.Agent.SignRevision(board, "One " + RevisionProbe.Unrelated(), p.PostId, p.Digest, now), Ct);
        var fork2 = await _forum.AppendStoredPostAsync(sender.Agent.SignRevision(board, "Two " + RevisionProbe.Unrelated(), p.PostId, p.Digest, now), Ct);
        var duplicate = sender.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), now);
        await _forum.AppendStoredPostAsync(duplicate, Ct);
        var second = await _forum.AppendStoredPostAsync(duplicate, Ct);
        var question = await AskAsync(other, board);
        var result = await AnswerAsync(other, board, question);
        await _forum.AppendStoredPostAsync(sender.Agent.SignVote(board, result.Digest, now), Ct);
        await _forum.AppendStoredPostAsync(sender.Agent.SignVote(board, result.Digest, now), Ct);
        await using (var holder = await LateCommitHolder.BeginAsync(_forum.AppRole, "public", Ct))
        {
            await LateFlagAsync(holder, p.PostId, sender.Agent.AgentId, "spam");
            await LateFlagAsync(holder, p.PostId, sender.Agent.AgentId, "spam");
            await holder.CommitAsync(Ct);
        }

        var queries = new[] { "revision forks", "digests stored twice", "flag triples recorded twice", "votes by one author" };
        var found = new List<string>();
        foreach (var heading in queries)
        {
            var rows = await RowsAsync(OracleQuery(heading).Replace("$SEQ", mark.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            found.Add($"{heading}: {rows.Count}");
        }

        var clean = new List<string>();
        var markAfter = await HighestAsync();
        foreach (var heading in queries)
            clean.Add($"{heading}: {(await RowsAsync(OracleQuery(heading).Replace("$SEQ", markAfter.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))).Count}");

        var log = await RevisionProbe.ReadLogAsync(_forum, Ct);
        var posts = Curia.Application.Projections.PostProjector.Fold(log);
        var index = CorrectionIndex.Fold(posts, _ => true);
        var original = posts.First(x => x.PostId == p.PostId);
        var forkByCode = string.Join(",", index.For(original).Select(x => x.PostId).Order(StringComparer.Ordinal));
        var forkBySql = string.Join(",", new[] { fork1.PostId, fork2.PostId }.Order(StringComparer.Ordinal));

        Said(
            "seeded: revision forks: 1 | digests stored twice: 1 | flag triples recorded twice: 1 | votes by one author: 1; clean: all 0; the fork is the one CorrectionIndex finds",
            $"seeded: {string.Join(" | ", found)}; clean: {(clean.All(c => c.EndsWith(": 0", StringComparison.Ordinal)) ? "all 0" : string.Join(" | ", clean))}; the fork is {(forkByCode == forkBySql ? "the one CorrectionIndex finds" : $"not the one CorrectionIndex finds ({forkByCode} against {forkBySql})")}");
        Assert.NotEqual(second.PostId, string.Empty);
    }

    /// <summary>
    /// LCB26. A fork or a duplicate already stored is read as it stands (trap 120, by surface). A log seeded with a fork and a duplicate through the direct-append seam is served by every read surface
    /// listed here (the single read of each post, the thread, the board listing, <c>superseded_by</c>, the batch's <c>successors</c>) as <c>f4f8d899</c> served it: no refusal, no repair, both served and the
    /// fork named. A control: the guard is on writes only; red under FLCB28.
    /// </summary>
    [Fact]
    public async Task R8_86_LCB26_AStoredForkAndDuplicateAreReadAsTheyStand()
    {
        var board = RevisionProbe.Board();
        var sender = await T2Async(board);
        var now = _forum.Now;
        var p = await AskAsync(sender, board);
        var fork1 = await _forum.AppendStoredPostAsync(sender.Agent.SignRevision(board, "One " + RevisionProbe.Unrelated(), p.PostId, p.Digest, now), Ct);
        var fork2 = await _forum.AppendStoredPostAsync(sender.Agent.SignRevision(board, "Two " + RevisionProbe.Unrelated(), p.PostId, p.Digest, now), Ct);
        var wire = sender.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), now);
        var dup1 = await _forum.AppendStoredPostAsync(wire, Ct);
        var dup2 = await _forum.AppendStoredPostAsync(wire, Ct);

        var said = new List<string>();
        foreach (var id in new[] { p.PostId, fork1.PostId, fork2.PostId, dup1.PostId, dup2.PostId })
        {
            using var response = await _forum.Client.GetAsync(new Uri($"/v1/posts/{id}", UriKind.Relative), Ct);
            said.Add(((int)response.StatusCode).ToString(CultureInfo.InvariantCulture));
        }

        var superseded = await RevisionProbe.SupersededByAsync(_forum, p.PostId, Ct);
        using var thread = await _forum.Client.GetAsync(new Uri($"/v1/threads/{p.PostId}", UriKind.Relative), Ct);
        using var listing = await _forum.Client.GetAsync(new Uri($"/v1/boards/{board}/posts", UriKind.Relative), Ct);

        Said(
            "single reads 200,200,200,200,200; superseded_by names 2; thread 200; board listing 200",
            $"single reads {string.Join(",", said)}; superseded_by names {superseded.Length}; thread {(int)thread.StatusCode}; board listing {(int)listing.StatusCode}");
    }

    /// <summary>
    /// LCB29 (route half). The cap, through the route. A question, a revision and a vote whose delta is made 10,001 events by the lock holder (events inserted as the application role) are each <c>503</c> with
    /// the type, status and title from the words and <c>detail</c> null, nothing stored, <c>budget.used</c> unchanged, and neither <c>AppendGuardRefused</c> nor <c>AppendGuardRedecided</c> in the log; the same
    /// request sent again once the delta is within the cap is <c>201</c>; at a delta of exactly the cap a duplicate in it is <c>409 already-recorded</c>.
    /// </summary>
    [Fact]
    public async Task R6_60_LCB29_ADeltaPastTheCapIsRefusedUnreadAndExactlyTheCapIsEvaluated()
    {
        var cap = PublishedLateCommitWords.DeltaCap;
        var tooLarge = PublishedLateCommitWords.DeltaTooLarge();
        var refused = PublishedLateCommitWords.RefusedEvent();
        var redecided = PublishedLateCommitWords.RedecidedEvent();
        var board = RevisionProbe.Board();
        var sender = await T2Async(board);
        var other = await T2Async(board);
        var question = await AskAsync(other, board);
        var target = await AnswerAsync(other, board, question);
        var own = await AskAsync(sender, board);
        var now = _forum.Now;

        static Task Filler(LateCommitHolder holder, int count, CancellationToken ct) => holder.ExecuteAsync(
            "INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) SELECT 'filler-' || @tag || '-' || g, 'test.event', 'filler-' || @tag || '-' || g, NULL, '{}'::jsonb, now() FROM generate_series(1, @n) g;",
            ct, ("tag", Guid.NewGuid().ToString("N")), ("n", count));

        var expected = new List<string>();
        var said = new List<string>();
        var kinds = new (string Name, Func<byte[]> Wire)[]
        {
            ("question", () => sender.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), now)),
            ("revision", () => sender.Agent.SignRevision(board, "Corrected " + RevisionProbe.Unrelated(), own.PostId, own.Digest, now)),
            ("vote", () => sender.Agent.SignVote(board, target.Digest, now)),
        };

        foreach (var (name, make) in kinds)
        {
            var used = (await BudgetReadouts.ReadAsync(_forum, sender)).Entry("posting").GetProperty("used").GetInt32();
            var mark = _forum.Log.Mark();
            var postsBefore = await RevisionProbe.PostsByAsync(_forum, sender.Agent.AgentId);
            var wire = make();
            var sent = await SendBehindAsync(sender, wire, holder => Filler(holder, cap + 1, Ct));
            var usedAfter = (await BudgetReadouts.ReadAsync(_forum, sender)).Entry("posting").GetProperty("used").GetInt32();
            var postsAfter = await RevisionProbe.PostsByAsync(_forum, sender.Agent.AgentId);
            var lines = LinesOf(mark, refused.Id) + LinesOf(mark, redecided.Id);
            var detailNull = !sent.Body.Contains("\"detail\":\"", StringComparison.Ordinal);
            expected.Add($"{name}: {tooLarge.Status} {tooLarge.Type}, title from the words, detail null, stored 0, budget moved 0, guard log lines 0");
            said.Add($"{name}: {Slug(sent)}, title {(sent.Title == tooLarge.Title ? "from the words" : $"\"{sent.Title}\"")}, detail {(detailNull ? "null" : "served")}, stored {postsAfter - postsBefore}, budget moved {usedAfter - used}, guard log lines {lines}");

            var again = await RevisionProbe.SendAsync(_forum, sender, wire, cancellationToken: Ct);
            expected.Add($"{name} sent again within the cap: 201");
            said.Add($"{name} sent again within the cap: {again.Says}");
        }

        var duplicate = sender.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), now);
        var late = LatePost(duplicate);
        var exact = await SendBehindAsync(sender, duplicate, async holder =>
        {
            await InsertAsync(holder, late);
            await Filler(holder, cap - 1, Ct);
        });
        expected.Add("at exactly the cap a duplicate in the delta: 409 curia/ingest/already-recorded");
        said.Add($"at exactly the cap a duplicate in the delta: {Slug(exact)}");

        Said(string.Join(" | ", expected), string.Join(" | ", said));
    }
}
