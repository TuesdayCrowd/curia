using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.54 (add.), through <see cref="PostVerifier"/>: the reader fetches the author's lifecycle itself and folds it, so
/// what a Forum lists, omits or withholds is what these facts meet. The cases are <c>conformance/lifecycle/</c>'s,
/// served by <see cref="LifecycleCaseForum"/>; <see cref="LifecycleConformanceTests"/> hands the same documents to
/// the check directly.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LifecycleVerifierTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-lifecycle-heads-").FullName;
    private readonly List<LifecycleCaseForum> _forums = [];

    public void Dispose()
    {
        foreach (var forum in _forums) forum.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string CaseDirectory(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "conformance", "lifecycle", name);
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"conformance/lifecycle/{name} is not above {AppContext.BaseDirectory}");
    }

    private LifecycleCaseForum ForumOf(string name)
    {
        var forum = new LifecycleCaseForum(CaseDirectory(name));
        _forums.Add(forum);
        return forum;
    }

    private async Task<(PostVerification Result, LifecycleCaseForum Forum)> VerifyCaseAsync(string name)
    {
        var forum = ForumOf(name);
        var verifier = new PostVerifier(forum.Client(), new HeadStore(_home));
        return (await verifier.VerifyAsync(forum.Post(), TestContext.Current.CancellationToken), forum);
    }

    /// <summary>The intact case, asserted first: a Forum double that did not verify would report every fall below as caught.</summary>
    [Fact]
    public async Task R6_54_ALifecycleThatFoldsVerifiesOnTheFifthLine()
    {
        var (result, _) = await VerifyCaseAsync("rotation-then-post");

        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.Inclusion.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.KeyBinding.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.Lifecycle.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.Overall);
        Assert.Contains("lifecycle   verified: ", result.Render(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The omission the next link catches. The Forum leaves the rotation at 2, the entry immediately before the post,
    /// out of the lifecycle it lists; the retirement after the post links from 2 and the entry before it in the list
    /// is 1. Through the verifier and a Forum, never the check alone: it is the fetching that stops at the first entry
    /// after the post, and a fetch that stopped at the post would never meet the link.
    /// </summary>
    [Fact]
    public async Task R6_54_AnEntryOmittedImmediatelyBeforeThePostIsCaughtByTheNextLink()
    {
        var (result, forum) = await VerifyCaseAsync("link-after-post-skips");

        Assert.Equal(CheckOutcome.Failed, result.Lifecycle.Outcome);
        Assert.Contains("lifecycle entry 4 names 2 as the entry before it, and the entry before it is 1", result.Lifecycle.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Failed, result.Overall);

        // The entry after the post was fetched: the fold met it.
        Assert.Contains("/v1/log/entries/4", forum.Requests);
    }

    [Fact]
    public async Task R6_54_APostAfterItsKeysRetirementIsNotVerifiedOverall()
    {
        var (result, _) = await VerifyCaseAsync("post-after-retirement");

        // Every other check passes: the key set the double serves still calls the key active, and the post is intact.
        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.Inclusion.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.KeyBinding.Outcome);

        Assert.Equal(CheckOutcome.Failed, result.Lifecycle.Outcome);
        Assert.Contains("was retired at log index 2, before the post at 3", result.Lifecycle.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Failed, result.Overall);
        Assert.Equal(Curia.Client.Cli.ExitCode.Unverified, Curia.Client.Cli.ExitCode.ForOutcomes(result.Overall));
        Assert.Contains("\nFAILED. ", result.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R6_54_ARotationTheAuthorDidNotSignFailsInBothReaders()
    {
        var (result, _) = await VerifyCaseAsync("forged-rotation");

        Assert.Equal(CheckOutcome.Failed, result.Lifecycle.Outcome);
        Assert.Contains("is not signed by a key that was live there", result.Lifecycle.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.Failed, result.Overall);

        // The other reader's half is rust/curia-testis/tests/lifecycle.rs, which holds the same case to the same predicate.
        var (verdict, predicate) = LifecycleConformanceTests.Judge("forged-rotation");
        Assert.Equal(("failed", "rotation-unauthorized"), (verdict, predicate));
    }

    [Fact]
    public async Task R6_54_NoLifecycleTheForumListsIsCouldNotBeChecked()
    {
        var (result, forum) = await VerifyCaseAsync("no-lifecycle-supplied");

        Assert.Empty(forum.Lifecycle());
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Lifecycle.Outcome);
        Assert.Contains("lists no lifecycle for the author", result.Lifecycle.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.CouldNotCheck, result.Overall);
    }

    [Fact]
    public async Task R6_54_AnIdentityEnrolledBeforeG16WhoseBindingFollowsItsPostIsNotEstablishedNotBroken()
    {
        var (result, _) = await VerifyCaseAsync("pre-g16-first-binding-after-post");

        Assert.Equal(CheckOutcome.CouldNotCheck, result.Lifecycle.Outcome);
        Assert.Contains("which is not before this post at 1", result.Lifecycle.Detail, StringComparison.Ordinal);
        Assert.NotEqual(CheckOutcome.Failed, result.Lifecycle.Outcome);
    }

    /// <summary>
    /// More than 1,024 lifecycle entries before the post is not a lifecycle this reader will fold, and none of them is
    /// fetched: a Forum cannot make a reader fetch without bound.
    /// </summary>
    [Fact]
    public async Task R6_54_ALifecycleOfMoreThan1024EntriesBeforeThePostCouldNotBeChecked()
    {
        using var log = new StubLog(treeSize: 1105, postIndex: 1100, keyIndex: 1);
        log.ServedLifecycle = [.. Enumerable.Range(0, 1030).Select(i => (long)i)];
        var requestsBefore = log.Requests.Count;

        var verifier = new PostVerifier(log.Client(), new HeadStore(_home));
        var result = await verifier.VerifyAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);

        Assert.Equal(CheckOutcome.CouldNotCheck, verification!.Lifecycle.Outcome);
        Assert.Contains("more than 1,024 entries before the post", verification.Lifecycle.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckOutcome.CouldNotCheck, verification.Overall);

        // Not one of the lifecycle entries was asked for: only the post's own and its key's.
        var entries = log.Requests.Skip(requestsBefore).Where(r => r.Contains("/v1/log/entries/", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(entries, r => r.EndsWith("/v1/log/entries/5", StringComparison.Ordinal));
        Assert.DoesNotContain(entries, r => r.EndsWith("/v1/log/entries/500", StringComparison.Ordinal));
    }

    /// <summary>Spec section 4.8: the bound is a literal, 1,024, and not one a refactor may move.</summary>
    [Fact]
    public void R6_54_TheBoundOnEntriesBeforeAPostIs1024() =>
        Assert.Equal(1024, LifecycleCheck.MaxEntriesBeforePost);

    /// <summary>
    /// Exactly 1,024 entries before the post is within the bound: the reader fetches them and folds, and whatever it
    /// concludes is not that the lifecycle was too long.
    /// </summary>
    [Fact]
    public async Task R6_54_ALifecycleOfExactly1024EntriesBeforeThePostIsFetched()
    {
        using var log = new StubLog(treeSize: 1105, postIndex: 1100, keyIndex: 1);
        log.ServedLifecycle = [.. Enumerable.Range(0, LifecycleCheck.MaxEntriesBeforePost).Select(i => (long)i)];
        var requestsBefore = log.Requests.Count;

        var verifier = new PostVerifier(log.Client(), new HeadStore(_home));
        var result = await verifier.VerifyAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);

        Assert.DoesNotContain("more than 1,024 entries", verification!.Lifecycle.Detail, StringComparison.Ordinal);
        var entries = log.Requests.Skip(requestsBefore).Where(r => r.Contains("/v1/log/entries/", StringComparison.Ordinal)).ToList();
        Assert.Contains(entries, r => r.EndsWith("/v1/log/entries/500", StringComparison.Ordinal));
    }

    /// <summary>One more than 1,024 before the post is refused outright, and the log is asked for none of the lifecycle.</summary>
    [Fact]
    public async Task R6_54_ALifecycleOf1025EntriesBeforeThePostIsRefusedWithoutAFetch()
    {
        using var log = new StubLog(treeSize: 1105, postIndex: 1100, keyIndex: 1);
        log.ServedLifecycle = [.. Enumerable.Range(0, LifecycleCheck.MaxEntriesBeforePost + 1).Select(i => (long)i)];
        var requestsBefore = log.Requests.Count;

        var verifier = new PostVerifier(log.Client(), new HeadStore(_home));
        var result = await verifier.VerifyAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(result.TryGetValue(out var verification, out var refusal), refusal?.Summary);

        Assert.Equal(CheckOutcome.CouldNotCheck, verification!.Lifecycle.Outcome);
        Assert.Contains("more than 1,024 entries before the post", verification.Lifecycle.Detail, StringComparison.Ordinal);

        // The stub's own leaves 0 and 1 are the enrolment and the key's binding, which the key-binding check reads;
        // the lifecycle fetch is every other entry and every proof below the post.
        var lifecycleFetches = log.Requests.Skip(requestsBefore)
            .Where(r => r.Contains("/v1/log/entries/", StringComparison.Ordinal) || r.Contains("/v1/log/proof/", StringComparison.Ordinal))
            .Where(r => long.TryParse(r[(r.LastIndexOf('/') + 1)..].Split('?')[0], out var i) && i is >= 2 and < 1100)
            .ToList();
        Assert.Empty(lifecycleFetches);
    }

    /// <summary>R6.54 (add.), the read path: the Forum's key set says a key was retired, and a passage after it says so as the Forum's word.</summary>
    [Fact]
    public async Task R6_54_ARetiredKeysLineOnReadIsTheForumsWord()
    {
        using var log = new StubLog();
        var client = log.Client();
        var posted = await client.GetPostAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(posted.TryGetValue(out var post, out var postRefusal), postRefusal?.Summary);
        var served = await client.GetJwksAsync(StubLog.Author, TestContext.Current.CancellationToken);
        Assert.True(served.TryGetValue(out var keys, out var keysRefusal), keysRefusal?.Summary);

        // The key was retired at leaf 1; the post is at leaf 2.
        var retired = keys.Select(k => k with { Status = "retired", RetiredLogIndex = 1 }).ToImmutableArray();
        var after = new Passage(post!, SignatureCheck.Verify(post!, retired)).Render();

        Assert.Contains("key       \"alice-1\" retired at log index 1 (as the Forum's key set says)", after, StringComparison.Ordinal);
        Assert.Contains("signature NOT VERIFIED: signed under a key the Forum's key set says was retired at log index 1, before this post at 2", after, StringComparison.Ordinal);

        // A retirement after the post leaves the signature as it is and still says what the Forum says.
        var later = keys.Select(k => k with { Status = "retired", RetiredLogIndex = 3 }).ToImmutableArray();
        var before = new Passage(post!, SignatureCheck.Verify(post!, later)).Render();
        Assert.Contains("key       \"alice-1\" retired at log index 3 (as the Forum's key set says)", before, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT VERIFIED", before, StringComparison.Ordinal);

        // An active key prints no marker.
        var active = new Passage(post!, SignatureCheck.Verify(post!, keys)).Render();
        Assert.DoesNotContain("\nkey       ", active, StringComparison.Ordinal);
    }

    // ---- R6.57: disputed ---------------------------------------------------------------------------

    /// <summary>
    /// R6.57: the signature holds, the post is in the log, the key was bound before it, and the operator declared that key
    /// compromised from a log index at or below the post's in a declaration that follows it. Every other line is verified
    /// and the lifecycle line is DISPUTED: neither verified nor failed, and the summary is DISPUTED.
    /// </summary>
    [Fact]
    public async Task R6_57_APostWhoseKeyWasDeclaredCompromisedFromBeforeItIsDisputed()
    {
        var (result, _) = await VerifyCaseAsync("compromised-before-post");

        Assert.Equal(CheckOutcome.Verified, result.Signature.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.Inclusion.Outcome);
        Assert.Equal(CheckOutcome.Verified, result.KeyBinding.Outcome);
        Assert.Equal(CheckOutcome.Disputed, result.Lifecycle.Outcome);
        Assert.Equal(CheckOutcome.Disputed, result.Overall);
        Assert.Contains("lifecycle   DISPUTED: ", result.Render(), StringComparison.Ordinal);
        Assert.Contains("declared compromised from log index 1 by the declaration at log index 4", result.Lifecycle.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// R6.57: disputed is never printed as verified, in the summary or on the lifecycle line, and nothing that reads
    /// the rendering for the word VERIFIED finds it (F2-4: <c>Overall</c> mapping Disputed to Verified).
    /// </summary>
    [Fact]
    public async Task R6_57_DisputedIsNeverPrintedAsVerified()
    {
        var (result, _) = await VerifyCaseAsync("compromised-before-post");
        var rendered = result.Render();

        Assert.NotEqual(CheckOutcome.Verified, result.Overall);
        Assert.Contains("\nDISPUTED. ", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("VERIFIED.", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("lifecycle   verified", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// R6.57: <c>curia verify</c> exits 9 on a disputed post, and only when nothing failed (6) and nothing could not be
    /// checked (8): those outrank it. The code is its own, and the help names it.
    /// </summary>
    [Fact]
    public async Task R6_57_CuriaVerifyExitsNineOnADisputedPost()
    {
        var (result, _) = await VerifyCaseAsync("compromised-before-post");

        Assert.Equal(9, Curia.Client.Cli.ExitCode.Disputed);
        Assert.Equal(Curia.Client.Cli.ExitCode.Disputed, Curia.Client.Cli.ExitCode.ForOutcomes(result.Overall));
        Assert.Equal(Curia.Client.Cli.ExitCode.Unverified, Curia.Client.Cli.ExitCode.ForOutcomes(CheckOutcome.Disputed, CheckOutcome.Failed));
        Assert.Equal(Curia.Client.Cli.ExitCode.ForumFault, Curia.Client.Cli.ExitCode.ForOutcomes(CheckOutcome.Disputed, CheckOutcome.CouldNotCheck));
        Assert.Equal(Curia.Client.Cli.ExitCode.Ok, Curia.Client.Cli.ExitCode.ForOutcomes(CheckOutcome.Verified));
    }

    /// <summary>
    /// R6.57: <c>Overall</c> ranks failed, then could not be checked, then disputed, then verified. A disputed lifecycle
    /// beside a consistency check that did not run is not established; beside a failure it is failed; beside five checks that
    /// held it is disputed.
    /// </summary>
    [Fact]
    public void R6_57_OverallRanksDisputedBelowAnAbsenceAndAFailure()
    {
        PostVerification With(Check consistency, Check lifecycle) => new(
            "p", Check.Verified("s"), Check.Verified("i"), consistency, Check.Verified("k"), lifecycle, null, 1, 2);

        var disputed = Check.Disputed("d");
        Assert.Equal(CheckOutcome.Disputed, With(Check.Verified("c"), disputed).Overall);
        Assert.Equal(CheckOutcome.CouldNotCheck, With(Check.CouldNotCheck("c"), disputed).Overall);
        Assert.Equal(CheckOutcome.Failed, With(Check.Failed("c"), disputed).Overall);
        Assert.Equal(CheckOutcome.Verified, With(Check.Verified("c"), Check.Verified("l")).Overall);
    }

    /// <summary>
    /// R6.57: a declaration follows the post it disputes, so the reader fetches the lifecycle through it. Here a rotation
    /// at 3 lies between the post (2) and the declaration (5); stopping at the first entry after the post would never reach
    /// the declaration (F2-7). Through the verifier and a Forum, never the check alone: it is the fetching that decides.
    /// </summary>
    [Fact]
    public async Task R6_57_ADeclarationBeyondTheFirstEntryAfterThePostIsFetched()
    {
        var (result, forum) = await VerifyCaseAsync("compromised-after-intervening-rotation");

        Assert.Equal(CheckOutcome.Disputed, result.Lifecycle.Outcome);
        Assert.Equal(CheckOutcome.Disputed, result.Overall);
        Assert.Contains("/v1/log/entries/3", forum.Requests);
        Assert.Contains("/v1/log/entries/5", forum.Requests);
    }

    /// <summary>
    /// R6.57: the key set names the declaration for every kid it lists, so a post under the cascaded key k2 reaches it
    /// as a post under k1 does, and is disputed.
    /// </summary>
    [Fact]
    public async Task R6_57_APostUnderACascadedKeyIsDisputedThroughTheKeySet()
    {
        var (result, forum) = await VerifyCaseAsync("compromise-post-under-cascaded-key");

        Assert.Equal(CheckOutcome.Disputed, result.Lifecycle.Outcome);
        Assert.Equal(CheckOutcome.Disputed, result.Overall);
        Assert.Contains("/v1/log/entries/5", forum.Requests);
    }

    /// <summary>R6.57: a declaration that fails KF3 never disputes: the lifecycle line is failed, and so is the summary.</summary>
    [Theory]
    [InlineData("compromise-kids-omits-cascade")]
    [InlineData("compromise-kid-of-other-identity")]
    [InlineData("compromise-from-index-at-declaration")]
    public async Task R6_57_AFailedDeclarationFailsTheLifecycleAndDisputesNothing(string name)
    {
        var (result, _) = await VerifyCaseAsync(name);

        Assert.Equal(CheckOutcome.Failed, result.Lifecycle.Outcome);
        Assert.Equal(CheckOutcome.Failed, result.Overall);
        Assert.Equal(Curia.Client.Cli.ExitCode.Unverified, Curia.Client.Cli.ExitCode.ForOutcomes(result.Overall));
    }

    /// <summary>
    /// R6.57, the read path: the Forum's key set says a key was declared compromised, and a passage says so as the Forum's
    /// word, with the declaration's index and the index the compromise is stated from. The signature line is unchanged: it
    /// is <c>curia verify</c>, from the log, that disputes a post.
    /// </summary>
    [Fact]
    public async Task R6_57_ACompromisedKeysLineOnReadIsTheForumsWord()
    {
        using var log = new StubLog();
        var client = log.Client();
        var posted = await client.GetPostAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(posted.TryGetValue(out var post, out var postRefusal), postRefusal?.Summary);
        var served = await client.GetJwksAsync(StubLog.Author, TestContext.Current.CancellationToken);
        Assert.True(served.TryGetValue(out var keys, out var keysRefusal), keysRefusal?.Summary);

        var compromised = keys.Select(k => k with { Status = "compromised", CompromisedLogIndex = 4, DisputedFrom = 1 }).ToImmutableArray();
        var rendered = new Passage(post!, SignatureCheck.Verify(post!, compromised)).Render();

        Assert.Contains(
            "key       \"alice-1\" compromised at log index 4, content disputed from log index 1 (as the Forum's key set says)",
            rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT VERIFIED", rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// R6.26 (revised), a regression guard (green before this stage): a head verifier refuses a valid log-key signature whose
    /// header names a declaration's <c>typ</c>, <c>curia-key-compromise+jws</c>, by exact <c>typ</c>. The signature is
    /// pinned beside <c>rotation-then-post</c> (<c>head-as-compromise.json</c>): the same head, by the same log key, over the
    /// same bytes; the control is the head itself, which verifies (F2-33).
    /// </summary>
    [Fact]
    public void R6_26_TheHeadVerifierRefusesACompromiseTyp()
    {
        var directory = CaseDirectory("rotation-then-post");
        var keys = LifecycleConformanceTests.ReadLogKeys(Path.Combine(directory, "log-jwks.json"));

        var control = ActaCheck.HeadSignature(LifecycleConformanceTests.ReadHead(Path.Combine(directory, "head.json")), keys);
        Assert.Equal(CheckOutcome.Verified, control.Outcome);

        var verdict = ActaCheck.HeadSignature(LifecycleConformanceTests.ReadHead(Path.Combine(directory, "head-as-compromise.json")), keys);

        Assert.Equal(CheckOutcome.Failed, verdict.Outcome);
        Assert.Contains("does not verify", verdict.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// R6.49 (a regression guard, green before this stage): a head verifier refuses a valid log-key signature whose
    /// header names a lifecycle <c>typ</c>. A head is the one statement the log key makes about itself, and a verifier
    /// that took a rotation's or a retirement's signature for one would let a statement of another kind pass as a head.
    ///
    /// <para>The signatures are pinned beside the case (<c>head-as-rotation.json</c>, <c>head-as-retirement.json</c>): the
    /// same head, by the same log key, over the same bytes. The control is the head itself, which verifies, so a refusal
    /// below is the typ's and not a key or a byte that was wrong.</para>
    /// </summary>
    [Theory]
    [InlineData("head-as-rotation.json")]
    [InlineData("head-as-retirement.json")]
    public void R6_49_TheHeadVerifierRefusesALifecycleTyp(string file)
    {
        var directory = CaseDirectory("rotation-then-post");
        var keys = LifecycleConformanceTests.ReadLogKeys(Path.Combine(directory, "log-jwks.json"));

        var control = ActaCheck.HeadSignature(LifecycleConformanceTests.ReadHead(Path.Combine(directory, "head.json")), keys);
        Assert.Equal(CheckOutcome.Verified, control.Outcome);

        var verdict = ActaCheck.HeadSignature(LifecycleConformanceTests.ReadHead(Path.Combine(directory, file)), keys);

        Assert.Equal(CheckOutcome.Failed, verdict.Outcome);
        Assert.Contains("does not verify", verdict.Detail, StringComparison.Ordinal);
    }
}
