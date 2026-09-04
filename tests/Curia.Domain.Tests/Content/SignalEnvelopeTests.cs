using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Verification;
using Xunit;

namespace Curia.Domain.Tests.Content;

/// <summary>
/// The two kinds errata G8 adds to Table 9, as <see cref="PostEnvelope.Read"/> admits them:
/// R8.55's <c>vote</c> and R8.56's <c>verification</c>. Every required member has a test on both
/// sides, and the range of the meta-prediction is pinned at its ends.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SignalEnvelopeTests
{
    private static readonly string Target = "sha256:" + new string('a', 64);

    private static JsonValue.Object Envelope(string kind, params KeyValuePair<string, JsonValue>[] extra)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("v", new JsonValue.Number(1)),
            new("kind", new JsonValue.String(kind)),
            new("author", new JsonValue.String("https://agents.example/bob")),
            new("board", new JsonValue.String("b")),
            new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            new("created_at", new JsonValue.String("2026-09-04T12:00:00Z")),
            new("nonce", new JsonValue.String("n")),
        };
        members.AddRange(extra);
        return new JsonValue.Object([.. members]);
    }

    private static KeyValuePair<string, JsonValue> M(string name, JsonValue value) => new(name, value);

    private static JsonValue.Object Vote(int bp = 6200, long epoch = 17, string? target = null, bool endorse = true) =>
        Envelope("vote",
            M("target", new JsonValue.String(target ?? Target)),
            M("endorse", new JsonValue.Bool(endorse)),
            M("predicted_endorsement_bp", new JsonValue.Number(bp)),
            M("epoch", new JsonValue.Number(epoch)));

    private static JsonValue.Object Verification(string result = "contradicted", bool withRefs = true, bool withCode = false, string? method = "ran the repro") =>
        Envelope("verification",
            M("target", new JsonValue.String(Target)),
            M("body", new JsonValue.String("The build fails on 4.3.0 with the attached trace.")),
            M("method", method is null ? new JsonValue.Null() : new JsonValue.String(method)),
            M("result", new JsonValue.String(result)),
            M("refs", withRefs
                ? new JsonValue.Array([new JsonValue.Object([M("kind", new JsonValue.String("url")), M("value", new JsonValue.String("https://example.test/trace"))])])
                : new JsonValue.Array([])),
            M("code_blocks", withCode
                ? new JsonValue.Array([new JsonValue.Object([M("language", new JsonValue.String("sh")), M("source", new JsonValue.String("make test"))])])
                : new JsonValue.Array([])));

    private static string Error(JsonValue.Object o)
    {
        Assert.False(PostEnvelope.Read(o).TryGetValue(out _, out var error));
        return error!.Type;
    }

    [Fact]
    public void R8_55_AVoteCarriesItsTargetEndorsementPredictionAndEpoch()
    {
        Assert.True(PostEnvelope.Read(Vote()).TryGetValue(out var e, out var error), error?.Type);
        Assert.Equal(PostKind.Vote, e!.Kind);
        Assert.Equal(Target, e.Target);
        Assert.True(e.Endorse);
        Assert.Equal(6200, e.PredictedEndorsementBp);
        Assert.Equal(17L, e.Epoch);
        Assert.Equal(string.Empty, e.Body);
    }

    /// <summary>R8.29 / R6.33: basis points, inclusive at both ends, rejected outside -- never clamped.</summary>
    [Theory]
    [InlineData(0, true)]
    [InlineData(10000, true)]
    [InlineData(-1, false)]
    [InlineData(10001, false)]
    public void R8_29_ThePredictionIsInBasisPointsAtBothEnds(int bp, bool admitted)
    {
        var read = PostEnvelope.Read(Vote(bp: bp));
        Assert.Equal(admitted, read.TryGetValue(out _, out var error));
        if (!admitted) Assert.Equal("curia/content/predicted-endorsement-out-of-range", error!.Type);
    }

    [Fact]
    public void R8_29_AVoteWithoutAPredictionIsRejected() =>
        Assert.Equal("curia/content/predicted-endorsement-out-of-range",
            Error(Envelope("vote", M("target", new JsonValue.String(Target)), M("endorse", new JsonValue.Bool(true)), M("epoch", new JsonValue.Number(1)))));

    [Fact]
    public void R8_55_AVoteNamesItsTargetAsAServedDigest()
    {
        Assert.Equal("curia/content/target-required", Error(Vote(target: new string('a', 64))));
        Assert.Equal("curia/content/target-required", Error(Vote(target: "sha-256:" + new string('a', 64))));
        Assert.Equal("curia/content/target-required", Error(Vote(target: "01POSTID")));
    }

    [Fact]
    public void R8_49_AVoteNamesANonNegativeEpoch() =>
        Assert.Equal("curia/content/missing-or-invalid-field", Error(Vote(epoch: -1)));

    [Fact]
    public void R8_55_AVoteHasNoParent() =>
        Assert.Equal("curia/content/parent-not-allowed",
            Error(Envelope("vote", M("target", new JsonValue.String(Target)), M("endorse", new JsonValue.Bool(true)),
                M("predicted_endorsement_bp", new JsonValue.Number(1)), M("epoch", new JsonValue.Number(1)), M("parent", new JsonValue.String("01P")))));

    [Fact]
    public void R8_56_AVerificationCarriesMethodResultAndEvidence()
    {
        Assert.True(PostEnvelope.Read(Verification()).TryGetValue(out var e, out var error), error?.Type);
        Assert.Equal(PostKind.Verification, e!.Kind);
        Assert.Equal(Target, e.Target);
        Assert.Equal("ran the repro", e.Method);
        Assert.Equal(VerificationResult.Contradicted, e.Result);
        Assert.Single(e.Refs);
    }

    [Fact]
    public void R8_56_CodeBlocksAreEvidenceToo() =>
        Assert.True(PostEnvelope.Read(Verification(withRefs: false, withCode: true)).TryGetValue(out _, out var error), error?.Type);

    /// <summary>Table 13's "with evidence": prose alone is an assertion.</summary>
    [Fact]
    public void R8_56_ProseAloneIsNotEvidence() =>
        Assert.Equal("curia/content/evidence-required", Error(Verification(withRefs: false, withCode: false)));

    [Fact]
    public void R8_56_AMethodIsRequired()
    {
        Assert.Equal("curia/content/method-required", Error(Verification(method: null)));
        Assert.Equal("curia/content/method-required", Error(Verification(method: "   ")));
    }

    [Fact]
    public void R8_56_TheResultIsOneOfTwoSpellings() =>
        Assert.Equal("curia/content/result-invalid", Error(Verification(result: "passed")));

    [Fact]
    public void R8_56_AVerificationHasABody() =>
        Assert.Equal("curia/content/missing-or-invalid-field",
            Error(Envelope("verification", M("target", new JsonValue.String(Target)), M("method", new JsonValue.String("m")), M("result", new JsonValue.String("reproduced")))));

    /// <summary>The five discussion kinds are unaffected: none needs a target, and a stray one is ignored.</summary>
    [Fact]
    public void Table9_DiscussionKindsIgnoreTheSignalMembers()
    {
        var question = Envelope("question", M("title", new JsonValue.String("t")), M("body", new JsonValue.String("b")), M("target", new JsonValue.String(Target)));
        Assert.True(PostEnvelope.Read(question).TryGetValue(out var e, out var error), error?.Type);
        Assert.Null(e!.Target);
        Assert.Null(e.Endorse);
        Assert.Null(e.Result);
    }

    [Fact]
    public void G8_TheSevenKindsRoundTripTheirSpellings()
    {
        foreach (var kind in Enum.GetValues<PostKind>())
        {
            Assert.True(PostKinds.TryParse(PostKinds.Wire(kind), out var parsed));
            Assert.Equal(kind, parsed);
        }

        Assert.Equal(["vote", "verification"], new[] { PostKind.Vote, PostKind.Verification }.Select(PostKinds.Wire));
        Assert.All(new[] { PostKind.Vote, PostKind.Verification }, k => Assert.False(PostKinds.IsDiscussion(k)));
        Assert.False(PostKinds.IsServedToReaders(PostKind.Vote));
        Assert.True(PostKinds.IsServedToReaders(PostKind.Verification));
    }
}
