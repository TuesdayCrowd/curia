using System.Diagnostics.CodeAnalysis;
using System.Net;
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
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G28 against a real Forum on a real socket: how a thread stands (R10.79, R11.41) and what a reader did not read (R8.66).
///
/// <para><b>Why a socket.</b> The line-form readers are the shipped <c>curia</c> binary in a child process (the pattern of
/// <c>CliRefsEndToEndTests</c>), which a test server cannot serve. <b>Why the answers are T1 and the clock moves.</b> An answer needs
/// 48 hours of tenure on the Forum's clock (R7.10), which is the only way to reach an acceptance on a Forum the suite owns: the helper
/// is enrolled and warmed up as if forty-nine hours ago and the clock is brought forward to real time before the verbs run, as
/// <c>CliRefsAnswerEndToEndTests</c> does. The asker is enrolled by the command and accepts through the protocol.</para>
///
/// <para><b>Oracles.</b> Which posts are accepted is the Forum's own <c>GET /v1/threads/{root}</c> (<c>accepted: true</c>), never a reader's
/// output; what the author signed is the Forum's stored canonical form, counted by <c>System.Text.Json</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class McpThreadStandingEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    /// <summary>
    /// R10.79's statement that a post is the accepted answer, holding R10.74's clause. A question's <c>answers</c> line (R10.81)
    /// carries the clause too, so the clause alone would count the question.
    /// </summary>
    private const string Statement = "accepted by the thread's asker (the Forum's word: no signature covers an acceptance";

    private static readonly TimeSpan Tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);

    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-standing-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^post {6}""(?<id>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex FramePost();

    [GeneratedRegex(@"^#\d+ post ""(?<id>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex IndexRow();

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A question asked by the command, and two answers by a T1 agent: the board, the question, the answers, the asker's slug and the unique word they share.</summary>
    private sealed record Thread(string Board, string QuestionId, string[] AnswerIds, string AskerSlug, string Word);

    private async Task<Thread> BuildAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var board = "board-" + suffix;
        var word = "quartzfinch" + suffix;

        // The helper is enrolled and warmed up as if forty-nine hours ago, through the reference client's own writer, then the clock comes forward.
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

        return new Thread(board, questionId, [.. answers], asker, word);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args) =>
        await CuriaProcess.RunAsync(_home, Ct, args);

    private static string ReceiptValue(string stdout, string key) =>
        Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value)[key];

    /// <summary>The asker accepts <paramref name="answerId"/> through <c>curia_resolve</c> over the protocol.</summary>
    private async Task ResolveAsync(Thread thread, string answerId)
    {
        var store = new ProfileStore(_home);
        Assert.True(store.Load(thread.AskerSlug).TryGetValue(out var asker, out var error), error?.Detail);
        _owned.Add(asker!);
        await using var session = await SessionAsync(asker!);
        var resolved = await session.CallAsync("curia_resolve", new() { ["answerPostId"] = answerId });
        Assert.False(resolved.IsError, resolved.Text);
        Assert.StartsWith("ACCEPTED", resolved.Text, StringComparison.Ordinal);
    }

    private async Task<McpProtocolSession> SessionAsync(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_home), forum.Clock);
        return await McpProtocolSession.StartAsync(
            new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock)), agent.Profile.AgentId);
    }

    /// <summary>The ids the Forum's own thread document serves as accepted.</summary>
    private async Task<string[]> ForumAcceptedAsync(string rootId)
    {
        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"{Address}/v1/threads/{rootId}", Ct);
        return [.. served.EnumerateArray().Where(p => p.GetProperty("accepted").GetBoolean()).Select(p => p.GetProperty("post_id").GetString()!)];
    }

    /// <summary>The ids whose chunk of <paramref name="output"/> (from one start-of-post line to the next) holds the statement's clause.</summary>
    private static HashSet<string> IdsStatingAcceptance(string output, Regex start)
    {
        var starts = start.Matches(output);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < starts.Count; i++)
        {
            var end = i + 1 < starts.Count ? starts[i + 1].Index : output.Length;
            if (output[starts[i].Index..end].Contains(Statement, StringComparison.Ordinal)) ids.Add(starts[i].Groups["id"].Value);
        }

        return ids;
    }

    /// <summary>
    /// R10.79 against this Forum: after the asker accepts one of two answers through <c>curia_resolve</c>, each line-form verb states the
    /// acceptance for exactly the posts the Forum's own thread route serves with <c>accepted: true</c> (one of them: non-empty), and for none
    /// of the question or the other answer. Each row is a verb; <c>read</c> is run for each of the three posts.
    /// </summary>
    [Theory]
    [InlineData("read")]
    [InlineData("thread")]
    [InlineData("board")]
    [InlineData("search")]
    public async Task R10_79_EveryVerbNamesExactlyThePostsTheForumServesAsAccepted(string verb)
    {
        var thread = await BuildAsync();
        await ResolveAsync(thread, thread.AnswerIds[0]);

        var accepted = await ForumAcceptedAsync(thread.QuestionId);
        Assert.Equal([thread.AnswerIds[0]], accepted);
        string[] everyPost = [thread.QuestionId, .. thread.AnswerIds];

        HashSet<string> shown;
        var forumFlag = new[] { "--agent", thread.AskerSlug, "--forum", Address };
        switch (verb)
        {
            case "read":
                shown = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in everyPost)
                {
                    var read = await CuriaAsync(["read", id, .. forumFlag]);
                    Assert.True(read.Exit == 0, read.Stdout + read.Stderr);
                    Assert.Contains(id, read.Stdout, StringComparison.Ordinal);
                    if (read.Stdout.Contains(Statement, StringComparison.Ordinal)) shown.Add(id);
                }

                break;
            case "thread":
                var threadRun = await CuriaAsync(["thread", thread.QuestionId, .. forumFlag]);
                Assert.True(threadRun.Exit == 0, threadRun.Stdout + threadRun.Stderr);
                Assert.Equal(everyPost.Order(StringComparer.Ordinal), FramePost().Matches(threadRun.Stdout).Select(m => m.Groups["id"].Value).Order(StringComparer.Ordinal));
                shown = IdsStatingAcceptance(threadRun.Stdout, FramePost());
                break;
            case "board":
                var board = await CuriaAsync(["board", thread.Board, "--titles", .. forumFlag]);
                Assert.True(board.Exit == 0, board.Stdout + board.Stderr);
                Assert.All(everyPost, id => Assert.Contains(id, board.Stdout, StringComparison.Ordinal));
                shown = IdsStatingAcceptance(board.Stdout, IndexRow());
                break;
            default:
                var search = await CuriaAsync(["search", thread.Word, "--board", thread.Board, "--titles", .. forumFlag]);
                Assert.True(search.Exit == 0, search.Stdout + search.Stderr);
                Assert.All(everyPost, id => Assert.Contains(id, search.Stdout, StringComparison.Ordinal));
                shown = IdsStatingAcceptance(search.Stdout, IndexRow());
                break;
        }

        Assert.True(
            shown.SetEquals(accepted),
            $"curia {verb} printed the acceptance for {shown.Intersect(accepted).Count()} of the {accepted.Length} posts the Forum serves as accepted, and for {shown.Except(accepted).Count()} it does not");
    }

    /// <summary>
    /// R11.41 against this Forum: <c>curia_read</c> of a thread says <c>accepted none</c> on the line after the root line before an
    /// acceptance, and after one names the id the Forum's own thread document serves as accepted.
    /// </summary>
    [Fact]
    public async Task R11_41_ACuriaReadOfAThreadNamesTheAcceptedAnswerTheForumServes()
    {
        var thread = await BuildAsync();
        var store = new ProfileStore(_home);
        Assert.True(store.Load(thread.AskerSlug).TryGetValue(out var asker, out var error), error?.Detail);
        _owned.Add(asker!);
        await using var session = await SessionAsync(asker!);

        var before = await session.CallAsync("curia_read", new() { ["postId"] = thread.AnswerIds[0] });
        Assert.False(before.IsError, before.Text);
        var beforeLines = before.Blocks[0].Split('\n');
        Assert.StartsWith("thread root=", beforeLines[0], StringComparison.Ordinal);
        Assert.True(beforeLines.Length > 1 && beforeLines[1].StartsWith("accepted none", StringComparison.Ordinal),
            $"before any acceptance, the first block does not say 'accepted none' on the line after the root line:\n{before.Blocks[0]}");

        await ResolveAsync(thread, thread.AnswerIds[0]);
        var accepted = Assert.Single(await ForumAcceptedAsync(thread.QuestionId));

        var after = await session.CallAsync("curia_read", new() { ["postId"] = thread.AnswerIds[1] });
        Assert.False(after.IsError, after.Text);
        var afterLines = after.Blocks[0].Split('\n');
        Assert.True(afterLines.Length > 1 && afterLines[1].StartsWith("accepted answer=", StringComparison.Ordinal) && afterLines[1].Contains(JsonSerializer.Serialize(accepted), StringComparison.Ordinal),
            $"after the acceptance, the line after the root line does not name {accepted}:\n{after.Blocks[0]}");
        Assert.DoesNotContain(JsonSerializer.Serialize(thread.AnswerIds[1]), afterLines[1], StringComparison.Ordinal);
    }

    /// <summary>
    /// R8.66 against this Forum: a hand-signed answer whose <c>refs</c> holds one readable item and one spelled <c>value</c> is admitted (R8.62's
    /// open question, observed), stored and served, and both readers say how many of the signed items they did not read. The counts are from
    /// the stored canonical form, by <c>System.Text.Json</c>.
    /// </summary>
    [Fact]
    public async Task R8_66_ARealForumServesAPostWhoseEvidenceTheReaderCannotReadAndBothReadersSayHowMany()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var board = "board-" + suffix;
        var tokenEndpoint = Address + "/oauth/token";
        var postsUrl = Address + "/v1/posts";

        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - Tenure);
        var helper = ForumAgent.Create($"https://agents.example/signer-{suffix}", $"signer-{suffix}");
        var (dpop, _) = await helper.AuthenticateAsync(forum.Client, tokenEndpoint, forum.Now, Ct);
        await forum.AttestOwnerAsync(helper.AgentId, Ct, owner: "owner:" + helper.Kid);
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
        {
            var warm = Guid.NewGuid().ToString("N");
            using var warmed = await dpop.PostAsync(forum.Client, postsUrl, await dpop.GetTokenAsync(forum.Client, tokenEndpoint, forum.Now, Ct),
                helper.SignQuestion(board, $"warmup {i} {warm}", $"Warmup {i} {warm}", forum.Now), forum.Now, Ct);
            Assert.Equal(HttpStatusCode.Created, warmed.StatusCode);
        }

        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var asker = "asker-" + suffix;
        Assert.Equal(0, (await CuriaAsync("enrol", "--agent", asker, "--forum", Address)).Exit);
        var asked = await CuriaAsync("ask", "--agent", asker, "--board", board, "--title", "What does the evidence say", "--body", "A question to answer with evidence.", "--forum", Address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var questionId = ReceiptValue(asked.Stdout, "posted");

        var refs = Parse("""[{"kind":"url","target":"https://a.example/readable"},{"kind":"url","value":"https://b.example/spelled-value"}]""");
        using var answered = await dpop.PostAsync(forum.Client, postsUrl, await dpop.GetTokenAsync(forum.Client, tokenEndpoint, forum.Now, Ct),
            helper.SignAnswer(board, "An answer whose second reference is spelled value.", questionId, forum.Now, refs: refs), forum.Now, Ct);
        var receipt = await answered.Content.ReadAsStringAsync(Ct);
        Assert.True(answered.StatusCode == HttpStatusCode.Created, $"the Forum did not admit the post: {receipt}");
        var answerId = JsonDocument.Parse(receipt).RootElement.GetProperty("post_id").GetString()!;

        // What the author signed, from the stored canonical form: two items, one of which has no kind and target.
        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"{Address}/v1/posts/{answerId}", Ct);
        using var canonical = JsonDocument.Parse(served.GetProperty("canonical").GetString()!);
        var items = canonical.RootElement.GetProperty("refs").EnumerateArray().ToArray();
        var unreadable = items.Count(i => !(i.ValueKind == JsonValueKind.Object && i.TryGetProperty("kind", out _) && i.TryGetProperty("target", out _)));
        Assert.Equal((2, 1), (items.Length, unreadable));
        var expected = $"{items.Length - unreadable} reference(s)";

        var read = await CuriaAsync("read", answerId, "--agent", asker, "--forum", Address);
        Assert.True(read.Exit == 0, read.Stdout + read.Stderr);
        var verbLine = read.Stdout.Split('\n').FirstOrDefault(l => l.StartsWith("refs      ", StringComparison.Ordinal));
        Assert.True(verbLine is not null && verbLine.Contains(expected, StringComparison.Ordinal) && verbLine.Contains($"{unreadable} NOT READ", StringComparison.Ordinal),
            $"curia read of a post with {items.Length} signed references, {unreadable} unreadable, printed: {verbLine ?? "(no refs line)"}");

        Assert.True(new ProfileStore(_home).Load(asker).TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        await using var session = await SessionAsync(agent!);
        var viaTool = await session.CallAsync("curia_read", new() { ["postId"] = answerId });
        Assert.False(viaTool.IsError, viaTool.Text);
        var toolLine = viaTool.Text.Split('\n').FirstOrDefault(l => l.StartsWith("refs      ", StringComparison.Ordinal));
        Assert.True(toolLine is not null && toolLine.Contains(expected, StringComparison.Ordinal) && toolLine.Contains($"{unreadable} NOT READ", StringComparison.Ordinal),
            $"curia_read of a post with {items.Length} signed references, {unreadable} unreadable, printed: {toolLine ?? "(no refs line)"}");
    }

    private static JsonValue Parse(string raw) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(raw), AdmitLimits.Default).Match(v => v, e => throw new InvalidOperationException(e.Type));

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
