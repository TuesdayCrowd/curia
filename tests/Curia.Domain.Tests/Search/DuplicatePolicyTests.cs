using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DuplicatePolicyTests
{
    private static readonly DuplicateThresholds T = DuplicateThresholds.Published;

    [Fact]
    public void R8_18_ARefusalNeedsBothTheCosineAndTheLexicalFloor()
    {
        Assert.Equal(DuplicateVerdict.Refuse, DuplicatePolicy.Assess(PostKind.Question, 0.95, 0.6, false, T));
        Assert.Equal(DuplicateVerdict.Annotate, DuplicatePolicy.Assess(PostKind.Question, 0.95, 0.2, false, T));
        Assert.Equal(DuplicateVerdict.Annotate, DuplicatePolicy.Assess(PostKind.Question, 0.90, 0.9, false, T));
        Assert.Equal(DuplicateVerdict.None, DuplicatePolicy.Assess(PostKind.Question, 0.80, 0.9, false, T));
        Assert.Equal(DuplicateVerdict.Refuse, DuplicatePolicy.Assess(PostKind.Question, 0.94, 0.5, false, T));
    }

    [Fact]
    public void R8_60_OnlyAQuestionIsEverRefused()
    {
        foreach (var kind in new[] { PostKind.Answer, PostKind.Finding, PostKind.Comment, PostKind.Revision })
            Assert.Equal(DuplicateVerdict.Annotate, DuplicatePolicy.Assess(kind, 0.99, 1.0, false, T));
    }

    [Fact]
    public void R8_20_ASignedOverrideTurnsARefusalIntoAnAnnotation()
    {
        Assert.Equal(DuplicateVerdict.Annotate, DuplicatePolicy.Assess(PostKind.Question, 0.99, 1.0, true, T));
        Assert.Equal(DuplicateVerdict.None, DuplicatePolicy.Assess(PostKind.Question, 0.5, 1.0, true, T));
    }

    [Fact]
    public void R8_21_ThresholdsAreConfigurableAndValidated()
    {
        var strict = new DuplicateThresholds(1.0, 1.0, 0.99);
        Assert.Equal(DuplicateVerdict.None, DuplicatePolicy.Assess(PostKind.Question, 0.98, 1.0, false, strict));
        Assert.False(new DuplicateThresholds(0.9, 0.5, 0.95).IsValid);
        Assert.Throws<ArgumentException>(() => DuplicatePolicy.Assess(PostKind.Question, 1, 1, false, new DuplicateThresholds(0.9, 0.5, 0.95)));
    }

    [Fact]
    public void R8_18_LexicalOverlapIsJaccardOverTheSearchTokenizersTerms()
    {
        Assert.Equal(1.0, LexicalOverlap.Jaccard("ECONNRESET from npgsql", "econnreset from NPGSQL"));
        Assert.Equal(2.0 / 6.0, LexicalOverlap.Jaccard("a b c d", "c d e f"), 1e-12);
        Assert.Equal(0.0, LexicalOverlap.Jaccard("alpha", "beta"));
        Assert.Equal(1.0, LexicalOverlap.Jaccard("", "..."));
    }
}
