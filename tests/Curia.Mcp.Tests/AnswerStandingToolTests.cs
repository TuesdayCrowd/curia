using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// Errata G36 at the adapter: <c>curia_search</c> and <c>curia_read</c> print the Forum's word on which answer a question's thread has accepted
/// (R10.81, R11.50), <c>curia_read</c>'s first block is the one R11.41 gave it and is unchanged, and a cursor the Forum refuses as not belonging to the
/// request reaches the model worded by its cause, naming the cursor and none of the query's values. <see cref="StubLog"/> serves the member as its
/// knob says (<see cref="StubLog.AcceptedAnswer"/>), so the oracle is what each fact set on it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AnswerStandingToolTests : IDisposable
{
    private readonly StubLog _log = new() { ThreadRouteServesTheThread = true };
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-answers-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ForumTools Tools() => new(_log.Client(), MarkingMode.None, new HeadStore(_home));

    private static string[] Passages(CallToolResult result)
    {
        var passages = result.Content.OfType<EmbeddedResourceBlock>().Select(b => ((TextResourceContents)b.Resource).Text).ToArray();
        Assert.NotEmpty(passages);
        return passages;
    }

    private static string Text(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string[] AnswersLines(string passage) =>
        [.. passage.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').TakeWhile(l => l.Length > 0).Where(l => l.StartsWith("answers  ", StringComparison.Ordinal))];

    private static string[] QuestionPassages(string[] passages) =>
        [.. passages.Where(p => p.StartsWith($"post      \"{StubLog.PostId}\"", StringComparison.Ordinal))];

    public static TheoryData<string> States() => [.. Enum.GetNames<StubAcceptedAnswer>()];

    [Theory]
    [MemberData(nameof(States))]
    public async Task R10_81_CuriaSearchAndCuriaReadPrintTheLineForAQuestion(string stateName)
    {
        var state = Enum.Parse<StubAcceptedAnswer>(stateName);
        var ct = TestContext.Current.CancellationToken;
        _log.AcceptedAnswer = state;

        var read = await Tools().ReadAsync(StubLog.PostId, ct);
        var search = await Tools().SearchAsync(new SearchCriteria(), ct);

        var readQuestions = QuestionPassages(Passages(read));
        var searchQuestions = QuestionPassages(Passages(search));
        Assert.True(readQuestions.Length == 1 && searchQuestions.Length == 1, $"expected the question once in each result; read {readQuestions.Length}, search {searchQuestions.Length}");

        foreach (var passage in readQuestions.Concat(searchQuestions))
        {
            var lines = AnswersLines(passage);
            Assert.True(lines.Length == 1, $"a question served as {state} printed {lines.Length} 'answers  ' line(s) in its passage");
            switch (state)
            {
                case StubAcceptedAnswer.Standing:
                    Assert.StartsWith("answers  accepted: post ", lines[0], StringComparison.Ordinal);
                    Assert.Contains(DisplayLiteral.Of(StubLog.AnswerPostId), lines[0], StringComparison.Ordinal);
                    break;
                case StubAcceptedAnswer.StandingUnserved:
                    Assert.Contains(DisplayLiteral.Of(_log.Answer.PrefixedDigest), lines[0], StringComparison.Ordinal);
                    Assert.Contains("not served", lines[0], StringComparison.Ordinal);
                    break;
                case StubAcceptedAnswer.None:
                    Assert.StartsWith("answers  none accepted", lines[0], StringComparison.Ordinal);
                    break;
                default:
                    Assert.StartsWith("answers  NOT STATED", lines[0], StringComparison.Ordinal);
                    break;
            }
        }

        // The answer is not a question: its passage has no such line, whatever the Forum said of the question.
        foreach (var answer in Passages(read).Where(p => p.StartsWith($"post      \"{StubLog.AnswerPostId}\"", StringComparison.Ordinal)))
            Assert.Empty(AnswersLines(answer));

        // Control: R11.41's first block is unchanged (no stub state accepts any post of the thread through AcceptedPostIds).
        Assert.Contains("accepted none (the Forum served no post of this thread as accepted)", Text(read), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_50_AMismatchReachesTheModelWorkedByItsCauseNamingTheCursorAndNoQueryValue()
    {
        var ct = TestContext.Current.CancellationToken;
        _log.SearchRefusesTheCursor = true;
        const string Marker = "zzqueryvaluemarker";
        const string Cursor = "c3R1Yi1jdXJzb3I=";

        var thrown = await Assert.ThrowsAsync<McpException>(() => Tools().SearchAsync(new SearchCriteria { Query = Marker, Cursor = Cursor }, ct));

        Assert.Contains("cursor", thrown.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PublishedSearchPage.MismatchType(), thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Cursor, thrown.Message, StringComparison.Ordinal);
    }
}
