using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.30 (errata G30) at the readers: the count of results the floor removed reaches the person at <c>curia search</c> and the
/// agent at <c>curia_search</c>. The command is the shipped binary in a child process against a Forum on a real socket, and the
/// tool is called through a real session against the same Forum.
///
/// <para><b>Oracle.</b> The number of answers this test posted (each at V0, so a V1 floor removes all of them), never a number a
/// reader printed. The <c>not stated</c> half needs a Forum build that serves no <c>excluded_by_floor</c>, which the real Forum no
/// longer is, so it is a scripted response on a loopback socket.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class SearchFloorReadersEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private const int AnswersPosted = 3;

    private static readonly TimeSpan Tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);

    private readonly string _home = Directory.CreateTempSubdirectory("curia-search-floor-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^floor\s.*$", RegexOptions.Multiline)]
    private static partial Regex FloorLine();

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A question by one agent and <see cref="AnswersPosted"/> answers by a T1 agent, all carrying one nonce word.</summary>
    private async Task<(string Board, string Word, EnrolledAgent Reader)> SeedAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var board = "board-" + suffix;
        var word = "quartzheron" + suffix;

        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - Tenure);
        var helper = await EnrolAsync("helper-" + suffix);
        await forum.AttestOwnerAsync(helper.Profile.AgentId, Ct, owner: "owner:" + helper.Profile.Slug);
        var helperTools = ToolsFor(helper);
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
            Assert.StartsWith("POSTED", Flatten(await helperTools.AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, Ct)), StringComparison.Ordinal);
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

        var asker = await EnrolAsync("asker-" + suffix);
        var asked = Flatten(await ToolsFor(asker).AskAsync(board, $"Which {word} is it", $"A question about the {word}.", null, null, Ct));
        Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);
        var questionId = PostedId(asked);

        for (var i = 0; i < AnswersPosted; i++)
            Assert.StartsWith("POSTED", Flatten(await helperTools.AnswerAsync(questionId, $"Answer {i} about the {word}: {Guid.NewGuid():N}.", null, Ct)), StringComparison.Ordinal);

        return (board, word, asker);
    }

    [Fact]
    public async Task R9_30_TheTerminalPrintsTheFloorsCountAndNotStatedForAMissingOne()
    {
        var (board, word, reader) = await SeedAsync();

        var removed = await CuriaAsync("search", word, "--board", board, "--min-verification", "V1", "--agent", reader.Profile.Slug, "--forum", Address);
        Assert.True(removed.Exit == 0, removed.Stdout + removed.Stderr);
        var line = Assert.Single(FloorLine().Matches(removed.Stdout)).Value;
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"removed by floor {AnswersPosted};"), line, StringComparison.Ordinal);
        Assert.EndsWith("disputed 0", line.TrimEnd(), StringComparison.Ordinal);

        // A Forum build that serves no excluded_by_floor: the line says so and does not say 0.
        var scripted = await CuriaAgainstScriptedForumAsync(
            """{"results":[],"next_cursor":null,"floor":{"surface":"search","min_verification":"V0","source":"published","applies_to":["answer","finding"],"not_applicable_to":["question","comment"]},"model":"hashed-ngram@1","corpus_bound":0}""");
        Assert.True(scripted.Exit == 0, scripted.Stdout + scripted.Stderr);
        var scriptedLine = Assert.Single(FloorLine().Matches(scripted.Stdout)).Value;
        Assert.Contains("removed by floor not stated; disputed not stated", scriptedLine, StringComparison.Ordinal);
        Assert.DoesNotContain("removed by floor 0", scriptedLine, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_30_CuriaSearchPrintsTheFloorsCountBesideTheResultCount()
    {
        var (board, word, reader) = await SeedAsync();
        await using var session = await McpProtocolSession.StartAsync(ToolsFor(reader), reader.Profile.AgentId);

        var gated = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = word, ["board"] = board, ["min_verification"] = "V1" } });
        Assert.False(gated.IsError, gated.Text);
        Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"excluded_by_floor={AnswersPosted} excluded_disputed=0"), gated.Blocks[0], StringComparison.Ordinal);

        // The control: the same corpus under a V0 floor removes nothing, and says 0 and not by omission.
        var open = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = word, ["board"] = board, ["min_verification"] = "V0" } });
        Assert.False(open.IsError, open.Text);
        Assert.Contains("excluded_by_floor=0 excluded_disputed=0", open.Blocks[0], StringComparison.Ordinal);
    }

    /// <summary>The command against a loopback socket that answers every request with <paramref name="searchJson"/>.</summary>
    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAgainstScriptedForumAsync(string searchJson)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var serving = Task.Run(
            async () =>
            {
                while (!Ct.IsCancellationRequested)
                {
                    using var connection = await listener.AcceptTcpClientAsync(Ct);
                    var stream = connection.GetStream();
                    var buffer = new byte[8192];
                    _ = await stream.ReadAsync(buffer, Ct);
                    var body = Encoding.UTF8.GetBytes(searchJson);
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, Ct);
                    await stream.WriteAsync(body, Ct);
                }
            },
            Ct);
        var address = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);

        var run = await CuriaAsync("search", "anything", "--forum", address);
        listener.Stop();
        await Task.WhenAny(serving);
        return run;
    }

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args) =>
        await CuriaProcess.RunAsync(_home, Ct, args);

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

        var enrolled = await new ForumClient(forum.Client, forum.Client.BaseAddress!).EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var refusal), refusal?.Summary);
        return agent!;
    }

    /// <summary>The id a receipt printed as a display literal (R10.63), recovered by a JSON parser.</summary>
    private static string PostedId(string receipt) =>
        System.Text.Json.JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

    private static string Flatten(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}
