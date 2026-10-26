using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R10.82 (errata G40) at <c>curia_read</c>'s first block: the accepted line states, from the accepted answer's own served list (R9.32), that the answer
/// has been superseded, naming each revision and FORKED for two, and that the acceptance names the answer. The question's member and the answer's list
/// are served apart by the stub, so which of the two the line reads is the fact's own observation; an unrevised answer's line is R11.41's, byte for byte.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FirstBlockSupersededTests : IDisposable
{
    private const string RevisionId = "01TESTREVISION000000000000A";
    private static readonly string RevisionDigest = "sha256:" + new string('9', 64);
    private static readonly string SecondRevisionDigest = "sha256:" + new string('8', 64);

    private readonly StubLog _log = new() { ThreadRouteServesTheThread = true };
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-first-block-").FullName;

    public FirstBlockSupersededTests()
    {
        // The Forum serves the stub's answer as the accepted one: the first block's accepted line is written.
        _log.AcceptedPostIds.Add(StubLog.AnswerPostId);
        _log.AcceptedAnswer = StubAcceptedAnswer.Standing;
    }

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<string> AcceptedLineAsync()
    {
        var read = await new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home)).ReadAsync(StubLog.PostId, TestContext.Current.CancellationToken);
        var firstBlock = string.Join('\n', read.Content.OfType<TextContentBlock>().Select(b => b.Text));
        var lines = firstBlock.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.StartsWith("accepted answer=", StringComparison.Ordinal)).ToArray();

        Assert.True(lines.Length == 1, $"the first block held {lines.Length} accepted line(s):\n{firstBlock}");
        return lines[0];
    }

    [Fact]
    public async Task R10_82_TheFirstBlockReadsTheAnswersOwnList()
    {
        // The Forum's word on the answer says it was revised; its word on the question says nothing was.
        _log.AnswerSupersededBy = [new Successor(RevisionDigest, RevisionId)];
        _log.AcceptedAnswerSupersededBy = [];
        var answersList = await AcceptedLineAsync();

        Assert.Contains("SUPERSEDED", answersList, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(RevisionId), answersList, StringComparison.Ordinal);
        Assert.Contains("the acceptance names", answersList, StringComparison.Ordinal);
        Assert.Contains(AnswerStanding.Basis, answersList, StringComparison.Ordinal);

        // And the reverse: the question's member lists a revision the answer's own envelope does not, so the line says nothing of one.
        _log.AnswerSupersededBy = [];
        _log.AcceptedAnswerSupersededBy = [new Successor(RevisionDigest, RevisionId)];
        var questionsList = await AcceptedLineAsync();

        Assert.DoesNotContain("SUPERSEDED", questionsList, StringComparison.Ordinal);
        Assert.DoesNotContain(RevisionId, questionsList, StringComparison.Ordinal);
    }

    /// <summary>The control: an unrevised accepted answer's line is R11.41's, byte for byte, whatever the question's member holds.</summary>
    [Fact]
    public async Task R10_82_AnUnrevisedAcceptedLineIsUnchanged()
    {
        _log.AnswerSupersededBy = [];
        _log.AcceptedAnswerSupersededBy = [];

        var line = await AcceptedLineAsync();

        Assert.Equal($"accepted answer={DisplayLiteral.Of(StubLog.AnswerPostId)} {AnswerStanding.Basis}", line);
    }

    [Fact]
    public async Task R10_82_TwoRevisionsOfTheAcceptedAnswerAreForkedInTheLine()
    {
        _log.AnswerSupersededBy = [new Successor(RevisionDigest, RevisionId), new Successor(SecondRevisionDigest, null)];
        var forked = PublishedReadersWords.R1082Tokens()[0];

        var line = await AcceptedLineAsync();

        Assert.Contains(forked, line, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(RevisionId), line, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(SecondRevisionDigest), line, StringComparison.Ordinal);
        Assert.Contains("not served", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.82: where the Forum served the accepted answer without its list, the line says <c>revision NOT STATED</c> in the words the question's
    /// <c>answers</c> line uses, so silence is never read as "not revised".
    /// </summary>
    [Fact]
    public async Task R10_82_AnAbsentListOfTheAcceptedAnswerIsNotStatedInTheLine()
    {
        _log.OmitSupersededBy = true;
        var notStated = PublishedReadersWords.R1082Tokens()[1];

        var line = await AcceptedLineAsync();

        Assert.Contains("revision " + notStated, line, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPERSEDED", line, StringComparison.Ordinal);
    }
}
