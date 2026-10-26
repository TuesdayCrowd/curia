using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.52 (errata G40, closing D61 item 23): <c>curia_search</c> refuses a <c>criteria.cursor</c> that is empty or blank, <c>NOT SENT</c>, by its
/// dotted name, because the Forum reads a blank cursor as no cursor and a pager that carries its cursor in a variable then re-reads page one for
/// ever. An absent cursor and a real one still reach the Forum, and so does a non-blank cursor the Forum will judge. The stub's request list is the
/// account of what was sent; the cursor's name is parsed from the errata.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BlankCursorTests : IDisposable
{
    private const string RealCursor = "c3R1Yi1jdXJzb3I=";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-blank-cursor-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<McpProtocolSession> StartAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        return await McpProtocolSession.StartAsync(new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer), writer.Agent.Profile.AgentId, agent);
    }

    private static Dictionary<string, object?> Search(string? cursor)
    {
        var criteria = new Dictionary<string, object?> { ["query"] = "jcs", ["limit"] = 1 };
        if (cursor is not null) criteria["cursor"] = cursor;
        return new() { ["criteria"] = criteria };
    }

    public static TheoryData<string> Blanks() => ["", "  ", "\t"];

    [Theory]
    [MemberData(nameof(Blanks))]
    public async Task R11_52_ABlankCursorIsRefusedByItsDottedNameAndNothingIsSent(string blank)
    {
        var name = Assert.Single(PublishedReadersWords.R1152Arguments(), a => a.EndsWith("cursor", StringComparison.Ordinal));
        await using var session = await StartAsync();

        var result = await session.CallAsync("curia_search", Search(blank));

        Assert.True(
            result.IsError && result.Text.StartsWith("NOT SENT", StringComparison.Ordinal),
            $"a blank cursor was not refused NOT SENT:\n{result.Text}");
        Assert.Contains(name, result.Text, StringComparison.Ordinal);
        Assert.Empty(_log.Requests);
    }

    /// <summary>Controls: no cursor serves the first page, a real cursor reaches the Forum, and so does a non-blank cursor that is no cursor (the Forum judges it).</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(RealCursor)]
    [InlineData("not-a-cursor")]
    public async Task R11_52_AnAbsentAndANonBlankCursorStillReachTheForum(string? cursor)
    {
        await using var session = await StartAsync();

        var result = await session.CallAsync("curia_search", Search(cursor));

        Assert.False(result.IsError && result.Text.StartsWith("NOT SENT", StringComparison.Ordinal), result.Text);
        Assert.Single(_log.Requests, r => r.StartsWith("GET /v1/search", StringComparison.Ordinal));
        Assert.Equal(cursor is not null, _log.Queries.Any(q => q.Contains("cursor=", StringComparison.Ordinal)));
    }
}
