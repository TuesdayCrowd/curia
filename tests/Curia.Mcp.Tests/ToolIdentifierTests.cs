using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.57 (errata G43): <c>curia-mcp</c> refuses an identifier, or a search criterion, that holds U+0000 or an unpaired surrogate, <c>NOT SENT</c>,
/// naming the argument and never its value, for every argument that reaches a path and for <c>query</c>, <c>board</c>, <c>author</c> and each
/// <c>tags</c> element of <c>curia_search</c>. Measured on the unmodified tree: an id with U+0000 reaches Kestrel, which answers <c>400</c> with no
/// problem document, and the SDK says "An error occurred invoking ..." with no cause; a criterion with U+0000 is sent and matches nothing.
///
/// <para><b>Oracles.</b> The list of arguments is derived from the tool catalogue's published input schemas (<see cref="ToolCatalogue"/>), not
/// from the guard: every string argument whose name is <c>postId</c> or ends <c>PostId</c>, and <c>query</c>, <c>board</c>, <c>author</c> and the
/// elements of <c>tags</c> in <c>criteria</c>. The request list is the stub's. The bad values are built at run time and carry marker text on both
/// sides so an echo is visible. U+0000 goes through the protocol; an unpaired surrogate cannot (the SDK fails the JSON-RPC parse, PJ40), so its
/// cells call the tool method directly. Each refusal has its control: the same call with a good value is not refused.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ToolIdentifierTests : IDisposable
{
    private const string NotSent = "NOT SENT";
    private const string Before = "zqx";
    private const string After = "wvk";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-ident-").FullName;
    private readonly EnrolledAgent _agent;
    private readonly ForumTools _tools;

    public ToolIdentifierTests()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _agent = agent!;
        var writer = new ForumWriter(_agent, new ForumSession(_log.Client(), _agent, _log.Store, TimeProvider.System), TimeProvider.System);
        _tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
    }

    public void Dispose()
    {
        _agent.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<McpProtocolSession> StartAsync() => McpProtocolSession.StartAsync(_tools, _agent.Profile.AgentId);

    private static string Nul => Before + (char)0 + After;

    private static string Lone => Before + (char)0xD800 + After;

    // ---- the derived list -------------------------------------------------------------------------

    /// <summary>One place an identifier or criterion enters a tool: the tool, and the dotted path of the argument (<c>postId</c>, <c>criteria.tags[0]</c>).</summary>
    internal sealed record Argument(string Tool, string Path);

    /// <summary>Every such argument, derived from the catalogue's schemas.</summary>
    internal static List<Argument> DerivedArguments(ForumTools tools)
    {
        var found = new List<Argument>();
        foreach (var tool in ToolCatalogue.Build(tools))
        {
            var name = tool.ProtocolTool.Name;
            var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
            foreach (var property in properties.EnumerateObject())
            {
                if (property.Name is "postId" || property.Name.EndsWith("PostId", StringComparison.Ordinal))
                    found.Add(new Argument(name, property.Name));
            }

            if (name == "curia_search" && properties.TryGetProperty("criteria", out var criteria))
            {
                var members = criteria.GetProperty("properties");
                foreach (var member in new[] { "query", "board", "author" })
                    if (members.TryGetProperty(member, out _)) found.Add(new Argument(name, "criteria." + member));
                if (members.TryGetProperty("tags", out _)) found.Add(new Argument(name, "criteria.tags[0]"));
            }
        }

        return found;
    }

    public static TheoryData<string, string, string> Cells()
    {
        using var log = new StubLog();
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out _));
        using (agent)
        {
            var tools = new ForumTools(log.Client(), MarkingMode.None, new HeadStore(Path.GetTempPath()), new ForumWriter(agent!, new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System), TimeProvider.System));
            var data = new TheoryData<string, string, string>();
            foreach (var argument in DerivedArguments(tools))
                foreach (var kind in new[] { "nul", "surrogate" })
                    data.Add(argument.Tool, argument.Path, kind);
            return data;
        }
    }

    private static Dictionary<string, object?> Valid(string tool) => tool switch
    {
        "curia_read" => new() { ["postId"] = StubLog.PostId },
        "curia_verify" => new() { ["postId"] = StubLog.PostId },
        "curia_revise" => new() { ["postId"] = StubLog.PostId, ["body"] = "A corrected body." },
        "curia_flag" => new() { ["postId"] = StubLog.PostId, ["kind"] = "incorrect", ["rationale"] = "The premise is wrong." },
        "curia_answer" => new() { ["parentPostId"] = StubLog.PostId, ["body"] = "An answer." },
        "curia_comment" => new() { ["parentPostId"] = StubLog.PostId, ["body"] = "A reply." },
        "curia_resolve" => new() { ["answerPostId"] = StubLog.PostId },
        "curia_search" => new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = "jcs" } },
        _ => throw new InvalidOperationException($"{tool} has no valid call here"),
    };

    private static Dictionary<string, object?> With(string tool, string path, string value)
    {
        var arguments = Valid(tool);
        if (path.StartsWith("criteria.", StringComparison.Ordinal))
        {
            var criteria = (Dictionary<string, object?>)arguments["criteria"]!;
            if (path == "criteria.tags[0]") criteria["tags"] = new[] { value };
            else criteria[path["criteria.".Length..]] = value;
        }
        else
        {
            arguments[path] = value;
        }

        return arguments;
    }

    /// <summary>The tool method itself, called with the value in the argument: the only way an unpaired surrogate reaches a tool (the SDK fails the parse first).</summary>
    private Task<CallToolResult> DirectAsync(string tool, string path, string value)
    {
        const string Good = StubLog.PostId;
        return (tool, path) switch
        {
            ("curia_read", _) => _tools.ReadAsync(value, Ct),
            ("curia_verify", _) => _tools.VerifyAsync(value, null, Ct),
            ("curia_revise", _) => _tools.ReviseAsync(value, "A corrected body.", null, null, null, Ct),
            ("curia_flag", _) => _tools.FlagAsync(value, "incorrect", "The premise is wrong.", Ct),
            ("curia_answer", _) => _tools.AnswerAsync(value, "An answer.", null, Ct),
            ("curia_comment", _) => _tools.CommentAsync(value, "A reply.", null, Ct),
            ("curia_resolve", _) => _tools.ResolveAsync(value, Ct),
            ("curia_search", "criteria.query") => _tools.SearchAsync(new SearchCriteria { Query = value }, Ct),
            ("curia_search", "criteria.board") => _tools.SearchAsync(new SearchCriteria { Query = "jcs", Board = value }, Ct),
            ("curia_search", "criteria.author") => _tools.SearchAsync(new SearchCriteria { Query = "jcs", Author = value }, Ct),
            ("curia_search", "criteria.tags[0]") => _tools.SearchAsync(new SearchCriteria { Query = "jcs", Tags = [value] }, Ct),
            _ => throw new InvalidOperationException($"no direct call for {tool} {path} {Good}"),
        };
    }

    // ---- PJ9 ------------------------------------------------------------------------------------

    /// <summary>PJ9: each argument that reaches a path or a query is refused <c>NOT SENT</c>, by name, with nothing sent. Control first: a good value is not refused.</summary>
    [Theory]
    [MemberData(nameof(Cells))]
    public async Task R11_57_EveryToolArgumentThatReachesAPathOrAQueryIsRefusedNotSent(string tool, string path, string kind)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(path);
        await using var session = await StartAsync();

        var control = await session.CallAsync(tool, Valid(tool));
        Assert.False(control.Text.StartsWith(NotSent, StringComparison.Ordinal), control.Text);
        _log.Requests.Clear();

        string text;
        bool isError;
        if (kind == "nul")
        {
            var answer = await session.CallAsync(tool, With(tool, path, Nul));
            (isError, text) = (answer.IsError, answer.Text);
        }
        else
        {
            var result = await DirectAsync(tool, path, Lone);
            isError = result.IsError == true;
            text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        }

        Assert.True(isError, $"{tool} {path} {kind}: not an error: {text}");
        Assert.StartsWith(NotSent, text, StringComparison.Ordinal);
        Assert.Contains(path, text, StringComparison.Ordinal);
        Assert.Contains("Nothing was sent", text, StringComparison.Ordinal);
        Assert.Empty(_log.Requests);
    }

    // ---- PJ10 -----------------------------------------------------------------------------------

    /// <summary>PJ10: the refusal holds neither the character nor the text on either side of it. Green once refused; FPJ12 echoes the value and goes red.</summary>
    [Theory]
    [InlineData("curia_read", "postId", "nul")]
    [InlineData("curia_answer", "parentPostId", "nul")]
    [InlineData("curia_search", "criteria.board", "nul")]
    [InlineData("curia_read", "postId", "surrogate")]
    [InlineData("curia_resolve", "answerPostId", "surrogate")]
    public async Task R11_57_TheValueIsNeverEchoed(string tool, string path, string kind)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(path);
        await using var session = await StartAsync();
        string text;
        if (kind == "nul") text = (await session.CallAsync(tool, With(tool, path, Nul))).Text;
        else text = string.Join('\n', (await DirectAsync(tool, path, Lone)).Content.OfType<TextContentBlock>().Select(b => b.Text));

        Assert.StartsWith(NotSent, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Before, text, StringComparison.Ordinal);
        Assert.DoesNotContain(After, text, StringComparison.Ordinal);
        Assert.DoesNotContain(((char)0).ToString(), text, StringComparison.Ordinal);
        Assert.DoesNotContain(((char)0xD800).ToString(), text, StringComparison.Ordinal);
    }

    // ---- PJ11 -----------------------------------------------------------------------------------

    /// <summary>PJ11: the derived list holds the known floor (read, verify, revise, flag by <c>postId</c>; answer, comment by <c>parentPostId</c>; resolve by <c>answerPostId</c>; the four search criteria), counted before use.</summary>
    [Fact]
    public void R11_57_TheListOfArgumentsIsTheSchemasAndHasTheKnownFloor()
    {
        var derived = DerivedArguments(_tools).Select(a => $"{a.Tool}:{a.Path}").ToHashSet(StringComparer.Ordinal);

        string[] floor =
        [
            "curia_read:postId", "curia_verify:postId", "curia_revise:postId", "curia_flag:postId",
            "curia_answer:parentPostId", "curia_comment:parentPostId", "curia_resolve:answerPostId",
            "curia_search:criteria.query", "curia_search:criteria.board", "curia_search:criteria.author", "curia_search:criteria.tags[0]",
        ];
        Assert.True(derived.Count >= floor.Length, $"{derived.Count} derived: {string.Join(", ", derived)}");
        foreach (var known in floor) Assert.Contains(known, derived);
    }

    // ---- PJ40 -----------------------------------------------------------------------------------

    /// <summary>
    /// PJ40, a recorded non-property of the SDK: an unpaired surrogate in any string argument of any tool, sent as the frame a model host
    /// would send, is the JSON-RPC error -32603 "An error occurred." before any tool runs, and nothing is sent to the Forum. Green first;
    /// it flips on purpose if the SDK changes (no falsification row can hold it: it is a fact about the library).
    /// </summary>
    [Fact]
    public async Task R11_57_ALoneSurrogateIsTheSdksJsonRpcErrorNotOurs()
    {
        var checkedArguments = 0;
        await using var session = await RawFrameSession.StartAsync(_tools);
        foreach (var tool in ToolCatalogue.Build(_tools))
        {
            foreach (var path in StringLeaves(tool.ProtocolTool.InputSchema.GetProperty("properties"), string.Empty))
            {
                _log.Requests.Clear();
                var answer = await session.CallAsync(tool.ProtocolTool.Name, Arguments(path, "\"" + Before + "\\ud800" + After + "\""));

                Assert.True(answer.IsError, $"{tool.ProtocolTool.Name} {path}: {answer.Frame}");
                Assert.Contains("\"code\":-32603", answer.Frame, StringComparison.Ordinal);
                Assert.Equal("An error occurred.", answer.Text);
                Assert.Empty(_log.Requests);
                checkedArguments++;
            }
        }

        Assert.True(checkedArguments >= 20, $"only {checkedArguments} string arguments were varied");
    }

    private static List<string> StringLeaves(JsonElement properties, string prefix)
    {
        var leaves = new List<string>();
        foreach (var property in properties.EnumerateObject())
        {
            var path = prefix + property.Name;
            var type = property.Value.GetProperty("type");
            var types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(t => t.GetString()).ToArray() : [type.GetString()];
            if (types.Contains("string")) leaves.Add(path);
            else if (types.Contains("array") && property.Value.TryGetProperty("items", out var items))
            {
                var element = items.GetProperty("type");
                var elementTypes = element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().Select(t => t.GetString()).ToArray() : [element.GetString()];
                if (elementTypes.Contains("string")) leaves.Add(path + "[0]");
            }
            else if (types.Contains("object") && property.Value.TryGetProperty("properties", out var inner))
            {
                leaves.AddRange(StringLeaves(inner, path + "."));
            }
        }

        return leaves;
    }

    /// <summary>The arguments object as raw JSON text with <paramref name="rawValue"/> (already a JSON string literal) at <paramref name="path"/>.</summary>
    private static string Arguments(string path, string rawValue)
    {
        var parts = path.Split('.');
        string Build(int at)
        {
            var part = parts[at];
            var inner = at == parts.Length - 1 ? (part.EndsWith("[0]", StringComparison.Ordinal) ? "[" + rawValue + "]" : rawValue) : Build(at + 1);
            return "{\"" + part.Replace("[0]", string.Empty, StringComparison.Ordinal) + "\":" + inner + "}";
        }

        return Build(0);
    }

    // ---- PJ44 -----------------------------------------------------------------------------------

    /// <summary>
    /// PJ44, the control: a digest holding U+0000 is not refused by the tool. It is sent, and the Forum's <c>malformed</c> line comes back in a normal
    /// result (a digest that is no digest is the Forum's answer by cause). Green first; held by FPJ45, which extends the guard to <c>curia_recheck</c>.
    /// </summary>
    [Fact]
    public async Task R11_57_ARecheckDigestWithANulIsTheForumsMalformedLine()
    {
        _log.BatchAnswer = _ => [StubLog.BatchItem(null, "malformed")];
        await using var session = await StartAsync();

        var answer = await session.CallAsync("curia_recheck", new() { ["digests"] = new[] { "sha256:" + new string('a', 63) + (char)0 } });

        Assert.False(answer.IsError, answer.Text);
        Assert.DoesNotContain(NotSent, answer.Text, StringComparison.Ordinal);
        Assert.Contains("malformed", answer.Text, StringComparison.Ordinal);
        Assert.Single(_log.BatchRequests);
    }

    // ---- PJ45 -----------------------------------------------------------------------------------

    /// <summary>
    /// A U+0000 in a post <c>body</c> or a flag <c>rationale</c> is not an identifier and R11.57 does not refuse it: it is sent, because the stored-NUL
    /// question is an open lead (D61 item 28). Held by FPJ28, which gives <c>Identifier</c> a NUL clause so that these would be refused <c>NOT SENT</c>.
    /// </summary>
    [Theory]
    [InlineData("curia_comment", "body")]
    [InlineData("curia_answer", "body")]
    [InlineData("curia_flag", "rationale")]
    public async Task R11_57_ANulInABodyOrARationaleIsNotAnIdentifierAndIsSent(string tool, string member)
    {
        await using var session = await StartAsync();
        var control = await session.CallAsync(tool, Valid(tool));
        Assert.False(control.IsError, control.Text);

        _log.Requests.Clear();
        var arguments = Valid(tool);
        arguments[member] = Nul;
        var result = await session.CallAsync(tool, arguments);

        Assert.DoesNotContain(NotSent, result.Text, StringComparison.Ordinal);
        Assert.Contains(_log.Requests, r => r.StartsWith("POST ", StringComparison.Ordinal) && !r.Contains("/oauth/", StringComparison.Ordinal));
    }
}
