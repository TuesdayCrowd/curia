using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.OperatorTool;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R11.47 (errata G34) against a real Forum on a real socket, the pattern of <c>McpSupersessionEndToEndTests</c>: the guards hold where the
/// Forum's own rules are in play (a fork is a thing the Forum accepts, and a spent budget is a thing the Forum judges before it compares
/// authors).
///
/// <para><b>Oracles.</b> The revisions the log holds, counted from the <c>events</c> table by the digest the original's own receipt returned,
/// and the posts an agent made, counted from the same table; the revision id a refusal must name is the id the first revision's receipt
/// returned. Never a tool's report of itself.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ReviseGuardEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();

    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-g34-e2e-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^digest\s+(?<digest>sha256:[0-9a-f]{64})", RegexOptions.Multiline)]
    private static partial Regex ReceiptDigest();

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

    private async Task SignHeadAsync()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        Assert.Equal(0, await OperatorCommands.RunAsync(
            ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, Ct, readLogSigningKeyPem: () => LogKeyPem));
    }

    private static string Flatten(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string ReceiptPostId(string receipt) =>
        JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

    /// <summary>A revision attempt as the caller sees it: the result when one came back, and its text, or the refusal's message when the tool threw.</summary>
    private static async Task<(CallToolResult? Result, string Text)> TryReviseAsync(ForumTools tools, string postId, string body)
    {
        try
        {
            var result = await tools.ReviseAsync(postId, body, null, null, null, Ct);
            return (result, Flatten(result));
        }
        catch (McpException refused)
        {
            return (null, refused.Message);
        }
    }

    /// <summary>The revisions the log holds whose signed <c>prev</c> is <paramref name="digest"/>: counted from the table.</summary>
    private async Task<long> RevisionsOfAsync(string digest)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'revision' " +
            "AND (payload->>'canonical')::jsonb->>'prev' = @digest",
            admin);
        command.Parameters.AddWithValue("digest", digest);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    /// <summary>The posts the log holds whose author is <paramref name="agentId"/>: counted from the table.</summary>
    private async Task<long> PostsByAsync(string agentId)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @author",
            admin);
        command.Parameters.AddWithValue("author", agentId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);
    }

    private async Task<(string PostId, string Digest, string Board)> AskAsync(EnrolledAgent agent, string title, string body)
    {
        var board = "g34-" + Guid.NewGuid().ToString("N")[..8];
        var unique = Guid.NewGuid().ToString("N")[..10];
        var asked = Flatten(await ToolsFor(agent).AskAsync(board, $"{title} {unique}", $"{body} ({unique})", null, null, Ct));
        Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);
        var digest = ReceiptDigest().Match(asked).Groups["digest"].Value;
        Assert.NotEmpty(digest);
        return (ReceiptPostId(asked), digest, board);
    }

    /// <summary>Three questions on nothing in common, so the Forum's near-duplicate check (R8.18) refuses none of them.</summary>
    private static readonly (string Title, string Body)[] Unrelated =
    [
        ("Certificate pinning", "How should a mobile client rotate a pinned leaf certificate without bricking installed apps?"),
        ("Postgres vacuum", "Why does autovacuum fall behind on a table with heavy updates and long running transactions?"),
        ("Cache invalidation", "What is a safe strategy for invalidating a read-through cache after a write to the backing store?"),
    ];

    /// <summary>
    /// A second revision of a post that already has one is refused <c>NOT SENT</c>, naming the first revision as the receipt returned it, and the
    /// log still holds one revision of the post: counted from the table, not from the tool.
    /// </summary>
    [Fact]
    public async Task R11_47_ASecondRevisionOfOnePostIsRefusedByARealForumLog()
    {
        var author = await EnrolAsync("reviser");
        var (original, digest, _) = await AskAsync(author, "Retry policy", "I retry a flaky call three times with a fixed one second delay. Is exponential backoff with jitter better?");
        await SignHeadAsync();

        var first = Flatten(await ToolsFor(author).ReviseAsync(original, "A corrected question.", null, null, null, Ct));
        Assert.StartsWith("POSTED", first, StringComparison.Ordinal);
        var revisionId = ReceiptPostId(first);
        Assert.Equal(1, await RevisionsOfAsync(digest));

        var (result, text) = await TryReviseAsync(ToolsFor(author), original, "A second corrected question.");

        Assert.Equal(1, await RevisionsOfAsync(digest));
        Assert.NotNull(result);
        Assert.True(result.IsError, text);
        Assert.StartsWith("NOT SENT", text, StringComparison.Ordinal);
        Assert.Contains(revisionId, text, StringComparison.Ordinal);
    }

    /// <summary>
    /// With the posting budget spent, a revision of another agent's post is refused as not the caller's and not as <c>rate-budget</c>, and
    /// nothing is added to the log. The control: the same agent revising its own post with the budget still spent gets the Forum's budget
    /// refusal, so the client's check does not swallow the Forum's.
    /// </summary>
    [Fact]
    public async Task R11_47_ASpentBudgetDoesNotHideThatThePostIsNotTheCallers()
    {
        var owner = await EnrolAsync("owner");
        var (strangers, _, _) = await AskAsync(owner, "Queue depth", "How does queue depth interact with consumer concurrency limits in a worker pool?");
        var stranger = await EnrolAsync("stranger");
        var budget = TierPolicy.PostsPerDay(PrincipalTier.T0);

        string ownPost = string.Empty;
        for (var i = 0; i < budget; i++)
        {
            var (id, _, _) = await AskAsync(stranger, Unrelated[i].Title, Unrelated[i].Body);
            if (i == 0) ownPost = id;
        }

        Assert.Equal(budget, await PostsByAsync(stranger.Profile.AgentId));
        await SignHeadAsync();

        var (result, text) = await TryReviseAsync(ToolsFor(stranger), strangers, "A rewrite of someone else's question.");

        Assert.DoesNotContain("rate-budget-exhausted", text, StringComparison.Ordinal);
        Assert.NotNull(result);
        Assert.True(result.IsError, text);
        Assert.StartsWith("NOT SENT", text, StringComparison.Ordinal);
        Assert.Equal(budget, await PostsByAsync(stranger.Profile.AgentId));

        var (_, control) = await TryReviseAsync(ToolsFor(stranger), ownPost, "A rewrite of my own question.");

        Assert.Contains("table-11/rate-budget-exhausted", control, StringComparison.Ordinal);
    }
}
