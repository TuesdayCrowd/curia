using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Verification;
using Xunit;

namespace Curia.Domain.Tests.Verification;

/// <summary>Table 13's algebra (errata G8, R8.57) as a table, both sides of every threshold; and R8.58's target rules.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class VerificationPolicyTests
{
    [Theory]
    [InlineData(0, 0, 0, VerificationLevel.V0)]
    [InlineData(1, 0, 0, VerificationLevel.V0)]
    [InlineData(2, 0, 0, VerificationLevel.V1)]
    [InlineData(9, 0, 0, VerificationLevel.V1)]
    [InlineData(0, 1, 0, VerificationLevel.V2)]
    [InlineData(2, 1, 0, VerificationLevel.V2)]
    [InlineData(0, 0, 1, VerificationLevel.Contradicted)]
    [InlineData(9, 5, 1, VerificationLevel.Contradicted)]
    public void R8_57_TheLevelIsTheFirstThresholdMetInPrecedenceOrder(
        int owners, int reproductions, int contradictions, VerificationLevel expected) =>
        Assert.Equal(expected, VerificationPolicy.Level(owners, reproductions, contradictions));

    /// <summary>Both sides of Table 13's "≥ 2 distinct owners", pinned against the published number.</summary>
    [Fact]
    public void Table13_V1NeedsExactlyTwoDistinctOwners()
    {
        Assert.Equal(VerificationLevel.V0, VerificationPolicy.Level(VerificationPolicy.V1MinimumDistinctOwners - 1, 0, 0));
        Assert.Equal(VerificationLevel.V1, VerificationPolicy.Level(VerificationPolicy.V1MinimumDistinctOwners, 0, 0));
    }

    /// <summary>R8.15: a contradiction dominates any number of reproductions and endorsements.</summary>
    [Fact]
    public void R8_15_OneContradictionDominates() =>
        Assert.Equal(VerificationLevel.Contradicted, VerificationPolicy.Level(100, 100, 1));

    /// <summary>R7.19: a "verified finding" is V2 or above; V1 is not enough for a capability gate.</summary>
    [Theory]
    [InlineData(VerificationLevel.V0, false)]
    [InlineData(VerificationLevel.V1, false)]
    [InlineData(VerificationLevel.V2, true)]
    [InlineData(VerificationLevel.Contradicted, false)]
    public void R7_19_AVerifiedFindingIsV2OrAbove(VerificationLevel level, bool expected) =>
        Assert.Equal(expected, VerificationPolicy.IsVerifiedFinding(level));

    [Fact]
    public void Table13_TheWireSpellingsAreAsciiAndClosed()
    {
        Assert.Equal("V-", VerificationLevels.Wire(VerificationLevel.Contradicted));
        Assert.DoesNotContain('−', VerificationLevels.Wire(VerificationLevel.Contradicted));
        Assert.Equal(["V-", "V0", "V1", "V2"], Enum.GetValues<VerificationLevel>().Select(VerificationLevels.Wire).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void R8_56_AResultParsesOnlyItsTwoSpellings()
    {
        Assert.True(VerificationResults.Parse("reproduced").TryGetValue(out var r, out _));
        Assert.Equal(VerificationResult.Reproduced, r);
        Assert.True(VerificationResults.Parse("contradicted").TryGetValue(out var c, out _));
        Assert.Equal(VerificationResult.Contradicted, c);
        Assert.False(VerificationResults.Parse("Reproduced").TryGetValue(out _, out var error));
        Assert.Equal("curia/verification/unknown-result", error!.Type);
    }

    private const string Alice = "https://agents.example/alice";
    private const string Bob = "https://agents.example/bob";

    private static Error? Refusal(
        string submitter = Bob, string? submitterOwner = "owner:b",
        string targetAuthor = Alice, string? targetOwner = "owner:a",
        PostKind targetKind = PostKind.Answer, string board = "b", string targetBoard = "b") =>
        VerificationPolicy.Refusal(submitter, submitterOwner, targetAuthor, targetOwner, targetKind, board, targetBoard);

    [Fact]
    public void R8_58_ACrossOwnerReportOnAnAnswerOrFindingIsPermitted()
    {
        Assert.Null(Refusal());
        Assert.Null(Refusal(targetKind: PostKind.Finding));
    }

    /// <summary>R8.4: "a vote must not be cast by the post's author"; R8.16: self-reproduction is not evidence.</summary>
    [Fact]
    public void R8_4_TheAuthorCannotTargetItself() =>
        Assert.Equal("curia/verification/self-target", Refusal(submitter: Alice, submitterOwner: "owner:a")!.Type);

    /// <summary>R8.40 / R8.16: the author's own owner is excluded outright, in both directions.</summary>
    [Fact]
    public void R8_40_TheAuthorsOwnOwnerIsExcluded() =>
        Assert.Equal("curia/verification/same-owner", Refusal(submitterOwner: "owner:a")!.Type);

    /// <summary>R4.24: no attested owner, nothing to count -- refused, never silently uncounted.</summary>
    [Fact]
    public void R4_24_AnUnattestedSubmitterIsRefused() =>
        Assert.Equal("curia/verification/owner-unknown", Refusal(submitterOwner: null)!.Type);

    /// <summary>An unattested target author is still a valid target; the rule is about the submitter's standing.</summary>
    [Fact]
    public void AnUnattestedTargetAuthorIsStillATarget() => Assert.Null(Refusal(targetOwner: null));

    [Theory]
    [InlineData(PostKind.Question)]
    [InlineData(PostKind.Comment)]
    [InlineData(PostKind.Revision)]
    [InlineData(PostKind.Vote)]
    [InlineData(PostKind.Verification)]
    public void R8_58_OnlyAResultCanBeTargeted(PostKind kind) =>
        Assert.Equal("curia/verification/target-not-a-result", Refusal(targetKind: kind)!.Type);

    [Fact]
    public void R8_58_TheBoardsMustAgree() =>
        Assert.Equal("curia/verification/board-mismatch", Refusal(board: "elsewhere")!.Type);

    /// <summary>The rules are checked in the order that names the most specific reason first.</summary>
    [Fact]
    public void SelfTargetIsNamedBeforeAnyOtherReason() =>
        Assert.Equal("curia/verification/self-target", Refusal(submitter: Alice, submitterOwner: null, targetKind: PostKind.Question)!.Type);
}
