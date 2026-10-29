using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.OperatorTool;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R8.77 and R11.61 (errata G45), probes RVO37, RVO38 and RVO39, the exercise's case end to end: two <c>curia revision</c> processes started together, and two
/// <c>curia_revise</c> tool calls issued in parallel on one session (what a host that issues parallel tool calls does), against a real Forum on a real socket.
///
/// <para><b>Oracle.</b> The revisions the <c>events</c> table holds for the digest the original's own receipt returned, never a command's or a tool's report of
/// itself (the eighth exercise counted the database). The clients' guards (R11.47, R11.51) refuse a second revision after a verified read; these facts are
/// about the race they cannot see.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class RevisionRaceEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();

    private readonly string _home = Directory.CreateTempSubdirectory("curia-g45-e2e-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    /// <summary>Runs <c>curia &lt;args&gt;</c> from this suite's output, with the profile store under a temporary home.</summary>
    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "curia.dll"));
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["CURIA_CLIENT_HOME"] = _home;

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await stdout, await stderr);
    }

    private static (string PostId, string Digest) ReadReceipt(string stdout)
    {
        var found = Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value);
        return (found["posted"], found["digest"]);
    }

    /// <summary>
    /// RVO37. Two <c>curia revision</c> processes started together by one agent, five times, each on a post of its own: the table holds one revision of the
    /// post; one process exits 0 and the other exits 3 with R11.61's words (never exit 0 twice). The unmodified tree: both exit 0 and two are stored, in 5 of 5.
    /// </summary>
    [Fact]
    public async Task R11_61_RVO37_TwoRevisionProcessesTogetherStoreOneAndTheOtherIsToldWhy()
    {
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var address = forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        var lines = new List<string>();

        for (var trial = 1; trial <= 5; trial++)
        {
            var board = Board();
            var agent = "race" + trial + Suffix();
            Assert.Equal(0, (await CuriaAsync("enrol", "--agent", agent, "--forum", address)).Exit);
            var asked = await CuriaAsync("ask", "--agent", agent, "--board", board, "--title", "Original " + Unrelated(), "--body", "The original " + Unrelated(), "--forum", address);
            Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
            var (postId, digest) = ReadReceipt(asked.Stdout);

            var pair = await Task.WhenAll(
                Task.Run(() => CuriaAsync("revision", "--agent", agent, "--board", board, "--parent", postId, "--body", "First correction " + Unrelated(), "--forum", address), Ct),
                Task.Run(() => CuriaAsync("revision", "--agent", agent, "--board", board, "--parent", postId, "--body", "Second correction " + Unrelated(), "--forum", address), Ct));

            var stored = await RevisionsOfAsync(forum, digest);
            var exits = string.Join(",", pair.Select(p => p.Exit).Order());
            var refused = pair.Where(p => p.Exit != 0).ToArray();
            var refusedText = refused.Length == 1 ? refused[0].Stderr + refused[0].Stdout : string.Empty;
            var worded = refusedText.Contains(RevisionWording.AlreadyRevisedCause, StringComparison.OrdinalIgnoreCase)
                || refusedText.Contains(RevisionWording.InFlightCause, StringComparison.OrdinalIgnoreCase);
            lines.Add($"trial {trial}: stored {stored}; exits {exits}; {(worded ? "the refused one says why" : "no refusal worded by R11.61")}");
        }

        Said(
            Enumerable.Range(1, 5).Select(t => $"trial {t}: stored 1; exits 0,3; the refused one says why"),
            lines);
    }

    private async Task<EnrolledAgent> EnrolAgentAsync(string stem)
    {
        var slug = $"{stem}-{Suffix()}";
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

    private static string Flatten(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static async Task<(bool Error, string Text)> TryReviseAsync(ForumTools tools, string postId, string body)
    {
        try
        {
            var result = await tools.ReviseAsync(postId, body, null, null, null, Ct);
            return (result.IsError == true, Flatten(result));
        }
        catch (McpException refused)
        {
            return (true, refused.Message);
        }
    }

    /// <summary>
    /// RVO38. What a host does: two <c>curia_revise</c> calls issued in parallel on one session: one is POSTED and the other is an error that says why in
    /// R11.61's words; the table holds one revision. The unmodified tree: both POSTED, two stored.
    /// </summary>
    [Fact]
    public async Task R11_61_RVO38_TwoParallelReviseCallsPostOneAndTheOtherIsToldWhy()
    {
        var agent = await EnrolAgentAsync("parallel");
        var tools = ToolsFor(agent);
        var board = "g45-" + Suffix();
        var asked = Flatten(await tools.AskAsync(board, "Parallel " + Unrelated(), "A question " + Unrelated(), null, null, Ct));
        Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);
        var digest = Regex.Match(asked, @"^digest\s+(?<d>sha256:[0-9a-f]{64})", RegexOptions.Multiline).Groups["d"].Value;
        var postId = JsonSerializer.Deserialize<string>(asked.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            Assert.Equal(0, await OperatorCommands.RunAsync(
                ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, Ct, readLogSigningKeyPem: () => LogKeyPem));
        }

        var pair = await Task.WhenAll(
            Task.Run(() => TryReviseAsync(tools, postId, "First correction " + Unrelated()), Ct),
            Task.Run(() => TryReviseAsync(tools, postId, "Second correction " + Unrelated()), Ct));

        var posted = pair.Count(p => !p.Error && p.Text.StartsWith("POSTED", StringComparison.Ordinal));
        var refused = pair.Where(p => p.Error).ToArray();
        var says = refused.Length == 1
            && (refused[0].Text.Contains(RevisionWording.AlreadyRevisedCause, StringComparison.OrdinalIgnoreCase)
                || refused[0].Text.Contains(RevisionWording.InFlightCause, StringComparison.OrdinalIgnoreCase));
        Said(
            "1 POSTED; 1 error that says why in R11.61's words; stored 1",
            $"{posted} POSTED; {refused.Length} error{(says ? " that says why in R11.61's words" : string.Empty)}; stored {await RevisionsOfAsync(forum, digest)}");
    }

    /// <summary>
    /// RVO39, control. The guards stay the first line: after a first revision, a second <c>curia_revise</c> is refused by the client as <c>NOT SENT</c> and the Forum
    /// received no submission for it (the events table agrees). Green now; no falsification row names it (it holds the clients' guard, which the Forum's gate must not replace).
    /// </summary>
    [Fact]
    public async Task R11_61_RVO39_ASequentialSecondReviseIsRefusedByTheClientBeforeAnythingIsSent()
    {
        var agent = await EnrolAgentAsync("sequential");
        var tools = ToolsFor(agent);
        var board = "g45-" + Suffix();
        var asked = Flatten(await tools.AskAsync(board, "Sequential " + Unrelated(), "A question " + Unrelated(), null, null, Ct));
        Assert.StartsWith("POSTED", asked, StringComparison.Ordinal);
        var digest = Regex.Match(asked, @"^digest\s+(?<d>sha256:[0-9a-f]{64})", RegexOptions.Multiline).Groups["d"].Value;
        var postId = JsonSerializer.Deserialize<string>(asked.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;
        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            Assert.Equal(0, await OperatorCommands.RunAsync(
                ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, Ct, readLogSigningKeyPem: () => LogKeyPem));
        }

        var first = await TryReviseAsync(tools, postId, "First correction " + Unrelated());
        var second = await TryReviseAsync(tools, postId, "Second correction " + Unrelated());

        Said(
            "first POSTED; second NOT SENT; stored 1",
            $"first {(first.Error ? "an error" : first.Text.Split('\n')[0])}; second {(second.Error && second.Text.StartsWith("NOT SENT", StringComparison.Ordinal) ? "NOT SENT" : second.Text.Split('\n')[0])}; stored {await RevisionsOfAsync(forum, digest)}");
    }
}
