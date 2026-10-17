using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.57, D49's property on the read verbs: a post the Forum marks disputed (<c>disputed_by_log_index</c> on its
/// provenance) is not reported citable by any reader, whatever its key set says. In an honest Forum the key set and the
/// mark come from one fold and agree; these facts hand a reader the Forum that does not, which the test Forum's fold
/// cannot produce. The word every surface prints is <see cref="Passage.Outcome"/>'s.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ForumDisputedMarkTests
{
    private const long MarkedAt = 41;

    [Fact]
    public void R6_57_APostTheForumMarksDisputedReadsDisputedWhileItsKeySetSaysActive()
    {
        var passage = Marked(MarkedAt);

        Assert.Equal("active", passage.Post.Provenance.KeyStatus);
        Assert.Equal(CheckOutcome.Verified, passage.Verdict.ReadOutcome);
        Assert.Equal(CheckOutcome.Disputed, passage.Outcome);
    }

    [Fact]
    public void R6_57_APostTheForumDoesNotMarkStillReadsVerified()
    {
        Assert.Equal(CheckOutcome.Verified, Marked(null).Outcome);
    }

    [Fact]
    public void R6_57_AFailedSignatureIsNotSoftenedToDisputedByTheMark()
    {
        var failed = Marked(MarkedAt, new SignatureVerdict(false, "alice-1", "the signature does not verify"));

        Assert.Equal(CheckOutcome.Failed, failed.Outcome);
    }

    [Fact]
    public void R6_57_RenderSaysDisputedAndNamesTheMarkWhenOnlyTheForumMarksIt()
    {
        var text = Marked(MarkedAt).Render();

        Assert.Contains("signature DISPUTED:", text, StringComparison.Ordinal);
        Assert.Contains($"disputed by log index {MarkedAt}", text, StringComparison.Ordinal);
        Assert.DoesNotContain("signature verified locally", text, StringComparison.Ordinal);
        Assert.Contains("signature verified locally", Marked(null).Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void R6_57_TheTitledIndexWritesDisputedAndWithholdsTheTitleWhenOnlyTheForumMarksIt()
    {
        var marked = Marked(MarkedAt);
        var frame = new FrameBuilder();
        TitledIndex.Write(frame, 1, marked, MarkingMode.None, null);
        var text = frame.ToString();

        Assert.Contains("signature DISPUTED", text, StringComparison.Ordinal);
        Assert.Null(TitledIndex.Title(marked));
        Assert.Contains("(no title:", text, StringComparison.Ordinal);

        var plain = new FrameBuilder();
        TitledIndex.Write(plain, 1, Marked(null), MarkingMode.None, null);
        Assert.Contains("signature verified", plain.ToString(), StringComparison.Ordinal);
    }

    private static Passage Marked(long? disputedBy, SignatureVerdict? verdict = null)
    {
        var canonical = SpanCheckTests.Envelope("A body.");
        var post = SpanCheckTests.Post(canonical, Datamarking.Render(canonical, MarkingMode.None), MarkingMode.None);
        post = post with { Provenance = post.Provenance with { DisputedByLogIndex = disputedBy } };

        return new Passage(
            post,
            verdict ?? new SignatureVerdict(true, "alice-1", "recanonicalized bytes are byte-identical to the served canonical form", "ab", "sha256:ab"));
    }
}
