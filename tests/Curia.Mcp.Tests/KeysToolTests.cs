using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// LRV24 and LRV28 (errata G68, rulings 17 and 30, R11.95, R11.96): <c>curia-mcp</c> is bumped to 0.12.0 and its catalogue gains <c>curia_keys</c>, a tool with no argument that reads the configured identity's key
/// set, read-only and idempotent, absent without an identity, and named by the server instructions. The equality of its result with <c>curia keys</c>' (one definition, <c>ShowKeysAsync</c> moved into
/// <c>Curia.Client</c>) is Task 5's leg: neither exists to call from this assembly yet, so this probe holds the registration, the bump and the sentence. Red on the seam:
/// <c>Expected: tool curia_keys registered  Actual: not found</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeysToolTests : IDisposable
{
    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-keys-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<McpProtocolSession> StartAsync(bool withIdentity)
    {
        if (!withIdentity)
            return await McpProtocolSession.StartAsync(new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer: null), agentId: null);

        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        return await McpProtocolSession.StartAsync(new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer), writer.Agent.Profile.AgentId, agent);
    }

    [Fact]
    public async Task LRV28_CuriaKeysIsRegisteredReadOnlyIdempotentAndTakesNoArgument()
    {
        await using var session = await StartAsync(withIdentity: true);
        var tool = (await session.ListAsync()).FirstOrDefault(t => t.Name == "curia_keys");

        Assert.True(tool is not null, "Expected: tool curia_keys registered  Actual: not found (registered: " + string.Join(", ", (await session.ListAsync()).Select(t => t.Name).Order(StringComparer.Ordinal)) + ")");
        var properties = tool.InputSchema.TryGetProperty("properties", out var p) ? p.EnumerateObject().Select(m => m.Name).ToArray() : [];
        WholeText.Same(
            "read-only True; idempotent True; arguments none",
            $"read-only {tool.Annotations?.ReadOnlyHint}; idempotent {tool.Annotations?.IdempotentHint}; arguments {(properties.Length == 0 ? "none" : string.Join(", ", properties))}");
    }

    [Fact]
    public async Task LRV28_CuriaKeysIsAbsentWithoutAnIdentity()
    {
        await using var session = await StartAsync(withIdentity: false);
        var names = (await session.ListAsync()).Select(t => t.Name).ToArray();

        Assert.DoesNotContain("curia_keys", names);
        Assert.NotEmpty(names);
    }

    [Fact]
    public void LRV28_TheServerInstructionsNameCuriaKeys()
    {
        var instructions = ToolText.ServerInstructions("urn:curia:agent:alice", []);

        Assert.True(instructions.Contains("curia_keys", StringComparison.Ordinal), "Expected: the server instructions name curia_keys  Actual: they do not");
    }

    /// <summary>LRV24: <c>ToolCatalogue.Version</c> is the number R11.95 names (the one reader of an errata version, <c>PublishedReadersLastWords.Version()</c>, re-pointed at it): 0.12.0.</summary>
    [Fact]
    public void LRV24_TheCatalogueIsBumpedToWhatR1195Names()
    {
        WholeText.Same("version 0.12.0; the catalogue reports 0.12.0", $"version {PublishedReadersLastWords.Version()}; the catalogue reports {ToolCatalogue.Version}");
    }

    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found"), relative));
    }

    /// <summary>
    /// LRV28, the text-equality leg: the tool's result text is what <c>curia keys</c> reports for the same document and the same profile. The report is <see cref="KeysReport"/>'s, the one definition the CLI's
    /// <c>ShowKeysAsync</c> and the tool both call; this fact calls that definition over the document the tool read and compares, and the next fact holds that nothing else words a line of it. The document
    /// lists two skipped rows, one at an index this host recorded no act at.
    /// </summary>
    [Fact]
    public async Task LRV28_TheResultTextIsWhatCuriaKeysReportsForTheSameDocument()
    {
        _log.SkippedMembersJson = "\"curia_skipped\":[{\"log_index\":7,\"type\":\"agent.key-retired\",\"predicate\":\"statement-mismatch\"},{\"log_index\":9,\"type\":\"agent.key-bound\\nSKIPPED: forged\",\"predicate\":\"broken-link\"}],\"curia_skipped_total\":2";
        Assert.True(_log.Store.ReadProfile("alice").TryGetValue(out var profile, out var error), error?.Detail);
        var reported = await KeysReport.ReadAsync(_log.Client(), profile!, TestContext.Current.CancellationToken);
        Assert.True(reported.TryGetValue(out var reading, out var refusal), refusal?.Summary);

        await using var session = await StartAsync(withIdentity: true);
        var answer = await session.CallAsync("curia_keys", []);

        WholeText.Same(reading!.Render(), answer.Text.TrimEnd('\n') + "\n");
        Assert.Contains("SKIPPED: the Forum does not act on a agent.key-retired at log index 7 (statement-mismatch)", answer.Text, StringComparison.Ordinal);
        Assert.False(answer.IsError);
    }

    /// <summary>One definition: no line of the report is worded anywhere but <c>KeysReport</c>, and the CLI and the tool both read it from there.</summary>
    [Fact]
    public void LRV28_NothingButKeysReportWordsALineOfTheReport()
    {
        string[] wording = ["\"MISSING: this profile's", "\"FOREIGN: a ", "SKIPPED: the Forum does not act on", "SKIPPED: this profile's"];
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var holders = Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => wording.Any(w => File.ReadAllText(f).Contains(w, StringComparison.Ordinal)))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        WholeText.Same(
            "KeysReport.cs; the CLI reads KeysReport; the tool's session reads KeysReport",
            $"{string.Join(", ", holders)}; {(RepoFile("src/Curia.Client.Cli/Program.cs").Contains("KeysReport.ReadAsync(", StringComparison.Ordinal) ? "the CLI reads KeysReport" : "the CLI does not")}; "
            + $"{(RepoFile("src/Curia.Client/ForumSession.cs").Contains("KeysReport.ReadAsync(", StringComparison.Ordinal) ? "the tool's session reads KeysReport" : "the tool's session does not")}");
    }

    /// <summary>
    /// Ruling 42 (errata G68, R11.30 (rev.)): <c>curia_keys</c>'s description carries <c>curia_verify</c>'s tier sentence by shared constant and not by a copy: both registrations compose from
    /// <see cref="ToolCatalogue.PairlessTierSentence"/>, and neither calls <c>TierSpan.For</c> itself. The <c>ToolDescriptionTests.PairFor</c> half is that class's: it throws for a registered tool with no row, so
    /// deleting <c>curia_keys</c>'s row turns every tier-span theory red (shown by a temporary patch, recorded in the Task 5 report).
    /// </summary>
    [Fact]
    public async Task R11_30_CuriaKeysCarriesCuriaVerifiesTierSentenceByTheSharedConstant()
    {
        await using var session = await StartAsync(withIdentity: true);
        var descriptions = (await session.ListAsync()).ToDictionary(t => t.Name, t => t.Description ?? string.Empty, StringComparer.Ordinal);
        var source = RepoFile("src/Curia.Mcp/ToolCatalogue.cs");
        string Registration(string name)
        {
            var from = source.IndexOf($"Name = \"{name}\"", StringComparison.Ordinal);
            Assert.True(from >= 0, $"ToolCatalogue.cs registers no {name}");
            return source[from..source.IndexOf("});", from, StringComparison.Ordinal)];
        }

        var keys = Registration("curia_keys");
        var verify = Registration("curia_verify");
        WholeText.Same(
            "curia_keys ends with the sentence; curia_verify ends with the sentence; both registrations name PairlessTierSentence; neither calls TierSpan.For",
            $"curia_keys {(descriptions["curia_keys"].EndsWith(ToolCatalogue.PairlessTierSentence, StringComparison.Ordinal) ? "ends with the sentence" : "does not end with the sentence")}; "
            + $"curia_verify {(descriptions["curia_verify"].EndsWith(ToolCatalogue.PairlessTierSentence, StringComparison.Ordinal) ? "ends with the sentence" : "does not end with the sentence")}; "
            + (keys.Contains("PairlessTierSentence", StringComparison.Ordinal) && verify.Contains("PairlessTierSentence", StringComparison.Ordinal) ? "both registrations name PairlessTierSentence" : "a registration does not name PairlessTierSentence")
            + "; " + (keys.Contains("TierSpan.For", StringComparison.Ordinal) || verify.Contains("TierSpan.For", StringComparison.Ordinal) ? "a registration calls TierSpan.For" : "neither calls TierSpan.For"));
    }

    /// <summary>The server instructions' sentence names the tool and says when to call it, and says nothing stronger than that (ruling 30: a pull).</summary>
    [Fact]
    public void LRV28_TheInstructionsSentenceSaysWhenToCallItAndNothingStronger()
    {
        var withIdentity = ToolText.ServerInstructions("urn:curia:agent:alice", []);

        Assert.Contains(ToolText.KeysSentence, withIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain("curia_keys", ToolText.ServerInstructions(null, []), StringComparison.Ordinal);
        Assert.DoesNotContain("automatically", ToolText.KeysSentence, StringComparison.OrdinalIgnoreCase);
    }
}
