using System.Diagnostics.CodeAnalysis;
using System.Net;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.52 (errata G40, closing D61 item 21): a tool that reads a post before it signs answers an id the Forum serves no post under as
/// <c>NOT SENT</c>, naming the argument and not its value, and a failure of that read that says nothing about the id is still a failure of
/// the Forum. <c>curia_resolve</c> and <c>curia_flag</c> are refusals of a write and keep quoting the Forum's cause. The oracle is the stub's
/// own list of the requests it received and the id each row chose; the three tools and their arguments are parsed from the errata.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class MissingPostRefusalTests : IDisposable
{
    /// <summary>A well-formed id no post bears: the stub refuses every id but its own.</summary>
    private const string NoSuchPost = "01TESTPOSTID0000000000000Z";

    private readonly StubLog _log = new() { OtherPostsAreRefused = true };
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-missing-").FullName;

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
        "curia_resolve" => ("answerPostId", new() { ["answerPostId"] = id }),
        "curia_flag" => ("postId", new() { ["postId"] = id, ["kind"] = "incorrect", ["rationale"] = "The premise is wrong." }),
        _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "not a tool this fact calls"),
    };

    public static TheoryData<string, string> ReadingTools()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in new[] { "curia_revise", "curia_answer", "curia_comment" })
            foreach (var id in new[] { NoSuchPost, "a b" })
                data.Add(tool, id);
        return data;
    }

    [Fact]
    public void R11_52_TheThreeToolsTheFactCallsAreTheThreeTheErratumNames()
    {
        var named = PublishedReadersWords.R1152Tools();

        Assert.Contains("curia_revise", named);
        Assert.Contains("curia_answer", named);
        Assert.Contains("curia_comment", named);
    }

    [Theory]
    [MemberData(nameof(ReadingTools))]
    public async Task R11_52_AToolThatReadsBeforeItSignsAnswersAMissingPostNotSent(string tool, string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        await using var session = await StartAsync();
        var (argument, arguments) = CallOf(tool, id);

        var result = await session.CallAsync(tool, arguments);

        Assert.True(
            result.IsError && result.Text.StartsWith("NOT SENT", StringComparison.Ordinal),
            $"{tool} of an id no post bears was not answered NOT SENT:\n{result.Text}");
        Assert.Contains(argument, result.Text, StringComparison.Ordinal);
        Assert.Contains("serves no post", result.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nothing was signed", result.Text, StringComparison.OrdinalIgnoreCase);

        // The id is a caller's value: never echoed (the SDK's prefixed exception quoted it).
        Assert.DoesNotContain(id.Contains(' ', StringComparison.Ordinal) ? $"\"{id}\"" : id, result.Text, StringComparison.Ordinal);

        // The Forum was asked (the read), and nothing was written: no POST of any kind.
        Assert.Contains(_log.Requests, r => r.StartsWith("GET /v1/posts/", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    /// <summary>Control: the same three tools on the post the Forum does serve are not refused as missing, so the refusal above is the id's doing.</summary>
    [Theory]
    [InlineData("curia_revise")]
    [InlineData("curia_answer")]
    [InlineData("curia_comment")]
    public async Task R11_52_AnIdTheForumServesIsNotRefusedAsMissing(string tool)
    {
        await using var session = await StartAsync();
        var (_, arguments) = CallOf(tool, StubLog.PostId);

        var result = await session.CallAsync(tool, arguments);

        Assert.DoesNotContain("serves no post", result.Text, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<string> Faults() => ["503", "transport"];

    [Theory]
    [MemberData(nameof(Faults))]
    public async Task R11_52_AFaultOfTheReadIsStillAFailureOfTheForum(string fault)
    {
        if (fault == "503") _log.PostReadFailsWith = HttpStatusCode.ServiceUnavailable;
        else _log.PostReadThrows = true;
        await using var session = await StartAsync();

        foreach (var tool in new[] { "curia_revise", "curia_answer", "curia_comment" })
        {
            var (_, arguments) = CallOf(tool, StubLog.PostId);
            var result = await session.CallAsync(tool, arguments);

            // A failure of the read says nothing about the id: it is thrown (the SDK's own prefix), never worded as a missing post.
            Assert.True(result.IsError, $"{tool} with a Forum fault ({fault}) was not an error:\n{result.Text}");
            Assert.False(result.Text.StartsWith("NOT SENT", StringComparison.Ordinal), $"{tool} with a Forum fault ({fault}) was answered NOT SENT:\n{result.Text}");
            Assert.DoesNotContain("serves no post", result.Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("curia_resolve")]
    [InlineData("curia_flag")]
    public async Task R11_52_ResolveAndFlagKeepQuotingTheForumsCause(string tool)
    {
        _log.WritesRefuseAsMissingPost = true;
        await using var session = await StartAsync();
        var (_, arguments) = CallOf(tool, NoSuchPost);

        var result = await session.CallAsync(tool, arguments);

        Assert.True(result.IsError, result.Text);
        Assert.Contains("No such post", result.Text, StringComparison.Ordinal);
        Assert.Contains("post=" + NoSuchPost, result.Text, StringComparison.Ordinal);
        Assert.False(result.Text.StartsWith("NOT SENT", StringComparison.Ordinal), $"{tool} was converted to NOT SENT:\n{result.Text}");
    }
}
