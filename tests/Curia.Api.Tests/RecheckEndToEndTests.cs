using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G35 (R9.34, R11.49) against a real Forum on a real socket: <c>curia_recheck</c> follows a citation to the post ids and standings the log
/// holds, and says FORKED where two revisions chain to one post.
///
/// <para><b>Oracles.</b> The post ids are the <c>events</c> table's (<c>post.accepted</c> rows, selected by the digest the host computed over the
/// bytes it signed and by the signed <c>prev</c>), never anything the tool printed; the log's event count before and after the call shows that
/// the tool wrote nothing.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RecheckEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-recheck-").FullName;
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

    private async Task<(EnrolledAgent Agent, string Slug)> EnrolAsync(string stem)
    {
        var slug = $"{stem}-{Guid.NewGuid().ToString("N")[..8]}";
        Assert.True(
            new ProfileStore(_home).Create(slug, $"https://agents.example/{slug}", $"{slug}-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error),
            error?.Detail);
        _owned.Add(agent!);

        var enrolled = await new ForumClient(forum.Client, forum.Client.BaseAddress!).EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var refusal), refusal?.Summary);
        return (agent!, slug);
    }

    private ForumTools ToolsFor(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_home), forum.Clock);
        return new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock));
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

    private static string Digest(string receipt) => ReceiptDigest().Match(receipt).Groups["digest"].Value;

    private async Task<string[]> PostIdsOfDigestAsync(string digest)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT event_id FROM events WHERE event_type = 'post.accepted' AND payload->>'digest' = @value ORDER BY seq", admin);
        command.Parameters.AddWithValue("value", digest);
        return await IdsAsync(command);
    }

    private async Task<string[]> RevisionIdsOfAsync(string digest)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT event_id FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'revision' " +
            "AND (payload->>'canonical')::jsonb->>'prev' = @value ORDER BY seq", admin);
        command.Parameters.AddWithValue("value", digest);
        return await IdsAsync(command);
    }

    private static async Task<string[]> IdsAsync(NpgsqlCommand command)
    {
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) ids.Add(reader.GetString(0));
        return [.. ids];
    }

    private async Task<long> EventCountAsync()
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events", admin);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--forum");
        start.ArgumentList.Add(Address);
        start.Environment["CURIA_CLIENT_HOME"] = _home;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await stdout, await stderr);
    }

    /// <summary>The line for an element asked about: the state word, then the digest. A superseded line also holds its revisions' digests, so a bare Contains finds two lines for a revision's digest.</summary>
    private static string LineNaming(string text, string digest) =>
        Assert.Single(text.Split('\n'), l => System.Text.RegularExpressions.Regex.IsMatch(l, "^[A-Za-z]+ +\"" + System.Text.RegularExpressions.Regex.Escape(digest) + "\""));

    [Fact]
    public async Task R11_49_ARealForumsStandingsAreFollowedToTheLogsIds()
    {
        var (agent, _) = await EnrolAsync("recheck");
        var board = "g35-" + Guid.NewGuid().ToString("N")[..8];
        var tools = ToolsFor(agent);

        var asked = Flatten(await tools.AskAsync(board, $"Which finch {Guid.NewGuid():N}", "A question about a finch.", null, null, Ct));
        Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);
        var question = Digest(asked);
        var revised = Flatten(await tools.ReviseAsync(PostIdFrom(asked), "Corrected: which finch, exactly?", null, null, null, Ct));
        Assert.StartsWith("POSTED", revised, StringComparison.Ordinal);
        var revision = Digest(revised);
        var nobody = "sha256:" + new string('0', 64);

        // The oracles: the ids the log holds for each digest, read from the table.
        var questionId = Assert.Single(await PostIdsOfDigestAsync(question));
        var revisionId = Assert.Single(await RevisionIdsOfAsync(question));
        Assert.Equal(revisionId, Assert.Single(await PostIdsOfDigestAsync(revision)));

        var before = await EventCountAsync();
        await using var session = await McpProtocolSession.StartAsync(tools, agent.Profile.AgentId);
        var answer = await session.CallAsync("curia_recheck", new() { ["digests"] = new[] { question, revision, nobody, "nope" } });

        Assert.False(answer.IsError, answer.Text);
        var superseded = LineNaming(answer.Text, question);
        Assert.Contains("superseded", superseded, StringComparison.Ordinal);
        Assert.Contains(questionId, superseded, StringComparison.Ordinal);
        Assert.Contains(revisionId, superseded, StringComparison.Ordinal);
        Assert.DoesNotContain("FORKED", superseded, StringComparison.Ordinal);
        Assert.Contains("current", LineNaming(answer.Text, revision), StringComparison.Ordinal);
        Assert.Contains("unknown", LineNaming(answer.Text, nobody), StringComparison.Ordinal);
        Assert.Contains("digests[3]", answer.Text, StringComparison.Ordinal);
        Assert.Equal(before, await EventCountAsync());
    }

    [Fact]
    public async Task R11_49_AForkedCitationIsFORKEDAndNamesBothRevisions()
    {
        // The CLI stamps real time on what it signs and proves; the fixture's clock is fixed, and R6.32 refuses the future.
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

        var (agent, _) = await EnrolAsync("fork");
        var board = "g35-" + Guid.NewGuid().ToString("N")[..8];
        var tools = ToolsFor(agent);

        var asked = Flatten(await tools.AskAsync(board, $"Which wren {Guid.NewGuid():N}", "A question about a wren.", null, null, Ct));
        var question = Digest(asked);
        var postId = PostIdFrom(asked);
        Assert.StartsWith("POSTED", Flatten(await tools.ReviseAsync(postId, "First correction: which wren?", null, null, null, Ct)), StringComparison.Ordinal);

        // curia_revise refuses a post already superseded (G34), and so does `curia revision` (R11.51), so the second revision is signed in
        // process through the client library: the fork the Forum cannot prevent and the reference client will not make.
        var draft = new PostDraft { Kind = PostKind.Revision, Board = board, Body = "Second correction: which wren, then?", Parent = postId, Prev = question };
        Assert.True(SubmissionBuilder.Build(agent, draft, DateTimeOffset.UtcNow).TryGetValue(out var submission, out var buildError), buildError?.Detail);
        var forkSession = new ForumSession(new ForumClient(forum.Client, forum.Client.BaseAddress!), agent, new ProfileStore(_home), forum.Clock);
        var posted = await forkSession.SubmitAsync(submission!.Wire, Ct);
        Assert.True(posted.TryGetValue(out _, out var refusal), refusal?.Summary);

        var revisionIds = await RevisionIdsOfAsync(question);
        Assert.Equal(2, revisionIds.Length);

        await using var session = await McpProtocolSession.StartAsync(tools, agent.Profile.AgentId);
        var answer = await session.CallAsync("curia_recheck", new() { ["digests"] = new[] { question } });

        Assert.False(answer.IsError, answer.Text);
        var line = LineNaming(answer.Text, question);
        Assert.Contains("FORKED", line, StringComparison.Ordinal);
        foreach (var id in revisionIds)
            Assert.Contains(id, line, StringComparison.Ordinal);
    }

    private static string PostIdFrom(string receipt) =>
        System.Text.Json.JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;
}
