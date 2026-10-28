using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// PR 4 (errata G44), Task 0's end-to-end probes: the shipped <c>curia</c> binary in a child process and an MCP session over a real Forum on a real socket
/// (as <c>ReadersTellWhatChangedEndToEndTests</c> drives them). The thread is built through the tools: a T1 helper answers a question the CLI's asker asked,
/// then revises its answer with <c>curia_revise</c>, which writes the revised post's id as <c>parent</c> and its digest as <c>prev</c> (Task 0 item 2).
///
/// <para><b>Oracles.</b> The ids are the receipts of the posts the test made; the acceptances the log recorded are read from the <c>events</c> table by
/// SQL; the words are the errata's (<see cref="PublishedReadersWords"/>) and <see cref="AcceptWording"/>'s. The asker posts one question, so its daily
/// budget is never the reason a <c>resolve</c> is refused (plan ruling 14).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class AcceptTheTextEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private static readonly TimeSpan Tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);

    private readonly string _home = Directory.CreateTempSubdirectory("curia-accept-text-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Thread(string Board, string QuestionId, string AnswerId, string AskerSlug, string Word, EnrolledAgent Helper, ForumTools Tools);

    private async Task<Thread> BuildAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var board = "board-" + suffix;
        var word = "quartzfinch" + suffix;

        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - Tenure);
        var store = new ProfileStore(_home);
        Assert.True(store.Create("helper-" + suffix, $"https://agents.example/helper-{suffix}", $"helper-{suffix}-k", forum.Client.BaseAddress!).TryGetValue(out var helper, out var created), created?.Detail);
        _owned.Add(helper!);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        Assert.True((await client.EnrolAsync(helper!, Ct)).TryGetValue(out _, out var refusal), refusal?.Summary);
        await forum.AttestOwnerAsync(helper!.Profile.AgentId, Ct, owner: "owner:" + helper.Profile.Slug);

        var tools = new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(helper, new ForumSession(client, helper, store, forum.Clock), forum.Clock));
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
            Assert.StartsWith("POSTED", Flatten(await tools.AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, Ct)), StringComparison.Ordinal);
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

        var asker = "asker-" + suffix;
        Assert.Equal(0, (await CuriaAsync("enrol", "--agent", asker, "--forum", Address)).Exit);
        var asked = await CuriaAsync("ask", "--agent", asker, "--board", board, "--title", $"Which {word} is it", "--body", $"A question about the {word}.", "--forum", Address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var questionId = ReceiptValue(asked.Stdout, "posted");

        var answered = Flatten(await tools.AnswerAsync(questionId, $"The first answer about the {word}.", null, Ct));
        Assert.StartsWith("POSTED", answered, StringComparison.Ordinal);

        return new Thread(board, questionId, PostedId(answered), asker, word, helper, tools);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args) => await CuriaProcess.RunAsync(_home, Ct, args);

    private async Task<(int Exit, string Stdout, string Stderr)> AsAskerAsync(Thread thread, params string[] args) =>
        await CuriaAsync([.. args, "--agent", thread.AskerSlug, "--forum", Address]);

    private static string ReceiptValue(string stdout, string key) =>
        Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value)[key];

    private async Task<McpProtocolSession> AskerSessionAsync(Thread thread)
    {
        Assert.True(new ProfileStore(_home).Load(thread.AskerSlug).TryGetValue(out var asker, out var error), error?.Detail);
        _owned.Add(asker!);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, asker!, new ProfileStore(_home), forum.Clock);
        return await McpProtocolSession.StartAsync(
            new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(asker!, session, forum.Clock)), asker!.Profile.AgentId);
    }

    private async Task<string[]> AcceptedInOrderAsync(string root)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand(
            "SELECT payload->>'answer_id' FROM events WHERE event_type = 'answer.accepted' AND payload->>'thread_root' = @root ORDER BY seq;", connection);
        query.Parameters.AddWithValue("root", root);
        var ids = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) ids.Add(reader.GetString(0));
        return [.. ids];
    }

    private static string[] Lines(string text) => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    /// <summary>The thread, its answer revised by the helper (a revision written as <c>curia_revise</c> writes one): the revision's id.</summary>
    private static async Task<string> ReviseAnswerAsync(Thread thread)
    {
        var revised = Flatten(await thread.Tools.ReviseAsync(thread.AnswerId, $"A corrected first answer about the {thread.Word}.", null, null, null, Ct));
        Assert.StartsWith("POSTED", revised, StringComparison.Ordinal);
        return PostedId(revised);
    }

    /// <summary>
    /// PK27 (R11.60): <c>curia resolve</c> and <c>curia_resolve</c> take the id of a revision of an answer. The CLI exits 0 and prints the receipt naming the
    /// revision; the tool then answers the replay, not a refusal; the log holds one acceptance, of the revision, under the question.
    /// </summary>
    [Fact]
    public async Task R11_60_PK27_BothResolveVerbsTakeTheIdOfARevision()
    {
        var thread = await BuildAsync();
        var revisionId = await ReviseAnswerAsync(thread);

        var cli = await AsAskerAsync(thread, "resolve", revisionId);
        Assert.True(cli.Exit == 0, $"curia resolve of a revision exited {cli.Exit}:\n{cli.Stdout}{cli.Stderr}");
        Assert.Contains(DisplayLiteral.Of(revisionId), cli.Stdout, StringComparison.Ordinal);

        await using var session = await AskerSessionAsync(thread);
        var tool = await session.CallAsync("curia_resolve", new() { ["answerPostId"] = revisionId });
        Assert.False(tool.IsError, tool.Text);
        Assert.Contains("ACCEPTED", tool.Text, StringComparison.Ordinal);

        Assert.Equal([revisionId], await AcceptedInOrderAsync(thread.QuestionId));
    }

    /// <summary>
    /// PK28 (R11.60): the helper comments on the question and revises its own comment. The asker's <c>curia resolve</c> of that revision exits 3 with the
    /// Forum's detail naming <c>chain=comment</c>, and <c>curia_resolve</c> words the refusal by its cause (the root's kind, quoted) and never says that
    /// only a post of kind answer can be accepted. Nothing is recorded.
    /// </summary>
    [Fact]
    public async Task R11_60_PK28_ARevisionOfACommentIsRefusedByItsCauseOnBothReaders()
    {
        var thread = await BuildAsync();
        var commented = Flatten(await thread.Tools.CommentAsync(thread.QuestionId, $"A remark about the {thread.Word}.", null, Ct));
        Assert.StartsWith("POSTED", commented, StringComparison.Ordinal);
        var revised = Flatten(await thread.Tools.ReviseAsync(PostedId(commented), $"A corrected remark about the {thread.Word}.", null, null, null, Ct));
        Assert.StartsWith("POSTED", revised, StringComparison.Ordinal);
        var revisionId = PostedId(revised);

        var cli = await AsAskerAsync(thread, "resolve", revisionId);
        Assert.Equal(3, cli.Exit);
        Assert.Contains("chain=comment", cli.Stdout + cli.Stderr, StringComparison.Ordinal);

        await using var session = await AskerSessionAsync(thread);
        var tool = await session.CallAsync("curia_resolve", new() { ["answerPostId"] = revisionId });
        Assert.True(tool.IsError, tool.Text);
        Assert.Contains(DisplayLiteral.Of("comment"), tool.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("only a post of kind answer can be accepted", tool.Text, StringComparison.Ordinal);

        Assert.Empty(await AcceptedInOrderAsync(thread.QuestionId));
    }

    /// <summary>
    /// PK32 (R10.79, R10.85): the story of a moved acceptance is told once, on the question. The asker accepts the answer, its author revises it, the asker
    /// accepts the revision. <c>curia read</c> of the original prints no <c>thread</c> line saying it was accepted; <c>curia read</c> of the question has an
    /// <c>answers</c> line that names the revision by the id the test's receipt gave and says it is a revision.
    /// </summary>
    [Fact]
    public async Task R10_85_PK32_TheMovedAcceptanceIsToldOnTheQuestionAndNotOnTheOriginal()
    {
        var thread = await BuildAsync();
        Assert.Equal(0, (await AsAskerAsync(thread, "resolve", thread.AnswerId)).Exit);
        var revisionId = await ReviseAnswerAsync(thread);
        var moved = await AsAskerAsync(thread, "resolve", revisionId);
        Assert.True(moved.Exit == 0, $"curia resolve of the revision exited {moved.Exit}:\n{moved.Stdout}{moved.Stderr}");

        var original = await AsAskerAsync(thread, "read", thread.AnswerId);
        Assert.True(original.Exit == 0, original.Stdout + original.Stderr);
        Assert.DoesNotContain(Lines(original.Stdout), l => l.StartsWith("thread", StringComparison.Ordinal) && l.Contains("accepted", StringComparison.Ordinal));

        var question = await AsAskerAsync(thread, "read", thread.QuestionId);
        var answers = Lines(question.Stdout).Where(l => l.StartsWith("answers", StringComparison.Ordinal)).ToArray();
        var line = Assert.Single(answers);
        Assert.Contains(DisplayLiteral.Of(revisionId), line, StringComparison.Ordinal);
        Assert.Contains("a revision", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// PK35 (R11.60), end to end: after the acceptance moved to the revision, the first block of <c>curia_read</c> of the question states the accepted post
    /// from the question's own word, with its kind: <c>accepted revision=&lt;id&gt;</c>, and the id is the receipt's.
    /// </summary>
    [Fact]
    public async Task R11_60_PK35_CuriaReadStatesTheAcceptedRevisionWithItsKind()
    {
        var thread = await BuildAsync();
        Assert.Equal(0, (await AsAskerAsync(thread, "resolve", thread.AnswerId)).Exit);
        var revisionId = await ReviseAnswerAsync(thread);
        var moved = await AsAskerAsync(thread, "resolve", revisionId);
        Assert.True(moved.Exit == 0, $"curia resolve of the revision exited {moved.Exit}:\n{moved.Stdout}{moved.Stderr}");

        await using var session = await AskerSessionAsync(thread);
        var read = await session.CallAsync("curia_read", new() { ["postId"] = thread.QuestionId });
        Assert.False(read.IsError, read.Text);

        var accepted = Lines(read.Blocks[0]).Where(l => l.StartsWith("accepted", StringComparison.Ordinal)).ToArray();
        var line = Assert.Single(accepted);
        Assert.StartsWith($"accepted revision={DisplayLiteral.Of(revisionId)}", line, StringComparison.Ordinal);
    }

    /// <summary>The id a receipt printed as a display literal (R10.63), recovered by a JSON parser.</summary>
    private static string PostedId(string receipt) =>
        JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

    private static string Flatten(CallToolResult result)
    {
        var builder = new StringBuilder();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text:
                    builder.AppendLine(text.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    builder.AppendLine(resource.Text);
                    break;
                default:
                    break;
            }
        }

        return builder.ToString();
    }
}
