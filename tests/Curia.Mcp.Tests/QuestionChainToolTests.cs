using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// PR 3 (errata G50, R11.67), Task 0's probes RLW32 (the description's half) and RLW45 (the tool's half) at the adapter: <c>curia_answer</c>'s description states
/// the walk's bound, and an answer to a question whose <c>superseded_by</c> lists two revisions is told FORKED, with each branch named, that none was followed
/// and no latest revision. The words are R11.67's (<see cref="PublishedReadersLastWords"/>); the stub serves the list each row names.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class QuestionChainToolTests : IDisposable
{
    private const string QuestionId = "01RLWQUESTION00000000000A1";
    private const string FirstId = "01RLWBRANCH0000000000000A1";
    private const string SecondId = "01RLWBRANCH0000000000000A2";

    private static readonly string FirstDigest = "sha256:" + new string('4', 64);
    private static readonly string SecondDigest = "sha256:" + new string('5', 64);

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-question-chain-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<string> QuestionLineAsync(ImmutableArray<Successor> list)
    {
        var question = new PostDraft { Kind = PostKind.Question, Board = "b", Title = "A question about finches", Body = "Which finch is this?" };
        _log.ServePost(QuestionId, question, _log.SignDraft(question), list);

        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using var owned = agent;
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        await using var session = await McpProtocolSession.StartAsync(new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer), writer.Agent.Profile.AgentId, agent);
        var answer = await session.CallAsync("curia_answer", new() { ["parentPostId"] = QuestionId, ["body"] = "It is a goldfinch." });
        Assert.False(answer.IsError, answer.Text);
        return Assert.Single(answer.Text.Split('\n'), l => l.StartsWith("question", StringComparison.Ordinal));
    }

    /// <summary>
    /// RLW45 (the tool's half; added 2026-10-30, ruling 17). For a question whose list holds two successors <c>curia_answer</c>'s <c>question</c> line says FORKED,
    /// names each by post id, says that no branch was followed (R11.67's sentence) and names no latest revision; one successor in the same fact states its latest.
    /// </summary>
    [Fact]
    public async Task R11_67_RLW45_AnAnswerToAForkedQuestionIsToldNoBranchWasFollowed()
    {
        var (_, _, forked) = PublishedReadersLastWords.WalkWords();
        var none = PublishedReadersLastWords.NoneFollowed();

        var fork = await QuestionLineAsync([new Successor(FirstDigest, FirstId), new Successor(SecondDigest, SecondId)]);

        var problems = new List<string>();
        if (!fork.Contains(forked, StringComparison.Ordinal)) problems.Add($"Expected: FORKED Actual: {fork}");
        if (!fork.Contains(FirstId, StringComparison.Ordinal) || !fork.Contains(SecondId, StringComparison.Ordinal)) problems.Add($"Expected: both branches named Actual: {fork}");
        if (!fork.Contains(none, StringComparison.Ordinal)) problems.Add($"Expected: the none-followed sentence \"{none}\" Actual: {fork}");
        if (fork.Contains("is the latest", StringComparison.Ordinal)) problems.Add($"a fork names a latest revision: {fork}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>RLW32 (the description's half; the help's is in the client's <c>NotServedWordsTests</c>). <c>curia_answer</c>'s description carries R11.67's bound sentence and <c>NOT WALKED</c>.</summary>
    [Fact]
    public void R11_67_RLW32_TheDescriptionOfAnswerStatesTheBound()
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using var owned = agent;
        var writer = new ForumWriter(agent!, new ForumSession(client, agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        var description = PublishedReadersLastWords.Folded(
            ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, new HeadStore(Path.Combine(Path.GetTempPath(), "curia-tool-descriptions-never-written")), writer))
                .Single(t => t.ProtocolTool.Name == "curia_answer").ProtocolTool.Description ?? string.Empty);

        var missing = new[] { PublishedReadersLastWords.BoundSentence(), PublishedReadersLastWords.WalkWords().NotWalked }.Where(s => !description.Contains(s, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"Expected: curia_answer's description to carry {string.Join(" and ", missing.Select(m => $"\"{m}\""))} Actual: {description}");
    }
}
