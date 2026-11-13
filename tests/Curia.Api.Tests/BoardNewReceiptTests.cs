using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Curia.Api.Tests.NameProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R8.88 (errata G62), NMN15 to NMN20, on the production Forum over a real Postgres: the <c>201</c> of a question and of a finding carries <c>board_new</c>, true when no
/// post of a discussion kind that moderation serves was on its board in the log the act was decided on; no other kind carries it; it adds no read, is carried by the confirm
/// path's <c>201</c> and is stored nowhere.
///
/// <para><b>Oracles.</b> The expected value is what the test did: the first question on a board it made is the first, the second is not, a board whose only post it withheld has
/// none served. The number of reads is a counting <see cref="IEventReader"/> wrapped round the host's own; the stored event is read from the table. Each accepting row (the second
/// question on a board, <c>false</c>) comes first and is green on the seam, so the red is the rule's.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class BoardNewReceiptTests(ConfirmFixture forum) : IClassFixture<ConfirmFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The members of the receipt of a post as an array of names, and the value of <c>board_new</c> as the member says it: <c>true</c>, <c>false</c>, or <c>(absent)</c> / the raw text.</summary>
    private static string BoardNewOf(Sent sent)
    {
        if (sent.Status != 201) return $"(refused {sent.Says})";
        using var json = JsonDocument.Parse(sent.Body);
        return json.RootElement.TryGetProperty("board_new", out var member) ? member.GetRawText() : "(absent)";
    }

    private static byte[] Question(SceneActor actor, string board, DateTimeOffset now) =>
        actor.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), now);

    private static byte[] Finding(SceneActor actor, string board, DateTimeOffset now) =>
        actor.Agent.Sign(PostKind.Finding, board, RevisionProbe.Unrelated(), "Finding " + RevisionProbe.Unrelated(), null, now);

    /// <summary>
    /// NMN15. A question on a board nobody has posted to is <c>board_new: true</c>; the second question on it is <c>false</c>; a finding, by a T2 agent, likewise on a board of its own;
    /// and a board that exists elsewhere does not make a fresh one old. The second row of each pair (the accepting side) is green on the seam. Predicted red on the seam: <c>false</c>
    /// for the first of each pair.
    /// </summary>
    [Fact]
    public async Task R8_88_NMN15_AFirstQuestionAndAFirstFindingOnABoardAreNewAndTheSecondIsNot()
    {
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "nmn15", Ct);
        var wise = await BudgetBursts.T2Async(forum, Board(), Ct);
        var boardQ = Board();
        var boardF = Board();
        var boardElsewhere = Board();

        var q1 = await RevisionProbe.SendAsync(forum, asker, Question(asker, boardQ, forum.Now), cancellationToken: Ct);
        var q2 = await RevisionProbe.SendAsync(forum, asker, Question(asker, boardQ, forum.Now), cancellationToken: Ct);
        var f1 = await RevisionProbe.SendAsync(forum, wise, Finding(wise, boardF, forum.Now), cancellationToken: Ct);
        var f2 = await RevisionProbe.SendAsync(forum, wise, Finding(wise, boardF, forum.Now), cancellationToken: Ct);
        var otherBoard = await RevisionProbe.SendAsync(forum, asker, Question(asker, boardElsewhere, forum.Now), cancellationToken: Ct);

        Said(
            ["question 1: true", "question 2: false", "finding 1: true", "finding 2: false", "a board of its own after others exist: true"],
            [$"question 1: {BoardNewOf(q1)}", $"question 2: {BoardNewOf(q2)}", $"finding 1: {BoardNewOf(f1)}", $"finding 2: {BoardNewOf(f2)}", $"a board of its own after others exist: {BoardNewOf(otherBoard)}"]);
    }

    /// <summary>
    /// NMN16. The other kinds carry no member: an answer, a comment, a revision, a vote and a verification, each accepted, each with its receipt read for the member. A control (green
    /// on the seam: the seam adds nothing for them); it is what FNMN16 turns red.
    /// </summary>
    [Fact]
    public async Task R8_88_NMN16_NoOtherKindCarriesTheMember()
    {
        var board = Board();
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "nmn16", Ct);
        var answerer = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var voter = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var question = await RevisionProbe.SendAsync(forum, asker, Question(asker, board, forum.Now), cancellationToken: Ct);

        var answer = await RevisionProbe.SendAsync(forum, answerer, answerer.Agent.SignAnswer(board, RevisionProbe.Unrelated(), question.PostId ?? "none", forum.Now), cancellationToken: Ct);
        var comment = await RevisionProbe.SendAsync(forum, answerer, answerer.Agent.Sign(PostKind.Comment, board, RevisionProbe.Unrelated(), null, question.PostId ?? "none", forum.Now), cancellationToken: Ct);
        var revision = await RevisionProbe.SendAsync(forum, asker, asker.Agent.SignRevision(board, "Corrected " + RevisionProbe.Unrelated(), question.PostId ?? "none", question.Digest, forum.Now), cancellationToken: Ct);
        var vote = await RevisionProbe.SendAsync(forum, voter, voter.Agent.SignVote(board, answer.Digest ?? "none", forum.Now), cancellationToken: Ct);
        var verification = await RevisionProbe.SendAsync(forum, voter, voter.Agent.SignVerification(board, answer.Digest ?? "none", "reproduced", forum.Now), cancellationToken: Ct);

        Said(
            ["question: (a member)", "answer: (absent)", "comment: (absent)", "revision: (absent)", "vote: (absent)", "verification: (absent)"],
            [$"question: {(BoardNewOf(question) is "(absent)" ? "(absent)" : "(a member)")}", $"answer: {BoardNewOf(answer)}", $"comment: {BoardNewOf(comment)}", $"revision: {BoardNewOf(revision)}", $"vote: {BoardNewOf(vote)}", $"verification: {BoardNewOf(verification)}"]);
    }

    /// <summary>
    /// NMN17. No read is added (R8.88: "so that no read is added before the append and none is made after it"): the log reads of a question write equal those of a comment write
    /// under it, to a bound the measure of the seam fixes (a question's own reads are the comment's less its parent's lookup), and the same question text sent twice reads the same number.
    /// The figure is the reads a question write makes on the tree before the rule: <c>SeamReads</c>. A control, green on the seam; it holds FNMN19 and FNMN21.
    /// </summary>
    [Fact]
    public async Task R8_88_NMN17_AQuestionWriteReadsTheLogAsManyTimesAsItDidBeforeTheMember()
    {
        var counter = new Counter();
        await using var host = forum.NewHost(services =>
            services.CountRequestReads(counter));
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "nmn17", Ct);
        var token = await asker.Dpop.GetTokenAsync(host.Client, RevisionProbe.TokenEndpoint, forum.Now, Ct);

        async Task<int> ReadsOfAsync(byte[] wire)
        {
            counter.Count = 0;
            var sent = await RevisionProbe.SendViaAsync(forum, host.Client, asker, wire, token, Ct);
            Assert.True(sent.Status == 201, sent.Says);
            return counter.Count;
        }

        var first = await ReadsOfAsync(Question(asker, Board(), forum.Now));
        var onKnownBoard = Board();
        await ReadsOfAsync(Question(asker, onKnownBoard, forum.Now));
        var second = await ReadsOfAsync(Question(asker, onKnownBoard, forum.Now));

        Said($"{SeamReads} reads for a question on a new board and {SeamReads} on a known one", $"{first} reads for a question on a new board and {second} on a known one");
    }

    /// <summary>The log reads a question write makes on the tree before the rule (Task 1 measured it on the seam, which adds none).</summary>
    private const int SeamReads = 3;

    /// <summary>
    /// NMN18. The confirm path's <c>201</c> carries the member (errata G55: a question whose append commits and then throws is answered <c>201</c> composed from the log), and a
    /// <c>409 already-recorded</c> for the same bytes carries none. Predicted red on the seam: the confirmed receipt says <c>false</c> for a board that is new. The resend's own
    /// <c>201</c> is NMN33's (the clients print the resend's receipt) and is not driven here.
    /// </summary>
    [Fact]
    public async Task R8_88_NMN18_TheConfirmedReceiptCarriesTheMemberAndARepeatRefusalDoesNot()
    {
        await using var host = forum.NewHost();
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "nmn18", Ct);
        var board = Board();
        var wire = Question(asker, board, forum.Now);
        var token = await asker.Dpop.GetTokenAsync(host.Client, RevisionProbe.TokenEndpoint, forum.Now, Ct);

        host.Store.Arm(EventStoreFault.AppendThenThrow);
        Sent confirmed;
        try
        {
            confirmed = await RevisionProbe.SendViaAsync(forum, host.Client, asker, wire, token, Ct);
        }
        finally
        {
            host.Store.Disarm();
        }

        var repeat = await RevisionProbe.SendViaAsync(forum, host.Client, asker, wire, token, Ct);

        Said(
            "confirmed 201 with board_new true; the repeat 409 curia/ingest/already-recorded with no member",
            $"confirmed {confirmed.Status} with board_new {BoardNewOf(confirmed)}; the repeat {repeat.Says} with {(repeat.Body.Contains("board_new", StringComparison.Ordinal) ? "a member" : "no member")}");
    }

    /// <summary>
    /// NMN19. The stored event and the canonical form are unchanged: the <c>post.accepted</c> payload of a question holds the members it held before the rule and none called
    /// <c>board_new</c>, and the canonical text holds none either (R8.88: stored nowhere). A control, green on the seam; FNMN22 turns it red.
    /// </summary>
    [Fact]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "A constant statement; the digest is bound as a parameter.")]
    public async Task R8_88_NMN19_TheMemberIsStoredNowhere()
    {
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "nmn19", Ct);
        var sent = await RevisionProbe.SendAsync(forum, asker, Question(asker, Board(), forum.Now), cancellationToken: Ct);
        Assert.True(sent.Status == 201, sent.Says);

        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT payload::text FROM events WHERE event_type = 'post.accepted' AND payload->>'digest' = @digest;", admin);
        command.Parameters.AddWithValue("digest", sent.Digest!);
        var stored = (string?)await command.ExecuteScalarAsync(Ct) ?? "(no event)";
        using var json = JsonDocument.Parse(stored);
        var members = string.Join(" ", json.RootElement.EnumerateObject().Select(m => m.Name).Order(StringComparer.Ordinal));
        var canonicalHolds = json.RootElement.TryGetProperty("canonical", out var canonical) && canonical.GetString()!.Contains("board_new", StringComparison.Ordinal);

        Said("the payload holds author board canonical digest kind parent post_id risk_flags server_ts signature; the canonical text holds no board_new", $"the payload holds {members}; the canonical text holds {(canonicalHolds ? "a board_new" : "no board_new")}");
    }

    /// <summary>
    /// NMN20. A board is new to a question when no post of a discussion kind that moderation serves is on it: a board whose only post is withheld is new, and a board whose
    /// served post is under a different case is new (the compare is ordinal). The control, a board with a served post on the very spelling, is <c>false</c> and comes first
    /// (green on the seam). Predicted red: <c>false</c> for the other two.
    /// </summary>
    [Fact]
    public async Task R8_88_NMN20_ABoardWhoseOnlyPostIsWithheldOrWhoseServedPostIsInAnotherCaseIsNew()
    {
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "nmn20", Ct);
        var served = Board();
        var withheldOnly = Board();
        var otherCase = Board();
        await StoreQuestionAsync(forum, served, Ct);
        var (_, hidden) = await StoreQuestionAsync(forum, withheldOnly, Ct);
        await forum.WithholdAsync(hidden.PostId, Ct);
        await StoreQuestionAsync(forum, Upper(otherCase), Ct);

        var control = await RevisionProbe.SendAsync(forum, asker, Question(asker, served, forum.Now), cancellationToken: Ct);
        var withheld = await RevisionProbe.SendAsync(forum, asker, Question(asker, withheldOnly, forum.Now), cancellationToken: Ct);
        var differentCase = await RevisionProbe.SendAsync(forum, asker, Question(asker, otherCase, forum.Now), cancellationToken: Ct);

        Said(
            ["a served post on the spelling: false", "a board whose only post is withheld: true", "a served post under another case: true"],
            [$"a served post on the spelling: {BoardNewOf(control)}", $"a board whose only post is withheld: {BoardNewOf(withheld)}", $"a served post under another case: {BoardNewOf(differentCase)}"]);
    }

    /// <summary>
    /// NMN47 (Amendment 3, ruling 35). The member is computed before the near-duplicate check, so a question whose text the embedder finds no features in (the check answers <c>none</c> for it
    /// before it looks at the board) still carries it: a first question of punctuation only is <c>board_new: true</c> and a second, different one on the same board is <c>false</c>. A control
    /// that the embedder places neither text comes first (so the rows are the no-features case and not another). Red on the seam like NMN15; <b>green on the tree</b>, because the route's order
    /// is right and nothing held it, so its value is FNMN50.
    /// </summary>
    [Fact]
    public async Task R8_88_NMN47_AQuestionOfPunctuationOnlyStillCarriesTheMember()
    {
        var embedder = forum.Services.GetRequiredService<ITextEmbedder>();
        foreach (var text in new[] { "?!\n?!", "??\n!?" })
        {
            var embedded = await embedder.EmbedAsync(text, Curia.Domain.Search.EmbeddingRole.Passage, Ct);
            Assert.False(embedded.TryGetValue(out _, out var embedError), $"NOT REACHED: the embedder placed {text.Replace("\n", " / ", StringComparison.Ordinal)}");
            Assert.Equal(Curia.Domain.Search.EmbeddingErrors.NoFeatures().Type, embedError!.Type);
        }

        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "nmn47", Ct);
        var board = Board();

        var first = await RevisionProbe.SendAsync(forum, asker, asker.Agent.SignQuestion(board, "?!", "?!", forum.Now, tags: []), cancellationToken: Ct);
        var second = await RevisionProbe.SendAsync(forum, asker, asker.Agent.SignQuestion(board, "!?", "??", forum.Now, tags: []), cancellationToken: Ct);

        Said(
            ["a first question of punctuation: true", "a second, different one on the same board: false"],
            [$"a first question of punctuation: {BoardNewOf(first)}", $"a second, different one on the same board: {BoardNewOf(second)}"]);
    }

    /// <summary>
    /// NMN53 (ruling 36, M12; the half of non-property (d) that can be driven). One agent's two simultaneous first questions on one board are queued by the agent's hold (R7.27), so the second
    /// is decided on a log that holds the first: exactly one answers <c>true</c> and one <c>false</c>, never both <c>true</c>. The race between two agents is a recorded non-property and has no
    /// probe (ruling 37). Falsification FNMN56.
    /// </summary>
    [Fact]
    public async Task R8_88_NMN53_OneAgentsTwoSimultaneousFirstQuestionsAnswerTrueOnceAndFalseOnce()
    {
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "nmn53", Ct);
        var board = Board();
        var token = await asker.Dpop.GetTokenAsync(forum.Client, RevisionProbe.TokenEndpoint, forum.Now, Ct);

        var sent = await RevisionProbe.TogetherAsync(
            Enumerable.Range(0, 2).Select<int, Func<Task<Sent>>>(_ => () => RevisionProbe.SendViaAsync(forum, null, asker, Question(asker, board, forum.Now), token, Ct)));

        var statuses = string.Join(" and ", sent.Select(x => x.Says));
        var members = string.Join(" and ", sent.Select(BoardNewOf).Order(StringComparer.Ordinal));
        Said(
            ["answered 201 and 201", "board_new false and true"],
            [$"answered {statuses}", $"board_new {members}"]);
    }
}
