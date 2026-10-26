using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.45 (errata G33, closing D68 and D61 items 6, 8 and 9): a tool refuses, naming the argument and saying nothing was sent
/// (R11.43), a blank where a value was meant. Every fact varies one argument from a valid control and reads the stub's own list
/// of the requests it received, so "nothing was sent" is the Forum's account and not the tool's.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BlankValueTests : IDisposable
{
    private const string NotSent = "NOT SENT:";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-blank-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static async Task<McpProtocolSession> StartAsync(StubLog log, string home)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System), TimeProvider.System);
        var tools = new ForumTools(log.Client(), MarkingMode.None, new HeadStore(home), writer);
        return await McpProtocolSession.StartAsync(tools, writer.Agent.Profile.AgentId, agent);
    }

    /// <summary>The R11.45 sites, written from the sentence: a board, an author and a verification level in the criteria; a pin; a rationale; a body of an answer, a comment and a revision.</summary>
    private static readonly (string Tool, string Path)[] Sites =
    [
        ("curia_search", "criteria.board"), ("curia_search", "criteria.author"), ("curia_search", "criteria.min_verification"),
        ("curia_verify", "expectedDigest"), ("curia_flag", "rationale"),
        ("curia_answer", "body"), ("curia_comment", "body"), ("curia_revise", "body"),
    ];

    private static Dictionary<string, object?> Valid(string tool) => tool switch
    {
        "curia_search" => new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = "jcs" } },
        "curia_verify" => new() { ["postId"] = StubLog.PostId },
        "curia_flag" => new() { ["postId"] = StubLog.PostId, ["kind"] = "incorrect", ["rationale"] = "The premise is wrong." },
        "curia_answer" => new() { ["parentPostId"] = StubLog.PostId, ["body"] = "An answer." },
        "curia_comment" => new() { ["parentPostId"] = StubLog.PostId, ["body"] = "A reply." },
        "curia_revise" => new() { ["postId"] = StubLog.PostId, ["body"] = "A corrected body." },
        _ => throw new InvalidOperationException($"{tool} has no valid call here"),
    };

    private static Dictionary<string, object?> With(string tool, string path, object? value)
    {
        var arguments = Valid(tool);
        if (path.StartsWith("criteria.", StringComparison.Ordinal))
            ((Dictionary<string, object?>)arguments["criteria"]!)[path["criteria.".Length..]] = value;
        else
            arguments[path] = value;
        return arguments;
    }

    public static TheoryData<string, string> Blanks()
    {
        var data = new TheoryData<string, string>();
        foreach (var member in new[] { "board", "author", "min_verification" })
            foreach (var blank in new[] { "", "  ", "\t" })
                data.Add(member, blank);
        return data;
    }

    [Theory]
    [MemberData(nameof(Blanks))]
    public async Task R11_45_ABlankCriterionIsRefusedByNameAndNothingIsSent(string member, string blank)
    {
        await using var session = await StartAsync(_log, _home);
        var control = await session.CallAsync("curia_search", Valid("curia_search"));
        Assert.False(control.IsError, control.Text);
        Assert.NotEmpty(_log.Requests);

        _log.Requests.Clear();
        var result = await session.CallAsync("curia_search", With("curia_search", $"criteria.{member}", blank));

        Assert.True(
            result.IsError && result.Text.StartsWith(NotSent, StringComparison.Ordinal) && result.Text.Contains($"criteria.{member}", StringComparison.Ordinal),
            $"criteria.{member} blank was not refused by name:\n{result.Text}");
        Assert.Empty(_log.Requests);
    }

    /// <summary>The control: an empty <c>query</c> is valid (R9.25, absent returns everything) and an absent member is no filter.</summary>
    [Fact]
    public async Task R11_45_AnEmptyQueryAndAnAbsentMemberStillSearch()
    {
        await using var session = await StartAsync(_log, _home);

        var empty = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = "" } });
        var absent = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["board"] = "b" } });

        Assert.False(empty.IsError, empty.Text);
        Assert.False(absent.IsError, absent.Text);
        Assert.Equal(2, _log.Requests.Count(r => r.StartsWith("GET /v1/search", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task R11_45_ABlankPinIsRefusedAndAnAbsentOneIsNoPin()
    {
        await using var session = await StartAsync(_log, _home);
        var control = await session.CallAsync("curia_verify", Valid("curia_verify"));
        Assert.False(control.IsError || control.Text.StartsWith(NotSent, StringComparison.Ordinal), control.Text);

        _log.Requests.Clear();
        var blank = await session.CallAsync("curia_verify", With("curia_verify", "expectedDigest", "  "));

        Assert.True(
            blank.IsError && blank.Text.StartsWith(NotSent, StringComparison.Ordinal) && blank.Text.Contains("expectedDigest", StringComparison.Ordinal),
            $"a blank expectedDigest was not refused by name:\n{blank.Text}");
        Assert.Empty(_log.Requests);
    }

    public static TheoryData<string, string> BlankBodies()
    {
        var data = new TheoryData<string, string>();
        foreach (var tool in new[] { "curia_answer", "curia_comment", "curia_revise" })
            foreach (var blank in new[] { "", "  ", "\n" })
                data.Add(tool, blank);
        return data;
    }

    [Theory]
    [MemberData(nameof(BlankBodies))]
    public async Task R11_45_ABlankBodyOfAnAnswerACommentOrARevisionIsRefusedBeforeAnyRead(string tool, string blank)
    {
        await using var session = await StartAsync(_log, _home);

        _log.Requests.Clear();
        var result = await session.CallAsync(tool, With(tool, "body", blank));

        Assert.True(
            result.IsError && result.Text.StartsWith(NotSent, StringComparison.Ordinal) && result.Text.Contains("body", StringComparison.Ordinal),
            $"{tool} with a blank body was not refused by name:\n{result.Text}");
        Assert.Empty(_log.Requests);
    }

    /// <summary>The control: a question's body stays free (its title carries it, Table 9), so a blank one is posted.</summary>
    [Fact]
    public async Task R11_45_AQuestionWithAnEmptyBodyAndATitleIsPosted()
    {
        await using var session = await StartAsync(_log, _home);

        _log.Requests.Clear();
        var result = await session.CallAsync("curia_ask", new() { ["board"] = "b", ["title"] = "A title that carries the question", ["body"] = "" });

        Assert.False(result.IsError, result.Text);
        Assert.StartsWith("POSTED", result.Text, StringComparison.Ordinal);
        Assert.Contains(_log.Requests, r => r == "POST /v1/posts");
    }

    [Fact]
    public async Task R11_45_ABlankRationaleNamesTheField()
    {
        await using var session = await StartAsync(_log, _home);
        var control = await session.CallAsync("curia_flag", Valid("curia_flag"));
        Assert.False(control.IsError, control.Text);

        _log.Requests.Clear();
        var result = await session.CallAsync("curia_flag", With("curia_flag", "rationale", ""));

        Assert.True(result.IsError, result.Text);
        Assert.StartsWith("NOT SENT: rationale", result.Text, StringComparison.Ordinal);
        Assert.Empty(_log.Requests);
    }

    /// <summary>
    /// D61 item 8, ruled a non-property of this adapter (R11.45, ModelContextProtocol.Core 2.2.0): the SDK's argument binder fails
    /// on a lone surrogate before any tool filter runs, so the model is handed only the generic JSON-RPC -32603 "An error occurred."
    /// and nothing reaches the Forum. This pin exists so that an SDK change is noticed. A red here after a ModelContextProtocol.Core
    /// bump means item 8 is to be re-ruled, never that the assertion is to be loosened. The member-name row is the same case in a
    /// key, which the SDK also refuses while reading the request.
    /// </summary>
    [Theory]
    [InlineData("curia_read", """{"postId":"\ud800"}""")]
    [InlineData("curia_verify", """{"postId":"\ud800"}""")]
    [InlineData("curia_search", """{"criteria":{"query":"\ud800"}}""")]
    [InlineData("curia_search", """{"criteria":{"tags":["ok","\ud800"]}}""")]
    [InlineData("curia_search", """{"criteria":{"limit":"\ud800"}}""")]
    [InlineData("curia_read", """{"po\ud800stId":"x"}""")]
    public async Task R11_45_AnUnpairedSurrogateIsTheSdksBareErrorAndNothingIsSent(string tool, string argumentsJson)
    {
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home));
        await using var session = await RawFrameSession.StartAsync(tools);

        var result = await session.CallAsync(tool, argumentsJson);

        using var frame = JsonDocument.Parse(result.Frame);
        Assert.False(frame.RootElement.TryGetProperty("result", out _), result.Frame);
        var error = frame.RootElement.GetProperty("error");
        Assert.Equal(-32603, error.GetProperty("code").GetInt32());
        Assert.Equal("An error occurred.", error.GetProperty("message").GetString());
        Assert.Empty(_log.Requests);
    }

    /// <summary>The control: a valid surrogate pair (U+1F600) in <c>criteria.query</c>, written as its escape pair, is text and the search runs.</summary>
    [Fact]
    public async Task R11_45_AValidSurrogatePairIsStillText()
    {
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home));
        await using var session = await RawFrameSession.StartAsync(tools);

        var result = await session.CallAsync("curia_search", """{"criteria":{"query":"\ud83d\ude00"}}""");

        Assert.False(result.IsError, result.Frame);
        Assert.DoesNotContain(NotSent, result.Text, StringComparison.Ordinal);
        Assert.Contains(_log.Requests, r => r.StartsWith("GET /v1/search", StringComparison.Ordinal));
    }

    /// <summary>
    /// R14.12's gate for R11.52 (errata G40, moved fact): the gate's list of sites holds every argument R11.52 names (parsed from the errata: the pin
    /// and the dotted cursor), and the gate varies the cursor with a blank and the pin with each spelling that is not the published form, each refused
    /// by name with nothing sent.
    /// </summary>
    [Fact]
    public async Task R14_12_TheBoundaryGateVariesTheCursorAndThePinForm()
    {
        var named = PublishedReadersWords.R1152Arguments();
        var missing = named.Where(a => !Sites.Any(s => s.Path == a)).ToArray();
        Assert.True(missing.Length == 0, $"Expected the gate's sites to hold every argument R11.52 names ({string.Join(", ", named)}); the gate's list lacks {string.Join(", ", missing)}");

        await using var session = await StartAsync(_log, _home);
        var real = _log.Submission.PrefixedDigest;
        var rows = new List<(string Tool, string Path, string Value)>
        {
            ("curia_search", "criteria.cursor", ""),
            ("curia_search", "criteria.cursor", "  "),
            ("curia_verify", "expectedDigest", real.ToUpperInvariant()),
            ("curia_verify", "expectedDigest", real["sha256:".Length..]),
            ("curia_verify", "expectedDigest", real + " "),
        };

        foreach (var (tool, path, value) in rows)
        {
            _log.Requests.Clear();
            var result = await session.CallAsync(tool, With(tool, path, value));

            Assert.True(
                result.IsError && result.Text.StartsWith(NotSent, StringComparison.Ordinal) && result.Text.Contains(path, StringComparison.Ordinal),
                $"{tool} with {path} = '{value}' was not refused by name:\n{result.Text}");
            Assert.Empty(_log.Requests);
        }
    }

    /// <summary>
    /// R14.12's gate: the schema <c>tools/list</c> serves names every site R11.45 does, and a blank at each is refused by name.
    /// The sites are written from the sentence (eight tool-and-argument pairs: three criteria, a pin, a rationale, three bodies).
    /// </summary>
    [Fact]
    public async Task R14_12_TheGateFindsEveryBlankRefusalR1145Names()
    {
        await using var session = await StartAsync(_log, _home);
        var tools = await session.ListAsync();

        var refused = new List<(string Tool, string Path)>();
        foreach (var (tool, path) in Sites)
        {
            var schema = tools.Single(t => t.Name == tool).InputSchema;
            var top = path.Split('.')[0];
            Assert.True(schema.GetProperty("properties").TryGetProperty(top, out _), $"{tool}'s schema does not define {top}");

            foreach (var blank in new[] { "", "   " })
            {
                var result = await session.CallAsync(tool, With(tool, path, blank));
                if (result.IsError && result.Text.StartsWith(NotSent, StringComparison.Ordinal) && result.Text.Contains(path, StringComparison.Ordinal))
                    refused.Add((tool, path));
            }
        }

        Assert.Equal(Sites.Length * 2, refused.Count);
        Assert.Equal(Sites.OrderBy(s => s).ToArray(), refused.Distinct().OrderBy(s => s).ToArray());
    }
}
