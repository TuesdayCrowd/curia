using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.OperatorTool;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G32 against a real Forum on a real socket: an agent that revises its own post (<c>curia_revise</c>) and then reads, searches and verifies
/// the original, by <c>curia-mcp</c> and by the shipped <c>curia</c> binary, is told by every reader that the original was corrected (R9.32, R10.80,
/// R11.44), and the description that promised the report is held to the member that keeps the promise.
///
/// <para><b>Oracles.</b> The revision a reader must name is the digest and id <c>curia_revise</c>'s own receipt returned and the successors the Forum's
/// batch route gives for the original's digest, never anything a reader printed; the exits a reader must keep are the exits it gave before the revision
/// landed. The description property compares what a tool description claims with the member the Forum serves, in both directions, and is not edited to pass.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class McpSupersessionEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();

    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-superseded-").FullName;
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

    private async Task<(string Exit, string Stdout)> RunCliAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--forum");
        start.ArgumentList.Add(Address);
        start.Environment["CURIA_CLIENT_HOME"] = _home;
        start.Environment["CURIA_TESTIS_BIN"] = TestisBinary.Locate();

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        _ = await stderr;
        return (process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), await stdout);
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

    private static string ReceiptPostId(string receipt) =>
        JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

    /// <summary>The lines of <paramref name="output"/> that state a supersession and name <paramref name="digest"/> and <paramref name="id"/>.</summary>
    private static string[] LinesNaming(string output, string digest, string id) =>
        [.. output.Split('\n').Where(l =>
            l.Contains(digest, StringComparison.Ordinal)
            && l.Contains(id, StringComparison.Ordinal)
            && l.Contains("supersed", StringComparison.OrdinalIgnoreCase))];

    private static string[] Stating(string output) =>
        [.. output.Split('\n').Where(l => l.Contains("supersed", StringComparison.OrdinalIgnoreCase))];

    [Fact]
    public async Task R11_44_ARevisedPostIsReportedSupersededByEveryReaderOfTheOriginal()
    {
        var agent = await EnrolAsync("reviser");
        var board = "mcp-" + Guid.NewGuid().ToString("N")[..8];
        var word = "quartzfinch" + Guid.NewGuid().ToString("N")[..8];

        await using var session = await SessionAsync(agent);
        var asked = Flatten(await ToolsFor(agent).AskAsync(board, $"Which {word} is it", $"A question about the {word}.", null, null, Ct));
        Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);
        var original = ReceiptPostId(asked);
        var originalDigest = ReceiptDigest().Match(asked).Groups["digest"].Value;
        Assert.NotEmpty(originalDigest);

        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            Assert.Equal(0, await OperatorCommands.RunAsync(
                ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, Ct, readLogSigningKeyPem: () => LogKeyPem));
        }

        string[][] CliVerbs(string id) =>
        [
            ["read", id],
            ["thread", id],
            ["board", board, "--titles"],
            ["search", word, "--board", board, "--titles"],
            ["verify", id],
        ];

        // The exits before the revision: the verdicts the revision must not move.
        var before = new List<string>();
        foreach (var verb in CliVerbs(original))
            before.Add((await RunCliAsync(verb)).Exit);

        // Non-vacuity: every verb answered, so "the exits did not move" compares real verdicts and not five usage errors.
        Assert.Equal(["0", "0", "0", "0", "0"], before);

        var revised = Flatten(await ToolsFor(agent).ReviseAsync(original, $"Corrected: which {word} is it, exactly?", null, null, null, Ct));
        Assert.StartsWith("POSTED", revised, StringComparison.Ordinal);
        var revisionId = ReceiptPostId(revised);
        var revisionDigest = ReceiptDigest().Match(revised).Groups["digest"].Value;
        Assert.NotEmpty(revisionDigest);

        // The second witness: the Forum's own batch gives the same revision for the original's digest (R9.10).
        using var batch = await forum.Client.PostAsJsonAsync("/v1/posts/batch", new { digests = new[] { originalDigest } }, Ct);
        var successors = JsonDocument.Parse(await batch.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("items")[0].GetProperty("successors")
            .EnumerateArray().Select(s => s.GetString()!).ToArray();
        Assert.Equal([revisionDigest], successors);

        var failures = new List<string>();

        // PR 3 (errata G47, R11.62) changed this fact's object, not its strength: curia_read's first block now carries one more line that names the
        // revision, the root's own (it holds `the text that stands`); that line is asserted apart, and the passage's line is still counted as it was.
        void Expect(string reader, string output, bool firstBlockStatesTheRoot = false)
        {
            var named = LinesNaming(output, revisionDigest, revisionId);
            if (firstBlockStatesTheRoot)
            {
                var firstBlock = named.Where(l => l.Contains("the text that stands", StringComparison.Ordinal)).ToArray();
                if (firstBlock.Length != 1) failures.Add($"{reader}: {firstBlock.Length} first-block line(s) state the root's revision and the text that stands (expected one)");
                // A counted filter, not a set difference: a passage line printed twice stays two lines and fails the count below.
                named = [.. named.Where(l => !l.Contains("the text that stands", StringComparison.Ordinal))];
            }

            if (named.Length != 1)
                failures.Add($"{reader}: {named.Length} line(s) state the supersession and name the revision's digest and id (expected one); the lines that mention supersession: [{string.Join(" | ", Stating(output))}]");
        }

        // The MCP readers, by the protocol.
        var read = await session.CallAsync("curia_read", new() { ["postId"] = original });
        Assert.False(read.IsError, read.Text);
        Expect("curia_read", read.Text, firstBlockStatesTheRoot: true);

        var search = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = word, ["board"] = board } });
        Assert.False(search.IsError, search.Text);
        Expect("curia_search", search.Text);

        var verified = await session.CallAsync("curia_verify", new() { ["postId"] = original });
        Assert.False(verified.IsError, verified.Text);
        Expect("curia_verify", verified.Text);

        // The shipped binary's readers, and their exits.
        var after = new List<string>();
        var verbs = CliVerbs(original);
        for (var i = 0; i < verbs.Length; i++)
        {
            var (exit, stdout) = await RunCliAsync(verbs[i]);
            after.Add(exit);
            Expect($"curia {verbs[i][0]}", stdout);
        }

        Assert.Equal(before, after);

        // The revision's own read names no supersession: nothing corrects it.
        var own = await RunCliAsync("read", revisionId);
        Assert.Empty(Stating(own.Stdout));

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// <b>The property, not edited to pass.</b> <c>claims</c> is whether <c>curia_revise</c>'s description says a post is "reported superseded";
    /// <c>serves</c> is whether a post the Forum serves carries a member that says so. The two are equal, or one of them is a lie (R11.44).
    /// </summary>
    [Fact]
    public async Task R11_44_AToolDescriptionPromisesASupersessionReportOnlyWhereTheForumServesOne()
    {
        var agent = await EnrolAsync("desc");
        var board = "mcp-" + Guid.NewGuid().ToString("N")[..8];
        var asked = Flatten(await ToolsFor(agent).AskAsync(board, $"Description {Guid.NewGuid():N}", "A question so the Forum serves a post.", null, null, Ct));
        var postId = ReceiptPostId(asked);

        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", Ct);
        var members = served.GetProperty("provenance").EnumerateObject().Select(m => m.Name).ToArray();
        Assert.NotEmpty(members);
        var serves = members.Any(m => m.Contains("supersed", StringComparison.OrdinalIgnoreCase));

        await using var session = await SessionAsync(agent);
        var description = (await session.ListAsync()).Single(t => t.Name == "curia_revise").Description ?? string.Empty;
        Assert.NotEmpty(description);
        var claims = Regex.IsMatch(description, "reported superseded", RegexOptions.IgnoreCase);

        Assert.True(
            claims == serves,
            $"R11.44: curia_revise's description {(claims ? "promises" : "does not promise")} a supersession report; the Forum's served post {(serves ? "carries" : "carries none")} (members: {string.Join(", ", members)}).\n{description}");
    }
}
