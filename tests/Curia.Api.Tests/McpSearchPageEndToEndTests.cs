using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G36 against a real Forum on a real socket, by <c>curia-mcp</c> over the protocol: a question hit says whether its thread is settled and by which
/// answer (R10.81, R11.50), following the answer's own post id from a search to the accepted answer; and a cursor presented under another query reaches the
/// model as a refusal worded by its cause, naming the cursor and none of the query's values (R9.35, R11.50).
///
/// <para><b>Oracles.</b> The answer a reader must name is the post id the events table holds for the digest <c>curia_answer</c>'s own receipt returned, read
/// by SQL, never anything a reader printed; the time that lets an agent answer is the fixture's clock, moved past Table 11's tenure window; the cursor is the one
/// a first page printed, taken from its text.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class McpSearchPageEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-search-page-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^digest\s+(?<digest>sha256:[0-9a-f]{64})", RegexOptions.Multiline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ReceiptDigest();

    [GeneratedRegex(@"next_cursor=\\?""(?<cursor>[^""\\]+)", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NextCursor();

    private async Task<EnrolledAgent> EnrolAsync(string stem)
    {
        var slug = $"{stem}-{Guid.NewGuid().ToString("N")[..8]}";
        Assert.True(
            new ProfileStore(_home).Create(slug, $"https://agents.example/{slug}", $"{slug}-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error),
            error?.Detail);
        _owned.Add(agent!);

        var enrolled = await new ForumClient(forum.Client, forum.Client.BaseAddress!).EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var refusal), refusal?.Summary);
        return agent!;
    }

    private ForumTools ToolsFor(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_home), forum.Clock);
        return new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock));
    }

    private async Task<McpProtocolSession> SessionAsync(EnrolledAgent agent) =>
        await McpProtocolSession.StartAsync(ToolsFor(agent), agent.Profile.AgentId);

    private static string Flatten(CallToolResult result) =>
        string.Join('\n', result.Content.Select(block => block switch
        {
            TextContentBlock text => text.Text,
            EmbeddedResourceBlock { Resource: TextResourceContents resource } => resource.Text,
            _ => string.Empty,
        }));

    private static string ReceiptPostId(string receipt) =>
        JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

    private async Task<string> EventIdOfAsync(string digest)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand("SELECT event_id FROM events WHERE event_type = 'post.accepted' AND payload->>'digest' = @digest;", connection);
        query.Parameters.AddWithValue("digest", digest);
        return (string)(await query.ExecuteScalarAsync(Ct))!;
    }

    private static string[] AnswersLines(string output) =>
        [.. output.Split('\n').Where(l => l.StartsWith("answers  ", StringComparison.Ordinal))];

    [Fact]
    public async Task R10_81_ARealForumsQuestionIsFollowedFromNoneToTheAcceptedAnswer()
    {
        var asker = await EnrolAsync("asker");
        var board = "mcp-" + Guid.NewGuid().ToString("N")[..8];
        var word = "quartzheron" + Guid.NewGuid().ToString("N")[..8];

        await using var session = await SessionAsync(asker);
        var asked = Flatten(await ToolsFor(asker).AskAsync(board, $"Which {word} settles it", $"A question about the {word}.", null, null, Ct));
        Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);
        var question = ReceiptPostId(asked);

        async Task<string> SearchTextAsync()
        {
            var search = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = word, ["board"] = board } });
            Assert.False(search.IsError, search.Text);
            return search.Text;
        }

        // Before an answer exists: the hit says none accepted.
        var before = AnswersLines(await SearchTextAsync());
        Assert.True(before.Length == 1 && before[0].StartsWith("answers  none accepted", StringComparison.Ordinal), $"the question hit printed: [{string.Join(" | ", before)}]");

        var (answerId, answerDigest) = await AnswerAsT1Async(board, question, word);

        // An answer nobody accepted does not settle the question.
        var unsettled = AnswersLines(await SearchTextAsync());
        Assert.True(unsettled.Length == 1 && unsettled[0].StartsWith("answers  none accepted", StringComparison.Ordinal), $"after an unaccepted answer the question hit printed: [{string.Join(" | ", unsettled)}]");

        // The asker accepts it through curia_resolve; the hit now names the answer, by the id the events table holds for the digest.
        var resolved = Flatten(await ToolsFor(asker).ResolveAsync(answerId, Ct));
        Assert.StartsWith("ACCEPTED", resolved, StringComparison.Ordinal);
        var eventId = await EventIdOfAsync(answerDigest);
        Assert.Equal(answerId, eventId);

        var after = AnswersLines(await SearchTextAsync());
        Assert.True(after.Length == 1 && after[0].Contains($"accepted: post \"{eventId}\"", StringComparison.Ordinal), $"after the acceptance the question hit printed: [{string.Join(" | ", after)}]");

        // Control: the answer's own read still prints its thread line (R10.79), and carries no answers line (it is not a question).
        var read = await session.CallAsync("curia_read", new() { ["postId"] = answerId });
        Assert.False(read.IsError, read.Text);
        Assert.Contains("thread    accepted by the thread's asker", read.Text, StringComparison.Ordinal);
        var answerPassage = read.Blocks.Single(b => b.StartsWith($"post      \"{answerId}\"", StringComparison.Ordinal));
        Assert.Empty(AnswersLines(answerPassage));
    }

    /// <summary>A T1 agent answers: an attested owner, three questions, and the tenure clock (Table 11); the fixture's clock is the fake one.</summary>
    private async Task<(string AnswerId, string AnswerDigest)> AnswerAsT1Async(string board, string question, string word)
    {
        var answerer = await EnrolAsync("answerer");
        await forum.AttestOwnerAsync(answerer.Profile.AgentId, Ct, owner: "owner:answerer-" + Guid.NewGuid().ToString("N")[..8]);
        for (var i = 0; i < 3; i++)
        {
            var warmup = Flatten(await ToolsFor(answerer).AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"A warmup question {i} {Guid.NewGuid():N} about nothing here.", null, null, Ct));
            Assert.StartsWith("POSTED", warmup, StringComparison.Ordinal);
        }

        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1));
        var answered = Flatten(await ToolsFor(answerer).AnswerAsync(question, $"Use exponential backoff for the {word}.", null, Ct));
        Assert.StartsWith("POSTED", answered, StringComparison.Ordinal);
        var digest = ReceiptDigest().Match(answered).Groups["digest"].Value;
        Assert.NotEmpty(digest);
        return (ReceiptPostId(answered), digest);
    }

    /// <summary>
    /// Control for the fact above: the flow it relies on is sound on this fixture whatever a reader prints. The answer is posted by a T1 agent, accepted by the
    /// asker through <c>curia_resolve</c>, named by the events table's id for its digest, and its own read prints the thread line R10.79 gave it.
    /// </summary>
    [Fact]
    public async Task R10_81_TheFlowTheProbeFollowsIsSoundOnThisFixture()
    {
        var asker = await EnrolAsync("asker");
        var board = "mcp-" + Guid.NewGuid().ToString("N")[..8];
        var word = "quartzwren" + Guid.NewGuid().ToString("N")[..8];
        await using var session = await SessionAsync(asker);
        var question = ReceiptPostId(Flatten(await ToolsFor(asker).AskAsync(board, $"Which {word} settles it", $"A question about the {word}.", null, null, Ct)));
        var (answerId, answerDigest) = await AnswerAsT1Async(board, question, word);

        Assert.StartsWith("ACCEPTED", Flatten(await ToolsFor(asker).ResolveAsync(answerId, Ct)), StringComparison.Ordinal);
        Assert.Equal(answerId, await EventIdOfAsync(answerDigest));

        var read = await session.CallAsync("curia_read", new() { ["postId"] = answerId });
        Assert.False(read.IsError, read.Text);
        Assert.Contains("thread    accepted by the thread's asker", read.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_50_AMismatchReachesTheModelWorkedByItsCause()
    {
        var agent = await EnrolAsync("pager");
        var board = "mcp-" + Guid.NewGuid().ToString("N")[..8];
        var word = "quartzfinch" + Guid.NewGuid().ToString("N")[..8];

        await using var session = await SessionAsync(agent);
        for (var i = 0; i < 3; i++)
        {
            var asked = Flatten(await ToolsFor(agent).AskAsync(board, $"Which {word} number {i} is it {Guid.NewGuid():N}", $"A question {i} about the {word} {Guid.NewGuid():N}.", null, null, Ct));
            Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);
        }

        var first = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = word, ["board"] = board, ["limit"] = 1 } });
        Assert.False(first.IsError, first.Text);
        var cursorMatch = NextCursor().Match(first.Text);
        Assert.True(cursorMatch.Success, $"page one printed no cursor:\n{first.Text}");
        var cursor = cursorMatch.Groups["cursor"].Value;

        // Control: the same query continues with its own cursor.
        var same = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = word, ["board"] = board, ["limit"] = 1, ["cursor"] = cursor } });
        Assert.False(same.IsError, same.Text);

        // Another limit is another query.
        var refused = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = word, ["board"] = board, ["limit"] = 2, ["cursor"] = cursor } });

        Assert.True(refused.IsError, $"expected a refusal for a cursor presented under another limit, got a page:\n{refused.Text}");
        Assert.Contains("cursor", refused.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(PublishedSearchPage.MismatchType(), refused.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(word, refused.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(board, refused.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(cursor, refused.Text, StringComparison.Ordinal);
    }
}
