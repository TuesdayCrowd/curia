using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Acta;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// <see cref="ActaCheck"/>'s predicates, called directly.
///
/// <para><b>Why directly and not only through <see cref="PostVerifier"/>.</b> A falsification run
/// found a branch the orchestrator can never reach: it re-requests a proof against the head's own
/// size, so <see cref="ActaCheck.HeadCovers"/>'s size comparison is dead from that direction and
/// deleting it left every test green. <see cref="ActaCheck"/> is public API and its predicates are
/// documented as usable on their own, so the choice was to test the branch or delete it. An
/// untested branch in a verifier is the worse of the two.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ActaCheckTests : IDisposable
{
    private readonly StubLog _log = new(treeSize: 8, postIndex: 3);

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A head that covers exactly the size a proof climbed to, over the same root.</summary>
    [Fact]
    public void R6_49_AHeadCoversTheSizeAndRootAProofClimbedTo()
    {
        var head = Head();

        var covers = ActaCheck.HeadCovers(head, head.TreeSize, head.RootHash!);

        Assert.Equal(CheckOutcome.Verified, covers.Outcome);
    }

    /// <summary>
    /// A head for a different size does not cover this proof, whatever its root says. Unreachable
    /// through <see cref="PostVerifier"/> by construction, and a real possibility for any other
    /// caller of this predicate.
    /// </summary>
    [Fact]
    public void R6_49_AHeadForADifferentSizeDoesNotCoverAProof()
    {
        var head = Head();

        var covers = ActaCheck.HeadCovers(head, head.TreeSize - 1, head.RootHash!);

        Assert.Equal(CheckOutcome.Failed, covers.Outcome);
        Assert.Contains("leaves and the proof is against", covers.Detail, StringComparison.Ordinal);
    }

    /// <summary>A head over a different root does not cover it either, at any size.</summary>
    [Fact]
    public void R6_49_AHeadOverADifferentRootDoesNotCoverAProof()
    {
        var head = Head();
        var elsewhere = LogEntries.Prefixed(_log.RootAt(2));

        Assert.NotEqual(head.RootHash, elsewhere);

        var covers = ActaCheck.HeadCovers(head, head.TreeSize, elsewhere);

        Assert.Equal(CheckOutcome.Failed, covers.Outcome);
    }

    /// <summary>
    /// A consistency proof whose stated roots are not the two heads' roots is refused before its
    /// arithmetic is consulted.
    ///
    /// <para>Otherwise a Forum that had equivocated could hand over a self-consistent proof between
    /// two forks it invented: the nodes would verify perfectly, and about neither head the client
    /// holds. The roots have to come from the heads.</para>
    /// </summary>
    [Fact]
    public void R6_23_AProofStatingRootsTheHeadsDoNotHoldIsRefused()
    {
        var from = Head(4);
        var to = Head(8);

        var honest = Consistency(4, 8);
        Assert.Equal(CheckOutcome.Verified, ActaCheck.Consistency(from, to, honest).Outcome);

        var forged = honest with { FromRoot = LogEntries.Prefixed(_log.RootAt(2)) };

        var check = ActaCheck.Consistency(from, to, forged);

        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("from_root", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// The <c>to_root</c> half of the same guard, which nothing reached.
    ///
    /// <para>Both roots have to come from the two heads rather than from the proof, and the
    /// <c>from_root</c> half already had a test while this one did not — so a mutant deleting it
    /// survived the whole suite. A Forum that had equivocated could otherwise hand over a proof
    /// self-consistent between two trees it invented, whose nodes verify perfectly and whose
    /// endpoints are about neither head the client holds.</para>
    /// </summary>
    [Fact]
    public void R6_23_AProofStatingATargetRootTheHeadDoesNotHoldIsRefused()
    {
        var from = Head(4);
        var to = Head(8);

        var forged = Consistency(4, 8) with { ToRoot = LogEntries.Prefixed(_log.RootAt(2)) };

        var check = ActaCheck.Consistency(from, to, forged);

        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("to_root", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A proof between sizes the two heads do not name is refused. A proof is about two specific
    /// trees, and one about some other pair says nothing about these.
    /// </summary>
    [Fact]
    public void R6_23_AProofBetweenOtherSizesIsRefused()
    {
        var check = ActaCheck.Consistency(Head(4), Head(8), Consistency(2, 8));

        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("the proof runs", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>An audit path that does not carry the leaf to the root fails, and says so.</summary>
    [Fact]
    public void R6_48_AnAuditPathWithATamperedNodeFails()
    {
        var entry = Entry();
        var proof = Proof();
        var post = Post();

        Assert.Equal(CheckOutcome.Verified, ActaCheck.Inclusion(entry, proof, post).Outcome);

        var tampered = proof with
        {
            AuditPath = proof.AuditPath.SetItem(0, LogEntries.Prefixed(_log.RootAt(2))),
        };

        var check = ActaCheck.Inclusion(entry, tampered, post);

        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("does not carry leaf", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>An audit path node that is not a digest at all is a malformed proof, named as one.</summary>
    [Fact]
    public void R6_48_AnAuditPathNodeThatIsNotADigestIsRefused()
    {
        var proof = Proof() with { AuditPath = Proof().AuditPath.SetItem(0, "sha256:not-hex") };

        var check = ActaCheck.Inclusion(Entry(), proof, Post());

        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("not a sha256 digest", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>An entry for a different leaf than the proof is about is refused before any hashing.</summary>
    [Fact]
    public void R6_48_AnEntryForADifferentLeafThanTheProofIsRefused()
    {
        var check = ActaCheck.Inclusion(Entry() with { LogIndex = 0 }, Proof(), Post());

        Assert.Equal(CheckOutcome.Failed, check.Outcome);
        Assert.Contains("the entry is leaf 0", check.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every document below is fetched through <see cref="ForumClient"/>'s public log methods and
    /// parsed by the client's own readers, rather than constructed here. A document assembled in a
    /// test is a shape the wire may never carry, and this project has already shipped one covering
    /// test whose fixture could not have failed.
    /// </summary>
    private SignedHeadDocument Head(int? treeSize = null)
    {
        var previous = _log.HeadTreeSize;
        if (treeSize is { } size) _log.HeadTreeSize = size;
        try
        {
            return Read(_log.Client().GetLogHeadAsync(Ct));
        }
        finally
        {
            _log.HeadTreeSize = previous;
        }
    }

    private ConsistencyProofDocument Consistency(int from, int to) =>
        Read(_log.Client().GetConsistencyProofAsync(from, to, Ct));

    private InclusionProofDocument Proof() =>
        Read(_log.Client().GetInclusionProofAsync(_log.PostIndex, _log.HeadTreeSize, Ct));

    private LogEntryDocument Entry() =>
        Read(_log.Client().GetLogEntryAsync(_log.PostIndex, Ct));

    private ProvenancePost Post() =>
        Read(_log.Client().GetPostAsync(StubLog.PostId, Domain.Serving.MarkingMode.None, Ct));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Read<T>(Task<ForumResult<T>> fetch)
    {
        var result = fetch.GetAwaiter().GetResult();
        Assert.True(result.TryGetValue(out var value, out var refusal), refusal?.Summary);
        return value!;
    }

}
