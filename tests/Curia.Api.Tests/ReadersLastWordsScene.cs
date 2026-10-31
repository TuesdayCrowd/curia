using System.Collections.Immutable;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>A post made in a scene: its id and the digest the Forum returned for it.</summary>
internal sealed record RlwPost(string Id, string Digest);

/// <summary>An enrolled T1 agent with its session, its tools and the profile name the <c>curia</c> command knows it by.</summary>
internal sealed record RlwAgent(EnrolledAgent Agent, ForumSession Session, ForumTools Tools, string Slug);

/// <summary>
/// PR 3 (errata G50), Task 0's shared scene for the probes that need a question, its revisions and an answerer on a real Forum on a real socket: T1 agents
/// (an attested owner, three questions and the tenure clock, which the fixture's clock moves) made as <c>ChainEndToEndTests</c> makes its helper, whose profiles
/// are in one home so the <c>curia</c> command in a child process and the tools in process are the same agent. A revision is hand-signed with the reference
/// client's own builder and submitted through <see cref="ForumSession"/>, so every gate the Forum has applies to it (the revised-once gate among them) and the
/// client's own rules do not; the ids and digests are the receipts the Forum returned, and the chain a probe expects is read from the <c>events</c> table by
/// SQL from the connection string the fixture holds, never from <c>CorrectionIndex</c>.
/// </summary>
internal sealed class ReadersLastWordsScene(KestrelForumFixture forum) : IDisposable
{
    private static readonly TimeSpan Tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);

    private readonly List<IDisposable> _owned = [];

    internal string Home { get; } = Directory.CreateTempSubdirectory("curia-rlw-scene-").FullName;

    internal string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(Home, recursive: true);
    }

    internal static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>A T1 agent: enrolled, owner attested, three clean questions, and the clock back at the present.</summary>
    internal async Task<RlwAgent> T1Async(string stem, string board)
    {
        var suffix = Suffix();
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - Tenure);
        var store = new ProfileStore(Home);
        var slug = $"{stem}-{suffix}";
        Assert.True(store.Create(slug, $"https://agents.example/{slug}", $"{slug}-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var created), created?.Detail);
        _owned.Add(agent!);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        Assert.True((await client.EnrolAsync(agent!, Ct)).TryGetValue(out _, out var refusal), refusal?.Summary);
        await forum.AttestOwnerAsync(agent!.Profile.AgentId, Ct, owner: "owner:" + slug);

        var session = new ForumSession(client, agent, store, forum.Clock);
        var tools = new ForumTools(client, MarkingMode.Datamark, new HeadStore(Home), new ForumWriter(agent, session, forum.Clock));
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
            Assert.StartsWith("POSTED", Flatten(await tools.AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, Ct)), StringComparison.Ordinal);
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        return new RlwAgent(agent, session, tools, slug);
    }

    /// <summary>The same agent with tools that reach the Forum through <paramref name="address"/> (a counting proxy in front of it), and the session they sign through.</summary>
    internal ForumTools ToolsThrough(RlwAgent agent, string address)
    {
        var http = new HttpClient { BaseAddress = new Uri(address) };
        _owned.Add(http);
        var client = new ForumClient(http, new Uri(address));
        var session = new ForumSession(client, agent.Agent, new ProfileStore(Home), forum.Clock);
        return new ForumTools(client, MarkingMode.Datamark, new HeadStore(Home), new ForumWriter(agent.Agent, session, forum.Clock));
    }

    internal static string Flatten(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Join('\n', result.Content.Select(b => b switch
        {
            ModelContextProtocol.Protocol.TextContentBlock t => t.Text,
            ModelContextProtocol.Protocol.EmbeddedResourceBlock { Resource: ModelContextProtocol.Protocol.TextResourceContents r } => r.Text,
            _ => string.Empty,
        }));

    private async Task<RlwPost> SubmitAsync(RlwAgent agent, PostDraft draft)
    {
        Assert.True(SubmissionBuilder.Build(agent.Agent, draft, forum.Now).TryGetValue(out var signed, out var error), error?.Detail);
        var posted = await agent.Session.SubmitAsync(signed!.Wire, MarkingMode.None, Ct);
        Assert.True(posted.TryGetValue(out var receipt, out var refusal), refusal?.Summary);
        return new RlwPost(receipt!.PostId, receipt.Digest);
    }

    internal Task<RlwPost> AskAsync(RlwAgent agent, string board, string word) =>
        SubmitAsync(agent, new PostDraft { Kind = PostKind.Question, Board = board, Title = $"Which {word} is it {Suffix()}", Body = $"A question about the {word} {Guid.NewGuid():N}." });

    /// <summary>A revision signed as <c>curia revision</c> signs one: <c>parent</c> is the post revised (or <paramref name="parent"/>, the author's word, where a fact hand-signs a lie) and <c>prev</c> its digest.</summary>
    internal Task<RlwPost> ReviseAsync(RlwAgent agent, string board, RlwPost revised, string body, string? parent = null) =>
        SubmitAsync(agent, new PostDraft { Kind = PostKind.Revision, Board = board, Parent = parent ?? revised.Id, Prev = revised.Digest, Body = body });

    /// <summary>A question and <paramref name="depth"/> revisions of it, each of the one before, as <c>curia revision</c> would make them; index 0 is the question.</summary>
    internal async Task<List<RlwPost>> QuestionWithRevisionsAsync(RlwAgent asker, string board, string word, int depth)
    {
        var chain = new List<RlwPost> { await AskAsync(asker, board, word) };
        for (var i = 1; i <= depth; i++) chain.Add(await ReviseAsync(asker, board, chain[i - 1], $"Revision {i} of the question about the {word} {Guid.NewGuid():N}."));
        return chain;
    }

    /// <summary>
    /// A fork, made as the register's step says in spirit: the first revision is withheld, the second is hand-signed (the revised-once gate lets it in because the
    /// first is not served) and the first is restored, so the question's <c>superseded_by</c> lists two served revisions. Returns the question and both.
    /// </summary>
    internal async Task<(RlwPost Question, RlwPost First, RlwPost Second)> ForkAsync(RlwAgent asker, string board, string word)
    {
        var question = await AskAsync(asker, board, word);
        var first = await ReviseAsync(asker, board, question, $"First branch of the question about the {word} {Guid.NewGuid():N}.");
        await forum.WithholdAsync(first.Id, Ct);
        var second = await ReviseAsync(asker, board, question, $"Second branch of the question about the {word} {Guid.NewGuid():N}.");
        await forum.RestoreAsync(first.Id, Ct);
        return (question, first, second);
    }

    internal Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args) => CuriaProcess.RunAsync(Home, Ct, args);

    /// <summary>The revisions of <paramref name="digest"/> the <c>events</c> table holds, in log order, as (post id, digest): the oracle for a chain, never the Forum's own index.</summary>
    internal async Task<ImmutableArray<(string Id, string Digest)>> RevisionsOfAsync(string digest)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand(
            "SELECT aggregate_id, payload->>'digest' FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'revision' AND (payload->>'canonical')::jsonb->>'prev' = @digest ORDER BY seq;", connection);
        query.Parameters.AddWithValue("digest", digest);
        var found = ImmutableArray.CreateBuilder<(string, string)>();
        await using var reader = await query.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) found.Add((reader.GetString(0), reader.GetString(1)));
        return found.ToImmutable();
    }

    /// <summary>The chain from <paramref name="question"/> to its head by the first revision at each step, walked over the events table (a fork's first branch).</summary>
    internal async Task<List<(string Id, string Digest)>> ChainFromEventsAsync(RlwPost question)
    {
        var chain = new List<(string, string)>();
        var digest = question.Digest;
        while (true)
        {
            var next = await RevisionsOfAsync(digest);
            if (next.IsEmpty) return chain;
            chain.Add(next[0]);
            digest = next[0].Digest;
        }
    }

    /// <summary>How many answers the log holds whose signed <c>parent</c> is <paramref name="parent"/>, by SQL.</summary>
    internal async Task<long> AnswersTo(string parent)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'answer' AND (payload->>'canonical')::jsonb->>'parent' = @parent;", connection);
        count.Parameters.AddWithValue("parent", parent);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }
}
