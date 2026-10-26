using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Application.Ports;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R8.70 (errata G38) through <c>curia</c>, the shipped command in a child process against a real Forum on a real socket: <c>curia answer --parent</c>
/// naming a comment exits 3 (a refusal of the submission's own content), prints the cause, and stores nothing; the control, <c>--parent</c> naming the
/// question, exits 0. <c>--board</c> is a free argument of the verb, so the Forum is the only party that can refuse the parent here.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class CliParentEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-cli-parent-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(CancellationToken ct, params string[] args) =>
        await CuriaProcess.RunAsync(_home, ct, args);

    private static string PostIdOf(string stdout) =>
        Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value)["posted"];

    private async Task<int> LogLengthAsync(CancellationToken ct) =>
        (await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(ct))
            .Match(r => r.Count, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    [Fact]
    public async Task R8_70_CuriaAnswerWithACommentParentExitsThreeAndStoresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);

        // An answerer enrolled and warmed up as if 49 hours ago, through the reference client's own writer (as CliRefsAnswerEndToEndTests does).
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - tenure);
        var store = new ProfileStore(_home);
        Assert.True(store.Create("answerer", "https://agents.example/answerer", "answerer-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var created), created?.Detail);
        _owned.Add(agent!);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        Assert.True((await client.EnrolAsync(agent!, ct)).TryGetValue(out _, out var refusal), refusal?.Summary);
        await forum.AttestOwnerAsync(agent!.Profile.AgentId, ct, owner: "owner:answerer");

        var session = new ForumSession(client, agent, store, forum.Clock);
        var tools = new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock));
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
            await tools.AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, ct);

        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

        Assert.Equal(0, (await CuriaAsync(ct, "enrol", "--agent", "asker-p", "--forum", address)).Exit);
        var asked = await CuriaAsync(ct, "ask", "--agent", "asker-p", "--board", board, "--title", "Which entry does this pin", "--body", "a question body", "--forum", address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var questionId = PostIdOf(asked.Stdout);
        var commented = await CuriaAsync(ct, "comment", "--agent", "asker-p", "--board", board, "--parent", questionId, "--body", "a comment on the question", "--forum", address);
        Assert.True(commented.Exit == 0, commented.Stdout + commented.Stderr);
        var commentId = PostIdOf(commented.Stdout);
        var before = await LogLengthAsync(ct);

        var refused = await CuriaAsync(ct, "answer", "--agent", "answerer", "--board", board, "--parent", commentId, "--body", "an answer under the comment", "--forum", address);

        Assert.True(refused.Exit == 3, $"curia answer --parent <a comment> exited {refused.Exit}:\n{refused.Stdout}{refused.Stderr}");
        Assert.Contains(PublishedParentRule.Slugs()[1], refused.Stdout + refused.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, await LogLengthAsync(ct));

        var control = await CuriaAsync(ct, "answer", "--agent", "answerer", "--board", board, "--parent", questionId, "--body", "an answer under the question", "--forum", address);
        Assert.True(control.Exit == 0, $"curia answer --parent <the question> exited {control.Exit}:\n{control.Stdout}{control.Stderr}");
        Assert.Equal(before + 1, await LogLengthAsync(ct));
    }
}
