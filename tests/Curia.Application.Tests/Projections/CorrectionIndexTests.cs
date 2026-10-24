using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R9.32 (errata G32): the one rule that says which posts correct another, as the Forum lists them on a post it serves. The
/// rule is R9.28's (a revision, chained to the original by <c>prev</c>, by the original's own author), so the expected lists
/// here are written by hand from that sentence and never read from the code under test. <see cref="CitationCheck"/> already
/// applies the same sentence to the batch route; SU2 holds the two to one answer.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CorrectionIndexTests
{
    private static readonly string D1 = "sha256:" + new string('1', 64);
    private static readonly string D2 = "sha256:" + new string('2', 64);
    private static readonly string D3 = "sha256:" + new string('3', 64);
    private static readonly string D4 = "sha256:" + new string('4', 64);
    private static readonly string D5 = "sha256:" + new string('5', 64);
    private static readonly string D6 = "sha256:" + new string('6', 64);

    private const string AuthorA = "https://agents.example/a";
    private const string AuthorB = "https://agents.example/b";

    private static PostView Post(string id, string digest, string? prev = null, string? kind = null, string author = AuthorA) => new(
        id, "{}", "sig", digest, author, "board", kind ?? (prev is null ? "question" : "revision"),
        prev is null ? null : "q-1", ServerTimestamp.At(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero)), [], prev);

    private static bool AllServable(string _) => true;

    /// <summary>The original, its author's revision, and four posts that name its digest and correct nothing.</summary>
    private static readonly ImmutableArray<PostView> Hostile =
    [
        Post("q-1", D1),
        Post("r-1", D2, prev: D1),
        Post("c-b", D3, prev: D1, kind: "comment", author: AuthorB),
        Post("r-b", D4, prev: D1, author: AuthorB),
        Post("c-a", D5, prev: D1, kind: "comment"),
    ];

    [Fact]
    public void R9_32_OnlyARevisionByTheOriginalsAuthorIsACorrection()
    {
        var index = CorrectionIndex.Fold(Hostile, AllServable);

        var listed = index.For(Hostile[0]);

        // Written from R9.28 by hand: r-1 alone. The comment by B, B's revision and the author's own comment name D1 and correct nothing.
        Assert.Equal([new Successor(D2, "r-1")], listed);
    }

    [Fact]
    public void R9_32_TheIndexAndTheBatchShareOneRule()
    {
        ImmutableArray<PostView> world =
        [
            .. Hostile,
            Post("q-2", D6),
            Post("r-2", "sha256:" + new string('7', 64), prev: D6),
        ];
        var index = CorrectionIndex.Fold(world, AllServable);

        Assert.NotEmpty(world);
        var listedAnywhere = false;
        foreach (var post in world)
        {
            var batch = CitationCheck.Resolve(post.Digest, world, AllServable).Successors;
            var served = index.For(post).Select(s => s.Digest).ToArray();
            Assert.True(batch.SequenceEqual(served), $"post {post.PostId}: the batch route lists [{string.Join(", ", batch)}], the served member lists [{string.Join(", ", served)}]");
            listedAnywhere |= !batch.IsEmpty;
        }

        Assert.True(listedAnywhere, "the world held no correction, so the comparison compared two empty lists");
    }

    [Fact]
    public void R9_32_ACorrectionIsOneHopAndAForkIsBothBranchesInLogOrder()
    {
        var d7 = "sha256:" + new string('7', 64);
        ImmutableArray<PostView> chain =
        [
            Post("q-1", D1),
            Post("r-1", D2, prev: D1),
            Post("r-2", D3, prev: D2),
        ];
        var index = CorrectionIndex.Fold(chain, AllServable);

        Assert.Equal([new Successor(D2, "r-1")], index.For(chain[0]));
        Assert.Equal([new Successor(D3, "r-2")], index.For(chain[1]));
        Assert.Empty(index.For(chain[2]));

        ImmutableArray<PostView> fork =
        [
            Post("q-1", D1),
            Post("r-1", D2, prev: D1),
            Post("r-1b", d7, prev: D1),
        ];
        Assert.Equal([new Successor(D2, "r-1"), new Successor(d7, "r-1b")], CorrectionIndex.Fold(fork, AllServable).For(fork[0]));
    }
}
