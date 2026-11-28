using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Canon.Acta;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Xunit;
using static Curia.Application.Tests.Acta.LogBuilder;

namespace Curia.Application.Tests.Acta;

/// <summary>
/// R6.69 (errata G87): the Forum's Acta takes leaf hashes from the row when the row has one, and the controls that do not read it (the recomputing fold, the audit) catch a stored leaf that is
/// not its row's. A stored value the Forum trusts for a proof is new trust in the database, so every probe here is of the form "a wrong stored leaf is found", never "a right one passes".
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class StoredLeafTests
{
    private static IReadOnlyList<AppendedEvent> Honest(int n) =>
        LogOf([.. Enumerable.Range(0, n).Select(i => Post("post-" + i))]);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} {e.Detail}"));

    /// <summary>The same rows with row <paramref name="index"/>'s stored leaf replaced (what a holder of the database credential can write); <see langword="null"/> removes it.</summary>
    private static IReadOnlyList<AppendedEvent> WithStoredLeaf(IReadOnlyList<AppendedEvent> log, int index, string? leaf) =>
        [.. log.Select((e, i) => i == index ? new AppendedEvent(e.Seq, e.AggregateId, e.ServerTimestamp, e.Event, leaf) : e)];

    private const string WrongLeaf = "sha256:0000000000000000000000000000000000000000000000000000000000000000";

    [Fact]
    public void R6_69_SL1_EveryRowTheStoreAppendsCarriesItsOwnLeaf()
    {
        var log = Honest(6);

        Assert.All(log, row => Assert.Equal(LogEntries.Prefixed(Require(LogLeaf.Hash(row))), row.StoredLeafHash));
    }

    [Fact]
    public void R6_69_SL2_AFoldOverStoredLeavesHasTheRootOfTheRecomputingFold()
    {
        var log = Honest(11);

        var stored = Require(ActaLog.Fold(log));
        var recomputed = Require(ActaLog.FoldRecomputed(log));

        Assert.Equal(Convert.ToHexString([.. recomputed.Root]), Convert.ToHexString([.. stored.Root]));
        Assert.Equal(0, stored.LeavesRecomputed);
        Assert.Equal(log.Count, recomputed.LeavesRecomputed);
    }

    [Fact]
    public void R6_69_SL3_AWrongStoredLeafIsFoundByTheAuditAndNamesItsIndex()
    {
        var log = WithStoredLeaf(Honest(8), 3, WrongLeaf);

        var findings = Require(StoredLeafAudit.Check(log));

        Assert.Equal([3L], findings.Mismatched);
        Assert.Empty(findings.Missing);
    }

    /// <summary>
    /// The harm (amendment 1, option A): every proof the Forum serves from the stored leaves fails the client's check against the head the operator signed, except the wrong row's own, whose
    /// audit path holds only its siblings. That one verifies, and serves a <c>LeafHash</c> that is not the leaf the client recomputes from the entry (R6.52's second check names it).
    /// </summary>
    [Fact]
    public void R6_69_SL4_AWrongStoredLeafMakesEveryOtherProofFailAndTheRowsOwnServeALeafThatIsNotItsRows()
    {
        var honest = Honest(8);
        var signedRoot = Require(ActaLog.FoldRecomputed(honest)).Root;
        var served = Require(ActaLog.Fold(WithStoredLeaf(honest, 3, WrongLeaf)));

        for (var i = 0; i < honest.Count; i++)
        {
            var proof = served.Inclusion(i, honest.Count)!;
            var leafTheClientRecomputes = Require(LogLeaf.Hash(honest[i]));
            var verifies = MerkleTree.VerifyInclusion(leafTheClientRecomputes.AsSpan(), i, honest.Count, proof.AuditPath, signedRoot.AsSpan());
            if (i == 3)
                Assert.NotEqual(Convert.ToHexString([.. leafTheClientRecomputes]), Convert.ToHexString([.. proof.LeafHash]));
            else
                Assert.False(verifies, $"the proof for index {i} verified against the signed root although a stored leaf of the log is wrong");
        }
    }

    [Fact]
    public void R6_69_SL4_Control_WithNoWrongLeafEveryProofVerifiesAgainstTheSignedRoot()
    {
        var honest = Honest(8);
        var signedRoot = Require(ActaLog.FoldRecomputed(honest)).Root;
        var served = Require(ActaLog.Fold(honest));

        for (var i = 0; i < honest.Count; i++)
        {
            var proof = served.Inclusion(i, honest.Count)!;
            Assert.True(MerkleTree.VerifyInclusion(Require(LogLeaf.Hash(honest[i])).AsSpan(), i, honest.Count, proof.AuditPath, signedRoot.AsSpan()));
        }
    }

    [Fact]
    public void R6_69_SL5_TheRecomputingFoldIsNotMovedByAWrongStoredLeaf()
    {
        var honest = Honest(8);

        var recomputed = Require(ActaLog.FoldRecomputed(WithStoredLeaf(honest, 3, WrongLeaf)));

        Assert.Equal(Convert.ToHexString([.. Require(ActaLog.FoldRecomputed(honest)).Root]), Convert.ToHexString([.. recomputed.Root]));
    }

    [Fact]
    public void R6_69_SL6_ARowWithNoLeafIsReportedAsMissingServedByRecomputationAndCounted()
    {
        var honest = Honest(8);
        var log = WithStoredLeaf(WithStoredLeaf(honest, 2, null), 5, null);

        var findings = Require(StoredLeafAudit.Check(log));
        var served = Require(ActaLog.Fold(log));

        Assert.Equal([2L, 5L], findings.Missing);
        Assert.Empty(findings.Mismatched);
        Assert.Equal(2, served.LeavesRecomputed);
        Assert.Equal(Convert.ToHexString([.. Require(ActaLog.FoldRecomputed(honest)).Root]), Convert.ToHexString([.. served.Root]));
    }

    [Fact]
    public void R6_69_SL7_AnHonestLogHasNoFindings()
    {
        var findings = Require(StoredLeafAudit.Check(Honest(8)));

        Assert.Empty(findings.Mismatched);
        Assert.Empty(findings.Missing);
    }

    [Fact]
    public void R6_69_SL8_AStoredLeafThatIsNotTheWireFormIsAMismatchAndIsServedByRecomputation()
    {
        var log = WithStoredLeaf(Honest(4), 1, "not-a-hash");

        Assert.Equal([1L], Require(StoredLeafAudit.Check(log)).Mismatched);
        Assert.Equal(1, Require(ActaLog.Fold(log)).LeavesRecomputed);
    }
}
