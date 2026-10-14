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
