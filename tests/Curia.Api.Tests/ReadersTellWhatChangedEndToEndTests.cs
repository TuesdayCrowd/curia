using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
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
/// Errata G40 against a real Forum on a real socket, through the shipped <c>curia</c> binary in a child process and an MCP session: a switch of the
/// accepted answer told as a switch (R8.71), a question's line saying its accepted answer was revised (R10.82), and the three tools that read before
/// they sign answering a missing post <c>NOT SENT</c> (R11.52).
///
/// <para><b>Oracles.</b> The ids are the receipts of the posts the test made; the <c>answer.accepted</c> events, the event count and the agent's post count
/// are read from the <c>events</c> table by SQL; the phrases are the errata's (<see cref="PublishedReadersWords"/>). A reader's report of itself is never
/// the expectation. The answerer is a T1 helper (enrolled and warmed as in <c>McpThreadStandingEndToEndTests</c>) and the asker and the stranger are
/// enrolled by the command.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ReadersTellWhatChangedEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private static readonly TimeSpan Tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);

    private readonly string _home = Directory.CreateTempSubdirectory("curia-readers-changed-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    [GeneratedRegex(@"\banswers\s+(accepted|none accepted|NOT STATED)", RegexOptions.CultureInvariant)]
    private static partial Regex AnswersLine();

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The board, the question, the two answers, the asker's slug, the unique word, and the answerer's agent and tools.</summary>
    private sealed record Thread(string Board, string QuestionId, string[] AnswerIds, string AskerSlug, string Word, EnrolledAgent Helper, ForumTools Tools);

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

        var answers = new List<string>();
        foreach (var which in new[] { "first", "second" })
        {
            var receipt = Flatten(await tools.AnswerAsync(questionId, $"The {which} answer about the {word}.", null, Ct));
            Assert.StartsWith("POSTED", receipt, StringComparison.Ordinal);
            answers.Add(PostedId(receipt));
        }

        return new Thread(board, questionId, [.. answers], asker, word, helper, tools);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args) =>
        await CuriaProcess.RunAsync(_home, Ct, args);

    private async Task<(int Exit, string Stdout, string Stderr)> AsAskerAsync(Thread thread, params string[] args) =>
        await CuriaAsync([.. args, "--agent", thread.AskerSlug, "--forum", Address]);

    private static string ReceiptValue(string stdout, string key) =>
        Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value)[key];

    private async Task<McpProtocolSession> SessionAsync(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_home), forum.Clock);
        return await McpProtocolSession.StartAsync(
            new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock)), agent.Profile.AgentId);
    }

    private async Task<McpProtocolSession> AskerSessionAsync(Thread thread)
    {
        Assert.True(new ProfileStore(_home).Load(thread.AskerSlug).TryGetValue(out var asker, out var error), error?.Detail);
        _owned.Add(asker!);
        return await SessionAsync(asker!);
    }

    /// <summary>The answers the log recorded as accepted for the thread, in order, by SQL.</summary>
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

    private async Task<long> AllEventsAsync()
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await query.ExecuteScalarAsync(Ct))!;
    }

    private async Task<long> PostsByAsync(string author)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @author;", connection);
        query.Parameters.AddWithValue("author", author);
        return (long)(await query.ExecuteScalarAsync(Ct))!;
    }

    private static string[] Lines(string text) => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    private static void StatesReplaced(string call, string receipt, string displaced) =>
        Assert.True(
            ReplacedLine(receipt).Contains(DisplayLiteral.Of(displaced), StringComparison.Ordinal),
            $"Expected {call} to hold a replaced line naming {DisplayLiteral.Of(displaced)}; the receipt read:\n{receipt}");

    private static string ReplacedLine(string text) =>
        Lines(text).FirstOrDefault(l => l.StartsWith("replaced", StringComparison.Ordinal)) ?? string.Empty;

    /// <summary>
    /// R8.71 against a real Forum, on both readers: the switch receipt of <c>curia resolve</c> and of <c>curia_resolve</c> holds the displaced answer's id
    /// from the test's own receipts on a <c>replaced</c> line; the first acceptance says no earlier answer stood; a replay says nothing of replacement; and
    /// the <c>answer.accepted</c> events the table holds, in order, agree with the sequence of calls that recorded.
    /// </summary>
    [Fact]
    public async Task R8_71_ASwitchIsToldOnBothReadersAgainstARealForum()
    {
        var states = PublishedReadersWords.R871States();
        var thread = await BuildAsync();
        var (a, b) = (thread.AnswerIds[0], thread.AnswerIds[1]);

        var cliFirst = await AsAskerAsync(thread, "resolve", a);
        var cliSwitch = await AsAskerAsync(thread, "resolve", b);
        var cliReplay = await AsAskerAsync(thread, "resolve", b);
        foreach (var run in new[] { cliFirst, cliSwitch, cliReplay }) Assert.True(run.Exit == 0, run.Stdout + run.Stderr);

        await using var session = await AskerSessionAsync(thread);
        var mcpSwitchBack = await session.CallAsync("curia_resolve", new() { ["answerPostId"] = a });
        var mcpSwitch = await session.CallAsync("curia_resolve", new() { ["answerPostId"] = b });
        var mcpReplay = await session.CallAsync("curia_resolve", new() { ["answerPostId"] = b });
        foreach (var call in new[] { mcpSwitchBack, mcpSwitch, mcpReplay }) Assert.False(call.IsError, call.Text);

        // The log: the four calls that recorded, in order, and none for the replays.
        Assert.Equal([a, b, a, b], await AcceptedInOrderAsync(thread.QuestionId));

        // The switch names the answer it displaced, on a replaced line, on both readers.
        StatesReplaced("curia resolve (the switch to B)", cliSwitch.Stdout, a);
        StatesReplaced("curia_resolve (A over B)", mcpSwitchBack.Text, b);
        StatesReplaced("curia_resolve (B over A)", mcpSwitch.Text, a);
        Assert.Contains(states[0], cliSwitch.Stdout, StringComparison.Ordinal);
        Assert.Contains(states[0], mcpSwitch.Text, StringComparison.Ordinal);

        // The first acceptance says no earlier answer stood.
        Assert.Contains(states[1], cliFirst.Stdout, StringComparison.Ordinal);

        // A replay says nothing of replacement.
        Assert.Equal(string.Empty, ReplacedLine(cliReplay.Stdout));
        Assert.Equal(string.Empty, ReplacedLine(mcpReplay.Text));
        Assert.Contains("nothing was recorded", cliReplay.Stdout, StringComparison.Ordinal);
        Assert.Contains("nothing was recorded", mcpReplay.Text, StringComparison.Ordinal);
    }

    /// <summary>The thread with its first answer accepted by the asker, revised by its author, and a comment by a stranger naming its digest.</summary>
    private async Task<(Thread Thread, string RevisionId, string StrangerCommentId)> RevisedAcceptedThreadAsync()
    {
        var thread = await BuildAsync();
        var a = thread.AnswerIds[0];
        var resolved = await AsAskerAsync(thread, "resolve", a);
        Assert.True(resolved.Exit == 0, resolved.Stdout + resolved.Stderr);

        var revised = Flatten(await thread.Tools.ReviseAsync(a, $"A corrected first answer about the {thread.Word}.", null, null, null, Ct));
        Assert.StartsWith("POSTED", revised, StringComparison.Ordinal);
        var revisionId = PostedId(revised);

        // A stranger names the answer's digest in a comment: R9.28 counts that as no correction.
        var stranger = "stranger-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(0, (await CuriaAsync("enrol", "--agent", stranger, "--forum", Address)).Exit);
        var digest = (await forum.Client.GetFromJsonAsync<JsonElement>($"{Address}/v1/posts/{a}", Ct)).GetProperty("digest").GetString()!;
        var comment = await CuriaAsync("comment", "--agent", stranger, "--board", thread.Board, "--parent", a, "--body", "This answer is wrong.", "--refs", digest, "--forum", Address);
        Assert.True(comment.Exit == 0, comment.Stdout + comment.Stderr);
        return (thread, revisionId, ReceiptValue(comment.Stdout, "posted"));
    }

    /// <summary>
    /// R10.82 against a real Forum: every form of the question (<c>read</c>, <c>board --titles</c>, <c>search --titles</c>, <c>browse read --plain</c>) has an
    /// <c>answers</c> line that states the accepted answer was superseded and names the revision by the post id the test's own receipt gave, and names
    /// neither the stranger's comment nor anything the stranger did.
    /// </summary>
    [Fact]
    public async Task R10_82_EveryFormOfTheQuestionStatesTheRevision()
    {
        var (thread, revisionId, strangerCommentId) = await RevisedAcceptedThreadAsync();

        var forms = new Dictionary<string, (int Exit, string Stdout, string Stderr)>
        {
            ["read"] = await AsAskerAsync(thread, "read", thread.QuestionId),
            ["board --titles"] = await AsAskerAsync(thread, "board", thread.Board, "--titles"),
            ["search --titles"] = await AsAskerAsync(thread, "search", thread.Word, "--board", thread.Board, "--titles"),
            ["browse read --plain"] = await AsAskerAsync(thread, "browse", "read", thread.QuestionId, "--plain"),
        };

        foreach (var (form, run) in forms)
        {
            Assert.True(run.Exit == 0, $"curia {form} exited {run.Exit}:\n{run.Stdout}{run.Stderr}");
            // The board also holds the harness's warmup questions, whose lines are rightly `none accepted`: the line under test is the one that
            // belongs to this thread's question, the first `answers` line after the line that names the question by its post id.
            var all = Lines(run.Stdout);
            var at = Array.FindIndex(all, l => l.Contains(DisplayLiteral.Of(thread.QuestionId), StringComparison.Ordinal));
            Assert.True(at >= 0, $"curia {form} never names the question {thread.QuestionId}:\n{run.Stdout}");
            var line = all.Skip(at).FirstOrDefault(l => AnswersLine().IsMatch(l));
            Assert.True(line is not null, $"curia {form} printed no answers line for the question:\n{run.Stdout}");

            Assert.Contains("SUPERSEDED", line, StringComparison.Ordinal);
            Assert.Contains(DisplayLiteral.Of(revisionId), line, StringComparison.Ordinal);
            Assert.DoesNotContain(strangerCommentId, line, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// R10.82 at <c>curia_read</c>: the first block's accepted line and the question's own <c>answers</c> line both state the revision and name it by the
    /// post id the test's receipt gave.
    /// </summary>
    [Fact]
    public async Task R10_82_CuriaReadStatesTheRevisionInTheFirstBlockAndTheQuestionLine()
    {
        var (thread, revisionId, _) = await RevisedAcceptedThreadAsync();
        await using var session = await AskerSessionAsync(thread);

        var read = await session.CallAsync("curia_read", new() { ["postId"] = thread.QuestionId });
        Assert.False(read.IsError, read.Text);

        var accepted = Lines(read.Blocks[0]).FirstOrDefault(l => l.StartsWith("accepted answer=", StringComparison.Ordinal));
        Assert.True(accepted is not null, $"the first block has no accepted line:\n{read.Blocks[0]}");
        Assert.Contains(DisplayLiteral.Of(revisionId), accepted, StringComparison.Ordinal);

        var question = Lines(read.Text).Where(l => AnswersLine().IsMatch(l)).ToArray();
        Assert.Single(question);
        Assert.Contains(DisplayLiteral.Of(revisionId), question[0], StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.52 against a real Forum: <c>curia_revise</c>, <c>curia_answer</c> and <c>curia_comment</c> of a well-formed id no post bears and of an id with a
    /// space are refused <c>NOT SENT</c>, and the events table holds exactly as many events and as many posts by the agent as before the calls.
    /// </summary>
    [Fact]
    public async Task R11_52_TheThreeToolsRefuseAMissingPostAgainstARealForum()
    {
        var thread = await BuildAsync();
        await using var session = await SessionAsync(thread.Helper);
        var author = thread.Helper.Profile.AgentId;
        var eventsBefore = await AllEventsAsync();
        var postsBefore = await PostsByAsync(author);

        foreach (var id in new[] { "01ARZ3NDEKTSV4RRFFQ69G5FAV", "a b" })
        {
            var calls = new (string Tool, Dictionary<string, object?> Arguments)[]
            {
                ("curia_revise", new() { ["postId"] = id, ["body"] = "A corrected body." }),
                ("curia_answer", new() { ["parentPostId"] = id, ["body"] = "An answer." }),
                ("curia_comment", new() { ["parentPostId"] = id, ["body"] = "A reply." }),
            };

            foreach (var (tool, arguments) in calls)
            {
                var result = await session.CallAsync(tool, arguments);

                Assert.True(
                    result.IsError && result.Text.StartsWith("NOT SENT", StringComparison.Ordinal),
                    $"Expected: NOT SENT Actual: {tool} of '{id}' answered:\n{result.Text}");
                Assert.DoesNotContain(id.Contains(' ', StringComparison.Ordinal) ? $"\"{id}\"" : id, result.Text, StringComparison.Ordinal);
            }
        }

        Assert.Equal(eventsBefore, await AllEventsAsync());
        Assert.Equal(postsBefore, await PostsByAsync(author));
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
