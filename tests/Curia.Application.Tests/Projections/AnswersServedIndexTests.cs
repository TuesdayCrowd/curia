using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R9.69 (errata G76), probes ANC1 and ANC6: the number of answers the Forum serves to a question. <b>Oracle:</b> the scene each fact builds (post by post: id, kind, parent,
/// author, <c>prev</c>, and the serving filter the fact chooses), tabulated by hand beside R9.69's sentence; never the index's own report.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AnswersServedIndexTests
{
    private static readonly string Q = PostLogScene.Id("Q");
    private static readonly string Q2 = PostLogScene.Id("Q2");
    private static readonly string A1 = PostLogScene.Id("A1");
    private static readonly string A2 = PostLogScene.Id("A2");
    private static readonly string A3 = PostLogScene.Id("A3");
    private static readonly string A1r = PostLogScene.Id("A1R");
    private static readonly string A3r = PostLogScene.Id("A3R");
    private static readonly string C = PostLogScene.Id("C");
    private static readonly string B1 = PostLogScene.Id("B1");
    private const string Asker = "https://agents.example/asker";
    private const string Ans1 = "https://agents.example/ans1";
    private const string Ans2 = "https://agents.example/ans2";
    private const string Ans3 = "https://agents.example/ans3";

    /// <summary>
    /// R9.69's hand table. Q has answers A1, A2, A3 (A3 withheld in the base scene); A1r is A1's author's revision of A1; A3r is A3's author's revision of A3; C is a comment on Q;
    /// B1 answers another question Q2. Served to Q: A1 and A2 only (a revision is no answer, a comment is none, a withheld answer is not served, and a revision of a withheld answer is not counted).
    /// </summary>
    private static async Task<PostLogScene.Built> BuildAsync(CancellationToken ct)
    {
        var scene = new PostLogScene();
        await scene.PostAsync(Q, PostLogScene.DigestOf('q'), PostKind.Question, Asker, null, null, ct).ConfigureAwait(false);
        await scene.PostAsync(Q2, PostLogScene.DigestOf('r'), PostKind.Question, Asker, null, null, ct).ConfigureAwait(false);
        await scene.PostAsync(A1, PostLogScene.DigestOf('1'), PostKind.Answer, Ans1, Q, null, ct).ConfigureAwait(false);
        await scene.PostAsync(A2, PostLogScene.DigestOf('2'), PostKind.Answer, Ans2, Q, null, ct).ConfigureAwait(false);
        await scene.PostAsync(A3, PostLogScene.DigestOf('3'), PostKind.Answer, Ans3, Q, null, ct).ConfigureAwait(false);
        await scene.PostAsync(A1r, PostLogScene.DigestOf('4'), PostKind.Revision, Ans1, Q, PostLogScene.DigestOf('1'), ct).ConfigureAwait(false);
        await scene.PostAsync(A3r, PostLogScene.DigestOf('5'), PostKind.Revision, Ans3, Q, PostLogScene.DigestOf('3'), ct).ConfigureAwait(false);
        await scene.PostAsync(C, PostLogScene.DigestOf('6'), PostKind.Comment, Ans2, Q, null, ct).ConfigureAwait(false);
        await scene.PostAsync(B1, PostLogScene.DigestOf('7'), PostKind.Answer, Ans1, Q2, null, ct).ConfigureAwait(false);
        return await scene.BuildAsync(ct).ConfigureAwait(false);
    }

    /// <summary>ANC1. Red on the tree (no member): the count is 2 for the base scene, 3 with A3 served, still 2 with A1r withheld, 1 for the other question, and 0 for a question nobody answered.</summary>
    [Fact]
    public async Task R9_69_ANC1_ServedIsTheNumberOfServedAnswerPostsAndNothingElse()
    {
        var built = await BuildAsync(TestContext.Current.CancellationToken);
        var question = built.Post(Q);

        Assert.Equal(2, built.Index(id => id != A3).ServedAnswersTo(question));
        Assert.Equal(3, built.Index(_ => true).ServedAnswersTo(question));
        Assert.Equal(2, built.Index(id => id != A3 && id != A1r).ServedAnswersTo(question));
        Assert.Equal(1, built.Index(id => id != A3).ServedAnswersTo(built.Post(Q2)));

        // A withheld answer's own revision is served and is still not counted: it is no answer.
        Assert.Equal(1, built.Index(id => id != A3 && id != A2).ServedAnswersTo(question));
        Assert.Equal(0, built.Index(id => id != A1 && id != A2 && id != A3).ServedAnswersTo(question));
    }

    /// <summary>
    /// ANC6. The index is built once per request: the serving filter is asked of each post a bounded number of times, however many questions the response holds. The scene is
    /// thirty questions of three answers each; a count that scans the log per question asks the filter about every answer once per question, so the calls made by the thirty counts grow with
    /// the product and not the sum. <b>Non-vacuity:</b> every count is also asserted to be 3, so a stub that never asks the filter is red.
    /// </summary>
    [Fact]
    public async Task R9_69_ANC6_TheCountIsMadeInOnePassNotOnePerQuestion()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = new PostLogScene();
        const int Questions = 30;
        var questionIds = new List<string>();
        for (var i = 0; i < Questions; i++)
        {
            var q = PostLogScene.Id($"Q{i}");
            questionIds.Add(q);
            await scene.PostAsync(q, $"sha256:{i:x64}", PostKind.Question, Asker, null, null, ct);
            for (var a = 0; a < 3; a++)
                await scene.PostAsync(PostLogScene.Id($"A{i}X{a}"), $"sha256:{(i + 1) * 1000 + a:x64}", PostKind.Answer, Ans1, q, null, ct);
        }

        var built = await scene.BuildAsync(ct);
        var calls = 0;
        var index = built.Index(_ =>
        {
            calls++;
            return true;
        });

        var afterFold = calls;
        var counts = questionIds.Select(q => index.ServedAnswersTo(built.Post(q))).ToArray();
        var made = calls - afterFold;

        Assert.All(counts, n => Assert.Equal(3, n));
        Assert.True(made <= built.Posts.Length, $"Expected: at most one filter call per post ({built.Posts.Length}) for {Questions} counts Actual: {made} calls after the fold");
    }
}
