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
            "read-only true; idempotent true; arguments none",
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
}
