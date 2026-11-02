using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R8.85 and R11.78 (errata G56), the cost side: NAM12 (the route's half), NAM14, NAM30 and NAM36. A filter is never refused for its grammar; a reply onto a
/// stored board the grammar refuses is refused at the door but accepted under a qualifying board; the reply tools say so before they sign; and the readers serve a
/// stored refused board as they did. The refusals and the reads are two halves of one decision (the grammar protects new posts; stored boards are read as they
/// are), so each fact that asserts a refusal asserts the read beside it.
///
/// <para><b>Oracles.</b> A stored refused board is appended by <see cref="ForumFixture.AppendStoredPostAsync"/> (hand-signed, or signed by a profile's agent through
/// <see cref="ProfileAgentSigner"/> where the tool must be the author), never through the door. What was stored is counted from the <c>events</c> table by SQL on the
/// fixture's own connection string. The reply tools are called in process over the Forum's client; their "nothing was sent" is the events count not moving.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed partial class BoardStoredReadersEndToEndTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-board-stored-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    private async Task<long> PostEventsAsync()
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = 'post.accepted';", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task<EnrolledAgent> EnrolProfileAgentAsync()
    {
        var slug = $"stored-{Guid.NewGuid().ToString("N")[..8]}";
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

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A tool that throws instead of answering NOT SENT is the red the probe records, with the exception's words.")]
    private static async Task<(ModelContextProtocol.Protocol.CallToolResult? Result, string? Threw)> TryAsync(Func<Task<ModelContextProtocol.Protocol.CallToolResult>> call)
    {
        try
        {
            return (await call(), null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- NAM12 (the route's half) ---------------------------------------------------------------

    /// <summary>
    /// NAM12: a filter is never refused for its grammar: <c>board=Meta</c>, <c>board=a%20b</c>, <c>board=%2E%2E</c> and a 200-character board answer 200 (empty where nothing
    /// matches), and the positive pair in the same fact: a present blank <c>board=</c> is refused (R9.54).
    /// </summary>
    [Fact]
    public async Task R8_85_NAM12_AFilterIsNeverRefusedForItsGrammarButABlankOneIs()
    {
        var served = new List<string>();
        foreach (var filter in new[] { "Meta", "a%20b", "%2E%2E", new string('x', 200) })
        {
            using var response = await forum.Client.GetAsync(new Uri($"/v1/search?q=x&board={filter}", UriKind.Relative), Ct);
            served.Add($"{(filter.Length > 10 ? "200-character" : filter)}: {(int)response.StatusCode}");
        }

        Said(["Meta: 200", "a%20b: 200", "%2E%2E: 200", "200-character: 200"], served);

        using var blank = await forum.Client.GetAsync(new Uri("/v1/search?q=x&board=", UriKind.Relative), Ct);
        Said("400", ((int)blank.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- NAM14 ----------------------------------------------------------------------------------

    /// <summary>
    /// NAM14: a reply onto a stored refused board. Through the door an answer with board <c>Meta-x</c> is 400 even though its parent is stored on it; with a qualifying
    /// board the same parent takes the answer (201), the thread lists it and the refused board's listing does not (the Forum compares no board with its parent's). The
    /// first leg is the decision, the second its stated cost.
    /// </summary>
    [Fact]
    public async Task R8_85_NAM14_AReplyOnARefusedBoardIsRefusedAndOnAQualifyingBoardIsTaken()
    {
        var stem = Suffix();
        var refused = $"Meta-{stem}";
        var qualifying = $"meta-{stem}";
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "asker", Ct);
        var (questionId, _) = await forum.AppendStoredPostAsync(asker.Agent.SignQuestion(refused, "A body " + Unrelated(), "A title " + Unrelated(), forum.Now), Ct);
        var answerer = await AcceptedAnswerScene.T1Async(forum, qualifying, Ct);

        var onRefused = await RevisionProbe.SendAsync(forum, answerer, answerer.Agent.SignAnswer(refused, "An answer " + Unrelated(), questionId, forum.Now), cancellationToken: Ct);
        var onQualifying = await RevisionProbe.SendAsync(forum, answerer, answerer.Agent.SignAnswer(qualifying, "An answer " + Unrelated(), questionId, forum.Now), cancellationToken: Ct);

        Said(
            [$"{PublishedBoardWords.BoardStatus()} {PublishedBoardWords.BoardType()}", "201"],
            [onRefused.Says, onQualifying.Status.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        var thread = await forum.Client.GetFromJsonAsync<JsonElement>(new Uri($"/v1/threads/{questionId}", UriKind.Relative), Ct);
        Assert.Contains(thread.EnumerateArray(), p => p.GetProperty("post_id").GetString() == onQualifying.PostId);
        var listed = await forum.Client.GetFromJsonAsync<JsonElement>(new Uri($"/v1/boards/{Uri.EscapeDataString(refused)}/posts", UriKind.Relative), Ct);
        Assert.DoesNotContain(listed.EnumerateArray(), p => p.GetProperty("post_id").GetString() == onQualifying.PostId);
        Assert.Contains(listed.EnumerateArray(), p => p.GetProperty("post_id").GetString() == questionId);
    }

    // ---- NAM30 ----------------------------------------------------------------------------------

    /// <summary>
    /// NAM30: the reply tools on a stored refused board. <c>curia_answer</c>, <c>curia_comment</c> and <c>curia_revise</c> of a stored <c>Meta-x</c> post (the profile's
    /// own agent is its author, so the revise leg reaches the board check; stored by the direct-append seam) are <c>NOT SENT</c>: the client's refusal text, then the
    /// sentence naming the post id; the events table does not move. The controls: a comment and a revision under a qualifying post are posted.
    /// </summary>
    [Fact]
    public async Task R11_78_NAM30_TheReplyToolsSayTheBoardIsTheRepliedToPostsBeforeTheySign()
    {
        var stem = Suffix();

        // The controls first, by an agent of their own (a T0 agent may post three times a day and a stored post counts): a qualifying parent takes a comment and a revision.
        var second = await EnrolProfileAgentAsync();
        var secondTools = ToolsFor(second);
        var (goodPost, _) = await forum.AppendStoredPostAsync(
            ProfileAgentSigner.Sign(second, PostKind.Question, $"meta-{stem}", "A good body " + Unrelated(), "A good title " + Unrelated(), null, null, forum.Now), Ct);
        var controlsBefore = await PostEventsAsync();
        var comment = await secondTools.CommentAsync(goodPost, "A comment " + Unrelated(), null, Ct);
        var revision = await secondTools.ReviseAsync(goodPost, "A correction " + Unrelated(), null, null, null, Ct);
        Assert.True(comment.IsError != true, string.Join(' ', comment.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text)));
        Assert.True(revision.IsError != true, string.Join(' ', revision.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text)));
        Assert.Equal(controlsBefore + 2, await PostEventsAsync());

        var agent = await EnrolProfileAgentAsync();
        var tools = ToolsFor(agent);
        var (refusedPost, _) = await forum.AppendStoredPostAsync(
            ProfileAgentSigner.Sign(agent, PostKind.Question, $"Meta-{stem}", "A stored body " + Unrelated(), "A stored title " + Unrelated(), null, null, forum.Now), Ct);
        var before = await PostEventsAsync();

        var results = new Dictionary<string, (ModelContextProtocol.Protocol.CallToolResult? Result, string? Threw)>
        {
            ["curia_answer"] = await TryAsync(() => tools.AnswerAsync(refusedPost, "An answer " + Unrelated(), null, Ct)),
            ["curia_comment"] = await TryAsync(() => tools.CommentAsync(refusedPost, "A comment " + Unrelated(), null, Ct)),
            ["curia_revise"] = await TryAsync(() => tools.ReviseAsync(refusedPost, "A correction " + Unrelated(), null, null, null, Ct)),
        };

        var problems = new List<string>();
        foreach (var (tool, (result, threw)) in results)
        {
            if (result is null)
            {
                problems.Add($"{tool}: the call threw (the Forum's refusal reached the agent, so the tool went on to send): {Fold(threw ?? string.Empty)[..Math.Min(160, Fold(threw ?? string.Empty).Length)]}");
                continue;
            }

            var text = Fold(string.Join(' ', result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text)));
            if (result.IsError != true) problems.Add($"{tool}: not an error:\n{text}");
            else if (!text.StartsWith("NOT SENT", StringComparison.Ordinal)) problems.Add($"{tool}: not NOT SENT:\n{text}");
            else if (!text.Contains(Fold($"\"{PublishedBoardWords.BoardType()}: {PublishedBoardWords.BoardTitle()} (board: not-lower-case)\""), StringComparison.Ordinal)) problems.Add($"{tool}: lacks the client's refusal text:\n{text}");
            else if (!text.Contains(Fold(PublishedBoardWords.ReplySentenceFor(refusedPost)), StringComparison.Ordinal)) problems.Add($"{tool}: lacks the reply sentence naming the post:\n{text}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Equal(before, await PostEventsAsync());
    }

    // ---- NAM36 (the tools' half) ----------------------------------------------------------------

    /// <summary>
    /// NAM36 (a control, green on the seam; held by FNAM13): <c>curia_read</c> of a post on a stored refused board reads it, verified, as it did before this PR; the
    /// command-line half is the Kestrel class's. The positive pair is NAM11's new-post refusal.
    /// </summary>
    [Fact]
    public async Task R8_85_NAM36_CuriaReadServesAPostOnAStoredRefusedBoard()
    {
        var agent = await EnrolProfileAgentAsync();
        var tools = ToolsFor(agent);
        var (post, _) = await forum.AppendStoredPostAsync(
            ProfileAgentSigner.Sign(agent, PostKind.Question, $"Meta-{Suffix()}", "A stored body " + Unrelated(), "A stored title " + Unrelated(), null, null, forum.Now), Ct);

        var read = await tools.ReadAsync(post, Ct);

        Assert.True(read.IsError != true, string.Join(' ', read.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text)));
        Assert.Contains(post, string.Join(' ', read.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(b => b.Text)), StringComparison.Ordinal);
    }
}
