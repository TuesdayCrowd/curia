using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Domain.Primitives;
using Curia.Domain.Verification;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// R8.57 over the read model: what counts, what supersedes, and what the level is. The domain
/// algebra is pinned in <c>VerificationPolicyTests</c>; this is the fold that feeds it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class VerificationProjectorTests
{
    private const string Alice = "https://agents.example/alice";
    private const string Bob = "https://agents.example/bob";
    private const string Carol = "https://agents.example/carol";
    private const string Dave = "https://agents.example/dave";

    private static readonly string AnswerDigest = "sha256:" + new string('1', 64);
    private static readonly string QuestionDigest = "sha256:" + new string('0', 64);
    private static readonly ServerTimestamp At = ServerTimestamp.At(new DateTimeOffset(2026, 9, 4, 0, 0, 0, TimeSpan.Zero));

    private static readonly ImmutableDictionary<string, AgentStanding> Standings = new Dictionary<string, AgentStanding>(StringComparer.Ordinal)
    {
        [Alice] = Standing(Alice, "owner:a"),
        [Bob] = Standing(Bob, "owner:b"),
        [Carol] = Standing(Carol, "owner:c"),
        [Dave] = Standing(Dave, null),
    }.ToImmutableDictionary(StringComparer.Ordinal);

    private static AgentStanding Standing(string agent, string? owner) =>
        new(agent, [], At.Value, owner, owner is not null, 0, 0, 0, null);

    private static PostView Post(string id, string kind, string digest, string author, string canonical, string board = "b") =>
        new(id, canonical, "sig", digest, author, board, kind, null, At, []);

    private static PostView Answer() =>
        Post("a-1", "answer", AnswerDigest, Alice, """{"body":"x","kind":"answer","parent":"q-1"}""");

    private static PostView Question() =>
        Post("q-1", "question", QuestionDigest, Alice, """{"body":"x","kind":"question","title":"t"}""");

    private static int _seq;

    private static PostView Vote(string voter, bool endorse = true, string target = null!, string board = "b")
    {
        var n = ++_seq;
        var canonical = $$"""{"author":"{{voter}}","board":"{{board}}","content_type":"agent-authored/untrusted","created_at":"2026-09-04T12:00:00Z","endorse":{{(endorse ? "true" : "false")}},"epoch":1,"kind":"vote","nonce":"n{{n}}","predicted_endorsement_bp":5000,"target":"{{target ?? AnswerDigest}}","v":1}""";
        return Post($"v-{n}", "vote", "sha256:" + n.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), voter, canonical, board);
    }

    private static PostView Report(string reporter, string result, string target = null!)
    {
        var n = ++_seq;
        var canonical = $$"""{"author":"{{reporter}}","board":"b","body":"report","content_type":"agent-authored/untrusted","created_at":"2026-09-04T12:00:00Z","kind":"verification","method":"ran it","nonce":"n{{n}}","refs":[{"kind":"url","value":"https://example.test/{{n}}"}],"result":"{{result}}","target":"{{target ?? AnswerDigest}}","v":1}""";
        return Post($"r-{n}", "verification", "sha256:" + n.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), reporter, canonical);
    }

    private static VerificationFold Fold(params PostView[] posts) =>
        VerificationProjector.Fold([.. posts], Standings, _ => true);

    [Fact]
    public void Table13_TwoDistinctOwnersEndorsingMakeV1()
    {
        var fold = Fold(Answer(), Vote(Bob), Vote(Carol));

        Assert.Equal(VerificationLevel.V1, fold.LevelOf(AnswerDigest));
        Assert.Equal(2, fold.StateOf(AnswerDigest).DistinctEndorsingOwners);
    }

    [Fact]
    public void Table13_OneEndorsementIsV0() =>
        Assert.Equal(VerificationLevel.V0, Fold(Answer(), Vote(Bob)).LevelOf(AnswerDigest));

    /// <summary>R8.40: an owner counts once, however many agents it runs.</summary>
    [Fact]
    public void R8_40_TwoAgentsUnderOneOwnerAreOneOwner()
    {
        var standings = Standings.SetItem("https://agents.example/bob2", Standing("https://agents.example/bob2", "owner:b"));
        var fold = VerificationProjector.Fold([Answer(), Vote(Bob), Vote("https://agents.example/bob2")], standings, _ => true);

        Assert.Equal(VerificationLevel.V0, fold.LevelOf(AnswerDigest));
        Assert.Equal(1, fold.StateOf(AnswerDigest).DistinctEndorsingOwners);
    }

    /// <summary>R8.31 / Table 13: a rejecting vote is an opinion for the rate, never a level change.</summary>
    [Fact]
    public void Table13_RejectingVotesDoNotMoveTheLevel() =>
        Assert.Equal(VerificationLevel.V0, Fold(Answer(), Vote(Bob, endorse: false), Vote(Carol, endorse: false)).LevelOf(AnswerDigest));

    [Fact]
    public void R8_16_ACrossOwnerReproductionMakesV2()
    {
        var report = Report(Bob, "reproduced");
        var fold = Fold(Answer(), report);

        Assert.Equal(VerificationLevel.V2, fold.LevelOf(AnswerDigest));
        Assert.Equal([report.Digest], fold.StateOf(AnswerDigest).Reproductions);
    }

    [Fact]
    public void R8_15_AContradictionDominatesAndIsSurfaced()
    {
        var contradiction = Report(Carol, "contradicted");
        var fold = Fold(Answer(), Vote(Bob), Vote(Carol), Report(Bob, "reproduced"), contradiction);

        Assert.Equal(VerificationLevel.Contradicted, fold.LevelOf(AnswerDigest));
        Assert.Equal([contradiction.Digest], fold.StateOf(AnswerDigest).Contradictions);
    }

    /// <summary>R6.25 / R10.36: a withheld contradiction stops counting, and the level rises again.</summary>
    [Fact]
    public void R10_36_AWithheldContradictionStopsCounting()
    {
        var contradiction = Report(Carol, "contradicted");
        var posts = new[] { Answer(), Report(Bob, "reproduced"), contradiction };

        Assert.Equal(VerificationLevel.Contradicted, VerificationProjector.Fold([.. posts], Standings, _ => true).LevelOf(AnswerDigest));
        Assert.Equal(VerificationLevel.V2, VerificationProjector.Fold([.. posts], Standings, id => id != contradiction.PostId).LevelOf(AnswerDigest));
    }

    /// <summary>R8.56: a later report from the same agent supersedes its own earlier one, and nobody else's.</summary>
    [Fact]
    public void R8_56_ALaterReportSupersedesTheSameAgentsEarlierOne()
    {
        var fold = Fold(Answer(), Report(Bob, "contradicted"), Report(Bob, "reproduced"), Report(Carol, "contradicted"));

        Assert.Equal(VerificationLevel.Contradicted, fold.LevelOf(AnswerDigest));
        Assert.Single(fold.StateOf(AnswerDigest).Reproductions);
        Assert.Single(fold.StateOf(AnswerDigest).Contradictions);

        Assert.Equal(VerificationLevel.V2, Fold(Answer(), Report(Bob, "contradicted"), Report(Bob, "reproduced")).LevelOf(AnswerDigest));
    }

    /// <summary>R8.4 / R8.40 applied on replay too: a self or same-owner signal in the log does not count.</summary>
    [Fact]
    public void R8_40_SelfAndSameOwnerSignalsInTheLogAreNotCounted()
    {
        var standings = Standings.SetItem("https://agents.example/alice2", Standing("https://agents.example/alice2", "owner:a"));
        var fold = VerificationProjector.Fold([Answer(), Vote(Alice), Vote("https://agents.example/alice2"), Vote(Bob)], standings, _ => true);

        Assert.Equal(VerificationLevel.V0, fold.LevelOf(AnswerDigest));
        Assert.Equal(1, fold.StateOf(AnswerDigest).DistinctEndorsingOwners);
    }

    /// <summary>R4.24: a vote from an agent with no attested owner is an anomaly, surfaced and not counted.</summary>
    [Fact]
    public void R4_24_AnUnattestedVoterIsAnAnomalyNotAnEndorser()
    {
        var vote = Vote(Dave);
        var fold = Fold(Answer(), vote, Vote(Bob));

        Assert.Equal(VerificationLevel.V0, fold.LevelOf(AnswerDigest));
        Assert.Equal([vote.PostId], fold.Anomalies);
    }

    /// <summary>R8.58: a signal on a question is not a signal on a result.</summary>
    [Fact]
    public void R8_58_ASignalOnAQuestionDoesNotCount()
    {
        var fold = Fold(Question(), Vote(Bob, target: QuestionDigest), Vote(Carol, target: QuestionDigest));

        Assert.Equal(VerificationLevel.V0, fold.LevelOf(QuestionDigest));
        Assert.Empty(fold.ByTarget);
    }

    [Fact]
    public void R8_58_ASignalOnAnotherBoardDoesNotCount() =>
        Assert.Equal(VerificationLevel.V0, Fold(Answer(), Vote(Bob, board: "elsewhere"), Vote(Carol, board: "elsewhere")).LevelOf(AnswerDigest));

    /// <summary>R8.5 / R8.57: a level attaches to a digest; a revision's digest starts at V0.</summary>
    [Fact]
    public void R8_57_ARevisionStartsAtV0()
    {
        var revision = Post("rev-1", "revision", "sha256:" + new string('2', 64), Alice, """{"body":"y","kind":"revision"}""");
        var fold = Fold(Answer(), Vote(Bob), Vote(Carol), revision);

        Assert.Equal(VerificationLevel.V1, fold.LevelOf(AnswerDigest));
        Assert.Equal(VerificationLevel.V0, fold.LevelOf(revision.Digest));
    }

    [Fact]
    public void ASignalOnAnUnknownDigestIsIgnored() =>
        Assert.Empty(Fold(Vote(Bob, target: "sha256:" + new string('f', 64))).ByTarget);

    /// <summary>R7.19: a verified finding is one at V2 or above, counted for its author.</summary>
    [Fact]
    public void R7_19_VerifiedFindingsAreCountedAtV2()
    {
        var finding = Post("f-1", "finding", "sha256:" + new string('3', 64), Alice, """{"body":"x","kind":"finding","title":"t"}""");
        var endorsedOnly = Fold(finding, Vote(Bob, target: finding.Digest), Vote(Carol, target: finding.Digest));
        Assert.Equal(0, VerificationProjector.VerifiedFindingsBy(endorsedOnly, [finding], Alice));

        var reproduced = Fold(finding, Report(Bob, "reproduced", target: finding.Digest));
        Assert.Equal(1, VerificationProjector.VerifiedFindingsBy(reproduced, [finding], Alice));

        var contradicted = Fold(finding, Report(Bob, "reproduced", target: finding.Digest), Report(Carol, "contradicted", target: finding.Digest));
        Assert.Equal(0, VerificationProjector.VerifiedFindingsBy(contradicted, [finding], Alice));
    }
}
