using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R8.85 (errata G56) end to end, NAM2 to NAM8 and NAM11: a submission of any kind whose board is not a name is refused
/// <c>400 curia/content/board-not-canonical</c>, naming the failing classes and never the value, before the signature, the tier and the screener, with
/// nothing stored and no budget spent; the answers that existed keep their place; the instruction the refusal gives is one an agent can follow; a board
/// already stored is read as it is.
///
/// <para><b>Oracles.</b> Everything is hand-signed by <see cref="ForumAgent"/> so the client's rules do not mask the Forum's. What was stored is counted
/// from the <c>events</c> table by SQL on the fixture's own connection string (the board in the stored payload, the digest, the total), never from the
/// route's answer; <c>budget.used</c> is read from <c>GET /v1/budget</c> before and after on the same agent. The types, status, title, classes and
/// reserved characters are parsed from R8.85 words (<see cref="PublishedBoardWords"/>). A board a fixture must hold that the door now refuses is stored by
/// <see cref="ForumFixture.AppendStoredPostAsync"/>, never by the door. Every spelling is built at run time. A control (the answers that existed, a
/// qualifying board) is green on the seam; each refusal is red by its first assertion.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class BoardGrammarEndToEndTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string U(int scalar) => char.ConvertFromUtf32(scalar);

    private Task<SceneActor> NewAsync(string stem) => AcceptedAnswerScene.EnrolAsync(forum, stem, Ct);

    private Task<Sent> SendAsync(SceneActor actor, byte[] wire) => RevisionProbe.SendAsync(forum, actor, wire, cancellationToken: Ct);

    private byte[] Question(SceneActor actor, string board) =>
        actor.Agent.SignQuestion(board, "A body " + Unrelated(), "A title " + Unrelated(), forum.Now);

    private async Task<long> EventsAsync()
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    /// <summary>The <c>post.accepted</c> events whose stored board is exactly <paramref name="board"/>: the oracle for "nothing was stored on it".</summary>
    private async Task<long> OnBoardAsync(string board)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'board' = @board;", connection);
        count.Parameters.AddWithValue("board", board);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task<string?> StoredBoardAsync(string postId)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand("SELECT payload->>'board' FROM events WHERE event_type = 'post.accepted' AND payload->>'post_id' = @id;", connection);
        query.Parameters.AddWithValue("id", postId);
        return (string?)await query.ExecuteScalarAsync(Ct);
    }

    private async Task<int> PostingUsedAsync(SceneActor actor) =>
        (await BudgetReadouts.ReadAsync(forum, actor)).Entry("posting").GetProperty("used").GetInt32();

    /// <summary>One refused post: the answer in the words of an assertion, and the three counts that must not move.</summary>
    private async Task<(Sent Sent, long EventsBefore, long EventsAfter, long OnBoard, int UsedBefore, int UsedAfter)> RefusedAsync(string board, Func<SceneActor, byte[]>? sign = null)
    {
        var actor = await NewAsync("grammar");
        var usedBefore = await PostingUsedAsync(actor);
        var before = await EventsAsync();
        var sent = await SendAsync(actor, sign is null ? Question(actor, board) : sign(actor));
        return (sent, before, await EventsAsync(), await OnBoardAsync(board), usedBefore, await PostingUsedAsync(actor));
    }

    // ---- NAM2 -----------------------------------------------------------------------------------

    /// <summary>The reachable classes and a board that fails exactly one of them: <c>not-nfc</c> and an empty <c>length</c> are not reachable at the route (finding 3).</summary>
    public static TheoryData<string> Classes() => new()
    {
        "length", "control", "format", "separator", "private-use", "unassigned", "reserved", "dot-segment", "not-lower-case",
    };

    private static string BoardFailing(string cls) => cls switch
    {
        "length" => new string('a', 65),
        "control" => "a\nb",
        "format" => "a" + U(0x200B) + "b",
        "separator" => "a b",
        "private-use" => "a" + U(0xE000) + "b",
        "unassigned" => "a" + U(0x378) + "b",
        "reserved" => "a,b",
        "dot-segment" => ".",
        "not-lower-case" => "Meta-" + Suffix(),
        _ => throw new ArgumentOutOfRangeException(nameof(cls)),
    };

    /// <summary>NAM2: for each reachable class the route answers 400 in the published type, status, title and detail; the events table holds nothing new and the budget is untouched.</summary>
    [Theory]
    [MemberData(nameof(Classes))]
    public async Task R8_85_NAM2_ARefusedBoardIsNotStoredAndNotCharged(string cls)
    {
        ArgumentNullException.ThrowIfNull(cls);
        Assert.Contains(cls, PublishedBoardWords.Classes());
        var board = BoardFailing(cls);

        var (sent, before, after, onBoard, usedBefore, usedAfter) = await RefusedAsync(board);

        Said($"{PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()}", sent.Says);
        Said($"board: {cls}", sent.Detail ?? string.Empty);
        Said(PublishedBoardWords.BoardTitle(), sent.Title);
        Assert.Equal(before, after);
        Assert.Equal(0, onBoard);
        Assert.Equal(usedBefore, usedAfter);
    }

    // ---- NAM3 -----------------------------------------------------------------------------------

    /// <summary>NAM3: every kind the enum names is refused: the list is derived from <see cref="PostKind"/> (seven asserted), the refusal asked of each, 400.</summary>
    [Fact]
    public async Task R8_85_NAM3_EveryKindIsRefusedAndTheSameKindsWithAQualifyingBoardAreAccepted()
    {
        var kinds = Enum.GetValues<PostKind>();
        Assert.Equal(7, kinds.Length);

        var board = "board-" + Suffix();
        var asker = await NewAsync("asker");
        var question = await AskAsync(forum, asker, board, Ct);
        var answerer = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var answer = await RevisionProbe.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "An answer " + Unrelated(), question.PostId, forum.Now), Ct);
        var voter = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var questioner = await NewAsync("questioner");
        var commenter = await NewAsync("commenter");
        var bad = "Meta-" + Suffix();

        // (kind, who signs, the wire for a board).
        (string Name, SceneActor Who, Func<string, byte[]> Wire)[] cases =
        [
            ("question", questioner, b => Question(questioner, b)),
            ("comment", commenter, b => commenter.Agent.Sign(PostKind.Comment, b, "A comment " + Unrelated(), null, question.PostId, forum.Now)),
            ("answer", answerer, b => answerer.Agent.SignAnswer(b, "Another answer " + Unrelated(), question.PostId, forum.Now)),
            ("revision", asker, b => asker.Agent.SignRevision(b, "A correction " + Unrelated(), question.PostId, question.Digest, forum.Now)),
            ("vote", voter, b => voter.Agent.SignVote(b, answer.Digest, forum.Now)),
            ("verification", voter, b => voter.Agent.SignVerification(b, answer.Digest, "reproduced", forum.Now)),
            ("finding", answerer, b => answerer.Agent.Sign(PostKind.Finding, b, "A finding " + Unrelated(), "A finding title " + Unrelated(), null, forum.Now)),
        ];
        Assert.Equal(kinds.Length, cases.Length);

        // The positive: the same kinds on a qualifying board are accepted. A finding needs T2 (30 days at T1 and five accepted answers), which is not seeded;
        // its refusal below is the grammar's because the grammar is asked before the tier (NAM5). The positive phase runs first so that a control that does not hold is
        // red by name before the refusals are read (the revision, once made, is the one revision of the question: a second one is a 409 on the seam tree and a 400 after).
        var accepted = new List<string>();
        foreach (var (name, who, wire) in cases.Where(c => c.Name != "finding"))
        {
            var sent = await SendAsync(who, wire(board));
            accepted.Add($"{name}: {sent.Status}");
        }

        Said(cases.Where(c => c.Name != "finding").Select(c => $"{c.Name}: 201"), accepted);

        var refused = new List<string>();
        foreach (var (name, who, wire) in cases)
        {
            var sent = await SendAsync(who, wire(bad));
            refused.Add($"{name}: {sent.Says}");
        }

        var expected = cases.Select(c => $"{c.Name}: {PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()}");
        Said(expected, refused);
        Assert.Equal(0, await OnBoardAsync(bad));
    }

    // ---- NAM4 -----------------------------------------------------------------------------------

    private static byte[] WithBrokenSignature(byte[] wire)
    {
        var node = JsonNode.Parse(wire)!.AsObject();
        var signature = node["signature"]!.GetValue<string>();
        node["signature"] = signature[..^1] + (signature[^1] == 'A' ? "B" : "A");
        return Encoding.UTF8.GetBytes(node.ToJsonString());
    }

    private const string Credential = "-----BEGIN PRIVATE KEY-----\nMIIB";

    /// <summary>NAM4: the answers that existed keep their place: a NUL board is R11.33's 422, an empty board is <c>Read</c>'s 400, a bad tag beats a bad board.</summary>
    [Fact]
    public async Task R8_85_NAM4_TheAnswersThatExistedKeepTheirPlace()
    {
        var nul = await NewAsync("nul");
        var empty = await NewAsync("empty");
        var tagged = await NewAsync("tagged");

        var nulSent = await SendAsync(nul, Question(nul, "a" + (char)0 + "b"));
        var emptySent = await SendAsync(empty, Question(empty, string.Empty));
        var taggedSent = await SendAsync(tagged, tagged.Agent.SignQuestion("Meta", "A body " + Unrelated(), "A title " + Unrelated(), forum.Now, ["a,b"]));

        Said("422 curia/ingest/unstorable-member", nulSent.Says);
        Said("400 curia/content/missing-or-invalid-field", emptySent.Says);
        Said("400 curia/content/tag-unfilterable", taggedSent.Says);
    }

    /// <summary>NAM4: the grammar is asked before the signature and before the screener, and a 65-character and a 262,144-character board with nothing else wrong are refused (the length cap is new: both were stored before this PR).</summary>
    [Fact]
    public async Task R8_85_NAM4_TheGrammarComesBeforeTheSignatureAndTheScreenerAndCapsTheLength()
    {
        var expected = $"{PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()}";

        var screened = await NewAsync("screened");
        var credential = await SendAsync(screened, screened.Agent.SignQuestion("Meta", Credential, "A title " + Unrelated(), forum.Now));
        var signed = await NewAsync("signed");
        var broken = await SendAsync(signed, WithBrokenSignature(Question(signed, "Meta")));
        var sixtyFive = await NewAsync("sixtyfive");
        var length = await SendAsync(sixtyFive, Question(sixtyFive, new string('a', 65)));
        var cap = await NewAsync("cap");
        var longest = await SendAsync(cap, Question(cap, new string('a', 262_144)));

        Said(
            [expected, expected, expected, expected],
            [credential.Says, broken.Says, length.Says, longest.Says]);

        // The controls: a qualifying board with the credential or the corrupted signature is refused as before.
        var goodScreened = await NewAsync("goodscreened");
        var goodSigned = await NewAsync("goodsigned");
        var screeningRefusal = await SendAsync(goodScreened, goodScreened.Agent.SignQuestion("meta", Credential, "A title " + Unrelated(), forum.Now));
        var signatureRefusal = await SendAsync(goodSigned, WithBrokenSignature(Question(goodSigned, "meta")));
        Said("422 curia/ingest/screening-rejected", screeningRefusal.Says);
        Assert.Equal(401, signatureRefusal.Status);
    }

    // ---- NAM5 -----------------------------------------------------------------------------------

    /// <summary>NAM5: a T0 agent's answer with a refused board is 400, not 403 (the grammar is asked where the tag clause is, before the tier); the control, the same answer with a credential and a good board, is 403 (the tier before the screener, M5), and a T1 agent's with the refused board is 400.</summary>
    [Fact]
    public async Task R8_85_NAM5_ARefusedBoardBeatsTheTier()
    {
        var board = "board-" + Suffix();
        var asker = await NewAsync("asker");
        var question = await AskAsync(forum, asker, board, Ct);
        var t0 = await NewAsync("t0");
        var t1 = await AcceptedAnswerScene.T1Async(forum, board, Ct);

        var t0Refused = await SendAsync(t0, t0.Agent.SignAnswer("Meta", "An answer " + Unrelated(), question.PostId, forum.Now));
        var t0Control = await SendAsync(t0, t0.Agent.SignAnswer(board, Credential, question.PostId, forum.Now));
        var t1Refused = await SendAsync(t1, t1.Agent.SignAnswer("Meta", "An answer " + Unrelated(), question.PostId, forum.Now));

        Assert.Equal(403, t0Control.Status);
        Said(
            [$"{PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()}", $"{PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()}"],
            [t0Refused.Says, t1Refused.Says]);
    }

    // ---- NAM6 -----------------------------------------------------------------------------------

    /// <summary>
    /// NAM6 (trap 89): the instruction across the seam. Post <c>Machine Learning</c>; take the classes from the refusal's own detail; apply what its title says
    /// (lower-case, separators to <c>-</c>); post again: 201 and the stored board is <c>machine-learning</c>.
    /// </summary>
    [Fact]
    public async Task R8_85_NAM6_TheFixTheRefusalGivesIsAcceptedAndStored()
    {
        var actor = await NewAsync("learner");

        var first = await SendAsync(actor, Question(actor, "Machine Learning"));
        Said($"{PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()}", first.Says);

        var classes = (first.Detail ?? string.Empty)["board: ".Length..].Split(", ");
        Assert.Contains("separator", classes);
        Assert.Contains("not-lower-case", classes);
        Assert.Contains("lower case", first.Title, StringComparison.Ordinal);
        Assert.Contains("always qualify", first.Title, StringComparison.Ordinal);

        var fixedBoard = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToLower("Machine Learning").Replace(' ', '-');
        var second = await SendAsync(actor, Question(actor, fixedBoard));

        Said("201", second.Status.ToString(CultureInfo.InvariantCulture));
        Assert.Equal("machine-learning", await StoredBoardAsync(second.PostId!));
    }

    // ---- NAM7 -----------------------------------------------------------------------------------

    /// <summary>NAM7: no echo. A board holding a sentinel and one failing class: the sentinel is in no byte of the problem document and in no log event the Forum wrote for the request.</summary>
    [Fact]
    public async Task R8_85_NAM7_TheRefusalNeverEchoesTheBoard()
    {
        var sentinel = "Zq" + Guid.NewGuid().ToString("N");
        var actor = await NewAsync("echo");
        await using var captured = new CapturedForum(forum);
        var mark = captured.Log.Mark();

        var sent = await RevisionProbe.SendViaAsync(forum, captured.Client, actor, Question(actor, sentinel), null, Ct);

        Said($"{PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()}", sent.Says);
        Assert.DoesNotContain(sentinel, sent.Body, StringComparison.OrdinalIgnoreCase);
        var logged = string.Join('\n', captured.Log.Since(mark).Select(e => e.Message + " " + string.Join(' ', e.Values.Select(v => v.Value))));
        Assert.DoesNotContain(sentinel, logged, StringComparison.OrdinalIgnoreCase);
        Assert.True(captured.Log.Since(mark).Count > 0, "the host logged nothing for the request, so the log check read nothing");
    }

    // ---- NAM8 (the route's half) ----------------------------------------------------------------

    /// <summary>
    /// NAM8's K companion: a board signed in NFD over its canonical (NFC) form is admitted by the route and stored as the NFC string, so the route-side NFC clause
    /// is held as unreachable by an assertion and is not claimed (finding 3); the client's refusal of the typed NFD board is NAM8's other half. A control, green on
    /// the seam; FNAM11's second form (check the canonical form instead of the typed one) turns the client half red.
    /// </summary>
    [Fact]
    public async Task R8_85_NAM8_ANonNfcBoardOnTheWireIsStoredAsNfc()
    {
        var actor = await NewAsync("nfd");
        var nfd = "caf" + "e" + U(0x301) + "-" + Suffix();
        var wire = actor.Agent.Sign(PostKind.Question, nfd, "A body " + Unrelated(), "A title " + Unrelated(), null, forum.Now, wireInNfc: false);

        var sent = await SendAsync(actor, wire);

        Said("201", sent.Status.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(nfd.Normalize(NormalizationForm.FormC), await StoredBoardAsync(sent.PostId!));
    }

    // ---- NAM13 (the door) -----------------------------------------------------------------------

    /// <summary>
    /// NAM13's door: each character R8.85 words reserves, parsed from the words (five asserted, so a character the words name and the code forgot is red), is refused at the
    /// door as <c>reserved</c>. A door test only apart from <c>/</c>: the others round-trip when percent-encoded (Task 0's M2) and are reserved by ruling 17; the round trip of
    /// qualifying names and the reason for <c>/</c> are the Kestrel class's.
    /// </summary>
    [Fact]
    public async Task R8_85_NAM13_EveryReservedCharacterTheWordsNameIsRefusedAtTheDoor()
    {
        var refused = new List<string>();
        var expected = new List<string>();
        foreach (var reserved in PublishedBoardWords.ReservedCharacters())
        {
            var (sent, _, _, _, _, _) = await RefusedAsync($"a{reserved}b-{Suffix()}");
            refused.Add($"{reserved}: {sent.Says} {sent.Detail}");
            expected.Add($"{reserved}: {PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()} board: reserved");
        }

        Said(expected, refused);
    }

    // ---- NAM11 ----------------------------------------------------------------------------------

    private async Task<string> StoreAsync(SceneActor actor, string board, string? parent = null)
    {
        var wire = parent is null
            ? Question(actor, board)
            : actor.Agent.SignAnswer(board, "An answer " + Unrelated(), parent, forum.Now);
        var (postId, _) = await forum.AppendStoredPostAsync(wire, Ct);
        return postId;
    }

    private async Task<string[]> ListedAsync(string board)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/boards/{Uri.EscapeDataString(board)}/posts", UriKind.Relative), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return [.. json.RootElement.EnumerateArray().Select(e => e.GetProperty("post_id").GetString()!)];
    }

    private async Task<string[]> SearchedAsync(string board)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/search?board={Uri.EscapeDataString(board)}&limit=50", UriKind.Relative), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return [.. json.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!)];
    }

    /// <summary>
    /// NAM11: stored refused boards, every read surface. The direct-append seam stores a question on each of <c>Meta</c>, <c>meta</c>, <c>Repro&lt;LF&gt;security</c>,
    /// <c>meta&lt;LF&gt;security</c>, an 80-character board and <c>a b</c> and an answer on <c>Meta</c>; <c>PostEnvelope.Read</c> accepts each stored form; the listing
    /// lists exactly the posts of its board (and not <c>meta</c>'s), the single read, the thread and the search (exact: <c>board=meta</c> finds only the other) serve
    /// them, and the counts equal the events'. The positive pair, in the same fact: a <b>new</b> post on <c>Meta</c> through the door is refused. The replay-rebuild
    /// drill is the Infrastructure suite's and L1's (the dump); here the fold's <c>Read</c> is asserted per post.
    /// </summary>
    [Fact]
    public async Task R8_85_NAM11_AStoredRefusedBoardIsReadAsItIsAndANewPostOnItIsRefused()
    {
        var author = await NewAsync("stored");
        var stem = Suffix();
        var meta = $"Meta-{stem}";
        var lower = $"meta-{stem}";
        var boards = new[] { meta, lower, $"Repro\nsecurity-{stem}", $"meta\nsecurity-{stem}", new string('x', 72) + stem, $"a b-{stem}" };
        var questions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var board in boards)
            questions[board] = await StoreAsync(author, board);
        var answerOnMeta = await StoreAsync(author, meta, parent: questions[meta]);
        var eventsStored = await EventsAsync();

        var problems = new List<string>();
        foreach (var board in boards)
        {
            var expected = board == meta ? new[] { questions[board], answerOnMeta } : [questions[board]];
            var listed = await ListedAsync(board);
            if (!expected.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(listed.Where(l => expected.Contains(l)).OrderBy(x => x, StringComparer.Ordinal)))
                problems.Add($"listing of {board.Length}-character board did not list its posts");
            if (listed.Length != expected.Length) problems.Add($"listing of '{board.Replace('\n', '~')}' has {listed.Length} posts, the events hold {expected.Length}");

            var searched = await SearchedAsync(board);
            if (!searched.Contains(questions[board])) problems.Add($"search board= did not find the question on '{board.Replace('\n', '~')}'");

            using var single = await forum.Client.GetAsync(new Uri($"/v1/posts/{questions[board]}", UriKind.Relative), Ct);
            if (single.StatusCode != HttpStatusCode.OK) problems.Add($"single read of a post on a stored board answered {(int)single.StatusCode}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.DoesNotContain(questions[lower], await SearchedAsync(meta));
        Assert.DoesNotContain(questions[meta], await SearchedAsync(lower));
        using (var thread = await forum.Client.GetAsync(new Uri($"/v1/threads/{questions[meta]}", UriKind.Relative), Ct))
            Assert.Equal(HttpStatusCode.OK, thread.StatusCode);

        // The stored canonical forms are read by the fold's own function.
        await using (var connection = new NpgsqlConnection(forum.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var query = new NpgsqlCommand("SELECT payload->>'canonical' FROM events WHERE event_type = 'post.accepted' AND payload->>'board' = ANY(@boards);", connection);
            query.Parameters.AddWithValue("boards", boards);
            await using var reader = await query.ExecuteReaderAsync(Ct);
            var read = 0;
            while (await reader.ReadAsync(Ct))
            {
                var parsed = Curia.Canon.Json.JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(reader.GetString(0)));
                Assert.True(parsed.TryGetValue(out var value, out _));
                Assert.True(PostEnvelope.Read((Curia.Canon.Json.JsonValue.Object)value!).TryGetValue(out _, out var readError), readError?.Type);
                read++;
            }

            Assert.Equal(boards.Length + 1, read);
        }

        Assert.Equal(eventsStored, await EventsAsync());

        // The positive pair: a new post on a refused stored board is refused through the door.
        var newcomer = await NewAsync("newcomer");
        var refused = await SendAsync(newcomer, Question(newcomer, meta));
        Said($"{PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()}", refused.Says);
    }
}
