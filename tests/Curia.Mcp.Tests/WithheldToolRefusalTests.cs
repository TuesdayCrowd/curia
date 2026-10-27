using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.54 (errata G41): a tool that reads a post before it signs answers an id the Forum says is withheld as <c>NOT SENT</c>, naming the argument and
/// not its value, saying nothing was signed; <c>curia_revise</c> adds that a new post is the remedy. <c>curia_flag</c> and <c>curia_resolve</c> are
/// writes of their own and are not converted. The Forum's word is the stub's own problem type, which is the entry's, parsed by
/// <c>PublishedWithheldWords</c>; the oracle is the result and the stub's list of the requests it received.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class WithheldToolRefusalTests : IDisposable
{
    private const string WithheldId = "01TESTPOSTID0000000000000W";

    private readonly StubLog _log = new() { PostReadNotFoundType = PublishedWithheldWords.PostWithheldType() };
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-withheld-").FullName;

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

    private static (string Argument, Dictionary<string, object?> Arguments) CallOf(string tool, string id) => tool switch
    {
        "curia_revise" => ("postId", new() { ["postId"] = id, ["body"] = "A corrected body." }),
        "curia_answer" => ("parentPostId", new() { ["parentPostId"] = id, ["body"] = "An answer." }),
        "curia_comment" => ("parentPostId", new() { ["parentPostId"] = id, ["body"] = "A reply." }),
        "curia_flag" => ("postId", new() { ["postId"] = id, ["kind"] = "incorrect", ["rationale"] = "The premise is wrong." }),
        "curia_resolve" => ("answerPostId", new() { ["answerPostId"] = id }),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "not a tool this fact calls"),
    };

    [Fact]
    public void R11_54_TheThreeToolsTheFactCallsAreTheThreeTheErratumNames()
    {
        var named = PublishedWithheldWords.ReplyTools();

        Assert.Contains("curia_revise", named);
        Assert.Contains("curia_answer", named);
        Assert.Contains("curia_comment", named);
    }

    /// <summary>PH14. The three tools answer a withheld post <c>NOT SENT</c>, naming the argument, saying the Forum is not serving it and that nothing was signed; the id is a caller's value and is not echoed; nothing was posted.</summary>
    [Theory]
    [InlineData("curia_revise")]
    [InlineData("curia_answer")]
    [InlineData("curia_comment")]
    public async Task R11_54_RevisingAnswerAndCommentRefuseAWithheldPostNotSent(string tool)
    {
        await using var session = await StartAsync();
        var (argument, arguments) = CallOf(tool, WithheldId);

        var result = await session.CallAsync(tool, arguments);

        Assert.True(
            result.IsError && result.Text.StartsWith("NOT SENT", StringComparison.Ordinal),
            $"{tool} of a withheld post was not answered NOT SENT:\n{result.Text}");
        Assert.Contains(argument, result.Text, StringComparison.Ordinal);
        Assert.Contains("not serving", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing was signed", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(WithheldId, result.Text, StringComparison.Ordinal);
        Assert.Contains(_log.Requests, r => r.StartsWith("GET /v1/posts/", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    /// <summary>PH15. A revision cannot be chained to a post its author may not read, so the revise result says a new post is the remedy; the answer and the comment do not claim it.</summary>
    [Fact]
    public async Task R11_54_ReviseSaysANewPostIsTheRemedy()
    {
        await using var session = await StartAsync();

        var revise = await session.CallAsync("curia_revise", CallOf("curia_revise", WithheldId).Arguments);
        Assert.Contains("new post", revise.Text, StringComparison.OrdinalIgnoreCase);

        foreach (var tool in new[] { "curia_answer", "curia_comment" })
        {
            var result = await session.CallAsync(tool, CallOf(tool, WithheldId).Arguments);
            Assert.DoesNotContain("new post", result.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>PH17. G41 decision 7: <c>curia_flag</c> is a Forum write of its own and is not converted: a flag of a post the reads call withheld is recorded.</summary>
    [Fact]
    public async Task R11_54_CuriaFlagOfAWithheldPostIsUnchanged()
    {
        await using var session = await StartAsync();
        var (_, arguments) = CallOf("curia_flag", WithheldId);

        var result = await session.CallAsync("curia_flag", arguments);

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("FLAGGED", result.Text, StringComparison.Ordinal);
        Assert.False(result.Text.StartsWith("NOT SENT", StringComparison.Ordinal), result.Text);
    }

    /// <summary>PH17. G41 decision 7: <c>curia_resolve</c> keeps quoting the Forum's cause for a refusal of its write; it is not worded <c>NOT SENT</c>.</summary>
    [Fact]
    public async Task R11_54_ResolveStillQuotesTheForum()
    {
        _log.WritesRefuseAsMissingPost = true;
        await using var session = await StartAsync();
        var (_, arguments) = CallOf("curia_resolve", WithheldId);

        var result = await session.CallAsync("curia_resolve", arguments);

        Assert.True(result.IsError, result.Text);
        Assert.Contains("No such post", result.Text, StringComparison.Ordinal);
        Assert.False(result.Text.StartsWith("NOT SENT", StringComparison.Ordinal), $"curia_resolve was converted to NOT SENT:\n{result.Text}");
    }
}
