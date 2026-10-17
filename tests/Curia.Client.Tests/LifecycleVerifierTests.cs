using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Client;
using Curia.Client.Cli;
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
[Collection("console")]
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
    /// R6.57: <c>curia verify</c> exits 9 on a disputed post, through the function the verb ends in
    /// (<c>Program.ReportVerification</c>), with the <c>DISPUTED.</c> line and the lifecycle line on stdout. Dropping
    /// <c>acta.Overall</c> from the exit, or the <c>DISPUTED.</c> line, turns it red (F2-54, F2-55).
    /// </summary>
    [Fact]
    public async Task R6_57_CuriaVerifyExitsNineOnADisputedPost()
    {
        var (result, _) = await VerifyCaseAsync("compromised-before-post");
        Assert.Equal(CheckOutcome.Disputed, result.Overall);

        var (exit, stdout, _) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportVerification(HeldSignature, HeldTestis, result)));

        Assert.Equal(9, exit);
        Assert.Contains("DISPUTED. The signature holds", stdout, StringComparison.Ordinal);
        Assert.Contains("lifecycle   ", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The controls beside the exit-nine fact, through the same function: a lifecycle that folds exits 0 and prints no
    /// <c>DISPUTED.</c>; a failed one exits 6 and prints none either, since a failure outranks disputed.
    /// </summary>
    [Theory]
    [InlineData("rotation-then-post", CheckOutcome.Verified, 0)]
    [InlineData("compromise-kids-omits-cascade", CheckOutcome.Failed, 6)]
    public async Task R6_57_CuriaVerifyExitsZeroOnAVerifiedPostAndSixOnAFailedOne(string name, CheckOutcome overall, int expectedExit)
    {
        var (result, _) = await VerifyCaseAsync(name);
        Assert.Equal(overall, result.Overall);

        var (exit, stdout, _) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.ReportVerification(HeldSignature, HeldTestis, result)));

        Assert.Equal(expectedExit, exit);
        Assert.DoesNotContain("DISPUTED.", stdout, StringComparison.Ordinal);
        Assert.Contains("lifecycle   ", stdout, StringComparison.Ordinal);
    }

    private static SignatureVerdict HeldSignature => new(true, "alice-1", "held");

    private static TestisResult HeldTestis => new(CheckOutcome.Verified, "held");

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

    // The declaration at 6 and the compromise stated from 3: the window is 3 <= index < 6, and the boundary pairs are 2/3 and 5/6.
    private const long Declaration = 6;
    private const long From = 3;

    private static async Task<(ProvenancePost Post, ImmutableArray<ForumJwk> Keys)> ServedAsync(StubLog log)
    {
        var client = log.Client();
        var posted = await client.GetPostAsync(StubLog.PostId, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(posted.TryGetValue(out var post, out var postRefusal), postRefusal?.Summary);
        var served = await client.GetJwksAsync(StubLog.Author, TestContext.Current.CancellationToken);
        Assert.True(served.TryGetValue(out var keys, out var keysRefusal), keysRefusal?.Summary);
        return (post!, keys);
    }

    private static Passage ReadAt(ProvenancePost post, ImmutableArray<ForumJwk> keys, long? index, long? declaration = Declaration, long? from = From)
    {
        var at = post with { LogIndex = index };
        var compromised = keys.Select(k => k with { Status = "compromised", CompromisedLogIndex = declaration, DisputedFrom = from }).ToImmutableArray();
        return new Passage(at, SignatureCheck.Verify(at, compromised));
    }

    /// <summary>
    /// R6.57: a reference reader reports a post whose log index the key set puts in the disputed window as disputed,
    /// distinctly from verified and from failed. At <c>from</c> and at <c>declaration - 1</c> (F2-49, F2-51, F2-53).
    /// </summary>
    [Theory]
    [InlineData(From)]
    [InlineData(Declaration - 1)]
    public async Task R6_57_ReadReportsAPostInTheWindowDisputedNotVerified(long index)
    {
        using var log = new StubLog();
        var (post, keys) = await ServedAsync(log);

        var passage = ReadAt(post, keys, index);
        var rendered = passage.Render();

        Assert.Equal(CheckOutcome.Disputed, passage.Outcome);
        Assert.Contains("signature DISPUTED:", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("verified locally", rendered, StringComparison.Ordinal);
        Assert.Equal(9, ExitCode.ForOutcomes(passage.Outcome));
    }

    /// <summary>R6.57: content below <c>from</c> stands. A post one before it is verified, and the key line is still printed.</summary>
    [Fact]
    public async Task R6_57_ReadLeavesAPostBelowTheCompromiseStanding()
    {
        using var log = new StubLog();
        var (post, keys) = await ServedAsync(log);

        var passage = ReadAt(post, keys, From - 1);
        var rendered = passage.Render();

        Assert.Equal(CheckOutcome.Verified, passage.Outcome);
        Assert.Contains("verified locally", rendered, StringComparison.Ordinal);
        Assert.Equal(0, ExitCode.ForOutcomes(passage.Outcome));
        Assert.Contains(
            "key       \"alice-1\" compromised at log index 6, content disputed from log index 3 (as the Forum's key set says)",
            rendered, StringComparison.Ordinal);
    }

    /// <summary>
    /// R6.57: a compromised key is honoured for nothing at or after the declaration. A post at <c>declaration</c> or after it
    /// reads as failed (exit 6), on the key set alone. The pair <c>declaration - 1</c> / <c>declaration</c> is the boundary (F2-50).
    /// </summary>
    [Theory]
    [InlineData(Declaration)]
    [InlineData(Declaration + 1)]
    public async Task R6_57_ReadFailsAPostAtOrAfterTheDeclaration(long index)
    {
        using var log = new StubLog();
        var (post, keys) = await ServedAsync(log);

        var passage = ReadAt(post, keys, index);

        Assert.Equal(CheckOutcome.Failed, passage.Outcome);
        Assert.Contains("signature NOT VERIFIED:", passage.Render(), StringComparison.Ordinal);
        Assert.DoesNotContain("verified locally", passage.Render(), StringComparison.Ordinal);
        Assert.Equal(6, ExitCode.ForOutcomes(passage.Outcome));
    }

    /// <summary>
    /// R6.57, the titled index: a disputed title is never shown. The in-window post prints <c>signature DISPUTED</c>, the
    /// no-title line and none of the title; the control, one below the window, prints the title under <c>signature verified</c> (F2-49, F2-52).
    /// </summary>
    [Fact]
    public async Task R6_57_TheTitledIndexNeverPrintsADisputedTitle()
    {
        using var log = new StubLog();
        var (post, keys) = await ServedAsync(log);
        const string title = "t";
        Assert.Equal(title, ReadAt(post, keys, From - 1).Envelope?.Title);

        string Index(Passage passage)
        {
            var frame = new FrameBuilder();
            TitledIndex.Write(frame, 1, passage, MarkingMode.None, null);
            return frame.ToString();
        }

        var disputed = Index(ReadAt(post, keys, From));
        Assert.Contains("signature DISPUTED", disputed, StringComparison.Ordinal);
        Assert.Contains("(no title: the key that signed it was declared compromised from before this post, so it is disputed, R6.57;", disputed, StringComparison.Ordinal);
        Assert.DoesNotContain("<<<", disputed, StringComparison.Ordinal);
        Assert.DoesNotContain("signature verified", disputed, StringComparison.Ordinal);
        Assert.Null(TitledIndex.Title(ReadAt(post, keys, From)));

        var control = Index(ReadAt(post, keys, From - 1));
        Assert.Contains("signature verified", control, StringComparison.Ordinal);
        Assert.Equal(title, TitledIndex.Title(ReadAt(post, keys, From - 1)));
        Assert.DoesNotContain("(no title", control, StringComparison.Ordinal);
    }

    /// <summary>
    /// R6.57: <c>curia verify</c> derives disputed from the log alone. The three-valued <c>SignatureVerdict.Outcome</c> stays
    /// Verified over every index in and beyond the window, at <c>declaration</c> and after it as well: the read path's
    /// disputed and failed are <c>ReadOutcome</c>'s.
    /// </summary>
    [Theory]
    [InlineData(From)]
    [InlineData(Declaration - 1)]
    [InlineData(Declaration)]
    [InlineData(Declaration + 1)]
    public async Task R6_57_VerifyDoesNotDeriveDisputedFromTheKeySet(long index)
    {
        using var log = new StubLog();
        var (post, keys) = await ServedAsync(log);

        var passage = ReadAt(post, keys, index);

        Assert.Equal(CheckOutcome.Verified, passage.Verdict.Outcome);
        Assert.True(passage.Verdict.Verified);
    }

    /// <summary>
    /// R6.57: a Forum that omits the post's log index, or one of the key set's two members, is not caught on the read path
    /// (the Forum's word); the passage reads verified, as it did. <c>curia verify</c> is what catches it, from the log.
    /// </summary>
    [Theory]
    [InlineData(null, Declaration, From)]
    [InlineData(Declaration, Declaration, null)]
    [InlineData(From, null, From)]
    public async Task R6_57_ReadWithoutAnIndexOrAMemberStaysVerified(long? index, long? declaration, long? from)
    {
        using var log = new StubLog();
        var (post, keys) = await ServedAsync(log);

        var passage = ReadAt(post, keys, index, declaration, from);

        Assert.Equal(CheckOutcome.Verified, passage.Outcome);
        Assert.Contains("verified locally", passage.Render(), StringComparison.Ordinal);
    }

    /// <summary>
    /// R6.57 on the CLI's read path: <c>read</c>/<c>thread</c> (<c>RenderAsync</c>) and <c>board --titles</c>/<c>search --titles</c>
    /// (<c>TitledAsync</c>) exit 9 for a post the Forum's key set puts in the window, and print neither as verified.
    /// </summary>
    [Fact]
    public async Task R6_57_TheReadVerbsExitNineOnADisputedPost()
    {
        var ct = TestContext.Current.CancellationToken;
        using var log = new StubLog { KeyCompromised = (4, 1) };
        var client = log.Client();
        var posted = await client.GetPostAsync(StubLog.PostId, MarkingMode.None, ct);
        Assert.True(posted.TryGetValue(out var post, out var refusal), refusal?.Summary);
        Assert.Equal(2, post!.LogIndex);

        var (readExit, readOut, _) = await ConsoleCapture.CaptureAsync(() => Program.RenderAsync(client, [post], StubLog.Forum, null, ct));
        Assert.Equal(9, readExit);
        Assert.Contains("signature DISPUTED:", readOut, StringComparison.Ordinal);
        Assert.DoesNotContain("verified locally", readOut, StringComparison.Ordinal);

        var (frame, titledExit) = await Program.TitledAsync(client, [post], MarkingMode.None, [null], ct);
        Assert.Equal(9, titledExit);
        Assert.Contains("signature DISPUTED", frame.ToString(), StringComparison.Ordinal);
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
