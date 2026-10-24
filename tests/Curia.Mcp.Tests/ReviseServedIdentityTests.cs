using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// D63 from the tools' side. <see cref="StubLog"/> serves its one post for every <c>/v1/posts/*</c>
/// request, so a request for any other id is answered with a valid, verifiable post that is not the
/// one asked for: the hostile Forum the client must not believe. The oracle is the recorded request
/// list, not the tool's report.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReviseServedIdentityTests : IDisposable
{
    private const string NotServed = "01TESTPOSTID0000000000000Q";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-d63-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReviseAsync_DoesNotChainARevisionToAPostItWasNotAskedFor()
    {
        await Assert.ThrowsAsync<McpException>(
            () => Tools().ReviseAsync(NotServed, "A corrected body.", null, null, null, Ct));

        Assert.Empty(Envelopes());
        Assert.DoesNotContain("POST /v1/posts", _log.Requests);
    }

    [Fact]
    public async Task ReadTool_RefusesAndDoesNotFrameTheWrongPost()
    {
        var thrown = await Assert.ThrowsAsync<McpException>(() => Tools().ReadAsync(NotServed, Ct));

        Assert.Contains(NotServed, thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(StubLog.PostId, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviseAsync_StillRevisesTheRequestedPost()
    {
        var result = await Tools().ReviseAsync(StubLog.PostId, "A corrected body.", null, null, null, Ct);

        Assert.NotEqual(true, result.IsError);
        using var document = JsonDocument.Parse(Envelopes().Last());
        Assert.Equal(StubLog.PostId, document.RootElement.GetProperty("envelope").GetProperty("parent").GetString());
    }

    /// <summary>The bodies of every write that carried an envelope; a token request carries none.</summary>
    private List<string> Envelopes() =>
        _log.WrittenBodies.Where(body => body.StartsWith("{\"envelope\"", StringComparison.Ordinal)).ToList();

    private ForumTools Tools()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var session = new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System);
        return new ForumTools(_log.Client(), MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent!, session, TimeProvider.System));
    }
}
