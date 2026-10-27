using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.55 (errata G42): an accepted <c>curia_answer</c> states, on a line of its own, the standing of the question as the Forum served it at the
/// read the tool already makes: superseded (naming each revision, FORKED where there are more than one), none reported, or NOT STATED. The line is a
/// note and never a refusal; <c>curia_comment</c> and <c>curia_revise</c> carry no such line (decision 8), and a revision's own
/// <c>supersession</c> line is the one R11.47 gave it.
///
/// <para><b>Oracles.</b> The revisions the test put on the stub's list, by the ids and digests it chose; the lines of the receipt, found by their
/// label. The four lines are compared with one another, because "checked and clear" and "not checked" must never be one line.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class QuestionStandingReceiptTests : IDisposable
{
    private const string QuestionId = "01PIQUESTION0000000000002";
    private const string FirstRevisionId = "01PIREVISION0000000000002";
    private const string SecondRevisionId = "01PIREVISION0000000000003";

    private static readonly string FirstDigest = "sha256:" + new string('4', 64);
    private static readonly string SecondDigest = "sha256:" + new string('5', 64);

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-pi-receipt-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<McpProtocolSession> StartAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
        return await McpProtocolSession.StartAsync(tools, writer.Agent.Profile.AgentId, agent);
    }

    private void ServeQuestion(ImmutableArray<Successor> list)
    {
        var question = new PostDraft { Kind = PostKind.Question, Board = "b", Title = "A question about finches", Body = "Which finch is this?" };
        _log.ServePost(QuestionId, question, _log.SignDraft(question), list);
    }

    private static string[] LinesStarting(string text, string label) =>
        [.. text.Split('\n').Where(l => l.StartsWith(label, StringComparison.Ordinal))];

    private async Task<string> AnswerReceiptAsync(ImmutableArray<Successor> list)
    {
        ServeQuestion(list);
        await using var session = await StartAsync();
        var answer = await session.CallAsync("curia_answer", new() { ["parentPostId"] = QuestionId, ["body"] = "It is a goldfinch." });
        Assert.False(answer.IsError, answer.Text);
        Assert.StartsWith("POSTED", answer.Text, StringComparison.Ordinal);
        return answer.Text;
    }

    [Fact]
    public async Task R11_55_AnAnswerReceiptStatesTheQuestionsStanding()
    {
        // A fresh stub per row would hide that the rows differ only by the list; each row re-serves the question under the same id.
        var one = Assert.Single(LinesStarting(await AnswerReceiptAsync([new Successor(FirstDigest, FirstRevisionId)]), "question"));
        var two = Assert.Single(LinesStarting(await AnswerReceiptAsync([new Successor(FirstDigest, FirstRevisionId), new Successor(SecondDigest, SecondRevisionId)]), "question"));
        var none = Assert.Single(LinesStarting(await AnswerReceiptAsync([]), "question"));
        var unstated = Assert.Single(LinesStarting(await AnswerReceiptAsync(default), "question"));

        Assert.Contains("SUPERSEDED by 1 revision(s)", one, StringComparison.Ordinal);
        Assert.Contains(FirstRevisionId, one, StringComparison.Ordinal);
        Assert.DoesNotContain("FORKED", one, StringComparison.Ordinal);

        Assert.Contains("SUPERSEDED by 2 revision(s)", two, StringComparison.Ordinal);
        Assert.Contains(FirstRevisionId, two, StringComparison.Ordinal);
        Assert.Contains(SecondRevisionId, two, StringComparison.Ordinal);
        Assert.Contains("FORKED", two, StringComparison.Ordinal);

        Assert.Contains("none was reported", none, StringComparison.Ordinal);
        Assert.Contains("NOT STATED", unstated, StringComparison.Ordinal);

        Assert.Equal(4, new[] { one, two, none, unstated }.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task R11_55_ACommentAndARevisionReceiptCarryNoQuestionLine()
    {
        ServeQuestion([]);
        await using var session = await StartAsync();

        var comment = await session.CallAsync("curia_comment", new() { ["parentPostId"] = QuestionId, ["body"] = "A remark on the question." });
        var revision = await session.CallAsync("curia_revise", new() { ["postId"] = QuestionId, ["body"] = "A corrected question body." });

        Assert.False(comment.IsError, comment.Text);
        Assert.False(revision.IsError, revision.Text);
        Assert.Empty(LinesStarting(comment.Text, "question"));
        Assert.Empty(LinesStarting(revision.Text, "question"));

        // The revision's own line is R11.47's, byte for byte, and the comment has none.
        Assert.Equal(
            "supersession none was reported (the Forum's word at the read; a revision that landed since is not seen)",
            Assert.Single(LinesStarting(revision.Text, "supersession")));
        Assert.Empty(LinesStarting(comment.Text, "supersession"));
    }
}
