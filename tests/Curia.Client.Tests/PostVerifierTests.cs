using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Tests.Shared;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.52, R6.53 and R11.29: the three checks, the three outcomes, and the retained head.
///
/// <para><b>Every test here asserts on which outcome, never on a boolean.</b> The requirement this
/// class exists to enforce is not "verification works" -- it is that <i>could not be checked</i> is
/// never reported as either of the other two, in either direction. A test written as
/// <c>Assert.True(verified)</c> would pass for a client that reported an unreachable host as a
/// forgery, which is the exact behaviour the tree had before this stage.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PostVerifierTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-head-store-").FullName;
    private readonly List<StubLog> _logs = [];

    public void Dispose()
    {
        foreach (var log in _logs) log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The intact case. Asserted first and in its own test because every falsification below is
    /// "this one thing changes and the verdict changes with it" -- and a suite where the intact case
    /// does not verify would report every falsification as caught while catching nothing.
    /// </summary>
    [Fact]
    public async Task R6_52_AnIntactPostVerifiesOnAllThreeChecksThatCanRun()
    {
        var result = await VerifyAsync(Log());

        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.Inclusion.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.Overall);

        // The one check that cannot have run: nothing was retained before this call.
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Consistency.Outcome);
    }

    /// <summary>
    /// The errata's own falsification for R6.52: "substitute the served <c>leaf_hash</c> for the
    /// recomputed leaf and the inclusion test must fail on an entry whose payload was altered; if it
    /// stays green, the requirement was quoted and not implemented."
    ///
    /// <para>The tree is <i>not</i> rebuilt, so the audit path still climbs to the served root and
    /// the arithmetic still verifies. Only the recomputation disagrees. A verifier that fed the
    /// served <c>leaf_hash</c> into the path check passes this test; one that recomputes fails it.</para>
    /// </summary>
    [Fact]
    public async Task R6_52_AnAlteredEntryPayloadFailsInclusionEvenThoughTheProofIsWellFormed()
    {
        var log = Log();
        log.AlterTheEntryPayload();

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Failed, result.Inclusion.Outcome);
        Assert.Contains("hashes to", result.Inclusion.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Failed, result.Overall);

        // And the signature is untouched: the alteration is in the log, not in the envelope. Two
        // separate claims, reported separately, which is the point of three lines rather than one.
        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
    }

    /// <summary>
    /// R11.29's falsification: "verify a post against an entry index the Forum chose freely and the
    /// binding test must refuse it."
    ///
    /// <para>Here the log is rebuilt around the rebound entry, so the leaf, the path and the root
    /// all agree with each other. Everything verifies except the one thing that ties any of it to
    /// the post in the reader's context.</para>
    /// </summary>
    [Fact]
    public async Task R11_29_AnEntryThatDoesNotCarryThisPostsCanonicalBytesIsRefused()
    {
        var log = Log();
        log.BindTheEntryToADifferentPost();

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Failed, result.Inclusion.Outcome);
        Assert.Contains("R11.29", result.Inclusion.Detail, StringComparison.Ordinal);
    }

    /// <summary>A proof whose <c>leaf_hash</c> is not the leaf's hash is a disagreement, and is named as one.</summary>
    [Fact]
    public async Task R6_52_AProofCarryingAWrongLeafHashFails()
    {
        var log = Log();
        log.ProofCarriesAWrongLeafHash = true;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Failed, result.Inclusion.Outcome);
    }

    /// <summary>
    /// The errata's falsification for the collapse: "map <i>could not be checked</i> onto
    /// <i>failed</i> and the unreachable-JWKS test must name the collapse; map it onto
    /// <i>verified</i> and it must name that too."
    ///
    /// <para>Both directions are refused by one equality: a client that reported this as verified
    /// and one that reported it as forged are both wrong, and a test asserting only
    /// <c>NotEqual(Verified)</c> would accept the second.</para>
    /// </summary>
    [Fact]
    public async Task R6_52_AnUnreachableKeySetIsCouldNotCheckAndIsNeitherFailedNorVerified()
    {
        var log = Log();
        log.JwksUnreachable = true;

        var result = await VerifyAsync(log);

        // One equality refuses both collapses: mapping this to Failed and mapping it to Verified
        // each fail this line, which is why it is stated as an equality rather than as two
        // inequalities that would each pass while the other was violated.
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Signature.Outcome);

        // And the sentence a reader sees says which, rather than saying the author has no such key.
        Assert.Contains("could not be fetched", result.Signature.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("no key matching", result.Signature.Detail, StringComparison.Ordinal);

        // The whole verification is then not established -- not failed. Nothing was refuted.
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Overall);
    }

    /// <summary>
    /// A log whose operator has never signed a head. Normal on a young Forum, and recurring whenever
    /// a signing schedule falls behind -- so reporting it as a failure would raise an alarm about
    /// the post every time the operator's cron did not run.
    /// </summary>
    [Fact]
    public async Task R6_49_ALogWithNoSignedHeadCannotAnchorAProofAndSaysSo()
    {
        var log = Log();
        log.NoSignedHead = true;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.CouldNotCheck, result.Inclusion.Outcome);
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Consistency.Outcome);
        Assert.Contains("sign-head", result.Inclusion.Detail, StringComparison.Ordinal);

        // The signature is unaffected: authorship does not depend on the log.
        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
    }

    /// <summary>
    /// A head whose signature does not verify is a <b>failure</b>, not an absence: something signed
    /// that head and it was not the key this log publishes, which is a claim about the material
    /// rather than about this client's reach.
    ///
    /// <para><b>This assertion said <c>CouldNotCheck</c> when it was first written</b>, while the
    /// method name said "Fails" — the orchestrator discarded the anchor's verdict and reported every
    /// unreachable anchor alike. R6.52's collapse is usually discussed in one direction, could-not-
    /// check reported as failed; this was the other, and it is the worse one, because it downgrades
    /// an attack to a network complaint. A name and an assertion that disagree are worth reading
    /// twice.</para>
    /// </summary>
    [Fact]
    public async Task R6_50_AHeadSignedByAKeyTheLogDoesNotPublishFails()
    {
        var log = Log();
        log.HeadSignedByAStranger = true;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Failed, result.Inclusion.Outcome);
        Assert.Contains("does not verify", result.Inclusion.Detail, StringComparison.Ordinal);

        // And it reaches the summary as a failure, so a caller reading one word is not told that
        // nothing went wrong.
        Assert.Equal(CheckOutcome.Failed, result.Overall);

        // The retained head is not advanced to one this client could not verify.
        Assert.Equal(CheckOutcome.Failed, result.Consistency.Outcome);
    }

    /// <summary>
    /// A head that verifies, over a root that is not this tree's.
    ///
    /// <para>The head's signature is good and the audit path climbs to a root: two checks that both
    /// pass, about two different trees. Nothing an intact Forum can produce, which is exactly why it
    /// has to be constructed -- a falsification run showed that deleting the comparison between the
    /// proof's root and the head's left the whole end-to-end suite green, because there no root ever
    /// disagreed.</para>
    /// </summary>
    [Fact]
    public async Task R6_49_AValidlySignedHeadOverADifferentRootFails()
    {
        var log = Log();
        log.HeadCommitsToTheWrongRoot = true;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Failed, result.Inclusion.Outcome);
        Assert.Contains("the signed head's root is", result.Inclusion.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Failed, result.Overall);
    }

    /// <summary>
    /// A post the latest signed head does not yet cover. The Forum serves the proof against the
    /// whole log with <c>head_signed: false</c>; a verifier that reported that as verified would be
    /// anchoring to a root nobody signed, and one that reported it as failed would raise an alarm
    /// about a post that is simply newer than the last signature.
    /// </summary>
    [Fact]
    public async Task R6_48_APostNewerThanTheSignedHeadIsNotYetCheckable()
    {
        var log = Log();
        log.HeadTreeSize = log.PostIndex;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.CouldNotCheck, result.Inclusion.Outcome);
        Assert.Contains("not covered by the signed head", result.Inclusion.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Overall);
    }

    /// <summary>
    /// R6.53's first read: nothing retained, so nothing to compare, and the head is retained for
    /// next time.
    ///
    /// <para><b>The retention is asserted, not the absence.</b> A consistency check with no cached
    /// head passes trivially -- this plan's own trap 2 -- so the assertion that carries the
    /// information is that a head is on disk afterwards, at the private mode, outside any agent
    /// directory.</para>
    /// </summary>
    [Fact]
    public async Task R6_53_TheFirstVerificationRetainsAHeadAndSaysItHadNothingToCompareAgainst()
    {
        var log = Log();
        var heads = new HeadStore(_home);

        Assert.Null(heads.Read(StubLog.Forum));

        var result = await VerifyAsync(log, heads);

        Assert.Equal(CheckOutcome.CouldNotCheck, result.Consistency.Outcome);
        Assert.Contains("retained no earlier head", result.Consistency.Detail, StringComparison.Ordinal);

        var retained = heads.Read(StubLog.Forum);
        Assert.NotNull(retained);
        Assert.Equal(log.HeadTreeSize, retained.TreeSize);
    }

    /// <summary>
    /// R6.53's second read: the log has grown, the proof from the retained head verifies, and the
    /// newer head replaces the older one.
    /// </summary>
    [Fact]
    public async Task R6_53_ASecondVerificationChecksTheLogAgainstTheRetainedHeadAndAdvancesIt()
    {
        var log = Log(treeSize: 8, postIndex: 2);
        var heads = new HeadStore(_home);

        log.HeadTreeSize = 4;
        var first = await VerifyAsync(log, heads);
        Assert.Equal(CheckOutcome.CouldNotCheck, first.Consistency.Outcome);
        Assert.Equal(4, heads.Read(StubLog.Forum)!.TreeSize);

        log.HeadTreeSize = 8;
        var second = await VerifyAsync(log, heads);

        Assert.Equal(CheckOutcome.Verified, second.Consistency.Outcome);
        Assert.Contains("extends the head this client retained", second.Consistency.Detail, StringComparison.Ordinal);
        Assert.Equal(8, heads.Read(StubLog.Forum)!.TreeSize);
        Assert.Equal(CheckOutcome.Verified, second.Overall);
    }

    /// <summary>
    /// R6.53's whole reason for existing: a Forum that serves a different root at a size this client
    /// has already seen has equivocated, and the retained head is the only evidence of it.
    ///
    /// <para><b>The retained head must survive the failure.</b> "A consistency failure SHALL be
    /// reported as a failed verification and SHALL NOT refresh the cache, because the retained head
    /// is the only evidence that the log equivocated, and refreshing on the failure is how a client
    /// that detected a fork forgets it." Both halves are asserted.</para>
    /// </summary>
    [Fact]
    public async Task R6_53_ADifferentRootAtASizeAlreadySeenIsAFailureAndDoesNotRefreshTheCache()
    {
        var log = Log();
        var heads = new HeadStore(_home);

        await VerifyAsync(log, heads);
        var retained = heads.Read(StubLog.Forum)!;

        // A second Forum on the same origin, with a different tree at the same size: the fork.
        var fork = Log();
        Assert.NotEqual(retained.RootHash, ForkRoot(fork, log.HeadTreeSize));

        var result = await VerifyAsync(fork, heads);

        Assert.Equal(CheckOutcome.Failed, result.Consistency.Outcome);
        Assert.Contains("equivocated", result.Consistency.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Failed, result.Overall);

        Assert.Equal(retained.RootHash, heads.Read(StubLog.Forum)!.RootHash);
    }

    /// <summary>
    /// R6.53: a retained head this client cannot read is left exactly where it is.
    ///
    /// <para>Corruption and absence look the same to a caller reading a null, and treating the
    /// first as the second overwrites the retained head with whatever the Forum is serving now —
    /// without the consistency proof R6.53 requires, and discarding the only record of what the log
    /// looked like before. That is "a client that detected a fork forgets it", reached by damaging
    /// the file rather than by failing the proof, and it is a cheaper attack than forging one.</para>
    /// </summary>
    [Fact]
    public async Task R6_53_AnUnreadableRetainedHeadIsNeitherTrustedNorOverwritten()
    {
        var log = Log();
        var heads = new HeadStore(_home);

        var path = Path.Combine(heads.DirectoryFor(StubLog.Forum), "head.json");
        Directory.CreateDirectory(heads.DirectoryFor(StubLog.Forum));
        await File.WriteAllTextAsync(path, "{ this is not a head", TestContext.Current.CancellationToken);

        var damaged = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);

        var result = await VerifyAsync(log, heads);

        Assert.Equal(CheckOutcome.CouldNotCheck, result.Consistency.Outcome);
        Assert.Contains("could not be read", result.Consistency.Detail, StringComparison.Ordinal);

        // The assertion that carries the information: the file is byte-for-byte what it was.
        Assert.Equal(damaged, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// The entry route and the proof route are two responses from one untrusted Forum, so the
    /// recomputed leaf is compared against <b>each</b> of them.
    ///
    /// <para>Every earlier fixture had the two agree with each other, so the comparison against the
    /// entry's own reported hash was never exercised — a guard in a verifier that no test reaches.
    /// Here only the entry route lies.</para>
    /// </summary>
    [Fact]
    public async Task R6_52_AWrongLeafHashOnTheEntryRouteAloneIsCaught()
    {
        var log = Log();
        log.EntryRouteReportsAWrongLeafHash = true;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Failed, result.Inclusion.Outcome);
        Assert.Contains("the log-entry route reported", result.Inclusion.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// A key set that arrives successfully and carries nothing is not a forged signature.
    ///
    /// <para>A 200 with <c>{"keys":[]}</c> is a different state from a fetch that failed, and both
    /// are different from a key that does not verify. The Forum is the untrusted party here, so this
    /// is the arm that fires when it answers without answering.</para>
    /// </summary>
    [Fact]
    public async Task R6_52_AnEmptyButSuccessfulKeySetIsCouldNotCheck()
    {
        var log = Log();
        log.JwksIsEmpty = true;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.CouldNotCheck, result.Signature.Outcome);
        Assert.Contains("published no keys at all", result.Signature.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Overall);
    }

    /// <summary>
    /// A head naming a <c>kid</c> the log does not publish is a failure. Rotation the Acta has not
    /// registered and outright substitution look identical from here, and R6.50 publishes every key
    /// before use precisely so that this case is refusable rather than ambiguous.
    /// </summary>
    [Fact]
    public async Task R6_50_AHeadNamingAKidTheLogDoesNotPublishFails()
    {
        var log = Log();
        log.HeadNamesAnUnpublishedKid = true;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Failed, result.Inclusion.Outcome);
        Assert.Contains("publishes no key with kid=", result.Inclusion.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Failed, result.Overall);
    }

    /// <summary>
    /// The post's own proof is against a size the signed head does not cover, so a proof against the
    /// head's size is requested and checked instead.
    ///
    /// <para>The ordinary case whenever the log has grown since the last signature, and until this
    /// test the branch was entered <b>zero</b> times: the stub tied the post's served proof size to
    /// the head's, so the two always already agreed. Established by instrumenting the branch and
    /// running every suite that touches the verifier, not by reasoning about the fixture.</para>
    /// </summary>
    [Fact]
    public async Task R6_48_AProofAgainstAnotherSizeIsReprovenAgainstTheSignedHead()
    {
        var log = Log(treeSize: 8, postIndex: 2);

        // The head covers 4; the log has grown to 8, and the post's proof is against the whole log.
        log.HeadTreeSize = 4;
        log.PostProofIsAgainstTheWholeLog = true;

        var result = await VerifyAsync(log);

        Assert.Equal(CheckOutcome.Verified, result.Inclusion.Outcome);
        Assert.Equal(4, result.AnchorTreeSize);

        // Non-vacuity: the post really did arrive with a proof against a different size, so the
        // verification above went through the re-request rather than past it.
        Assert.NotEqual(result.AnchorTreeSize, log.Leaves.Length);
    }

    /// <summary>
    /// A Forum serving an <i>older</i> head than this client retains is a lagging replica, not a
    /// rewrite — provided the log it serves still extends the retained one.
    ///
    /// <para>The proof runs in the direction it exists, from the smaller size to the larger, and the
    /// retained head stands. Nothing reached this branch before: every other test grows the log, so
    /// a mutant changing <c>&lt;</c> to <c>!=</c> at the size comparison survived the whole
    /// suite.</para>
    /// </summary>
    [Fact]
    public async Task R6_53_AnOlderHeadFromALaggingReplicaIsConsistentAndDoesNotRollTheCacheBack()
    {
        var log = Log(treeSize: 8, postIndex: 2);
        var heads = new HeadStore(_home);

        log.HeadTreeSize = 8;
        await VerifyAsync(log, heads);
        Assert.Equal(8, heads.Read(StubLog.Forum)!.TreeSize);

        // The same Forum now answers with an earlier head — a replica that has not caught up.
        log.HeadTreeSize = 4;
        var result = await VerifyAsync(log, heads);

        Assert.Equal(CheckOutcome.Verified, result.Consistency.Outcome);
        Assert.Contains("lagging replica", result.Consistency.Detail, StringComparison.Ordinal);

        // The retained head is not rolled back to the older one.
        Assert.Equal(8, heads.Read(StubLog.Forum)!.TreeSize);
    }

    /// <summary>
    /// R6.51 and R11.29: an entry is fetched as proof material, and no part of it comes back. The
    /// marker lives only in the entry's payload, so its absence from the rendering is the assertion.
    /// </summary>
    [Fact]
    public async Task R6_51_TheRenderedVerificationCarriesNoPartOfTheLogEntry()
    {
        var log = Log();
        var rendered = (await VerifyAsync(log)).Render();

        Assert.DoesNotContain(StubLog.EntryOnlyMarker, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("post.accepted", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(log.Canonical, rendered, StringComparison.Ordinal);

        // Non-vacuity: the marker really is in the material the verification handled, so its absence
        // above is a fact about the rendering rather than about an empty fixture.
        Assert.Contains(StubLog.EntryOnlyMarker, log.Canonical, StringComparison.Ordinal);
    }

    private static string ForkRoot(StubLog fork, int treeSize) =>
        Curia.Domain.Acta.LogEntries.Prefixed(fork.RootAt(treeSize));

    private StubLog Log(int treeSize = 5, int postIndex = 2)
    {
        var log = new StubLog(treeSize, postIndex);
        _logs.Add(log);
        return log;
    }

    private async Task<PostVerification> VerifyAsync(StubLog log, HeadStore? heads = null)
    {
        var verifier = new PostVerifier(log.Client(), heads ?? new HeadStore(_home));
        var result = await verifier.VerifyAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);
        return verification!;
    }
}
