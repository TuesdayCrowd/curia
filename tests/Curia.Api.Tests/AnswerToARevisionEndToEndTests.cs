using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
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
/// Errata G42 (R11.55, decision 9a) against a real Forum on a real socket: a T1 agent that answers the revision of a question is refused by the
/// Forum, and <c>curia_answer</c> adds the question the revision revises, as the Forum's own <c>superseded_by</c> confirms it; the same tool
/// answering the revised question itself is accepted and states the question's standing in its receipt; and <c>curia answer --parent</c> naming
/// the revision is refused with the Forum's words and, a recorded non-property, no pointer.
///
/// <para><b>Oracles.</b> The post ids and digests are the receipts' of the calls that made them; the event count is the <c>events</c> table's, read by
/// SQL before and after each call, never anything a tool printed; the refusal's problem type is the one R8.70's entry names
/// (<see cref="PublishedParentRule"/>).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class AnswerToARevisionEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-answer-revision-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    [GeneratedRegex(@"^digest\s+(?<digest>sha256:[0-9a-f]{64})", RegexOptions.Multiline)]
    private static partial Regex ReceiptDigest();

    private ForumTools ToolsFor(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_home), forum.Clock);
        return new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock));
    }

    private async Task<EnrolledAgent> EnrolAsync(string slug)
    {
        Assert.True(
            new ProfileStore(_home).Create(slug, $"https://agents.example/{slug}", $"{slug}-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error),
            error?.Detail);
        _owned.Add(agent!);
        Assert.True((await new ForumClient(forum.Client, forum.Client.BaseAddress!).EnrolAsync(agent!, Ct)).TryGetValue(out _, out var refusal), refusal?.Summary);
        return agent!;
    }

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

    private static string PostIdFrom(string receipt) =>
        System.Text.Json.JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

    private async Task<long> EventCountAsync()
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events", admin);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task G42_TheTwoDoorsAndTheRefusalAreTold()
    {
        var tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);
        var refusalType = PublishedParentRule.Slugs()[1];

        // An answerer enrolled and warmed up as if 49 hours ago (as CliParentEndToEndTests does), then the clock back to real time for the CLI.
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - tenure);
        var answerer = await EnrolAsync("answerer");
        await forum.AttestOwnerAsync(answerer.Profile.AgentId, Ct, owner: "owner:answerer");
        var answererTools = ToolsFor(answerer);
        var board = "g42-" + Guid.NewGuid().ToString("N")[..8];
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
            await answererTools.AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, Ct);
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

        var asker = await EnrolAsync("asker");
        var askerTools = ToolsFor(asker);
        var asked = Flatten(await askerTools.AskAsync(board, $"Which finch {Guid.NewGuid():N}", "A question about a finch.", null, null, Ct));
        Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);
        var questionId = PostIdFrom(asked);
        var revised = Flatten(await askerTools.ReviseAsync(questionId, "Corrected: which finch, exactly?", null, null, null, Ct));
        Assert.StartsWith("POSTED", revised, StringComparison.Ordinal);
        var revisionId = PostIdFrom(revised);
        Assert.NotEqual(questionId, revisionId);

        await using var session = await McpProtocolSession.StartAsync(answererTools, answerer.Profile.AgentId);

        // Door one: curia_answer on the revision. The Forum refuses, the tool says which question the revision revises, and nothing is recorded.
        var before = await EventCountAsync();
        var refused = await session.CallAsync("curia_answer", new() { ["parentPostId"] = revisionId, ["body"] = "It is a goldfinch." });
        Assert.True(refused.IsError, refused.Text);
        Assert.Contains(refusalType, refused.Text, StringComparison.Ordinal);
        Assert.Equal(before, await EventCountAsync());
        Assert.Contains(questionId, refused.Text, StringComparison.Ordinal);

        // Door two: curia_answer on the question itself is accepted, and its receipt states that the question was revised, naming the revision.
        var posted = await session.CallAsync("curia_answer", new() { ["parentPostId"] = questionId, ["body"] = "It is a goldfinch." });
        Assert.False(posted.IsError, posted.Text);
        Assert.StartsWith("POSTED", posted.Text, StringComparison.Ordinal);
        Assert.Equal(before + 1, await EventCountAsync());
        var standing = Assert.Single(posted.Text.Split('\n'), l => l.StartsWith("question", StringComparison.Ordinal));
        Assert.Contains(revisionId, standing, StringComparison.Ordinal);

        // The CLI door: refused with the Forum's words and exit 3, nothing recorded, and (decision 9a, a recorded non-property) no pointer.
        var afterAnswer = await EventCountAsync();
        var cli = await CuriaProcess.RunAsync(_home, Ct, "answer", "--agent", "answerer", "--board", board, "--parent", revisionId, "--body", "An answer under the revision.", "--forum", Address);
        Assert.True(cli.Exit == 3, $"curia answer --parent <a revision> exited {cli.Exit}:\n{cli.Stdout}{cli.Stderr}");
        Assert.Contains(refusalType, cli.Stdout + cli.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(questionId, cli.Stdout + cli.Stderr, StringComparison.Ordinal);
        Assert.Equal(afterAnswer, await EventCountAsync());
    }
}
