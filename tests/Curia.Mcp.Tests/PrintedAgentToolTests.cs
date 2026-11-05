using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// CWK9 (the tool's half), CWK11 (the behaviour), CWK50 and CWK51 (errata G59, R11.82, R11.83; plan ruling 5, ruling 6 and ruling 15): what <c>curia-mcp</c> prints for an agent to run, that it has no
/// fallback agent, and the version the entry bumps to. Each probe is in its failing form: on the seam the printed commands carry no agent and the catalogue is still 0.10.0.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PrintedAgentToolTests : IDisposable
{
    private const string UnknownDigest = "sha256:" + "ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12ab12";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-printed-agent-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Fails with everything found, or passes when nothing was.</summary>
    private static void Red(List<string> problems)
    {
        if (problems.Count > 0) throw new Xunit.Sdk.XunitException("Actual:   " + string.Join("; ", problems));
    }

    private static string Fold(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant);

    private static string TextOf(CallToolResult result) => Fold(string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text)));

    private ForumTools ToolsWithAgent(out EnrolledAgent agent)
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var loaded, out var error), error?.Detail);
        agent = loaded!;
        var writer = new ForumWriter(agent, new ForumSession(_log.Client(), agent, _log.Store, TimeProvider.System), TimeProvider.System);
        return new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
    }

    private ForumTools ToolsWithoutAgent() => new(_log.Client(), MarkingMode.None, new HeadStore(_home));

    /// <summary>
    /// CWK9 (the tool). <c>curia_recheck</c> of a digest the Forum does not know: with an agent configured the resend command in the line carries <c>--agent 'alice'</c> and names <c>curia_resend</c>; with none it
    /// prints <c>--agent &lt;name&gt;</c> with the sentence that <c>&lt;name&gt;</c> is the profile that kept the act, and still names <c>curia_resend</c>. Predicted red: <c>expected "--agent", found none</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK9_TheUnknownLineOfTheToolCarriesTheConfiguredAgentOrThePlaceholder()
    {
        var problems = new List<string>();

        var configured = ToolsWithAgent(out var agent);
        using (agent)
        {
            var text = TextOf(await configured.RecheckAsync([UnknownDigest], TestContext.Current.CancellationToken));
            var command = Regex.Match(text, @"`curia resend " + Regex.Escape(UnknownDigest) + @"([^`]*)`", RegexOptions.CultureInvariant);
            if (!command.Success) problems.Add("configured: no resend command in the unknown line (\"" + (text.Length <= 400 ? text : text[..400]) + "\")");
            else if (command.Groups[1].Value != " --agent 'alice'") problems.Add($"configured: expected \"--agent 'alice'\", found \"{(command.Groups[1].Value.Length == 0 ? "none" : command.Groups[1].Value.Trim())}\"");
            if (!text.Contains("curia_resend", StringComparison.Ordinal)) problems.Add("configured: the line does not name curia_resend");
        }

        var bare = TextOf(await ToolsWithoutAgent().RecheckAsync([UnknownDigest], TestContext.Current.CancellationToken));
        var placeholder = Regex.Match(bare, @"`curia resend " + Regex.Escape(UnknownDigest) + @"([^`]*)`", RegexOptions.CultureInvariant);
        if (!placeholder.Success || placeholder.Groups[1].Value != " --agent <name>") problems.Add($"no agent: expected \"--agent <name>\", found \"{(placeholder.Success ? placeholder.Groups[1].Value.Trim() : "none")}\"");
        if (!bare.Contains("is the profile that kept the act", StringComparison.Ordinal)) problems.Add("no agent: the sentence that <name> is the profile that kept the act is missing");
        if (!bare.Contains("curia_resend", StringComparison.Ordinal)) problems.Add("no agent: the line does not name curia_resend");

        Red(problems);
    }

    /// <summary>
    /// CWK51. The CLI spellings <c>curia-mcp</c> prints carry the configured agent as a shell word: <c>curia_resend</c> of a digest nothing kept says <c>`curia acts --agent 'alice'`</c> where it names the
    /// listing, and <c>curia_recheck</c>'s resend command carries it too. Predicted red: <c>printed "curia acts" with no agent</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK51_TheToolsPrintTheConfiguredAgentInTheCommandsTheyName()
    {
        var problems = new List<string>();
        var tools = ToolsWithAgent(out var agent);
        using (agent)
        {
            var refused = await Record.ExceptionAsync(async () => TextOf(await tools.ResendAsync(UnknownDigest, TestContext.Current.CancellationToken)));
            var result = TextOf(await SafeResend(tools));
            var spelling = Regex.Match(result + " " + (refused?.Message ?? string.Empty), @"`(curia acts)([^`]*)`", RegexOptions.CultureInvariant);
            if (!spelling.Success) problems.Add("the refusal of a resend of a digest nothing kept names no `curia acts` command, so the control did not run");
            else if (spelling.Groups[2].Value != " --agent 'alice'") problems.Add($"printed \"curia acts\" with no agent (found \"{spelling.Value}\")");
        }

        Red(problems);
    }

    private static async Task<CallToolResult> SafeResend(ForumTools tools)
    {
        try
        {
            return await tools.ResendAsync(UnknownDigest, TestContext.Current.CancellationToken);
        }
        catch (McpException refused)
        {
            return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = refused.Message }] };
        }
    }

    /// <summary>
    /// CWK11 (the behaviour). <c>curia-mcp</c> has no fallback agent: with none configured no write tool is registered and the tool list holds the read tools only (a control on the seam; the comment is the
    /// Architecture fence's).
    /// </summary>
    [Fact]
    public async Task R11_82_CWK11_WithNoAgentConfiguredNoWriteToolIsRegistered()
    {
        await using var session = await McpProtocolSession.StartAsync(ToolsWithoutAgent(), null);
        var names = (await session.ListAsync()).Select(t => t.Name).ToArray();
        var writes = new[] { "curia_ask", "curia_answer", "curia_comment", "curia_revise", "curia_resolve", "curia_flag", "curia_resend", "curia_acts", "curia_discard", "curia_budget" };
        Assert.Empty(names.Intersect(writes, StringComparer.Ordinal));
        Assert.Contains("curia_recheck", names);
    }

    /// <summary>
    /// CWK50. The version: the catalogue reports the version R11.87 names (0.10.4), the README says it where it names the adapter's version, and the one parser of a version out of an errata paragraph reads
    /// the entry's. Predicted red: <c>0.10.0</c>.
    /// </summary>
    [Fact]
    public void R11_85_CWK50_CuriaMcpIs0103()
    {
        // 0.10.4 is the figure the plan names and R11.87 states; the one reader of a version out of an errata paragraph is PublishedReadersLastWords.Version(), which the G62 build points at R11.87 (it read R11.84's 0.10.2 until then, R11.83's, 0.10.1, until then).
        var published = "0.10.4".ToString(System.Globalization.CultureInfo.InvariantCulture);
        var problems = new List<string>();
        if (ToolCatalogue.Version != published) problems.Add($"the catalogue reports {ToolCatalogue.Version}, expected {published}");
        if (PublishedReadersLastWords.Version() != published) problems.Add($"the one version parser reads {PublishedReadersLastWords.Version()}, expected {published}");

        // The README keeps its history (an entry ends "`curia-mcp` 0.9.1."): those mentions are history by their words and may name older versions. A statement of the current version is a sentence
        // that says "`curia-mcp` is X": every one must equal the parser's value (a stale pin beside a newer history entry is red), and the newest version the README names must be the current one.
        var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
        var stated = Regex.Matches(readme, @"`curia-mcp` is (\d+\.\d+\.\d+)", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        if (stated.Length == 0) problems.Add("the README states no current curia-mcp version (\"`curia-mcp` is X.Y.Z\")");
        foreach (var version in stated.Where(v => v != PublishedReadersLastWords.Version())) problems.Add($"the README says `curia-mcp` is {version}, the parser reads {PublishedReadersLastWords.Version()}");
        var mentions = Regex.Matches(readme, @"`curia-mcp` (?:is )?(\d+\.\d+\.\d+)", RegexOptions.CultureInvariant).Select(m => Version.Parse(m.Groups[1].Value)).ToArray();
        if (mentions.Length == 0) problems.Add("the README names no curia-mcp version");
        else if (mentions.Max()!.ToString() != published) problems.Add($"the README's newest curia-mcp version is {mentions.Max()}, expected {published}");

        Red(problems);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "README.md")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("README.md not found above " + AppContext.BaseDirectory);
    }
}
