using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Api.Tests.BoardListProbe;
using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R9.58 (errata G63), LOB53: <c>GET /v1/boards</c> takes no agent's write hold. The agent gate (R7.27) is entered by the two write paths and by nothing else, and the gate here is the fixture's recorder
/// (<see cref="ObservingAgentWriteGate"/>), the seam <c>AgentWriteGateEndpointTests</c> BRB38 reads for the flag. The reads below (bare, filtered, unmatched, an unknown name, a blank value, a limit out of
/// range and a malformed cursor) leave the recorder empty; the control, in the same assertion, is one post sent through the route by an enrolled agent, which enters it once (a recorder that cannot see an
/// entry is not a probe). Predicted green on the tree: the route takes no gate; FLOB26 turns it red by entering one.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BoardListGateHoldTests(AgentWriteGateForumFixture forum) : IClassFixture<AgentWriteGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Times(int n) => n == 1 ? "once" : $"{n} times";

    /// <summary>LOB53, list reads enter the agent gate never; a post enters it once.</summary>
    [Fact]
    public async Task R9_58_LOB53_ListReadsEnterTheAgentGateNeverAndAPostEntersItOnce()
    {
        var board = Name("lob53");
        _ = await StoreAsync(forum, board, 1, 1, Ct);
        forum.ResetSeams();

        foreach (var query in new[] { string.Empty, "board=" + board, "board=nowhere", "x=1", "board=", "limit=0", "cursor=@@@" })
            _ = await ListAsync(forum.Client, query, Ct);

        var enteredByReads = forum.Gate.Entered.Count;
        var countedByReads = forum.Gate.EnteredCount;

        var postBoard = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, postBoard, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lob53", Ct);
        var sent = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, postBoard, parent.PostId, forum.Now), null, Ct);
        var enteredByPost = forum.Gate.Entered.Count - enteredByReads;

        NameProbe.Said(
            "list reads entered the agent gate never; a post entered it once",
            $"list reads entered the agent gate {(enteredByReads + countedByReads == 0 ? "never" : Times(enteredByReads))}; a post{(BudgetBursts.Kind(sent).ToString() == "201" ? string.Empty : " (answered " + BudgetBursts.Kind(sent) + ")")} entered it {Times(enteredByPost)}");
    }
}
