using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R8.87 (errata G61) over a real Forum, RPB4 to RPB19: an answer, a comment and a revision are refused <c>400 curia/ingest/board-not-parents</c> unless their board is the board
/// stored for the post they follow, after AUTHORIZE and R8.70's and R8.72's checks (R8.77's and R8.78's for a revision) and before the near-duplicate check, SCREEN and PERSIST;
/// R8.85's grammar is not asked of them; no other kind is compared; nothing is stored and no budget spent by a refusal.
///
/// <para><b>Oracles.</b> What was stored is counted from the <c>events</c> table by SQL on the fixture's connection string, and the board a post is listed on from
/// <c>GET /v1/boards/{board}/posts</c>, a different artifact than the payload the comparison reads (RPB7); <c>budget.used</c> is read from <c>GET /v1/budget</c> before and after on
/// the same agent. The type, status, title and detail keys are parsed from R8.87 words (<see cref="PublishedReplyBoardWords"/>), the grammar's type from R8.85 words
/// (<see cref="PublishedBoardWords"/>), the parent checks' types from R8.70 and R8.72 (<see cref="PublishedParentRule"/>, <see cref="PublishedWithheldWords"/>). A board a
/// parent must hold that the door refuses is stored by <see cref="ForumFixture.AppendStoredPostAsync"/>, never through the door (the accepting side is not built from the code it
/// checks). Two boards are used that sort either way round the scene's (<c>a-</c> and <c>z-</c>), and a third that is a case variant. Every refusal has its control: the same act
/// with the parent's board is <c>201</c>. Each probe is in its failing form, and on the seam (the route compares nothing) the refusals answer <c>201</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ReplyBoardEndToEndTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<Sent> SendAsync(SceneActor actor, byte[] wire, string? token = null) => RevisionProbe.SendAsync(forum, actor, wire, token, Ct);

    private async Task<long> PostEventsAsync()
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = 'post.accepted'", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task<int> UsedAsync(SceneActor actor) =>
        (await BudgetReadouts.ReadAsync(forum, actor)).Entry("posting").GetProperty("used").GetInt32();

    /// <summary>The ids <c>GET /v1/boards/{board}/posts</c> lists on a board.</summary>
    private async Task<string[]> ListedAsync(string board)
    {
        var listed = await forum.Client.GetFromJsonAsync<JsonElement>(new Uri($"/v1/boards/{Uri.EscapeDataString(board)}/posts", UriKind.Relative), Ct);
        return [.. listed.EnumerateArray().Select(p => p.GetProperty("post_id").GetString()!)];
    }

    private static string Other(string which) => which == "before" ? "a-" + Suffix() : "z-" + Suffix();

    public static TheoryData<string> Sides() => new() { "before", "after" };

    private static string Said(Sent sent) => sent.Says;

    private static string Published => $"{PublishedReplyBoardWords.Status()} {PublishedReplyBoardWords.Type()}";

    /// <summary>
    /// One refused act and its three invariants. The answer must be the published refusal naming <paramref name="detailKey"/> and <paramref name="id"/> and no board; the events table
    /// and the sender's <c>budget.used</c> must not move. The problems are returned, so a probe can run several rows and report them together.
    /// </summary>
    private async Task<List<string>> RefusedAsync(string label, SceneActor who, byte[] wire, string detailKey, string id, params string[] boards)
    {
        var problems = new List<string>();
        var events = await PostEventsAsync();
        var used = await UsedAsync(who);

        var sent = await SendAsync(who, wire);

        if (!string.Equals(Said(sent), Published, StringComparison.Ordinal))
        {
            problems.Add($"{label}: Expected: {Published} Actual: {Said(sent)}");
        }
        else
        {
            if (sent.Title != PublishedReplyBoardWords.Title()) problems.Add($"{label}: the title is \"{sent.Title}\", expected \"{PublishedReplyBoardWords.Title()}\"");
            if (sent.Detail != detailKey + id) problems.Add($"{label}: the detail is \"{sent.Detail}\", expected \"{detailKey}{id}\"");
            foreach (var board in boards)
                if (sent.Body.Contains(board, StringComparison.Ordinal))
                    problems.Add($"{label}: the body echoes the board \"{board}\"");
        }

        var eventsAfter = await PostEventsAsync();
        if (eventsAfter != events) problems.Add($"{label}: the log moved by {eventsAfter - events} post(s)");
        var usedAfter = await UsedAsync(who);
        if (usedAfter != used) problems.Add($"{label}: the posting budget moved from {used} to {usedAfter}");
        return problems;
    }

    private static void None(List<string> problems, string expected)
    {
        if (problems.Count > 0)
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {string.Join("; ", problems)}");
    }

    private async Task<string> AcceptedAsync(string label, SceneActor who, byte[] wire, List<string> problems)
    {
        var sent = await SendAsync(who, wire);
        if (sent.Status != 201) problems.Add($"{label} (the control, on the parent's board): Expected: 201 Actual: {Said(sent)}");
        return sent.PostId ?? string.Empty;
    }

    private static (string Parent, string Original) Keys() => PublishedReplyBoardWords.DetailKeys();

    // ---- RPB4 -----------------------------------------------------------------------------------

    /// <summary>
    /// RPB4. An answer on a board other than its question's (one that sorts before the question's, one after) is refused <c>400 board-not-parents</c> in the published words, naming
    /// the question and no board, with the log and the budget unmoved; the same answer on the question's board is <c>201</c>. Predicted red: <c>201</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sides))]
    public async Task R8_87_RPB4_AnAnswerOnAnotherBoardIsRefusedAndOnItsQuestionsBoardIsTaken(string side)
    {
        ArgumentNullException.ThrowIfNull(side);
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var other = Other(side);

        var problems = await RefusedAsync(
            "answer on " + other, scene.Answerer, scene.Answerer.Agent.SignAnswer(other, "An answer " + Unrelated(), scene.Question.PostId, forum.Now), Keys().Parent, scene.Question.PostId, scene.Board, other);
        await AcceptedAsync("answer", scene.Answerer, scene.Answerer.Agent.SignAnswer(scene.Board, "An answer " + Unrelated(), scene.Question.PostId, forum.Now), problems);

        None(problems, $"400 {PublishedReplyBoardWords.Type()} naming the question and no board, nothing stored or spent, and 201 on the question's board");
    }

    // ---- RPB5 -----------------------------------------------------------------------------------

    /// <summary>
    /// RPB5. A comment on another board, under a question and under an answer, is refused in the published words naming its parent; on the parent's board it is <c>201</c>.
    /// Predicted red: <c>201</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sides))]
    public async Task R8_87_RPB5_ACommentOnAnotherBoardIsRefusedUnderAQuestionAndUnderAnAnswer(string side)
    {
        ArgumentNullException.ThrowIfNull(side);
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var other = Other(side);
        var problems = new List<string>();

        foreach (var (label, parent) in new[] { ("question", scene.Question), ("answer", scene.Answer1) })
        {
            problems.AddRange(await RefusedAsync(
                $"comment under a {label} on {other}", scene.Answerer, scene.Answerer.Agent.Sign(PostKind.Comment, other, "A comment " + Unrelated(), null, parent.PostId, forum.Now),
                Keys().Parent, parent.PostId, scene.Board, other));
            await AcceptedAsync($"comment under a {label}", scene.Answerer, scene.Answerer.Agent.Sign(PostKind.Comment, scene.Board, "A comment " + Unrelated(), null, parent.PostId, forum.Now), problems);
        }

        None(problems, $"400 {PublishedReplyBoardWords.Type()} for a comment on another board, under a question and under an answer, and 201 on the parent's board");
    }

    // ---- RPB6 -----------------------------------------------------------------------------------

    /// <summary>
    /// RPB6. A revision of the author's own question on another board is refused in the published words, naming the revised post as <c>original=</c> (not <c>parent=</c>); on the
    /// question's board it is <c>201</c>. Predicted red: <c>201</c> (the second revision is not reached).
    /// </summary>
    [Theory]
    [MemberData(nameof(Sides))]
    public async Task R8_87_RPB6_ARevisionOnAnotherBoardIsRefusedNamingTheOriginal(string side)
    {
        ArgumentNullException.ThrowIfNull(side);
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var other = Other(side);
        var question = await AskAsync(forum, scene.Answerer, scene.Board, Ct);

        var problems = await RefusedAsync(
            "revision on " + other, scene.Answerer, scene.Answerer.Agent.SignRevision(other, "Corrected " + Unrelated(), question.PostId, question.Digest, forum.Now),
            Keys().Original, question.PostId, scene.Board, other);
        await AcceptedAsync("revision", scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, "Corrected " + Unrelated(), question.PostId, question.Digest, forum.Now), problems);

        None(problems, $"400 {PublishedReplyBoardWords.Type()} naming original=<the question>, nothing stored or spent, and 201 on the question's board");
    }

    // ---- RPB7 -----------------------------------------------------------------------------------

    /// <summary>
    /// RPB7, derived from the listing and not from the comparison. After an answer, a comment and a revision are refused on another board, that board lists no post; after the
    /// same three are sent on the parent's board, that board lists all three and the other board still lists nothing. Predicted red: the other board lists the replies.
    /// </summary>
    [Theory]
    [MemberData(nameof(Sides))]
    public async Task R8_87_RPB7_ARefusedReplyIsListedOnNoBoardAndAnAcceptedOneOnItsThreadsBoard(string side)
    {
        ArgumentNullException.ThrowIfNull(side);
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var other = Other(side);
        var question = await AskAsync(forum, scene.Answerer, scene.Board, Ct);
        var problems = new List<string>();

        var refused = new (string Kind, byte[] Wire)[]
        {
            ("answer", scene.Answerer.Agent.SignAnswer(other, "An answer " + Unrelated(), question.PostId, forum.Now)),
            ("comment", scene.Answerer.Agent.Sign(PostKind.Comment, other, "A comment " + Unrelated(), null, question.PostId, forum.Now)),
            ("revision", scene.Answerer.Agent.SignRevision(other, "Corrected " + Unrelated(), question.PostId, question.Digest, forum.Now)),
        };
        foreach (var (_, wire) in refused) _ = await SendAsync(scene.Answerer, wire);
        var onOtherAfterRefusals = await ListedAsync(other);
        if (onOtherAfterRefusals.Length != 0) problems.Add($"after the refusals the board {other} lists {onOtherAfterRefusals.Length} post(s)");

        var accepted = new List<string>
        {
            await AcceptedAsync("answer", scene.Answerer, scene.Answerer.Agent.SignAnswer(scene.Board, "An answer " + Unrelated(), question.PostId, forum.Now), problems),
            await AcceptedAsync("comment", scene.Answerer, scene.Answerer.Agent.Sign(PostKind.Comment, scene.Board, "A comment " + Unrelated(), null, question.PostId, forum.Now), problems),
            await AcceptedAsync("revision", scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, "Corrected " + Unrelated(), question.PostId, question.Digest, forum.Now), problems),
        };
        var onParents = await ListedAsync(scene.Board);
        foreach (var id in accepted.Where(id => id.Length > 0).Where(id => !onParents.Contains(id))) problems.Add($"the board {scene.Board} does not list the reply {id}");
        var onOther = await ListedAsync(other);
        if (onOther.Length != 0) problems.Add($"the board {other} lists {onOther.Length} post(s) although every reply was filed on {scene.Board}");

        None(problems, $"no post listed on {other}; the three replies listed on {scene.Board}");
    }

    // ---- RPB8 -----------------------------------------------------------------------------------

    /// <summary>
    /// RPB8. The order: a withheld parent is <c>parent-withheld</c>; an absent parent is <c>parent-not-found</c>; an answer under a comment is <c>parent-not-a-question</c>; a T0
    /// agent's answer on a board the grammar refuses is <c>403</c> (the tier) and its body names no board and no post; a T1 agent over a spent posting budget is the budget's
    /// <c>403</c>. Each is on another board than its parent's, so the terminal refusals are shown to come first. The first, second, third and fifth are controls on the seam (the
    /// types are today's); the fourth is red there: <c>400 board-not-canonical</c>, G56's NAM5, which this entry flips on purpose.
    /// </summary>
    [Fact]
    public async Task R8_87_RPB8_TheTerminalRefusalsComeBeforeTheBoardsAndTheTierComesBeforeEveryRelationToTheLog()
    {
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var other = "a-" + Suffix();
        var problems = new List<string>();

        // A withheld question.
        var withheld = await AskAsync(forum, scene.Answerer, scene.Board, Ct);
        await forum.WithholdAsync(withheld.PostId, Ct);
        var sentWithheld = await SendAsync(scene.Answerer, scene.Answerer.Agent.SignAnswer(other, "An answer " + Unrelated(), withheld.PostId, forum.Now));
        if (Said(sentWithheld) != "400 " + PublishedWithheldWords.ParentWithheldType()) problems.Add($"withheld parent: Expected: 400 {PublishedWithheldWords.ParentWithheldType()} Actual: {Said(sentWithheld)}");

        // An absent post.
        var invented = "01" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();
        var sentAbsent = await SendAsync(scene.Answerer, scene.Answerer.Agent.SignAnswer(other, "An answer " + Unrelated(), invented, forum.Now));
        if (Said(sentAbsent) != "400 " + PublishedParentRule.Slugs()[0]) problems.Add($"absent parent: Expected: 400 {PublishedParentRule.Slugs()[0]} Actual: {Said(sentAbsent)}");

        // An answer under a comment.
        var sentComment = await SendAsync(scene.Answerer, scene.Answerer.Agent.SignAnswer(other, "An answer " + Unrelated(), scene.Comment.PostId, forum.Now));
        if (Said(sentComment) != "400 " + PublishedParentRule.Slugs()[1]) problems.Add($"answer under a comment: Expected: 400 {PublishedParentRule.Slugs()[1]} Actual: {Said(sentComment)}");

        // A T0 agent's answer on a board the grammar refuses, and not the parent's.
        var stranger = await AcceptedAnswerScene.EnrolAsync(forum, "stranger", Ct);
        var refusedBoard = "Meta-" + Suffix();
        var sentTier = await SendAsync(stranger, stranger.Agent.SignAnswer(refusedBoard, "An answer " + Unrelated(), scene.Question.PostId, forum.Now));
        if (sentTier.Status != 403) problems.Add($"T0 answer on {refusedBoard}: Expected: 403 Actual: {Said(sentTier)}");
        else
        {
            foreach (var leak in new[] { scene.Board, refusedBoard, scene.Question.PostId, "board-not-parents", "board-not-canonical" })
                if (sentTier.Body.Contains(leak, StringComparison.Ordinal)) problems.Add($"the T0 caller's 403 mentions {leak}");
        }

        // A T1 agent whose posting budget is spent.
        var spent = await AcceptedAnswerScene.T1Async(forum, scene.Board, Ct);
        await BudgetBursts.SeedPostsAsync(forum, spent.Agent, scene.Board, BudgetBursts.PostLimit("T1"), Ct);
        var sentBudget = await SendAsync(spent, spent.Agent.SignAnswer(other, "An answer " + Unrelated(), scene.Question.PostId, forum.Now));
        if (sentBudget.Status != 403 || sentBudget.Detail?.Contains("rate-budget-exhausted", StringComparison.Ordinal) != true)
            problems.Add($"spent budget: Expected: 403 table-11/rate-budget-exhausted Actual: {Said(sentBudget)} {sentBudget.Detail}");
        else if (sentBudget.Body.Contains(other, StringComparison.Ordinal) || sentBudget.Body.Contains("board-not-parents", StringComparison.Ordinal))
            problems.Add("the budget's 403 mentions a board");

        None(problems, "parent-withheld, parent-not-found, parent-not-a-question, the tier's 403 for a T0 caller, and the budget's 403, each before any relation of a board to its parent's");
    }

    // ---- RPB9 -----------------------------------------------------------------------------------

    /// <summary>
    /// RPB9, the accepting side, which closes the register's item 83 at the door. The parent is stored (by the fixture's own append, never through the route) on a board the grammar
    /// refuses: <c>Meta</c>, and one holding a line feed. An answer, a comment and a revision on that very board are <c>201</c> and are listed on it. Predicted red:
    /// <c>400 curia/content/board-not-canonical</c>.
    /// </summary>
    [Theory]
    [InlineData("upper-case")]
    [InlineData("line-feed")]
    public async Task R8_87_RPB9_AReplyOnAStoredBoardTheGrammarRefusesIsAcceptedWhenItIsTheParents(string shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        var stem = Suffix();
        var stored = shape == "upper-case" ? $"Meta-{stem}" : "Repro" + (char)10 + $"sec-{stem}";
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "asker", Ct);
        var answerer = await AcceptedAnswerScene.T1Async(forum, $"warm-{stem}", Ct);
        var (questionId, questionDigest) = await forum.AppendStoredPostAsync(asker.Agent.SignQuestion(stored, "A body " + Unrelated(), "A title " + Unrelated(), forum.Now), Ct);
        var problems = new List<string>();

        var ids = new[]
        {
            await AcceptedAsync("answer", answerer, answerer.Agent.SignAnswer(stored, "An answer " + Unrelated(), questionId, forum.Now), problems),
            await AcceptedAsync("comment", answerer, answerer.Agent.Sign(PostKind.Comment, stored, "A comment " + Unrelated(), null, questionId, forum.Now), problems),
            await AcceptedAsync("revision", asker, asker.Agent.SignRevision(stored, "Corrected " + Unrelated(), questionId, questionDigest, forum.Now), problems),
        };

        var listed = await ListedAsync(stored);
        foreach (var id in ids.Where(id => id.Length > 0).Where(id => !listed.Contains(id))) problems.Add($"the stored board does not list the accepted reply {id}");
        None(problems, $"201 for an answer, a comment and a revision on the stored board ({shape}), all listed on it");
    }

    // ---- RPB10 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB10. The grammar is not asked of replies: the same reply on <c>Meta-x</c> under a <c>meta-x</c> parent is <c>board-not-parents</c>, naming the parent, and not
    /// <c>board-not-canonical</c>. Predicted red: the grammar's type.
    /// </summary>
    [Fact]
    public async Task R8_87_RPB10_AReplyOnABoardTheGrammarRefusesIsJudgedByItsRelationAndNotByItsSpelling()
    {
        var stem = Suffix();
        var qualifying = $"meta-{stem}";
        var refused = $"Meta-{stem}";
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "asker", Ct);
        var answerer = await AcceptedAnswerScene.T1Async(forum, qualifying, Ct);
        var question = await AskAsync(forum, asker, qualifying, Ct);
        var problems = new List<string>();

        problems.AddRange(await RefusedAsync("answer on " + refused, answerer, answerer.Agent.SignAnswer(refused, "An answer " + Unrelated(), question.PostId, forum.Now), Keys().Parent, question.PostId, qualifying));
        problems.AddRange(await RefusedAsync("comment on " + refused, answerer, answerer.Agent.Sign(PostKind.Comment, refused, "A comment " + Unrelated(), null, question.PostId, forum.Now), Keys().Parent, question.PostId, qualifying));
        problems.AddRange(await RefusedAsync("revision on " + refused, asker, asker.Agent.SignRevision(refused, "Corrected " + Unrelated(), question.PostId, question.Digest, forum.Now), Keys().Original, question.PostId, qualifying));

        None(problems, $"{Published} (and not {PublishedBoardWords.BoardType()}) for an answer, a comment and a revision on {refused} under a {qualifying} parent");
    }

    // ---- RPB11 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB11, a <b>control</b>: the grammar is still asked of the four kinds that follow no post: a question, a finding, a vote and a verification on <c>Meta-x</c> are
    /// <c>400 curia/content/board-not-canonical</c> naming the class, parsed from R8.85's words, and nothing is stored. Green on the seam.
    /// </summary>
    [Fact]
    public async Task R8_87_RPB11_TheGrammarIsStillAskedOfTheOtherKinds_Control()
    {
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var refused = "Meta-" + Suffix();
        var expected = $"{PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()}";
        var events = await PostEventsAsync();
        Assert.Equal(10, PublishedBoardWords.Classes().Length);

        (string Kind, SceneActor Who, byte[] Wire)[] rows =
        [
            ("question", scene.Asker, scene.Asker.Agent.SignQuestion(refused, "A body " + Unrelated(), "A title " + Unrelated(), forum.Now)),
            ("finding", scene.Answerer, scene.Answerer.Agent.Sign(PostKind.Finding, refused, "A finding " + Unrelated(), "A finding " + Unrelated(), null, forum.Now)),
            ("vote", scene.Answerer, scene.Answerer.Agent.SignVote(refused, scene.Answer1.Digest, forum.Now)),
            ("verification", scene.Answerer, scene.Answerer.Agent.SignVerification(refused, scene.Answer1.Digest, "reproduced", forum.Now)),
        ];
        var problems = new List<string>();
        foreach (var (kind, who, wire) in rows)
        {
            var sent = await SendAsync(who, wire);
            if (Said(sent) != expected || sent.Detail != "board: not-lower-case") problems.Add($"{kind}: Expected: {expected} board: not-lower-case Actual: {Said(sent)} {sent.Detail}");
        }

        if (await PostEventsAsync() != events) problems.Add("the log moved");
        None(problems, "the grammar's 400 for a question, a finding, a vote and a verification on a board that is not a name");
    }

    // ---- RPB12 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB12, a <b>control</b> that makes the comparison's artifact a checked fact: for one post of each of Table 9's seven kinds, the board in the stored <c>post.accepted</c>
    /// payload equals the board in the canonical form the payload stores and in the envelope that was signed (three artifacts: the bytes this test signed, the payload's member and the
    /// payload's own canonical text), on a board whose name has a character in NFC. Green on the seam.
    /// </summary>
    [Fact]
    public async Task R8_87_RPB12_ThePayloadsBoardIsTheSignedBoardForEveryKind_Control()
    {
        var board = "caf" + char.ConvertFromUtf32(0xE9) + "-" + Suffix();
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "asker", Ct);
        var answerer = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var voter = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var wise = await BudgetBursts.T2Async(forum, board, Ct);
        var problems = new List<string>();
        var posted = new List<(string Kind, byte[] Wire, string PostId)>();

        async Task<Sent> PostAsync(string kind, SceneActor who, byte[] wire)
        {
            var sent = await SendAsync(who, wire);
            if (sent.Status != 201) problems.Add($"{kind}: Expected: 201 Actual: {Said(sent)}");
            else posted.Add((kind, wire, sent.PostId!));
            return sent;
        }

        var question = await PostAsync("question", asker, asker.Agent.SignQuestion(board, "A body " + Unrelated(), "A title " + Unrelated(), forum.Now));
        var answer = await PostAsync("answer", answerer, answerer.Agent.SignAnswer(board, "An answer " + Unrelated(), question.PostId ?? "none", forum.Now));
        await PostAsync("comment", answerer, answerer.Agent.Sign(PostKind.Comment, board, "A comment " + Unrelated(), null, question.PostId ?? "none", forum.Now));
        await PostAsync("revision", asker, asker.Agent.SignRevision(board, "Corrected " + Unrelated(), question.PostId ?? "none", question.Digest, forum.Now));
        await PostAsync("finding", wise, wise.Agent.Sign(PostKind.Finding, board, "A finding " + Unrelated(), "A finding " + Unrelated(), null, forum.Now));
        await PostAsync("vote", voter, voter.Agent.SignVote(board, answer.Digest ?? "none", forum.Now));
        await PostAsync("verification", voter, voter.Agent.SignVerification(board, answer.Digest ?? "none", "reproduced", forum.Now));

        var ids = posted;
        Assert.Equal(7, ids.Select(i => i.Kind).Distinct().Count());
        foreach (var (kind, wire, postId) in ids)
        {
            await using var connection = new NpgsqlConnection(forum.ConnectionString);
            await connection.OpenAsync(Ct);
            await using var query = new NpgsqlCommand("SELECT payload->>'board', payload->>'canonical' FROM events WHERE event_type = 'post.accepted' AND payload->>'post_id' = @id", connection);
            query.Parameters.AddWithValue("id", postId);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            if (!await reader.ReadAsync(Ct)) { problems.Add($"{kind}: no post.accepted for {postId}"); continue; }
            var (payloadBoard, canonical) = (reader.GetString(0), reader.GetString(1));
            var canonicalBoard = JsonDocument.Parse(canonical).RootElement.GetProperty("board").GetString();
            if (payloadBoard != board) problems.Add($"{kind}: the payload's board is \"{payloadBoard}\", expected \"{board}\"");
            if (canonicalBoard != board) problems.Add($"{kind}: the payload's canonical form holds board \"{canonicalBoard}\"");
            if (wire.Length > 0 && JsonDocument.Parse(wire).RootElement.GetProperty("envelope").GetProperty("board").GetString() != board) problems.Add($"{kind}: the signed envelope's board differs");
        }

        None(problems, "the board of the payload, of the stored canonical form and of the signed envelope equal, for all seven kinds");
    }

    // ---- RPB13 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB13. A revision is compared with the post its <c>prev</c> names, and its <c>parent</c> is not read: an invented <c>parent</c> with the right board is <c>201</c> (the
    /// accepting row, written first); a real <c>parent</c> on the revision's board with a <c>prev</c> of another board's post is refused naming <c>original=</c> that post, and
    /// with that post's board is <c>201</c>. Predicted red: the second row is <c>201</c>.
    /// </summary>
    [Fact]
    public async Task R8_87_RPB13_ARevisionIsComparedWithItsPrevAndItsParentIsNotRead()
    {
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var author = scene.Answerer;
        var problems = new List<string>();

        var first = await AskAsync(forum, author, scene.Board, Ct);
        var invented = "01" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();
        await AcceptedAsync("revision with an invented parent", author, author.Agent.SignRevision(scene.Board, "Corrected " + Unrelated(), invented, first.Digest, forum.Now), problems);

        var onBoardA = await AskAsync(forum, author, scene.Board, Ct);
        var elsewhere = "z-" + Suffix();
        var onBoardB = await AskAsync(forum, author, elsewhere, Ct);
        problems.AddRange(await RefusedAsync(
            "revision on A, parent A, prev a post on B", author, author.Agent.SignRevision(scene.Board, "Corrected " + Unrelated(), onBoardA.PostId, onBoardB.Digest, forum.Now),
            Keys().Original, onBoardB.PostId, scene.Board, elsewhere));
        await AcceptedAsync("revision on B, parent A, prev a post on B", author, author.Agent.SignRevision(elsewhere, "Corrected " + Unrelated(), onBoardA.PostId, onBoardB.Digest, forum.Now), problems);

        None(problems, "201 for an invented parent with the right board; original=<the prev's post> for a prev on another board; 201 once the board is the prev's");
    }

    // ---- RPB14 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB14. A revision of an original the Forum does not serve is still compared: with the original's board it is <c>201</c> (R8.72's last paragraph); on another board it is refused
    /// naming <c>original=</c>. Predicted red: the refused row is <c>201</c>.
    /// </summary>
    [Fact]
    public async Task R8_87_RPB14_ARevisionOfAWithheldOriginalIsStillCompared()
    {
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var author = scene.Answerer;
        var other = "a-" + Suffix();
        var original = await AskAsync(forum, author, scene.Board, Ct);
        await forum.WithholdAsync(original.PostId, Ct);

        var problems = await RefusedAsync(
            "revision of a withheld original on " + other, author, author.Agent.SignRevision(other, "Corrected " + Unrelated(), original.PostId, original.Digest, forum.Now),
            Keys().Original, original.PostId, scene.Board, other);
        await AcceptedAsync("revision of a withheld original", author, author.Agent.SignRevision(scene.Board, "Corrected " + Unrelated(), original.PostId, original.Digest, forum.Now), problems);

        None(problems, "original=<the withheld post> on another board, and 201 on its own");
    }

    // ---- RPB15 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB15, a <b>control</b>, and a correction of the plan (Task 1 found it): a vote and a verification on a board other than their target's are not <c>201</c> and never were. R8.58's
    /// signal rule (<c>VerificationPolicy.Refusal</c>, "a signal on another board's post would evade that board's policy") refuses them <c>422 curia/verification/board-mismatch</c>,
    /// and that refusal is not R8.87's: this entry compares an answer, a comment and a revision and extends nothing to a signal, whose own rule stays as it is. The probe holds the
    /// existing refusal (so a build that moved the signals under R8.87, or dropped the rule, turns it red) and holds that their board on the target's own board is <c>201</c>.
    /// Green on the seam. The plan and G61 (decision 9, recorded non-property (b)) said these were accepted; they are not.
    /// </summary>
    [Fact]
    public async Task R8_87_RPB15_ASignalIsStillRefusedByItsOwnRuleOnAnotherBoardThanItsTargets_Control()
    {
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var voter = await AcceptedAnswerScene.T1Async(forum, scene.Board, Ct);
        var other = "z-" + Suffix();
        var problems = new List<string>();
        var events = await PostEventsAsync();

        var vote = await SendAsync(voter, voter.Agent.SignVote(other, scene.Answer1.Digest, forum.Now));
        var verification = await SendAsync(voter, voter.Agent.SignVerification(other, scene.Answer1.Digest, "reproduced", forum.Now));
        foreach (var (kind, sent) in new[] { ("vote", vote), ("verification", verification) })
            if (Said(sent) != "422 curia/verification/board-mismatch") problems.Add($"{kind} on {other}: Expected: 422 curia/verification/board-mismatch Actual: {Said(sent)}");
        if (await PostEventsAsync() != events) problems.Add("the log moved");

        await AcceptedAsync("vote on the target's board", voter, voter.Agent.SignVote(scene.Board, scene.Answer1.Digest, forum.Now), problems);
        await AcceptedAsync("verification on the target's board", voter, voter.Agent.SignVerification(scene.Board, scene.Answer1.Digest, "reproduced", forum.Now), problems);

        None(problems, "422 board-mismatch (the signal rule's own) for a vote and a verification on another board than their target's, and 201 on the target's board");
    }

    // ---- RPB16 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB16. The comparison is ordinal: a stored <c>Meta-x</c> parent and a reply on <c>meta-x</c> are refused, and a <c>meta-x</c> parent and a reply on <c>Meta-x</c> are
    /// refused, for an answer and a comment. Predicted red: the first direction is <c>201</c>, the second is the grammar's refusal and not the published one.
    /// </summary>
    [Fact]
    public async Task R8_87_RPB16_TheComparisonDoesNotFoldCase()
    {
        var stem = Suffix();
        var upper = $"Meta-{stem}";
        var lower = $"meta-{stem}";
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "asker", Ct);
        var answerer = await AcceptedAnswerScene.T1Async(forum, $"warm-{stem}", Ct);
        var (storedUpper, _) = await forum.AppendStoredPostAsync(asker.Agent.SignQuestion(upper, "A body " + Unrelated(), "A title " + Unrelated(), forum.Now), Ct);
        var routeLower = await AskAsync(forum, asker, lower, Ct);
        var problems = new List<string>();

        problems.AddRange(await RefusedAsync("answer on the lower-case board under an upper-case parent", answerer, answerer.Agent.SignAnswer(lower, "An answer " + Unrelated(), storedUpper, forum.Now), Keys().Parent, storedUpper));
        problems.AddRange(await RefusedAsync("comment on the lower-case board under an upper-case parent", answerer, answerer.Agent.Sign(PostKind.Comment, lower, "A comment " + Unrelated(), null, storedUpper, forum.Now), Keys().Parent, storedUpper));
        problems.AddRange(await RefusedAsync("answer on the upper-case board under a lower-case parent", answerer, answerer.Agent.SignAnswer(upper, "An answer " + Unrelated(), routeLower.PostId, forum.Now), Keys().Parent, routeLower.PostId));
        problems.AddRange(await RefusedAsync("comment on the upper-case board under a lower-case parent", answerer, answerer.Agent.Sign(PostKind.Comment, upper, "A comment " + Unrelated(), null, routeLower.PostId, forum.Now), Keys().Parent, routeLower.PostId));

        None(problems, $"{Published} for both directions of a case difference, for an answer and a comment");
    }

    // ---- RPB17 ----------------------------------------------------------------------------------

    /// <summary>The members of <c>SubmissionGuard</c> as the compiled assembly declares them at <c>d134685c</c> (the guard has no leg for a board, R8.87 decision 5).</summary>
    private const string GuardSurface =
        "static Curia.Application.Ingest.SubmissionGuard.For(Curia.Application.Ingest.LogFacts, Curia.Application.Ingest.VerifiedSubmission): Curia.Application.Ports.AppendGuard\n"
        + "static Curia.Application.Ingest.SubmissionGuard.ForRevision(Curia.Application.Ingest.LogFacts, System.String, System.String): Curia.Application.Ports.AppendGuard\n"
        + "static Curia.Application.Ingest.SubmissionGuard.ForVote(Curia.Application.Ingest.LogFacts, System.String, System.String, System.String): Curia.Application.Ports.AppendGuard\n"
        + "static Curia.Application.Ingest.SubmissionGuard.RecordedIn(System.Collections.Generic.IReadOnlyList<Curia.Domain.AppendedEvent>, System.String): Curia.Domain.Primitives.Error\n"
        + "static Curia.Application.Ingest.SubmissionGuard.Verdict(Curia.Domain.Primitives.Error): Curia.Application.Ports.GuardVerdict";

    /// <summary>
    /// RPB17, a <b>control</b>: the append's guard is unchanged. The members <c>SubmissionGuard</c> declares, read from the compiled assembly, equal the list written here; a board leg
    /// would add one (and would be wrong: a stored post's board never changes, so nothing a late commit does invalidates the comparison). Green on the seam.
    /// </summary>
    [Fact]
    public void R8_87_RPB17_TheGuardIsUnchanged_Control()
    {
        var type = typeof(Curia.Application.Ingest.SubmissionGuard);
        var members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.Name.Contains('<', StringComparison.Ordinal) && (m is not ConstructorInfo || ((ConstructorInfo)m).IsPublic))
            .Select(Describe)
            .Order(StringComparer.Ordinal);

        var said = string.Join('\n', members);
        if (!string.Equals(GuardSurface, said, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {GuardSurface}{Environment.NewLine}Actual:   {said}");

        static string Describe(MemberInfo member) => member switch
        {
            MethodInfo m => $"{(m.IsStatic ? "static " : string.Empty)}{m.DeclaringType!.FullName}.{m.Name}({string.Join(", ", m.GetParameters().Select(p => Named(p.ParameterType)))}): {Named(m.ReturnType)}",
            _ => $"{member.MemberType} {member.DeclaringType!.FullName}.{member.Name}",
        };

        static string? Named(Type t) =>
            t.IsGenericType ? $"{t.GetGenericTypeDefinition().FullName!.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(a => Named(a)))}>" : t.FullName;
    }

    // ---- RPB18 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB18. The published words: the refusals of an answer, a comment and a revision have the status, type, title and detail key parsed from R8.87 words, and nothing else (no extra
    /// member, no board). Predicted red: <c>201</c>.
    /// </summary>
    [Fact]
    public async Task R8_87_RPB18_TheRefusalIsInTheWordsTheErrataPublishes()
    {
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var other = "z-" + Suffix();
        var question = await AskAsync(forum, scene.Answerer, scene.Board, Ct);
        var problems = new List<string>();

        var sends = new (string Kind, string Key, SceneActor Who, byte[] Wire)[]
        {
            ("answer", Keys().Parent, scene.Answerer, scene.Answerer.Agent.SignAnswer(other, "An answer " + Unrelated(), question.PostId, forum.Now)),
            ("comment", Keys().Parent, scene.Answerer, scene.Answerer.Agent.Sign(PostKind.Comment, other, "A comment " + Unrelated(), null, question.PostId, forum.Now)),
            ("revision", Keys().Original, scene.Answerer, scene.Answerer.Agent.SignRevision(other, "Corrected " + Unrelated(), question.PostId, question.Digest, forum.Now)),
        };
        foreach (var (kind, key, who, wire) in sends)
        {
            var sent = await SendAsync(who, wire);
            if (Said(sent) != Published) { problems.Add($"{kind}: Expected: {Published} Actual: {Said(sent)}"); continue; }
            using var body = JsonDocument.Parse(sent.Body);
            var members = body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
            if (sent.Title != PublishedReplyBoardWords.Title()) problems.Add($"{kind}: title \"{sent.Title}\"");
            if (sent.Detail != key + question.PostId) problems.Add($"{kind}: detail \"{sent.Detail}\", expected \"{key}{question.PostId}\"");
            if (!members.SequenceEqual(["detail", "title", "type"], StringComparer.Ordinal)) problems.Add($"{kind}: the problem holds the members {string.Join(",", members)}");
        }

        None(problems, $"{Published}, the published title and detail key, and a problem document of type, title and detail alone");
    }

    // ---- RPB19 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB19. A refused reply appends no event and spends no budget under a 16-wide burst of mismatched replies by one agent (R7.27's gate in front): every request answers
    /// <c>400</c> (the published refusal) or <c>409 curia/budget/agent-in-flight</c>, none is stored, and the agent's posting budget is as it was. Predicted red: the replies are
    /// stored (<c>201</c>).
    /// </summary>
    [Fact]
    public async Task R8_87_RPB19_ABurstOfMismatchedRepliesStoresNothingAndSpendsNothing()
    {
        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var other = "a-" + Suffix();
        var actor = scene.Answerer;
        var token = await actor.Dpop.GetTokenAsync(forum.Client, "http://localhost/oauth/token", forum.Now, Ct);
        var events = await PostEventsAsync();
        var used = await UsedAsync(actor);

        var sends = Enumerable.Range(0, 16).Select<int, Func<Task<Sent>>>(i => () => SendAsync(
            actor,
            i % 2 == 0
                ? actor.Agent.SignAnswer(other, $"An answer {i} " + Unrelated(), scene.Question.PostId, forum.Now)
                : actor.Agent.Sign(PostKind.Comment, other, $"A comment {i} " + Unrelated(), null, scene.Question.PostId, forum.Now),
            token));
        var answers = await TogetherAsync(sends);

        var problems = new List<string>();
        var refused = answers.Count(a => Said(a) == Published);
        var queued = answers.Count(a => Said(a) == "409 curia/budget/agent-in-flight");
        if (refused == 0) problems.Add("NOT REACHED: no request in the burst was answered with the published refusal");
        foreach (var a in answers.Where(a => Said(a) != Published && Said(a) != "409 curia/budget/agent-in-flight"))
            problems.Add($"a request in the burst was answered {Said(a)}");
        if (refused + queued != 16 && problems.Count == 0) problems.Add($"{refused} refused and {queued} in flight of 16");
        var stored = await PostEventsAsync() - events;
        if (stored != 0) problems.Add($"{stored} post(s) were stored");
        var usedAfter = await UsedAsync(actor);
        if (usedAfter != used) problems.Add($"the posting budget moved from {used} to {usedAfter}");

        None(problems, "every request refused (400 board-not-parents) or told 409 agent-in-flight, nothing stored, nothing spent");
    }
}
