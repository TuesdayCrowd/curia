using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Api.Tests.Fuzz;
using ModelContextProtocol.Protocol;
using Xunit;
using System.Text;

namespace Curia.Api.Tests;

/// <summary>
/// R10.100, R10.101 and R9.71 (b) (errata G76) against a real Forum on a real socket, through the shipped <c>curia</c> binary: ACA7 (an answer's write receipt and a later read of the same answer agree on whether its
/// question was revised first, ruling 23) and INX2's end-to-end half (each <c>curia inbox</c> row carries the question's count of served answers, and the banner says so).
///
/// <para><b>Oracles.</b> The order the test made the posts in (a receipt's id is what the call returned), the receipt the write printed (a different artifact from the later read: the receipt is the route's
/// pre-write read, the read is the log's order, which is ruling 23's whole point), and the hand count of the answers the test posted.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class AnswersEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-answers-e2e-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [GeneratedRegex(@"^posted\s+""?(?<id>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex PostedId();

    private Curia.Mcp.ForumTools ToolsFor(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_home), forum.Clock);
        return new Curia.Mcp.ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new Curia.Mcp.ForumWriter(agent, session, forum.Clock));
    }

    private async Task<EnrolledAgent> EnrolAsync(string slug)
    {
        Assert.True(
            new ProfileStore(_home).Create(slug, $"https://agents.example/{slug}", $"{slug}-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error),
            error?.Detail);
        _owned.Add(agent!);
        Assert.True((await new ForumClient(forum.Client, forum.Client.BaseAddress!).EnrolAsync(agent!, Ct, forum.Clock)).TryGetValue(out _, out var refusal), refusal?.Summary);
        return agent!;
    }

    private static string Flatten(CallToolResult result)
    {
        var builder = new StringBuilder();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text: builder.AppendLine(text.Text); break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }: builder.AppendLine(resource.Text); break;
                default: break;
            }
        }

        return builder.ToString();
    }

    private static string PostIdFrom(string receipt) =>
        System.Text.Json.JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

    /// <summary>An answerer enrolled and warmed up to T1 as if 49 hours ago (as AnswerToARevisionEndToEndTests does), the clock then back to real time for the CLI, and a board it made.</summary>
    private async Task<(EnrolledAgent Answerer, Curia.Mcp.ForumTools AnswererTools, string Board)> AnswererAsync(string slug)
    {
        var tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - tenure);
        var answerer = await EnrolAsync(slug);
        await forum.AttestOwnerAsync(answerer.Profile.AgentId, Ct, owner: "owner:" + slug);
        var tools = ToolsFor(answerer);
        var board = "g76-" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
            await tools.AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, Ct);
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        return (answerer, tools, board);
    }

    private async Task<string> AnswerViaCliAsync(string slug, string board, string questionId, string body)
    {
        var answered = await CuriaProcess.RunAsync(_home, Ct, "answer", "--agent", slug, "--board", board, "--parent", questionId, "--body", body, "--forum", Address);
        Assert.True(answered.Exit == 0, answered.Stdout + answered.Stderr);
        return answered.Stdout;
    }

    private async Task<string> ReadViaCliAsync(string postId)
    {
        var read = await CuriaProcess.RunAsync(_home, Ct, "read", postId, "--forum", Address);
        Assert.True(read.Exit == 0, read.Stdout + read.Stderr);
        return read.Stdout;
    }

    /// <summary>
    /// ACA7 (ruling 23). One question; an answer posted through <c>curia answer</c> before the question's revision and one after. <b>The receipts</b> (green on the tree: K3 found them already say it) say
    /// <c>question    none was reported</c> for the first and <c>question    SUPERSEDED by ...</c> for the second; <b>the later reads</b> of the same two answers (red on the tree: they print nothing) say
    /// <c>question  revised after this answer was posted</c> (<c>before</c>) for the first and <c>question  revised before this answer was posted</c> (<c>after</c>) for the second. The two artifacts
    /// agree on which side of the revision each answer fell.
    /// </summary>
    [Fact]
    public async Task R9_71_ACA7_TheReceiptAndTheLaterReadAgreeOnWhichSideOfTheRevisionAnAnswerFell()
    {
        var (_, _, board) = await AnswererAsync("answerer");
        var asker = await EnrolAsync("asker");
        var askerTools = ToolsFor(asker);
        var asked = Flatten(await askerTools.AskAsync(board, $"Which finch {Guid.NewGuid():N}", "A question about a finch.", null, null, Ct));
        Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);
        var questionId = PostIdFrom(asked);

        var earlyReceipt = await AnswerViaCliAsync("answerer", board, questionId, "A goldfinch, posted before the revision.");
        var early = PostedId().Match(earlyReceipt).Groups["id"].Value;
        var revised = Flatten(await askerTools.ReviseAsync(questionId, "Corrected: which finch, exactly?", null, null, null, Ct));
        Assert.StartsWith("POSTED", revised, StringComparison.Ordinal);
        var lateReceipt = await AnswerViaCliAsync("answerer", board, questionId, "A chaffinch, posted after the revision.");
        var late = PostedId().Match(lateReceipt).Groups["id"].Value;
        Assert.NotEqual(early, late);

        Assert.Single(earlyReceipt.Split('\n'), l => l.StartsWith("question    none was reported", StringComparison.Ordinal));
        Assert.Single(lateReceipt.Split('\n'), l => l.StartsWith("question    SUPERSEDED by", StringComparison.Ordinal));

        var earlyLine = (await ReadViaCliAsync(early)).Split('\n').Where(l => l.StartsWith("question  ", StringComparison.Ordinal)).ToArray();
        var lateLine = (await ReadViaCliAsync(late)).Split('\n').Where(l => l.StartsWith("question  ", StringComparison.Ordinal)).ToArray();

        Assert.True(earlyLine.Length == 1 && earlyLine[0].StartsWith("question  revised after this answer was posted: ", StringComparison.Ordinal), $"Expected: the read of the answer posted before the revision says the question was revised after it Actual: {string.Join(" / ", earlyLine)}");
        Assert.True(lateLine.Length == 1 && lateLine[0].StartsWith("question  revised before this answer was posted: ", StringComparison.Ordinal), $"Expected: the read of the answer posted after the revision says the question was revised before it Actual: {string.Join(" / ", lateLine)}");
    }

    /// <summary>
    /// INX2 (end to end). A reader who has asked and answered nothing runs <c>curia inbox</c> over two open questions, one with one answer and one with none: each row is its id, its author and
    /// <c>answers N served</c> (the hand count the test made), the banner says each line carries a count of answers and what chose the rows and in what order (R10.101), and the account line still counts the
    /// open questions. Red on the tree (the row is id and author only).
    /// </summary>
    [Fact]
    public async Task R10_101_INX2_EachInboxRowCarriesTheCountOfServedAnswersAndTheBannerSaysSo()
    {
        var (_, answererTools, board) = await AnswererAsync("answerer2");
        var asker = await EnrolAsync("asker2");
        var askerTools = ToolsFor(asker);
        var answeredId = PostIdFrom(Flatten(await askerTools.AskAsync(board, $"Which finch {Guid.NewGuid():N}", "A question about a finch.", null, null, Ct)));
        var openId = PostIdFrom(Flatten(await askerTools.AskAsync(board, $"Which pond {Guid.NewGuid():N}", "A question about a pond.", null, null, Ct)));
        Assert.StartsWith("POSTED", Flatten(await answererTools.AnswerAsync(answeredId, "A goldfinch.", null, Ct)), StringComparison.Ordinal);
        await EnrolAsync("reader");

        var inbox = await CuriaProcess.RunAsync(_home, Ct, "inbox", "--agent", "reader", "--board", board, "--forum", Address);

        Assert.True(inbox.Exit == 0, inbox.Stdout + inbox.Stderr);
        var lines = inbox.Stdout.Split('\n');
        Assert.True(lines.Any(l => l.StartsWith("\"" + answeredId + "\"", StringComparison.Ordinal)), "the inbox lists the answered question: " + inbox.Stdout);
        var answered = Assert.Single(lines, l => l.StartsWith("\"" + answeredId + "\"", StringComparison.Ordinal));
        var open = Assert.Single(lines, l => l.StartsWith("\"" + openId + "\"", StringComparison.Ordinal));
        Assert.NotNull(open);
        Assert.Contains("answers 1 served", answered, StringComparison.Ordinal);
        Assert.Contains("answers 0 served", open, StringComparison.Ordinal);

        var banner = string.Join('\n', lines.TakeWhile(l => !l.StartsWith("\"" + answeredId + "\"", StringComparison.Ordinal) && !l.StartsWith("\"" + openId + "\"", StringComparison.Ordinal)));
        Assert.Contains("count of answers", banner, StringComparison.Ordinal);
        Assert.Contains("oldest first", banner, StringComparison.Ordinal);
        Assert.True(
            banner.Contains("ranks nothing", StringComparison.Ordinal) || banner.Contains("ranked by nothing", StringComparison.Ordinal) || banner.Contains("no channel ranked", StringComparison.Ordinal),
            $"Expected: the banner says nothing ranked the rows Actual: {banner}");
    }
}
