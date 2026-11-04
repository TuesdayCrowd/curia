using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// CWK54 and CWK34 (errata G59, R6.61, R11.83; plan ruling 21): the roles in the lagging replica's lines, from the stub log, and the words of the entry as the errata publishes them.
/// CWK54 held the defect that <c>ActaCheck.Consistency(from, to, proof)</c> labelled its two roots by the direction of the computation (<c>from</c> the retained head, <c>to</c> the served one) while the
/// verifier made the <i>served</i> head <c>from</c> when the Forum served the smaller (a lagging replica), so both labels were wrong together; the check now takes the two heads by role
/// (<c>Consistency(retained, served, proof)</c>), works the direction out itself and names each head by its role.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RetainedHeadRoleTests : IDisposable
{
    private readonly StubLog _log = new(treeSize: 8, postIndex: 3);

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SignedHeadDocument Head(int size)
    {
        var previous = _log.HeadTreeSize;
        _log.HeadTreeSize = size;
        try
        {
            var read = _log.Client().GetLogHeadAsync(Ct).GetAwaiter().GetResult();
            Assert.True(read.TryGetValue(out var head, out var refusal), refusal?.Summary);
            return head!;
        }
        finally
        {
            _log.HeadTreeSize = previous;
        }
    }

    private ConsistencyProofDocument Proof(int from, int to)
    {
        var read = _log.Client().GetConsistencyProofAsync(from, to, Ct).GetAwaiter().GetResult();
        Assert.True(read.TryGetValue(out var proof, out var refusal), refusal?.Summary);
        return proof!;
    }

    /// <summary>
    /// CWK54. The roles in the lagging replica, from a stub: the retained head is the larger (tree size 8) and the Forum served the smaller (4). A proof whose <c>from_root</c> is wrong, then one whose
    /// <c>to_root</c> is wrong: each line names the head that disagrees by role (the served head for the root at 4, the retained head for the root at 8); the success line of an honest proof names the
    /// retained head as the larger. Predicted red: <c>the retained head's is "&lt;the served head's root&gt;"</c> for <c>from_root</c>, <c>the served head's is "&lt;the retained head's root&gt;"</c> for <c>to_root</c>.
    /// </summary>
    [Fact]
    public void R6_61_CWK54_TheLaggingReplicaNamesTheHeadThatDisagreesByRole()
    {
        var served = Head(4);
        var retained = Head(8);
        var (servedRoot, retainedRoot) = (Check.Quote(served.RootHash), Check.Quote(retained.RootHash));
        var honest = Proof(4, 8);
        var problems = new List<string>();

        var fromWrong = ActaCheck.Consistency(retained, served, honest with { FromRoot = honest.ToRoot });
        if (fromWrong.Outcome != CheckOutcome.Failed) problems.Add($"from_root wrong: expected FAILED, was {fromWrong.Outcome}");
        if (fromWrong.Detail.Contains($"the retained head's is {servedRoot}", StringComparison.Ordinal)) problems.Add($"from_root wrong: the retained head's is {servedRoot} (the served head's root, attributed to the retained head)");
        if (!fromWrong.Detail.Contains("served head", StringComparison.Ordinal)) problems.Add($"from_root wrong: the line does not name the served head (\"{fromWrong.Detail}\")");

        var toWrong = ActaCheck.Consistency(retained, served, honest with { ToRoot = honest.FromRoot });
        if (toWrong.Outcome != CheckOutcome.Failed) problems.Add($"to_root wrong: expected FAILED, was {toWrong.Outcome}");
        if (toWrong.Detail.Contains($"the served head's is {retainedRoot}", StringComparison.Ordinal)) problems.Add($"to_root wrong: the served head's is {retainedRoot} (the retained head's root, attributed to the served head)");
        if (!toWrong.Detail.Contains("retained head", StringComparison.Ordinal)) problems.Add($"to_root wrong: the line does not name the retained head (\"{toWrong.Detail}\")");

        var success = ActaCheck.Consistency(retained, served, honest);
        if (success.Outcome != CheckOutcome.Verified) problems.Add($"honest proof: expected verified, was {success.Outcome}");
        if (!success.Detail.Contains("retained head (tree size 8)", StringComparison.Ordinal)) problems.Add($"honest proof: the line does not name the retained head as the larger (\"{success.Detail}\")");
        if (success.Detail.Contains("this client retained at 4", StringComparison.Ordinal)) problems.Add("honest proof: the line says the head was retained at 4, the served head's size");
        Assert.True(problems.Count == 0, "Expected: the roles by head, never by direction. Actual: " + string.Join("; ", problems));
    }

    /// <summary>
    /// CWK34. The words come from the errata: <see cref="PublishedAgentWords"/> finds the two refusal paragraphs, the rule, the two could-not-be-checked paragraphs, the two failed paragraphs and the
    /// verified line (eight, asserted), each holding its stable phrase (including the key words' "changed or damaged", plan ruling 30), the 19 verbs, the five commands of the rule, the three reasons of
    /// the failed proof. A control on the seam: the printers' text is held to these words by CWK1, CWK4 and CWK21 to CWK28.
    /// </summary>
    [Fact]
    public void R11_83_CWK34_TheWordsOfTheEntryAreFoundWithTheirContent()
    {
        Assert.Equal(12, PublishedAgentWords.AssertParagraphs());
        Assert.Equal(19, PublishedAgentWords.IdentityVerbs.Length);
        Assert.Equal(5, PublishedAgentWords.RuleCommands.Length);
        Assert.Equal(3, PublishedAgentWords.ProofReasons.Length);
        Assert.Equal(2, PublishedAgentWords.UnreadableProofReasons.Length);
        Assert.Contains("changed or damaged", PublishedAgentWords.KeyWords, StringComparison.Ordinal);
        Assert.Contains("copy head.json aside", PublishedAgentWords.KeyWords, StringComparison.Ordinal);
        Assert.Contains("leave head.lock where it is", PublishedAgentWords.KeyWords, StringComparison.Ordinal);
        Assert.Contains("(head.lock stays)", PublishedAgentWords.SignatureWords, StringComparison.Ordinal);
        Assert.Contains("makes no claim about the Forum", PublishedAgentWords.SignatureWords, StringComparison.Ordinal);
        foreach (var words in new[] { PublishedAgentWords.SeveralWords, PublishedAgentWords.BlankWords, PublishedAgentWords.SignatureWords, PublishedAgentWords.KeyWords, PublishedAgentWords.FailedEqualWords, PublishedAgentWords.FailedProofWords, PublishedAgentWords.LaggingWords })
            Assert.NotEmpty(PublishedAgentWords.Stretches(words));
    }
}
