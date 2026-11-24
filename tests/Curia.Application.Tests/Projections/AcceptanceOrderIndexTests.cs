using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R9.71 (errata G76), probes ACA1 and the Application half of ACA5: the acceptance's instant and its order against the first revision, and an answer's order against its question's
/// first revision. <b>Oracle:</b> the scene's own order of calls and the clock reading the test set at each call, tabulated by hand beside R9.71's sentence: the relation is decided by the
/// order of the two events in the log and never by comparing their instants (register D94), so each row is built three ways (instants rising, equal, the clock stepping backwards).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptanceOrderIndexTests
{
    private static readonly string Q = PostLogScene.Id("Q");
    private static readonly string A = PostLogScene.Id("A");
    private static readonly string A2 = PostLogScene.Id("A2");
    private static readonly string AR = PostLogScene.Id("AR");
    private static readonly string AR2 = PostLogScene.Id("AR2");
    private const string Asker = "https://agents.example/asker";
    private const string Answerer = "https://agents.example/answerer";

    private static readonly string DigestQ = PostLogScene.DigestOf('q');
    private static readonly string DigestA = PostLogScene.DigestOf('a');
    private static readonly string DigestA2 = PostLogScene.DigestOf('b');
    private static readonly string DigestAR = PostLogScene.DigestOf('c');
    private static readonly string DigestAR2 = PostLogScene.DigestOf('d');

    /// <summary>The clock readings of the two events a row orders: the first call and the second call in the log.</summary>
    public static TheoryData<string> Clocks => new() { "rising", "equal", "backwards" };

    private static (DateTimeOffset First, DateTimeOffset Second) Instants(string clock) => clock switch
    {
        "rising" => (PostLogScene.Start.AddMinutes(5), PostLogScene.Start.AddMinutes(10)),
        "equal" => (PostLogScene.Start.AddMinutes(5), PostLogScene.Start.AddMinutes(5)),
        "backwards" => (PostLogScene.Start.AddMinutes(10), PostLogScene.Start.AddMinutes(5)),
        _ => throw new ArgumentOutOfRangeException(nameof(clock), clock, "not a clock this fact builds"),
    };

    private static async Task<PostLogScene> BaseAsync(CancellationToken ct)
    {
        var scene = new PostLogScene();
        await scene.PostAsync(Q, DigestQ, PostKind.Question, Asker, null, null, ct).ConfigureAwait(false);
        await scene.PostAsync(A, DigestA, PostKind.Answer, Answerer, Q, null, ct).ConfigureAwait(false);
        return scene;
    }

    /// <summary>ACA1, order one: accept A, then the answerer revises A. The first revision came after the acceptance, whatever the two instants say.</summary>
    [Theory]
    [MemberData(nameof(Clocks))]
    public async Task R9_71_ACA1_AnAcceptanceThenARevisionIsAfterAcceptanceWhateverTheInstants(string clock)
    {
        var ct = TestContext.Current.CancellationToken;
        var (first, second) = Instants(clock);
        var scene = await BaseAsync(ct);
        scene.Clock.Set(first);
        await scene.AcceptAsync(Q, A, Asker, ct);
        scene.Clock.Set(second);
        await scene.PostAsync(AR, DigestAR, PostKind.Revision, Answerer, Q, DigestA, ct);
        var built = await scene.BuildAsync(ct);

        var fact = built.Index(_ => true).AcceptanceOf(built.Post(Q));

        Assert.Equal(new AcceptanceFact(first, "after_acceptance"), fact);
    }

    /// <summary>ACA1, order two: the answerer revises A, then the asker accepts A (R8.71 allows it). The first revision came before the acceptance, whatever the two instants say.</summary>
    [Theory]
    [MemberData(nameof(Clocks))]
    public async Task R9_71_ACA1_ARevisionThenAnAcceptanceOfTheOriginalIsBeforeAcceptanceWhateverTheInstants(string clock)
    {
        var ct = TestContext.Current.CancellationToken;
        var (first, second) = Instants(clock);
        var scene = await BaseAsync(ct);
        scene.Clock.Set(first);
        await scene.PostAsync(AR, DigestAR, PostKind.Revision, Answerer, Q, DigestA, ct);
        scene.Clock.Set(second);
        await scene.AcceptAsync(Q, A, Asker, ct);
        var built = await scene.BuildAsync(ct);

        var fact = built.Index(_ => true).AcceptanceOf(built.Post(Q));

        Assert.Equal(new AcceptanceFact(second, "before_acceptance"), fact);
    }

    /// <summary>
    /// ACA1: with two acceptances the instant is the standing (latest) one's, and the relation is of the post that one names. A is accepted, revised (AR), A2 is accepted at a later
    /// reading and then revised (AR2) after: the standing acceptance names A2, its first revision came after it, and its instant is the second acceptance's, not the first's.
    /// </summary>
    [Fact]
    public async Task R9_71_ACA1_TheInstantAndTheRelationAreOfTheStandingAcceptanceNotTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await BaseAsync(ct);
        await scene.PostAsync(A2, DigestA2, PostKind.Answer, Answerer, Q, null, ct);
        scene.Clock.Set(PostLogScene.Start.AddMinutes(1));
        await scene.AcceptAsync(Q, A, Asker, ct);
        scene.Clock.Set(PostLogScene.Start.AddMinutes(2));
        await scene.PostAsync(AR, DigestAR, PostKind.Revision, Answerer, Q, DigestA, ct);
        scene.Clock.Set(PostLogScene.Start.AddMinutes(3));
        await scene.AcceptAsync(Q, A2, Asker, ct);
        scene.Clock.Set(PostLogScene.Start.AddMinutes(4));
        await scene.PostAsync(AR2, DigestAR2, PostKind.Revision, Answerer, Q, DigestA2, ct);
        var built = await scene.BuildAsync(ct);

        var fact = built.Index(_ => true).AcceptanceOf(built.Post(Q));

        Assert.Equal(new AcceptanceFact(PostLogScene.Start.AddMinutes(3), "after_acceptance"), fact);
    }

    /// <summary>ACA1: an acceptance of a post with no revision carries an instant and no relation.</summary>
    [Fact]
    public async Task R9_71_ACA1_AnAcceptanceOfAnUnrevisedPostCarriesTheInstantAndNoRelation()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await BaseAsync(ct);
        scene.Clock.Set(PostLogScene.Start.AddMinutes(7));
        await scene.AcceptAsync(Q, A, Asker, ct);
        var built = await scene.BuildAsync(ct);

        Assert.Equal(new AcceptanceFact(PostLogScene.Start.AddMinutes(7), null), built.Index(_ => true).AcceptanceOf(built.Post(Q)));
    }

    /// <summary>ACA1, a fork: the first revision in log order is the one the relation is of. A forks into AR (before the acceptance) and AR2 (after): the relation is <c>before_acceptance</c>.</summary>
    [Fact]
    public async Task R9_71_ACA1_AForkIsDecidedByTheFirstRevisionInLogOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await BaseAsync(ct);
        await scene.PostAsync(AR, DigestAR, PostKind.Revision, Answerer, Q, DigestA, ct);
        await scene.AcceptAsync(Q, A, Asker, ct);
        await scene.PostAsync(AR2, DigestAR2, PostKind.Revision, Answerer, Q, DigestA, ct);
        var built = await scene.BuildAsync(ct);

        Assert.Equal("before_acceptance", built.Index(_ => true).AcceptanceOf(built.Post(Q))?.FirstRevision);
    }

    /// <summary>
    /// ACA1, a control that is green on the tree (the refactor's guard, plan ruling 5): <see cref="AcceptanceProjector.Fold"/> and <see cref="AcceptanceProjector.Standing"/> are two readings of
    /// "latest wins" and agree on every generated log. The logs are generated (a seeded list of answer picks over three answers, repeats included, each acceptance at a clock reading the generator
    /// also picks) and the expected answer is the last pick the generator made, never either projector's.
    /// </summary>
    [Fact]
    public async Task R9_71_ACA1_Control_FoldAndStandingAgreeOnEveryGeneratedLog()
    {
        var ct = TestContext.Current.CancellationToken;
        string[] answers = [A, A2, PostLogScene.Id("A3")];

        for (var seed = 1; seed <= 40; seed++)
        {
            // A deterministic generator: a length of 1 to 8 and picks from the three answers, repeats included, fixed by the seed alone.
            var sequence = Enumerable.Range(0, 1 + (seed % 8)).Select(i => ((seed * 7) + (i * 5) + (i * i)) % 3).ToArray();
            var scene = await BaseAsync(ct);
            await scene.PostAsync(A2, DigestA2, PostKind.Answer, Answerer, Q, null, ct);
            await scene.PostAsync(answers[2], PostLogScene.DigestOf('e'), PostKind.Answer, Answerer, Q, null, ct);
            var minute = 1;
            foreach (var pick in sequence)
            {
                scene.Clock.Set(PostLogScene.Start.AddMinutes(minute++));
                await scene.AcceptAsync(Q, answers[pick], Asker, ct);
            }

            var built = await scene.BuildAsync(ct);
            var folded = AcceptanceProjector.Fold(built.Log)[Q];
            var standing = AcceptanceProjector.Standing([.. built.Log.Where(e => e.AggregateId.Value == Q)]);

            Assert.Equal(answers[sequence[^1]], folded);
            Assert.Equal(answers[sequence[^1]], standing?.AnswerId);
        }
    }

    // ---- ACA5, the Application half: an answer's order against its question's first revision --------------------------------------------------------------------------------------

    private static readonly string R1 = PostLogScene.Id("R1");
    private static readonly string R2 = PostLogScene.Id("R2");
    private static readonly string Early = PostLogScene.Id("EAR2Y");
    private static readonly string Between = PostLogScene.Id("BETWEEN");
    private static readonly string Late = PostLogScene.Id("2ATE");
    private const string Stranger = "https://agents.example/stranger";

    /// <summary>
    /// ACA5. Log order: Q, Early, R1 (the asker's revision of Q), Between, R2 (the asker's revision of R1), Late. The relation is to the FIRST revision, R1: Early is <c>before</c>; Between and Late are
    /// <c>after</c> (Between is after R1 and before R2, which is the plan's "an answer between the two is after"). The first revision is named by digest in every row.
    /// </summary>
    [Fact]
    public async Task R9_71_ACA5_AnAnswerIsRelatedToTheFirstRevisionByLogOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = new PostLogScene();
        await scene.PostAsync(Q, DigestQ, PostKind.Question, Asker, null, null, ct);
        await scene.PostAsync(Early, PostLogScene.DigestOf('1'), PostKind.Answer, Answerer, Q, null, ct);
        await scene.PostAsync(R1, PostLogScene.DigestOf('r'), PostKind.Revision, Asker, null, DigestQ, ct);
        await scene.PostAsync(Between, PostLogScene.DigestOf('2'), PostKind.Answer, Answerer, Q, null, ct);
        await scene.PostAsync(R2, PostLogScene.DigestOf('s'), PostKind.Revision, Asker, null, PostLogScene.DigestOf('r'), ct);
        await scene.PostAsync(Late, PostLogScene.DigestOf('3'), PostKind.Answer, Answerer, Q, null, ct);
        var built = await scene.BuildAsync(ct);
        var index = built.Index(_ => true);

        Assert.Equal(new QuestionRevisedFact(true, PostLogScene.DigestOf('r'), R1, "before"), index.QuestionRevisedOf(built.Post(Early)));
        Assert.Equal(new QuestionRevisedFact(true, PostLogScene.DigestOf('r'), R1, "after"), index.QuestionRevisedOf(built.Post(Between)));
        Assert.Equal(new QuestionRevisedFact(true, PostLogScene.DigestOf('r'), R1, "after"), index.QuestionRevisedOf(built.Post(Late)));
    }

    /// <summary>ACA5: an unrevised question, and a question whose only "revision" is a stranger's (R9.28's author clause), have no revision: <c>null</c> on the wire, so <see cref="QuestionRevisedFact.Revised"/> is false and no relation is named.</summary>
    [Fact]
    public async Task R9_71_ACA5_AnUnrevisedQuestionAndAStrangersRevisionLeaveTheMemberNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = new PostLogScene();
        await scene.PostAsync(Q, DigestQ, PostKind.Question, Asker, null, null, ct);
        await scene.PostAsync(Early, PostLogScene.DigestOf('1'), PostKind.Answer, Answerer, Q, null, ct);
        await scene.PostAsync(R1, PostLogScene.DigestOf('r'), PostKind.Revision, Stranger, Q, DigestQ, ct);
        await scene.PostAsync(Late, PostLogScene.DigestOf('3'), PostKind.Answer, Answerer, Q, null, ct);
        var built = await scene.BuildAsync(ct);
        var index = built.Index(_ => true);

        Assert.Equal(new QuestionRevisedFact(false, null, null, null), index.QuestionRevisedOf(built.Post(Early)));
        Assert.Equal(new QuestionRevisedFact(false, null, null, null), index.QuestionRevisedOf(built.Post(Late)));
    }

    /// <summary>ACA5: a fork of the question is decided by the first revision in log order, and a first revision the Forum does not serve is named by digest alone (R10.36).</summary>
    [Fact]
    public async Task R9_71_ACA5_AForkNamesTheFirstInLogOrderAndAWithheldRevisionHasNoPostId()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = new PostLogScene();
        await scene.PostAsync(Q, DigestQ, PostKind.Question, Asker, null, null, ct);
        await scene.PostAsync(R1, PostLogScene.DigestOf('r'), PostKind.Revision, Asker, null, DigestQ, ct);
        await scene.PostAsync(Early, PostLogScene.DigestOf('1'), PostKind.Answer, Answerer, Q, null, ct);
        await scene.PostAsync(R2, PostLogScene.DigestOf('s'), PostKind.Revision, Asker, null, DigestQ, ct);
        var built = await scene.BuildAsync(ct);

        Assert.Equal(new QuestionRevisedFact(true, PostLogScene.DigestOf('r'), R1, "after"), built.Index(_ => true).QuestionRevisedOf(built.Post(Early)));
        Assert.Equal(new QuestionRevisedFact(true, PostLogScene.DigestOf('r'), null, "after"), built.Index(id => id != R1).QuestionRevisedOf(built.Post(Early)));
    }
}
