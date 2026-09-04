using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>R9.10's state algebra (errata G7, R9.18–R9.19), as a table: every state, both sides of every edge.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CitationCheckTests
{
    private static readonly string Hex = new('a', 64);
    private static readonly string D1 = "sha256:" + new string('1', 64);
    private static readonly string D2 = "sha256:" + new string('2', 64);
    private static readonly string D3 = "sha256:" + new string('3', 64);
    private static readonly string D4 = "sha256:" + new string('4', 64);

    private static PostView Post(string id, string digest, string? prev = null) => new(
        id, "{}", "sig", digest, "https://agents.example/a", "board", prev is null ? "question" : "revision",
        prev is null ? null : "q-1", ServerTimestamp.At(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero)), [], prev);

    private static readonly ImmutableArray<PostView> Posts =
    [
        Post("q-1", D1),
        Post("r-1", D2, prev: D1),
        Post("r-2", D3, prev: D1),
        Post("q-2", D4),
    ];

    private static bool AllServable(string _) => true;

    [Theory]
    [InlineData("sha256:" + "0000000000000000000000000000000000000000000000000000000000000000", true)]
    [InlineData("sha256:" + "000000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData("sha256:" + "00000000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData("sha256:" + "000000000000000000000000000000000000000000000000000000000000000G", false)]
    [InlineData("sha256:" + "000000000000000000000000000000000000000000000000000000000000000A", false)]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData("sha-256:" + "0000000000000000000000000000000000000000000000000000000000000000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void R9_18_ADigestIsSha256AndSixtyFourLowercaseHexCharacters(string? value, bool expected) =>
        Assert.Equal(expected, CitationCheck.IsDigest(value));

    [Fact]
    public void R9_18_AMalformedElementIsNamedByPositionAndNeverEchoed()
    {
        var state = CitationCheck.Resolve("not a digest; <script>", Posts, AllServable);

        Assert.Equal(CitationStatus.Malformed, state.Status);
        Assert.Null(state.Digest);
        Assert.Null(state.Post);
        Assert.Empty(state.Successors);
    }

    [Fact]
    public void R9_18_AnUnknownDigestIsUnknownNotOmitted()
    {
        var state = CitationCheck.Resolve("sha256:" + Hex, Posts, AllServable);

        Assert.Equal(CitationStatus.Unknown, state.Status);
        Assert.Equal("sha256:" + Hex, state.Digest);
        Assert.Null(state.Post);
    }

    [Fact]
    public void R9_18_ACurrentPostIsServedWithNoSuccessors()
    {
        var state = CitationCheck.Resolve(D4, Posts, AllServable);

        Assert.Equal(CitationStatus.Current, state.Status);
        Assert.Equal("q-2", state.Post!.PostId);
        Assert.Empty(state.Successors);
        Assert.False(state.Forked);
    }

    /// <summary>R6.7: a revision chains by <c>prev</c>. Two revisions of one digest are a fork, reported and not resolved.</summary>
    [Fact]
    public void R9_19_ASupersededPostNamesItsSuccessorsAndReportsAFork()
    {
        var state = CitationCheck.Resolve(D1, Posts, AllServable);

        Assert.Equal(CitationStatus.Superseded, state.Status);
        Assert.Equal("q-1", state.Post!.PostId);
        Assert.Equal([D2, D3], state.Successors);
        Assert.True(state.Forked);
    }

    [Fact]
    public void R9_19_ASingleSuccessorIsNotAFork()
    {
        ImmutableArray<PostView> posts = [Post("q-1", D1), Post("r-1", D2, prev: D1)];

        var state = CitationCheck.Resolve(D1, posts, AllServable);

        Assert.Equal(CitationStatus.Superseded, state.Status);
        Assert.Equal([D2], state.Successors);
        Assert.False(state.Forked);
    }

    /// <summary>R6.25 / R10.36: withheld carries no post -- and still carries its successors, which may be served.</summary>
    [Fact]
    public void R9_19_AWithheldPostCarriesNoContentButStillItsSuccessors()
    {
        var state = CitationCheck.Resolve(D1, Posts, id => id != "q-1");

        Assert.Equal(CitationStatus.Withheld, state.Status);
        Assert.Null(state.Post);
        Assert.Equal([D2, D3], state.Successors);
    }

    /// <summary>The distinction an agent acts on: withheld is "was here, now not served"; unknown is "never here".</summary>
    [Fact]
    public void R9_18_WithheldAndUnknownAreDifferentAnswers()
    {
        var withheld = CitationCheck.Resolve(D4, Posts, id => id != "q-2");
        var unknown = CitationCheck.Resolve("sha256:" + Hex, Posts, id => id != "q-2");

        Assert.Equal(CitationStatus.Withheld, withheld.Status);
        Assert.Equal(CitationStatus.Unknown, unknown.Status);
    }

    [Fact]
    public void EveryStateHasASpelling()
    {
        var spellings = Enum.GetValues<CitationStatus>().Select(CitationStatuses.Wire).Order(StringComparer.Ordinal);
        Assert.Equal(["current", "malformed", "superseded", "unknown", "withheld"], spellings);
    }
}
