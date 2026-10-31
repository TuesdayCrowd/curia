using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Moderation;
using Curia.Tests.Shared;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G49 (R7.29, R9.50) at the receipts and the refusals of the three acts that spend a budget: SEB20, SEB22, SEB23 and SEB24.
///
/// <para><b>Oracles.</b> The count the events table (and the flag store's table) holds after each act, by SQL; the instant a refusal states is the stored
/// column plus the window (R7.28: never a receipt's or a payload's instant), with a clock that advances in sub-microsecond ticks. The members of a receipt's
/// <c>budget</c> are R9.50's; the token a refusal ends with is R7.29's.</para>
///
/// <para><b>Deviation from the plan, SEB22:</b> the finding kind is not driven. No helper signs a finding (it carries members no other kind has), and the
/// remaining create kinds, the vote and the verification are; the finding's receipt is the same code path as a question's (one post-accepted receipt), and
/// the fact says so in its failing text if a kind it was given is not driven.</para>
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetReceiptEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> BudgetNames => new(BudgetReadouts.Budgets);

    private static string Describe(JsonElement budget) =>
        budget.ValueKind == JsonValueKind.Object
            ? $"{budget.GetProperty("name").GetString()} limit={budget.GetProperty("limit").GetRawText()} used={budget.GetProperty("used").GetRawText()} remaining={budget.GetProperty("remaining").GetRawText()}"
            : "no budget member";

    private static string Expect(string name, int limit, int used) => $"{name} limit={limit} used={used} remaining={Math.Max(0, limit - used)}";

    /// <summary>
    /// SEB20. Read after commit, per budget: after an act of the kind the receipt's <c>used</c> equals the next readout's <c>used</c> equals the SQL count.
    /// </summary>
    [Theory]
    [MemberData(nameof(BudgetNames))]
    public async Task R7_29_SEB20_ReadAfterCommit(string budget)
    {
        var acts = await BudgetActor.T0Async(forum, budget, Ct);
        var limit = BudgetReadouts.LimitOf("T0", budget);

        var act = await acts.NextAsync();
        Assert.True(act.Status is 200 or 201, $"Expected: the act stored Actual: {act.Says}");
        var sql = (await BudgetReadouts.OracleAsync(forum, "T0", budget, acts.Actor.Agent.AgentId)).Used;
        var readout = await BudgetReadouts.ReadAsync(forum, acts.Actor);

        Probe.Said(Expect(budget, limit, sql), Describe(act.Budget));
        Assert.Equal(sql, readout.Entry(budget).GetProperty("used").GetInt32());
    }

    /// <summary>
    /// SEB22. A receipt per path: each kind a T2 agent may create (question, comment, answer, revision), a vote and a verification, is <c>201</c> with a
    /// <c>budget</c> named <c>posting</c> whose <c>used</c> is the SQL count after it; an acceptance is <c>201</c> named <c>acceptance</c>, its replay <c>200</c>
    /// with <c>used</c> unchanged, a replacing acceptance <c>201</c> with <c>used</c> one higher; a flag is <c>201</c> named <c>flag</c>.
    /// </summary>
    [Fact]
    public async Task R7_29_SEB22_AReceiptPerPath()
    {
        var board = Probe.Board();
        var actor = await BudgetBursts.T2Async(forum, board, Ct);
        var other = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var result = await BudgetBursts.StoreAsync(forum, ForumAgent.Create("https://agents.example/result-" + Probe.Suffix(), "result").SignAnswer(board, Probe.Unrelated(), other.PostId, forum.Now), Ct);
        var limit = BudgetReadouts.LimitOf("T2", "posting");
        var problems = new List<string>();

        var question = await Probe.AskAsync(forum, actor, board, Ct);
        var wires = new (string Kind, byte[] Wire)[]
        {
            ("question", actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now)),
            ("comment", actor.Agent.Sign(PostKind.Comment, board, Probe.Unrelated(), null, question.PostId, forum.Now)),
            ("answer", actor.Agent.SignAnswer(board, Probe.Unrelated(), other.PostId, forum.Now)),
            ("revision", actor.Agent.SignRevision(board, Probe.Unrelated(), question.PostId, question.Digest, forum.Now)),
            ("vote", actor.Agent.SignVote(board, result.Digest, forum.Now)),
            ("verification", actor.Agent.SignVerification(board, result.Digest, "reproduced", forum.Now)),
        };

        foreach (var (kind, wire) in wires)
        {
            var sent = await Probe.SendAsync(forum, actor, wire, cancellationToken: Ct);
            var act = new Act(sent.Status, sent.Type, sent.Detail, sent.Body);
            var used = (await BudgetReadouts.OracleAsync(forum, "T2", "posting", actor.Agent.AgentId)).Used;
            if (sent.Status != 201 || Describe(act.Budget) != Expect("posting", limit, used)) problems.Add($"{kind}: Expected: 201 {Expect("posting", limit, used)} Actual: {act.Says} {Describe(act.Budget)}");
        }

        var scene = await AcceptedAnswerScene.BuildAsync(forum, Ct);
        var accepts = LimitOfAccepts();
        var first = await BudgetReadouts.AcceptAsync(forum, scene.Asker, scene.Answer1.PostId);
        var replay = await BudgetReadouts.AcceptAsync(forum, scene.Asker, scene.Answer1.PostId);
        var replacing = await BudgetReadouts.AcceptAsync(forum, scene.Asker, scene.Answer2.PostId);
        foreach (var (what, act, status, used) in new[] { ("acceptance", first, 201, 1), ("replay", replay, 200, 1), ("replacing acceptance", replacing, 201, 2) })
            if (act.Status != status || Describe(act.Budget) != Expect("acceptance", accepts, used)) problems.Add($"{what}: Expected: {status} {Expect("acceptance", accepts, used)} Actual: {act.Says} {Describe(act.Budget)}");

        var flagger = await AcceptedAnswerScene.EnrolAsync(forum, "flagger", Ct);
        var flag = await BudgetReadouts.FlagAsync(forum, flagger, other.PostId, FlagKind.Spam);
        if (flag.Status != 201 || Describe(flag.Budget) != Expect("flag", BudgetReadouts.LimitOf("T0", "flag"), 1)) problems.Add($"flag: Expected: 201 {Expect("flag", BudgetReadouts.LimitOf("T0", "flag"), 1)} Actual: {flag.Says} {Describe(flag.Budget)}");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));

        int LimitOfAccepts() => BudgetReadouts.LimitOf("T0", "acceptance");
    }

    /// <summary>
    /// SEB23. A receipt states no instant, and a refused act no <c>budget</c>: every receipt's <c>budget</c> has exactly R9.50's four members (no
    /// <c>next_slot_at</c>); a refused act (the budget spent) carries none; the positive is in the same fact. A receipt's own <c>server_ts</c> is not the
    /// stored column (SEB54 sees that), which is why no instant is on it.
    /// </summary>
    [Theory]
    [MemberData(nameof(BudgetNames))]
    public async Task R9_50_SEB23_AReceiptStatesNoInstantAndARefusalNoBudget(string budget)
    {
        var acts = await BudgetActor.T0Async(forum, budget, Ct);
        var limit = BudgetReadouts.LimitOf("T0", budget);
        await acts.SpendAsync(limit - 1);

        var last = await acts.NextAsync();
        var refused = await acts.NextAsync();

        var members = last.Budget.ValueKind == JsonValueKind.Object ? last.Budget.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray() : [];
        var published = PublishedBudgetReadout.ReceiptMembers().Order(StringComparer.Ordinal).ToArray();
        Assert.True(members.SequenceEqual(published, StringComparer.Ordinal), $"Expected: a receipt budget of {string.Join(',', published)} Actual: {(members.Length == 0 ? "no budget member" : string.Join(',', members))}");
        Assert.True(refused.Status == 403 && refused.Budget.ValueKind == JsonValueKind.Undefined, $"Expected: 403 with no budget member Actual: {refused.Says}");
    }

    /// <summary>
    /// SEB24. A refusal states the instant: for each of the three budget reasons the detail ends <c>next_slot_at=&lt;instant&gt;</c> equal to the stored
    /// column of the oldest in-window entry (the k-th where over the limit) plus 86,400 s, with the title unchanged; and no other reason's detail has the
    /// token (the ones the host produces here are asserted; the rest of <see cref="AccessPolicy.DenialReasons"/> are named in the failure as not produced by
    /// this fact).
    /// </summary>
    [Theory]
    [MemberData(nameof(BudgetNames))]
    public async Task R7_29_SEB24_ARefusalStatesTheInstant(string budget)
    {
        var acts = await BudgetActor.T0Async(forum, budget, Ct);
        var limit = BudgetReadouts.LimitOf("T0", budget);
        await acts.SpendAsync(limit);
        var instants = await BudgetReadouts.InstantsAsync(forum, budget, acts.Actor.Agent.AgentId);

        var refused = await acts.NextAsync();

        var reason = BudgetReadoutEndpointTests.ReasonOf(budget);
        var token = PublishedBudgetReadout.RefusalToken();
        var expected = $"{reason} tier=T0 {token}{BudgetReadouts.Form(instants[0] + BudgetReadouts.Window)}";
        Assert.True(refused.Status == 403 && refused.Detail == expected, $"Expected: 403 {expected} Actual: {refused.Says}");
    }

    /// <summary>
    /// SEB24, over the limit and the other reasons. Five posts for a T0 agent (limit three): the refusal's instant is the third oldest's column plus the
    /// window (k = 3); and the denials a T0 agent can draw that are not budgets (a tier denial for an answer) end without the token, with the budget reasons
    /// the only ones that carry it (the policy's enumeration).
    /// </summary>
    [Fact]
    public async Task R7_29_SEB24_TheKthOldestWhenOverTheLimitAndNoTokenOnOtherReasons()
    {
        var acts = await BudgetActor.T0Async(forum, "posting", Ct);
        var board = Probe.Board();
        for (var i = 0; i < 5; i++)
        {
            forum.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1 + i));
            await BudgetBursts.SeedPostsAsync(forum, acts.Actor.Agent, board, 1, Ct);
        }

        var instants = await BudgetReadouts.InstantsAsync(forum, "posting", acts.Actor.Agent.AgentId);
        var refused = await acts.NextAsync();
        var token = PublishedBudgetReadout.RefusalToken();
        var expected = $"table-11/rate-budget-exhausted tier=T0 {token}{BudgetReadouts.Form(instants[2] + BudgetReadouts.Window)}";
        Assert.True(refused.Status == 403 && refused.Detail == expected, $"Expected: 403 {expected} Actual: {refused.Says}");

        var answer = await Probe.SendAsync(forum, acts.Actor, acts.Actor.Agent.SignAnswer(board, Probe.Unrelated(), (await BudgetBursts.StoredQuestionAsync(forum, board, Ct)).PostId, forum.Now), cancellationToken: Ct);
        var budgetReasons = AccessPolicy.DenialReasons.Where(r => r.StartsWith("table-11/", StringComparison.Ordinal) && r.EndsWith("-budget-exhausted", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        Assert.True(
            answer.Status == 403 && answer.Detail is not null && !answer.Detail.Contains(token, StringComparison.Ordinal) && !budgetReasons.Any(r => answer.Detail.Contains(r, StringComparison.Ordinal)),
            $"Expected: a tier denial with no {token} Actual: {answer.Says} {answer.Detail}; the reasons this fact does not produce: {string.Join(", ", AccessPolicy.DenialReasons.Except(budgetReasons).Where(r => r != "table-10/denied"))}");
    }
}
