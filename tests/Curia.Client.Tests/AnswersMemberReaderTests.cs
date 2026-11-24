using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R10.100 (errata G76), probes ANC7 and RVT3 (the reader half) and ACA4's strictness rows: <c>ForumDocuments</c> reads <c>provenance.answers</c>, <c>provenance.revises</c> and the
/// acceptance's <c>first_revision</c> as closed shapes. <b>Oracle:</b> the JSON each row writes (the member's text is the row's own input, served by <see cref="ProvenanceRewriter"/> over
/// the stub), and R10.100's sentence of what is malformed: a member on a post of another kind, a <c>served</c> that is not a non-negative integer, a relation word outside its closed
/// set. A row that is accepted on the tree is red (the reader ignores a member it does not know); a row that is green on the tree is said so in its own summary (a control).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AnswersMemberReaderTests : IDisposable
{
    private readonly AnswersWireHarness _wire = new();

    public void Dispose()
    {
        _wire.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<(bool Ok, RefusalKind? Kind, string Summary)> ReadWithAsync(string member, string json, string kind = "question")
    {
        _wire.Member(StubLog.PostId, member, json, kind);
        var read = await _wire.ReadAsync(StubLog.PostId);
        var ok = read.TryGetValue(out _, out var refusal);
        return (ok, refusal?.Kind, refusal?.Summary ?? string.Empty);
    }

    /// <summary>ANC7, the valid rows: <c>{"served": 0}</c> and <c>{"served": 2}</c> on a question are read. (Green on the tree, which ignores the member: a control that the strict rows below are strict about the right thing.)</summary>
    [Theory]
    [InlineData("{\"served\":0}")]
    [InlineData("{\"served\":2}")]
    public async Task R10_100_ANC7_Control_AServedCountOfZeroOrNIsRead(string answers)
    {
        var (ok, _, summary) = await ReadWithAsync("answers", answers);

        Assert.True(ok, $"Expected: answers {answers} on a question is read Actual: {summary}");
    }

    /// <summary>
    /// ANC7. <c>answers</c> that is not <c>{"served": n}</c> with n a non-negative integer is a malformed response, never coerced to 0: a negative, a string, a missing
    /// <c>served</c>, a null, an array, a bare number. Red on the tree (accepted).
    /// </summary>
    [Theory]
    [InlineData("{\"served\":-1}")]
    [InlineData("{\"served\":\"2\"}")]
    [InlineData("{\"served\":null}")]
    [InlineData("{\"served\":true}")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[2]")]
    [InlineData("2")]
    [InlineData("\"2\"")]
    public async Task R10_100_ANC7_AnAnswersMemberThatIsNotAServedCountIsMalformed(string answers)
    {
        var (ok, kind, summary) = await ReadWithAsync("answers", answers);

        Assert.False(ok, $"Expected: answers {answers} refused as malformed Actual: the post was read");
        Assert.Equal(RefusalKind.Malformed, kind);
        Assert.NotEmpty(summary);
    }

    /// <summary>ANC7: a fractional <c>served</c> never reaches the member's reader (the ADMIT profile refuses a non-integer number, R6.33), so it is malformed on the tree too: a row that is green on the tree for the parser's reason.</summary>
    [Fact]
    public async Task R10_100_ANC7_Control_AFractionalServedIsRefusedByTheParserAlready()
    {
        var (ok, kind, _) = await ReadWithAsync("answers", "{\"served\":1.5}");

        Assert.False(ok);
        Assert.Equal(RefusalKind.Malformed, kind);
    }

    /// <summary>ANC7: the member on a post that is not a question is malformed (R9.69: no post of another kind carries it). Red on the tree.</summary>
    [Theory]
    [InlineData("answer")]
    [InlineData("comment")]
    [InlineData("revision")]
    public async Task R10_100_ANC7_AnAnswersMemberOnAPostThatIsNotAQuestionIsMalformed(string kind)
    {
        var (ok, refusal, _) = await ReadWithAsync("answers", "{\"served\":2}", kind);

        Assert.False(ok, $"Expected: answers on a post of kind {kind} refused as malformed Actual: the post was read");
        Assert.Equal(RefusalKind.Malformed, refusal);
    }

    // ---- RVT3: revises ----------------------------------------------------------------------------------------------------------------------------------------------------------

    private const string Root = "01RVT3ROOT00000000000000A";

    /// <summary>RVT3, the valid rows (green on the tree): <c>revises</c> with a post id and the kind <c>question</c>, and with the kind alone (the root is not served).</summary>
    [Theory]
    [InlineData("{\"post_id\":\"" + Root + "\",\"kind\":\"question\"}")]
    [InlineData("{\"kind\":\"question\"}")]
    public async Task R10_100_RVT3_Control_ARevisesMemberOnARevisionIsRead(string revises)
    {
        var (ok, _, summary) = await ReadWithAsync("revises", revises, "revision");

        Assert.True(ok, $"Expected: revises {revises} on a revision is read Actual: {summary}");
    }

    /// <summary>RVT3. An unknown <c>kind</c> word, no <c>kind</c>, a non-string word or post id, null and a non-object are malformed, never coerced to the nearer word (ruling 6). Red on the tree.</summary>
    [Theory]
    [InlineData("{\"post_id\":\"" + Root + "\",\"kind\":\"answer\"}")]
    [InlineData("{\"post_id\":\"" + Root + "\",\"kind\":\"Question\"}")]
    [InlineData("{\"post_id\":\"" + Root + "\"}")]
    [InlineData("{\"post_id\":\"" + Root + "\",\"kind\":7}")]
    [InlineData("{\"post_id\":7,\"kind\":\"question\"}")]
    [InlineData("{\"post_id\":\"\",\"kind\":\"question\"}")]
    [InlineData("null")]
    [InlineData("\"" + Root + "\"")]
    [InlineData("[]")]
    public async Task R10_100_RVT3_ARevisesMemberOutsideItsClosedShapeIsMalformed(string revises)
    {
        var (ok, kind, _) = await ReadWithAsync("revises", revises, "revision");

        Assert.False(ok, $"Expected: revises {revises} refused as malformed Actual: the post was read");
        Assert.Equal(RefusalKind.Malformed, kind);
    }

    /// <summary>RVT3: <c>revises</c> on a post that is not a revision is malformed (R9.70: no other post carries it). Red on the tree.</summary>
    [Theory]
    [InlineData("question")]
    [InlineData("answer")]
    [InlineData("comment")]
    public async Task R10_100_RVT3_ARevisesMemberOnAPostThatIsNotARevisionIsMalformed(string kind)
    {
        var (ok, refusal, _) = await ReadWithAsync("revises", "{\"post_id\":\"" + Root + "\",\"kind\":\"question\"}", kind);

        Assert.False(ok, $"Expected: revises on a post of kind {kind} refused as malformed Actual: the post was read");
        Assert.Equal(RefusalKind.Malformed, refusal);
    }

    // ---- ACA4: the acceptance's and the answer's members ------------------------------------------------------------------------------------------------------------------

    private async Task<(bool Ok, RefusalKind? Kind)> ReadAcceptedWithAsync(string acceptedAnswerMembersJson)
    {
        _wire.Log.AcceptedAnswer = StubAcceptedAnswer.Standing;
        _wire.Log.AcceptedAnswerSupersededBy = AnswersWireHarness.Revision("sha256:" + new string('b', 64), "01ACA4REVISION00000000001");
        _wire.Edit(StubLog.PostId, post =>
        {
            var accepted = post["provenance"]!["accepted_answer"]!.AsObject();
            foreach (var member in System.Text.Json.Nodes.JsonNode.Parse("{" + acceptedAnswerMembersJson + "}")!.AsObject().ToList())
            {
                accepted[member.Key] = member.Value?.DeepClone();
            }
        });
        var read = await _wire.ReadAsync(StubLog.PostId);
        return (read.TryGetValue(out _, out var refusal), refusal?.Kind);
    }

    /// <summary>ACA4, the valid rows (green on the tree): <c>accepted_at</c> as the receipt writes it and <c>first_revision</c> in its closed set are read.</summary>
    [Theory]
    [InlineData("\"accepted_at\":\"2026-11-24T00:40:40.7654940+00:00\",\"first_revision\":\"before_acceptance\"")]
    [InlineData("\"accepted_at\":\"2026-11-24T00:40:40.7654940+00:00\",\"first_revision\":\"after_acceptance\"")]
    public async Task R10_100_ACA4_Control_AnAcceptanceWithItsInstantAndItsRelationIsRead(string members)
    {
        var (ok, _) = await ReadAcceptedWithAsync(members);

        Assert.True(ok);
    }

    /// <summary>ACA4: a relation word outside its closed set (R10.100, ruling 6), or one that is not a string, is malformed; so is an <c>accepted_at</c> that is not a string. Red on the tree.</summary>
    [Theory]
    [InlineData("\"accepted_at\":\"2026-11-24T00:40:40.7654940+00:00\",\"first_revision\":\"later\"")]
    [InlineData("\"accepted_at\":\"2026-11-24T00:40:40.7654940+00:00\",\"first_revision\":\"before\"")]
    [InlineData("\"accepted_at\":\"2026-11-24T00:40:40.7654940+00:00\",\"first_revision\":3")]
    [InlineData("\"accepted_at\":\"2026-11-24T00:40:40.7654940+00:00\",\"first_revision\":null")]
    [InlineData("\"accepted_at\":7,\"first_revision\":\"after_acceptance\"")]
    [InlineData("\"accepted_at\":null,\"first_revision\":\"after_acceptance\"")]
    public async Task R10_100_ACA4_AnAcceptanceMemberOutsideItsClosedShapeIsMalformed(string members)
    {
        var (ok, kind) = await ReadAcceptedWithAsync(members);

        Assert.False(ok, $"Expected: accepted_answer with {members} refused as malformed Actual: the post was read");
        Assert.Equal(RefusalKind.Malformed, kind);
    }

    /// <summary>ACA6 (reader): <c>question_revised</c> that is not <c>null</c> or <c>{first_revision: {digest, post_id?}, answer_posted: before|after}</c> is malformed; so is the member on a post that is not an answer. Red on the tree.</summary>
    [Theory]
    [InlineData("answer", "{\"first_revision\":{\"digest\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"},\"answer_posted\":\"later\"}")]
    [InlineData("answer", "{\"first_revision\":{\"digest\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"},\"answer_posted\":null}")]
    [InlineData("answer", "{\"first_revision\":{\"digest\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}}")]
    [InlineData("answer", "{\"answer_posted\":\"before\"}")]
    [InlineData("answer", "{\"first_revision\":{\"digest\":\"nope\"},\"answer_posted\":\"before\"}")]
    [InlineData("answer", "{\"first_revision\":{},\"answer_posted\":\"before\"}")]
    [InlineData("answer", "\"before\"")]
    [InlineData("question", "null")]
    [InlineData("comment", "null")]
    [InlineData("revision", "null")]
    public async Task R10_100_ACA6_AQuestionRevisedMemberOutsideItsClosedShapeOrOnAnotherKindIsMalformed(string kind, string member)
    {
        var (ok, refusal, _) = await ReadWithAsync("question_revised", member, kind);

        Assert.False(ok, $"Expected: question_revised {member} on a post of kind {kind} refused as malformed Actual: the post was read");
        Assert.Equal(RefusalKind.Malformed, refusal);
    }

    /// <summary>ACA6 (reader), the valid rows (green on the tree): <c>null</c>, and both relation words, on an answer.</summary>
    [Theory]
    [InlineData("null")]
    [InlineData("{\"first_revision\":{\"digest\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"post_id\":\"01ACA6REV000000000000000A\"},\"answer_posted\":\"before\"}")]
    [InlineData("{\"first_revision\":{\"digest\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"},\"answer_posted\":\"after\"}")]
    public async Task R10_100_ACA6_Control_AQuestionRevisedMemberOnAnAnswerIsRead(string member)
    {
        var (ok, _, summary) = await ReadWithAsync("question_revised", member, "answer");

        Assert.True(ok, $"Expected: question_revised {member} on an answer is read Actual: {summary}");
    }
}
