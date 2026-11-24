using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R9.70 (errata G76), probe RVT1: <c>revises</c> names the correction chain's root by R9.28's walk from <c>prev</c> and never reads the revision's author-written <c>parent</c>.
/// <b>Oracle:</b> the scenes' own ledgers (which digest each revision names as <c>prev</c>); a revision's <c>parent</c> is set to a different question's id on purpose in every row, so a build
/// that reads it names the wrong post. The member is a stub extension (<see cref="IndexProbeStubs"/>) until Task 3.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RevisesIndexTests
{
    private static readonly string Q = PostLogScene.Id("Q");
    private static readonly string Other = PostLogScene.Id("3THER");
    private static readonly string R1 = PostLogScene.Id("R1");
    private static readonly string R2 = PostLogScene.Id("R2");
    private static readonly string R1b = PostLogScene.Id("R1B");
    private const string Asker = "https://agents.example/asker";

    /// <summary>
    /// Q (asker) and another question; R1 revises Q (<c>prev</c> Q's digest), R2 revises R1, R1b is a second revision of Q (a fork). Every revision's <c>parent</c> names the OTHER question.
    /// </summary>
    private static async Task<PostLogScene.Built> BuildAsync(CancellationToken ct)
    {
        var scene = new PostLogScene();
        await scene.PostAsync(Q, PostLogScene.DigestOf('q'), PostKind.Question, Asker, null, null, ct).ConfigureAwait(false);
        await scene.PostAsync(Other, PostLogScene.DigestOf('o'), PostKind.Question, Asker, null, null, ct).ConfigureAwait(false);
        await scene.PostAsync(R1, PostLogScene.DigestOf('1'), PostKind.Revision, Asker, Other, PostLogScene.DigestOf('q'), ct).ConfigureAwait(false);
        await scene.PostAsync(R2, PostLogScene.DigestOf('2'), PostKind.Revision, Asker, Other, PostLogScene.DigestOf('1'), ct).ConfigureAwait(false);
        await scene.PostAsync(R1b, PostLogScene.DigestOf('3'), PostKind.Revision, Asker, Other, PostLogScene.DigestOf('q'), ct).ConfigureAwait(false);
        return await scene.BuildAsync(ct).ConfigureAwait(false);
    }

    /// <summary>RVT1. One hop, two hops and a fork name Q, the kind is <c>question</c>, and the post id is Q's where Q is served. Red on the tree (no member).</summary>
    [Theory]
    [InlineData("R1")]
    [InlineData("R2")]
    [InlineData("R1b")]
    public async Task R9_70_RVT1_EveryRevisionOfAChainNamesTheChainsRootAndNotItsParentOrItsPrev(string which)
    {
        var built = await BuildAsync(TestContext.Current.CancellationToken);
        var revision = which switch { "R1" => R1, "R2" => R2, _ => R1b };

        var fact = built.Index(_ => true).RevisesOf(built.Post(revision));

        Assert.Equal(new RevisesFact("question", Q), fact);
    }

    /// <summary>RVT1, the withheld root: a revision of a question the Forum does not serve names the kind and no post id (R10.36).</summary>
    [Fact]
    public async Task R9_70_RVT1_ARevisionOfAWithheldQuestionNamesTheKindAndNoPostId()
    {
        var built = await BuildAsync(TestContext.Current.CancellationToken);

        var fact = built.Index(id => id != Q).RevisesOf(built.Post(R2));

        Assert.Equal(new RevisesFact("question", null), fact);
    }

    /// <summary>RVT1, a control inside the Application half: the question itself, and a revision chain's root, is not a revision, so it carries no <c>revises</c>.</summary>
    [Fact]
    public async Task R9_70_RVT1_AQuestionIsNotARevisionAndCarriesNothing()
    {
        var built = await BuildAsync(TestContext.Current.CancellationToken);

        Assert.Null(built.Index(_ => true).RevisesOf(built.Post(Q)));
    }
}
