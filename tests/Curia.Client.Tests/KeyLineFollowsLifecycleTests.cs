using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// LRV18 to LRV20 (errata G68, rulings 14 and 15, R11.95): a verification does not read the key line verified over a rotation the lifecycle line shows does not verify, and does not read the lifecycle
/// line verified over a mark the Forum makes past the head it holds. The logs are the reader vectors' (<c>conformance/lifecycle/</c>), served by <see cref="LifecycleCaseForum"/>; the words are
/// parsed from the errata (<see cref="PublishedClientWords"/>), never typed here.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class KeyLineFollowsLifecycleTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-keyline-heads-").FullName;
    private readonly List<LifecycleCaseForum> _forums = [];

    public void Dispose()
    {
        foreach (var forum in _forums) forum.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string CaseDirectory(string name) => Path.Combine(LifecycleConformanceTests.Root, name);

    private LifecycleCaseForum ForumOf(string directory)
    {
        var forum = new LifecycleCaseForum(directory);
        _forums.Add(forum);
        return forum;
    }

    private async Task<(PostVerification Result, ProvenancePost Post)> VerifyAsync(string directory, Func<ProvenancePost, ProvenancePost>? serve = null)
    {
        var forum = ForumOf(directory);
        var post = serve?.Invoke(forum.Post()) ?? forum.Post();
        var verifier = new PostVerifier(forum.Client(), new HeadStore(_home));
        return (await verifier.VerifyAsync(post, TestContext.Current.CancellationToken), post);
    }

    /// <summary>The log index of the case's first statement-bearing <c>agent.key-bound</c>: its rotation.</summary>
    private static long RotationLeaf(string directory) =>
        Directory.EnumerateFiles(Path.Combine(directory, "entries"), "*.json")
            .Select(file => (Index: long.Parse(Path.GetFileNameWithoutExtension(file), CultureInfo.InvariantCulture), Entry: JsonDocument.Parse(File.ReadAllBytes(file))))
            .Where(x => x.Entry.RootElement.GetProperty("entry").GetProperty("event_type").GetString() == "agent.key-bound" && x.Entry.RootElement.GetProperty("entry").GetProperty("payload").TryGetProperty("statement", out _))
            .Select(x => x.Index)
            .Order()
            .First();

    private static (long Leaf, string Kid) BindingOf(string directory, long index)
    {
        using var entry = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "entries", index.ToString(CultureInfo.InvariantCulture) + ".json")));
        return (index, entry.RootElement.GetProperty("entry").GetProperty("payload").GetProperty("kid").GetString()!);
    }

    private static string Fill(string template, params (string Name, string Value)[] values) =>
        values.Aggregate(template, (text, v) => text.Replace(v.Name, v.Value, StringComparison.Ordinal));

    /// <summary>
    /// LRV18: a forged rotation binding the attacker's kid, a post signed under it, a head covering both: the key line is Failed with R11.95's words, the lifecycle line Failed, the exit 6. Red on the seam, where the
    /// key line reads one leaf: <c>Expected: Failed  Actual: Verified</c>.
    /// </summary>
    [Fact]
    public async Task LRV18_AForgedRotationsKeyLineIsFailedWithThePublishedWords()
    {
        var (result, _) = await VerifyAsync(CaseDirectory("forged-rotation"));
        var (leaf, kid) = BindingOf(CaseDirectory("forged-rotation"), 2);
        var words = Fill(PublishedClientWords.KeyLineRotationLifecycleFailed, ("<i>", leaf.ToString(CultureInfo.InvariantCulture)), ("<kid>", kid));

        WholeText.Same(
            "key Failed; lifecycle Failed; exit 6; the key line carries the published words",
            $"key {result.KeyBinding.Outcome}; lifecycle {result.Lifecycle.Outcome}; exit {ExitCode.ForOutcomes(result.Overall)}; "
            + (PublishedClientWords.Says(result.Render(), words) ? "the key line carries the published words" : "the key line does not carry the published words: " + result.KeyBinding.Detail));
    }

    /// <summary>LRV18: a rotation whose lifecycle could not be checked (an entry the key set lists is unfetchable): the key line is CouldNotCheck, with R11.95's second words.</summary>
    [Fact]
    public async Task LRV18_ARotationWhoseLifecycleCouldNotBeCheckedHasAKeyLineThatCouldNotBeChecked()
    {
        // The post is signed under the rotation's key, and the rotation's statement carries a typ this build does not know, so the lifecycle line cannot say (unknown-lifecycle-kind).
        var directory = CaseDirectory("unknown-statement-typ");
        var (result, _) = await VerifyAsync(directory);
        var (leaf, kid) = BindingOf(directory, RotationLeaf(directory));
        var words = Fill(PublishedClientWords.KeyLineRotationLifecycleCouldNot, ("<i>", leaf.ToString(CultureInfo.InvariantCulture)), ("<kid>", kid));

        WholeText.Same(
            "key CouldNotCheck; lifecycle CouldNotCheck; the key line carries the published words",
            $"key {result.KeyBinding.Outcome}; lifecycle {result.Lifecycle.Outcome}; "
            + (PublishedClientWords.Says(result.Render(), words) ? "the key line carries the published words" : "the key line does not carry the published words: " + result.KeyBinding.Detail));
    }

    /// <summary>LRV18's control: a genuine rotation is Verified on the key line and the lifecycle line.</summary>
    [Fact]
    public async Task LRV18_AGenuineRotationsKeyLineIsVerified_Control()
    {
        var (result, _) = await VerifyAsync(CaseDirectory("rotation-then-post"));

        WholeText.Same("key Verified; lifecycle Verified", $"key {result.KeyBinding.Outcome}; lifecycle {result.Lifecycle.Outcome}");
    }

    /// <summary>LRV18's control, the root: the enrolment's own binding stays Verified when the lifecycle line fails (a post after the key's retirement), since the enrolment is the root (ruling 7).</summary>
    [Fact]
    public async Task LRV18_TheEnrolmentsOwnBindingStaysVerifiedWhenTheLifecycleLineFails_Control()
    {
        var (result, _) = await VerifyAsync(CaseDirectory("post-after-retirement"));

        WholeText.Same("key Verified; lifecycle Failed", $"key {result.KeyBinding.Outcome}; lifecycle {result.Lifecycle.Outcome}");
    }

    /// <summary>
    /// LRV19 (item 256): a served post whose <c>disputed_by_log_index</c> is at or past the head's tree size: the lifecycle line is CouldNotCheck with R11.95's third sentence naming the index and the tree size,
    /// <c>Overall</c> is CouldNotCheck, and the CLI exits 8. Red on the seam: <c>Expected: CouldNotCheck  Actual: Verified</c>.
    /// </summary>
    [Fact]
    public async Task LRV19_AMarkPastTheHeadIsCouldNotBeChecked()
    {
        var (result, _) = await VerifyAsync(CaseDirectory("rotation-then-post"), post => post with { Provenance = post.Provenance with { KeyStatus = "compromised", DisputedByLogIndex = 6 } });
        var words = Fill(PublishedClientWords.LifecycleMarkPastHead, ("<d>", "6"), ("<n>", "5"));

        WholeText.Same(
            "lifecycle CouldNotCheck; overall CouldNotCheck; exit 8; the published words",
            $"lifecycle {result.Lifecycle.Outcome}; overall {result.Overall}; exit {ExitCode.ForOutcomes(result.Overall)}; "
            + (PublishedClientWords.Says(result.Render(), words) ? "the published words" : "not the published words: " + result.Lifecycle.Detail));
    }

    /// <summary>LRV19's controls: a mark below the head with the chain showing the declaration is Disputed as before; no mark is Verified.</summary>
    [Fact]
    public async Task LRV19_AMarkInsideTheHeadAndNoMarkAreAsBefore_Control()
    {
        var (inside, _) = await VerifyAsync(CaseDirectory("compromised-before-post"), post => post with { Provenance = post.Provenance with { KeyStatus = "compromised", DisputedByLogIndex = 4 } });
        var (none, _) = await VerifyAsync(CaseDirectory("rotation-then-post"));

        WholeText.Same("mark inside the head: Disputed; no mark: Verified", $"mark inside the head: {inside.Lifecycle.Outcome}; no mark: {none.Lifecycle.Outcome}");
    }

    /// <summary>
    /// LRV20: R11.95's three sentences are the published ones: parsed from the errata (<see cref="PublishedClientWords.AllPublished"/> holds them, so a test cannot hand-copy them), and each appears verbatim in
    /// <c>PostVerifier</c>'s output with the values substituted. <c>curia verify</c> and <c>curia_verify</c> print one <c>Render()</c>, so this is the words of both. Red on the seam: the key line and the lifecycle line
    /// say something else.
    /// </summary>
    [Fact]
    public async Task LRV20_TheThreeSentencesAreThePublishedOnes()
    {
        var published = PublishedClientWords.AllPublished();
        var held = new[] { PublishedClientWords.KeyLineRotationLifecycleFailed, PublishedClientWords.KeyLineRotationLifecycleCouldNot, PublishedClientWords.LifecycleMarkPastHead }.All(published.Contains);

        var (forged, _) = await VerifyAsync(CaseDirectory("forged-rotation"));
        var unknown = CaseDirectory("unknown-statement-typ");
        var (undecided, _) = await VerifyAsync(unknown);
        var (marked, _) = await VerifyAsync(CaseDirectory("rotation-then-post"), post => post with { Provenance = post.Provenance with { KeyStatus = "compromised", DisputedByLogIndex = 6 } });
        var (leaf, kid) = BindingOf(CaseDirectory("forged-rotation"), 2);
        var (undecidedLeaf, undecidedKid) = BindingOf(unknown, RotationLeaf(unknown));

        var failedWords = Fill(PublishedClientWords.KeyLineRotationLifecycleFailed, ("<i>", leaf.ToString(CultureInfo.InvariantCulture)), ("<kid>", kid));
        var couldNotWords = Fill(PublishedClientWords.KeyLineRotationLifecycleCouldNot, ("<i>", undecidedLeaf.ToString(CultureInfo.InvariantCulture)), ("<kid>", undecidedKid));
        var markWords = Fill(PublishedClientWords.LifecycleMarkPastHead, ("<d>", "6"), ("<n>", "5"));

        WholeText.Same(
            "published: held; failed: said; could not: said; mark: said",
            $"published: {(held ? "held" : "not held")}; failed: {(PublishedClientWords.Says(forged.Render(), failedWords) ? "said" : "not said")}; could not: {(PublishedClientWords.Says(undecided.Render(), couldNotWords) ? "said" : "not said")}; "
            + $"mark: {(PublishedClientWords.Says(marked.Render(), markWords) ? "said" : "not said")}");
    }
}
