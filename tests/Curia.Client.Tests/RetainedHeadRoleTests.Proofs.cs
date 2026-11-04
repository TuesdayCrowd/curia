using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>CWK56 and CWK57 (errata G59, R6.61, R11.83; plan ruling 36): a proof the check cannot read is a failure inside the published frame, in both orientations. The stub log and its helpers are the other half of the class.</summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RetainedHeadRoleTests
{
    // ----- CWK56 and CWK57 (plan ruling 36) -----

    /// <summary><paramref name="head"/> with its signed <c>root_hash</c> member replaced: the head a Forum that signed a root that is no digest would serve.</summary>
    private static SignedHeadDocument WithRoot(SignedHeadDocument head, string root) =>
        head with { Head = new Curia.Canon.Json.JsonValue.Object([.. head.Head.Members.Select(m => m.Key == "root_hash" ? new KeyValuePair<string, Curia.Canon.Json.JsonValue>(m.Key, new Curia.Canon.Json.JsonValue.String(root)) : m)]) };

    /// <summary>The frame of every failed proof, as the errata publishes it, and the reason of plan ruling 36, found in <paramref name="check"/> (both heads named by role, sizes, kids, retained_at).</summary>
    private static List<string> FrameProblems(Check check, string label, string reasonStretch, int retainedSize, int servedSize)
    {
        var problems = new List<string>();
        if (check.Outcome != CheckOutcome.Failed) problems.Add($"{label}: expected FAILED, was {check.Outcome} (\"{check.Detail}\")");
        // The published words are the whole line, which the reader prefixes with its label "consistency FAILED: "; the frame is what comes before the reason.
        var frame = PublishedAgentWords.FailedProofWords.Split("<reason>")[0].Replace("consistency FAILED: ", string.Empty, StringComparison.Ordinal);
        foreach (var stretch in PublishedAgentWords.Stretches(frame))
            if (!check.Detail.Contains(stretch, StringComparison.Ordinal)) problems.Add($"{label}: the frame lacks \"{stretch}\" (\"{check.Detail}\")");
        if (!check.Detail.Contains($"tree size {retainedSize}", StringComparison.Ordinal) || !check.Detail.Contains($"tree size {servedSize}", StringComparison.Ordinal)) problems.Add($"{label}: the frame does not name both tree sizes ({retainedSize}, {servedSize}): \"{check.Detail}\"");
        if (!check.Detail.Contains("retained at NOT STATED", StringComparison.Ordinal)) problems.Add($"{label}: the frame does not state retained_at (NOT STATED where the file records none): \"{check.Detail}\"");
        if (!check.Detail.Contains("The retained head is kept.", StringComparison.Ordinal)) problems.Add($"{label}: the frame does not say the retained head is kept");
        if (!check.Detail.Contains(reasonStretch, StringComparison.Ordinal)) problems.Add($"{label}: the reason \"{reasonStretch}\" is missing (\"{check.Detail}\")");
        return problems;
    }

    /// <summary>
    /// CWK56. A malformed root on the proof, both orientations (the retained head smaller, then larger): the proof's roots equal the heads' and are not sha256 digests. <c>FAILED</c> inside the
    /// published frame with the reason <c>the Forum's proof names a root that is not a sha256 digest: &lt;root as a display literal&gt;</c>, for the smaller head's root and for the larger's. Predicted red:
    /// <c>a root on the consistency proof is not a sha256 digest</c> with no head named.
    /// </summary>
    [Fact]
    public void R6_61_CWK56_AMalformedRootOnTheProofIsFailedInsideTheFrame()
    {
        var reason = PublishedAgentWords.UnreadableProofReasons.Single(r => r.Contains("root", StringComparison.Ordinal));
        var stretch = reason[..reason.IndexOf('<', StringComparison.Ordinal)];
        Assert.True(stretch.Length >= 20, "the published root reason is under 20 characters before its placeholder");
        var problems = new List<string>();
        foreach (var (retainedSize, servedSize) in new[] { (4, 8), (8, 4) })
            foreach (var larger in new[] { false, true })
            {
                const string Bad = "sha256:not-hex";
                var retained = Head(retainedSize);
                var served = Head(servedSize);
                var smallerIsRetained = retainedSize < servedSize;
                // Malform the root of the smaller head, then of the larger one.
                if (smallerIsRetained != larger) retained = WithRoot(retained, Bad); else served = WithRoot(served, Bad);
                var honest = Proof(Math.Min(retainedSize, servedSize), Math.Max(retainedSize, servedSize));
                var proof = honest with { FromRoot = (smallerIsRetained ? retained : served).RootHash!, ToRoot = (smallerIsRetained ? served : retained).RootHash! };
                var check = ActaCheck.Consistency(retained, served, proof);
                var label = $"retained {retainedSize}, served {servedSize}, {(larger ? "larger" : "smaller")} head's root";
                problems.AddRange(FrameProblems(check, label, stretch + Check.Quote(Bad), retainedSize, servedSize));
            }

        Assert.True(problems.Count == 0, "Expected: FAILED in the published frame with the root reason, in both orientations. Actual: " + string.Join("; ", problems));
    }

    /// <summary>
    /// CWK57. A malformed path node, both orientations: the proof's roots are right and its first node is not a sha256 digest. <c>FAILED</c> inside the published frame with <c>the Forum's proof holds a path
    /// node that is not a sha256 digest: &lt;node as a display literal&gt;</c>. Predicted red: <c>the consistency path holds something that is not a sha256 digest</c> with no head named.
    /// </summary>
    [Fact]
    public void R6_61_CWK57_AMalformedPathNodeIsFailedInsideTheFrame()
    {
        var reason = PublishedAgentWords.UnreadableProofReasons.Single(r => r.Contains("path node", StringComparison.Ordinal));
        var stretch = reason[..reason.IndexOf('<', StringComparison.Ordinal)];
        Assert.True(stretch.Length >= 20, "the published node reason is under 20 characters before its placeholder");
        var problems = new List<string>();
        foreach (var (retainedSize, servedSize) in new[] { (4, 8), (8, 4) })
        {
            const string Bad = "sha256:zz";
            var honest = Proof(Math.Min(retainedSize, servedSize), Math.Max(retainedSize, servedSize));
            var proof = honest with { Path = honest.Path.SetItem(0, Bad) };
            var check = ActaCheck.Consistency(Head(retainedSize), Head(servedSize), proof);
            problems.AddRange(FrameProblems(check, $"retained {retainedSize}, served {servedSize}", stretch + Check.Quote(Bad), retainedSize, servedSize));
        }

        Assert.True(problems.Count == 0, "Expected: FAILED in the published frame with the path-node reason, in both orientations. Actual: " + string.Join("; ", problems));
    }
}
