using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R8.73 (errata G43) at the adapter: <c>curia_ask</c> refuses a tag the filter cannot name, <c>NOT SENT</c>, naming the index and never the value,
/// through the client's local admission refusal (the wrapper carries the detail), and sends nothing; a tag it can name posts. Measured on the
/// unmodified tree: <c>["x","a,b"]</c> posted, and the post could not be found by either spelling.
///
/// <para><b>Oracles.</b> The request list is the stub's; the tags are built at run time. Each refusal has its control: <c>["x","y"]</c> posts.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ToolTagTests : IDisposable
{
    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-tags-").FullName;

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
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
        return await McpProtocolSession.StartAsync(tools, writer.Agent.Profile.AgentId, agent);
    }

    private static Dictionary<string, object?> Ask(string[] tags) => new()
    {
        ["board"] = "b",
        ["title"] = "A question",
        ["body"] = "A body.",
        ["tags"] = tags,
    };

    /// <summary>PJ28: a comma tag at index 1 is refused <c>NOT SENT</c>, naming <c>tags[1]</c>, never the value; nothing is sent. The control posts.</summary>
    [Fact]
    public async Task R8_73_AskRefusesACommaTagNotSent()
    {
        await using var session = await StartAsync();

        var control = await session.CallAsync("curia_ask", Ask(["x", "y"]));
        Assert.False(control.IsError, control.Text);
        Assert.Contains(_log.Requests, r => r == "POST /v1/posts");
        _log.Requests.Clear();

        var refused = await session.CallAsync("curia_ask", Ask(["x", "secret,token"]));

        Assert.True(refused.IsError, refused.Text);
        Assert.StartsWith("NOT SENT", refused.Text, StringComparison.Ordinal);
        Assert.Contains("tags[1]", refused.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", refused.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("token", refused.Text, StringComparison.Ordinal);
        Assert.Empty(_log.Requests);
    }

    /// <summary>PJ28's other classes: empty, edge white space and U+0000 are refused the same way. Built at run time.</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("leading")]
    [InlineData("trailing")]
    [InlineData("nul")]
    public async Task R8_73_AskRefusesTheOtherUnfilterableClassesNotSent(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var tag = kind switch
        {
            "empty" => string.Empty,
            "leading" => " x",
            "trailing" => "x ",
            _ => "b" + (char)0 + "c",
        };
        await using var session = await StartAsync();

        var refused = await session.CallAsync("curia_ask", Ask(["x", tag]));

        Assert.True(refused.IsError, refused.Text);
        Assert.StartsWith("NOT SENT", refused.Text, StringComparison.Ordinal);
        Assert.Contains("tags[1]", refused.Text, StringComparison.Ordinal);
        Assert.Empty(_log.Requests);
    }

    /// <summary>
    /// PJ18, the tool's door (R8.74): <c>curia_comment</c> takes a reference whose scheme is in any case and is not refused as a reference form; the
    /// post is sent. The command line's door is <c>CliTagAndRefTests</c>'; the Forum serving the target as typed is PJ39's.
    /// </summary>
    [Theory]
    [InlineData("HTTPS://example.org/a")]
    [InlineData("Http://a.b")]
    [InlineData("https://example.org/a")]
    public async Task R8_74_CommentTakesAnyCaseOfTheScheme(string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        await using var session = await StartAsync();

        var answer = await session.CallAsync("curia_comment", new() { ["parentPostId"] = StubLog.PostId, ["body"] = "A reply.", ["refs"] = new[] { reference } });

        Assert.False(answer.IsError, answer.Text);
        Assert.DoesNotContain("A reference is a post digest", answer.Text, StringComparison.Ordinal);
        Assert.Contains(_log.Requests, r => r == "POST /v1/posts");
    }
}
