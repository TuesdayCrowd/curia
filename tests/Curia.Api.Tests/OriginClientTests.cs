using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Api.Tests.OriginProbe;

namespace Curia.Api.Tests;

/// <summary>
/// The consumers of errata G82 (the plan's Task 5): the shipped <c>curia</c> and the MCP adapter's tools, over a Forum on a real socket whose origin is a reserved loopback port.
/// <b>ORG11c</b> is the positive path, the real <c>curia enrol</c>, <c>ask</c> and <c>read</c> under the origin's own name (exit 0 each), which holds that no client change was needed. <b>ORG11d</b> addresses the
/// same Forum by a name that is not the origin and asserts what a reader meets: the Forum's own problem type, <c>curia</c>'s exit 3 and never <c>curia/client/unreadable-problem</c>, and the same type through
/// <c>curia-mcp</c>'s tools (R11.30's read tools, <c>curia_search</c> and <c>curia_read</c>, and a signed write). When <c>CURIA_CAPTURE_DIR</c> names a directory, each page is written there for the cold reads
/// CRO-a and CRO-b; nothing is written otherwise.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class OriginClientTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-origin-client-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Own => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private string Another => $"http://localhost:{forum.Client.BaseAddress!.Port.ToString(CultureInfo.InvariantCulture)}";

    [GeneratedRegex(@"^posted\s+""?(?<id>[^\s""]+)", RegexMultiline)]
    private static partial Regex Posted();

    private const RegexOptions RegexMultiline = RegexOptions.Multiline;

    /// <summary>Writes a page for the cold readers when the run asks for it; the port and the temporary home are replaced so that a page names neither.</summary>
    private void Capture(string name, string text)
    {
        if (Environment.GetEnvironmentVariable("CURIA_CAPTURE_DIR") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var port = forum.Client.BaseAddress!.Port.ToString(CultureInfo.InvariantCulture);
        var page = text.Replace(_home, "<home>", StringComparison.Ordinal).Replace(port, "<port>", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(dir, name + ".txt"), page);
    }

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(string page, params string[] args)
    {
        var result = await CuriaProcess.RunAsync(_home, Ct, args);
        Capture(page, $"$ curia {string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a))}\nexit {result.Exit.ToString(CultureInfo.InvariantCulture)}\n--- stdout\n{result.Stdout}--- stderr\n{result.Stderr}");
        return result;
    }

    /// <summary>ORG11c. The real <c>curia</c> under the origin's own name: enrol, ask and read each exit 0, and the post read is the one asked. Green on the tree and after; it is the proof that Task 3 asked nothing of the client.</summary>
    [Fact]
    public async Task R5_26_ORG11c_TheRealCuriaEnrolsAsksAndReadsUnderTheOriginsName()
    {
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);   // a child process signs its proofs with the real clock
        var slug = "org11c-" + Guid.NewGuid().ToString("N")[..8];
        var enrolled = await CuriaAsync("own-enrol", "enrol", "--agent", slug, "--forum", Own);
        Assert.True(enrolled.Exit == 0, enrolled.Stdout + enrolled.Stderr);

        var board = "org11c-" + Guid.NewGuid().ToString("N")[..8];
        var asked = await CuriaAsync("own-ask", FirstAsk.Args(slug, board, "Origin question " + Guid.NewGuid().ToString("N")[..8], "Is the name the Forum was told the one every credential is made for?", Own, makesBoard: true));
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);
        var id = Posted().Match(asked.Stdout).Groups["id"].Value;
        Assert.NotEmpty(id);

        var read = await CuriaAsync("own-read", "read", id, "--forum", Own);
        Assert.True(read.Exit == 0, read.Stdout + read.Stderr);
    }

    /// <summary>
    /// ORG11d. The same Forum addressed as <c>localhost</c>, which its origin does not name. <c>curia</c> exits 3 for search, read, enrol and ask, each stating the Forum's type and none
    /// <c>unreadable-problem</c>; the MCP adapter's <c>curia_search</c> and <c>curia_read</c> relay the same type as a refusal. The pages are captured for CRO-a.
    /// </summary>
    [Fact]
    public async Task R5_27_ORG11d_AReaderAddressedUnderAnotherNameIsToldTheForumsOwnWords()
    {
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);
        var slug = "org11d-" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(0, (await CuriaProcess.RunAsync(_home, Ct, "enrol", "--agent", slug, "--forum", Own)).Exit);
        var failures = new List<string>();

        var runs = new (string Page, string[] Args, int Exit)[]
        {
            ("other-search", ["search", "zebra", "--board", "org11d", "--forum", Another], 3),
            ("other-read", ["read", "sha256-absent", "--forum", Another], 3),
            ("other-enrol", ["enrol", "--agent", "org11d-new-" + Guid.NewGuid().ToString("N")[..8], "--forum", Another], 3),
            // exit 8, not 3: the ask first reads the board list, and a read it could not make is "NOT SENT ... run the command again" (a cold-read finding for CRO-a: that advice does not fit a name refusal).
            ("other-ask", FirstAsk.Args(slug, "org11d", "A question asked under another name", "Does it say what refused it?", Another, makesBoard: true), 8),
        };
        foreach (var (page, args, exit) in runs)
        {
            var result = await CuriaAsync(page, args);
            Expect(failures, result.Exit == exit, $"{page}: expected exit {exit.ToString(CultureInfo.InvariantCulture)}, got {result.Exit.ToString(CultureInfo.InvariantCulture)}; stderr: {result.Stderr.Trim()}");
            Expect(failures, result.Stderr.Contains(HostRefusalType, StringComparison.Ordinal), $"{page}: stderr does not carry {HostRefusalType}: {result.Stderr.Trim()}");
            Expect(failures, !result.Stderr.Contains("unreadable-problem", StringComparison.Ordinal), $"{page}: stderr carries unreadable-problem: {result.Stderr.Trim()}");
        }

        // The adapter's tools, over the client they are built on, dialled at the same wrong name.
        using var http = new HttpClient { BaseAddress = new Uri(Another) };
        var client = new ForumClient(http, http.BaseAddress);
        var tools = new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home));
        await using var session = await McpProtocolSession.StartAsync(tools, agentId: null);
        foreach (var (page, tool, arguments) in new (string, string, Dictionary<string, object?>)[]
        {
            ("other-mcp-search", "curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = "zebra", ["board"] = "org11d" } }),
            ("other-mcp-read", "curia_read", new() { ["postId"] = "sha256-absent" }),
        })
        {
            var answer = await session.CallAsync(tool, arguments);
            Capture(page, $"curia-mcp tool {tool}\nis_error {answer.IsError}\n--- text\n{answer.Text}\n");
            Expect(failures, answer.IsError, $"{page}: expected a refusal, the tool answered: {answer.Text}");
            Expect(failures, answer.Text.Contains(HostRefusalType, StringComparison.Ordinal), $"{page}: the refusal does not carry {HostRefusalType}: {answer.Text}");
            Expect(failures, !answer.Text.Contains("unreadable-problem", StringComparison.Ordinal), $"{page}: the refusal carries unreadable-problem: {answer.Text}");
        }

        NoFailures(failures, "item 374: every consumer of a Forum addressed by a name it does not serve is told in the Forum's own words");
    }
}
