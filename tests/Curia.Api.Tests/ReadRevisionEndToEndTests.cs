using System.Diagnostics.CodeAnalysis;
using Curia.Api.Tests.Fuzz;
using Curia.Canon.Json;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// PR 3 (errata G50, R11.66), Task 0's probes RLW33 to RLW35, the real-Forum halves, on a real socket: the two shapes the ninth exercise stored (a revision whose
/// <c>parent</c> names another thread's post, and one whose <c>parent</c> is <c>../x</c>) and an honest chain, each through <c>curia_read</c>. The claims the note
/// makes are checked against the Forum: the chain root's own read lists the revision, and <c>curia_recheck</c> of the signed <c>prev</c> returns the predecessor.
///
/// <para><b>Oracles.</b> The ids and digests are the receipts of the posts the scene made (the lying revisions are hand-signed and submitted through the session,
/// so the Forum's gates apply and the client's rules do not); the chain root a probe reads is found by the test over the events by SQL, never by the code under test;
/// the words are R11.66's, parsed from the errata. RLW35 is green on the seam tree (a control, red under FRLW46).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReadRevisionEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string First(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text.Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string Id(string id) => DisplayLiteral.Of(id);

    private static List<string> Note(string block, RlwPost predecessor)
    {
        var problems = new List<string>();
        if (!block.Contains(PublishedReadersLastWords.PlacementClause(), StringComparison.Ordinal)) problems.Add($"the note does not say the Forum {PublishedReadersLastWords.PlacementClause()}");
        if (!block.Contains(Id(predecessor.Digest), StringComparison.Ordinal)) problems.Add($"the note does not name the signed prev {Id(predecessor.Digest)}");
        if (!block.Contains(PublishedReadersLastWords.NoteTool(), StringComparison.Ordinal)) problems.Add($"the note does not point at {PublishedReadersLastWords.NoteTool()}");
        return problems;
    }

    /// <summary>
    /// RLW33 (the real half). A revision whose <c>parent</c> names another thread's post: <c>curia_read</c> says <c>NOT LISTED</c> and the note, not "could not be
    /// read"; the other thread's own read is unchanged; and the pointer is true: <c>curia_recheck</c> of the signed <c>prev</c> returns the predecessor, whose own
    /// <c>curia_read</c> lists the revision.
    /// </summary>
    [Fact]
    public async Task R11_66_RLW33_ARevisionPlacedInAnotherThreadIsNotListed()
    {
        using var scene = new ReadersLastWordsScene(forum);
        var board = "board-" + ReadersLastWordsScene.Suffix();
        var asker = await scene.T1Async("asker", board);
        var question = await scene.AskAsync(asker, board, "finch");
        var other = await scene.AskAsync(asker, board, "wren");
        var revision = await scene.ReviseAsync(asker, board, question, "A corrected question whose parent lies.", parent: other.Id);
        var (_, notListed, _) = PublishedReadersLastWords.ReadLines();

        var block = First(await asker.Tools.ReadAsync(revision.Id, Ct));
        var problems = new List<string>();
        if (!block.Contains(notListed, StringComparison.Ordinal)) problems.Add($"Expected: {notListed} Actual: {block}");
        if (block.Contains(PublishedReadersLastWords.NotReadCause(), StringComparison.Ordinal)) problems.Add($"says the thread {PublishedReadersLastWords.NotReadCause()}: {block}");
        problems.AddRange(Note(block, question));

        var otherBlock = First(await asker.Tools.ReadAsync(other.Id, Ct));
        if (!otherBlock.StartsWith($"thread root={Id(other.Id)} posts=1", StringComparison.Ordinal)) problems.Add($"the other thread's own read changed: {otherBlock}");

        var rechecked = ReadersLastWordsScene.Flatten(await asker.Tools.RecheckAsync([question.Digest], Ct));
        if (!rechecked.Contains(Id(question.Id), StringComparison.Ordinal)) problems.Add($"the pointer does not lead where it says: recheck of the prev answers {rechecked.Trim()}");
        var root = await asker.Tools.ReadAsync(question.Id, Ct);
        if (!ReadersLastWordsScene.Flatten(root).Contains(revision.Id, StringComparison.Ordinal)) problems.Add("the predecessor's own read does not list the revision");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// RLW34 (the real half). A revision whose <c>parent</c> is <c>../x</c>, of an honest revision: <c>curia_read</c> says <c>CUT</c> and the note; the claim is true,
    /// the chain root's own read (found over the events by <c>prev</c>) lists the revision among its <c>posts=</c>, and <c>curia_recheck</c> of the signed <c>prev</c>
    /// returns the predecessor's post id.
    /// </summary>
    [Fact]
    public async Task R11_66_RLW34_AJunkParentIsCutAndTheNoteLeadsToTheChain()
    {
        using var scene = new ReadersLastWordsScene(forum);
        var board = "board-" + ReadersLastWordsScene.Suffix();
        var asker = await scene.T1Async("asker", board);
        var chain = await scene.QuestionWithRevisionsAsync(asker, board, "finch", 1);
        var lying = await scene.ReviseAsync(asker, board, chain[1], "A corrected text whose parent is junk.", parent: "../x");

        var block = First(await asker.Tools.ReadAsync(lying.Id, Ct));
        var problems = new List<string>();
        if (!block.Split('\n').Any(l => l.StartsWith(PublishedReadersLastWords.ReadLines().Cut, StringComparison.Ordinal))) problems.Add($"Expected: a CUT line Actual: {block}");
        problems.AddRange(Note(block, chain[1]));

        // The note's claim, by the test's own walk over the events: the chain root lists the revision, and the signed prev leads to the predecessor.
        var walked = await scene.ChainFromEventsAsync(chain[0]);
        if (!walked.Select(w => w.Id).Contains(lying.Id)) problems.Add("the events table's own walk from the chain root does not reach the revision");
        var rootRead = await asker.Tools.ReadAsync(chain[0].Id, Ct);
        if (!ReadersLastWordsScene.Flatten(rootRead).Contains(lying.Id, StringComparison.Ordinal)) problems.Add("the chain root's own read does not list the revision");
        var rechecked = ReadersLastWordsScene.Flatten(await asker.Tools.RecheckAsync([chain[1].Digest], Ct));
        if (!rechecked.Contains(Id(chain[1].Id), StringComparison.Ordinal)) problems.Add($"recheck of the signed prev does not return the predecessor: {rechecked.Trim()}");
        Assert.True(problems.Count == 0, $"Expected: the note on the CUT line, and the claim true ({string.Join("; ", problems)}) Actual first block: {block}");
    }

    /// <summary>RLW35, a control. An honest chain made by the clients' own route: a revision at depth 1 and at depth 3 reads as <c>thread root=&lt;the question&gt;</c> with no <c>NOT READ</c>, <c>NOT LISTED</c> or <c>CUT</c> and no note. Green on the seam tree; red under FRLW46.</summary>
    [Fact]
    public async Task R11_66_RLW35_AnHonestChainHasNoNote()
    {
        using var scene = new ReadersLastWordsScene(forum);
        var board = "board-" + ReadersLastWordsScene.Suffix();
        var asker = await scene.T1Async("asker", board);
        var chain = await scene.QuestionWithRevisionsAsync(asker, board, "finch", 3);
        var (_, notListed, cut) = PublishedReadersLastWords.ReadLines();
        const string notRead = "NOT READ";

        var problems = new List<string>();
        foreach (var (name, post) in new[] { ("depth 1", chain[1]), ("depth 3", chain[3]) })
        {
            var block = First(await asker.Tools.ReadAsync(post.Id, Ct));
            if (!block.StartsWith($"thread root={Id(chain[0].Id)}", StringComparison.Ordinal)) problems.Add($"{name}: Expected: thread root=<the question> Actual: {block}");
            foreach (var word in new[] { notRead, notListed, cut, PublishedReadersLastWords.PlacementClause(), PublishedReadersLastWords.NoteTool() })
                if (block.Contains(word, StringComparison.Ordinal)) problems.Add($"{name}: an honest chain says \"{word}\": {block}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
