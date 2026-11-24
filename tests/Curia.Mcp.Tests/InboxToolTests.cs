using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.104 and R10.101 (errata G76, with Amendment 1's ruling 17), probes INX3, INX4 and INX5: <c>curia_inbox</c>, its registration, its schema, the arguments it refuses before anything is sent, its
/// description, and what it returns. <b>Oracles.</b> The posts the test signs and serves (the stub's agent signs each; a body marker the test plants in every body must appear nowhere in the result, ruling 17),
/// the request the stub received, the CLI's words for the account line (<c>Program.Summary</c>'s literal, held by INX1), and R11.104's own paragraph, read from the errata at test time, for the parameters
/// and the phrases the description must carry. A refusal row is non-vacuous: the tool is first required to be listed, since a tool that does not exist refuses every call.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class InboxToolTests : IDisposable
{
    private const string Marker = "ZQ9BODYMARKER";
    private static readonly string[] Ids = ["01INXQUESTION00000000000A1", "01INXQUESTION00000000000A2", "01INXQUESTION00000000000A3"];
    private static readonly string[] Titles = ["First inbox question title", "Second inbox question title", "Third inbox question title"];
    private const string ForeignTitle = "A title nobody signed for this post";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-inbox-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ForumTools ToolsOver(HttpMessageHandler handler, bool identity = true)
    {
        var http = new HttpClient(handler, disposeHandler: false) { BaseAddress = StubLog.Forum };
        _owned.Add(http);
        var client = new ForumClient(http, StubLog.Forum);
        ForumWriter? writer = null;
        if (identity)
        {
            Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
            _owned.Add(agent!);
            writer = new ForumWriter(agent!, new ForumSession(client, agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        }

        return new ForumTools(client, MarkingMode.None, new HeadStore(_home), writer);
    }

    private ForumTools Tools(bool identity = true) => ToolsOver(_log.Handler, identity);

    private static async Task<McpProtocolSession> SessionAsync(ForumTools tools) =>
        await McpProtocolSession.StartAsync(tools, "https://agents.example/alice");

    private static List<(string Name, string Description, McpServerTool Tool)> Registered(ForumTools tools) =>
        [.. ToolCatalogue.Build(tools).Select(t => (t.ProtocolTool.Name, t.ProtocolTool.Description ?? string.Empty, t))];

    /// <summary>The inbox the stub serves: three questions, the third served with a canonical form its signature does not cover (so it does not verify), each body carrying <see cref="Marker"/>.</summary>
    private void ServeInbox()
    {
        var signed = new List<(PostDraft Draft, SignedSubmission Signed)>();
        for (var i = 0; i < 3; i++)
        {
            var draft = new PostDraft { Kind = PostKind.Question, Board = "b", Title = Titles[i], Body = $"{Marker}-{i} the body of question {i}." };
            signed.Add((draft, _log.SignDraft(draft)));
        }

        var foreign = new PostDraft { Kind = PostKind.Question, Board = "b", Title = ForeignTitle, Body = $"{Marker}-foreign the body nobody signed." };
        var foreignCanonical = Encoding.UTF8.GetString(_log.SignDraft(foreign).Canonical.Span);
        for (var i = 0; i < 3; i++)
            _log.ServePost(Ids[i], signed[i].Draft, signed[i].Signed, [], servedCanonical: i == 2 ? foreignCanonical : null);

        _log.InboxJson = "{\"results\":[" + string.Join(",", Ids.Select(id => _log.ServedPosts[id])) + "],\"next_cursor\":null,"
            + "\"open_before_exclusions\":7,\"excluded_as_own\":1,\"excluded_as_already_answered\":2}";
    }

    // ---- INX3 ---------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>INX3. <c>curia_inbox</c> is registered with an identity, read-only and idempotent (it changes nothing). Red on the tree (absent).</summary>
    [Fact]
    public void R11_104_INX3_IsRegisteredWithAnIdentityAsAReadOnlyIdempotentTool()
    {
        var tool = Registered(Tools()).SingleOrDefault(t => t.Name == "curia_inbox");

        Assert.True(tool.Tool is not null, "Expected: curia_inbox registered with an identity Actual: absent");
        Assert.True(tool.Tool!.ProtocolTool.Annotations?.ReadOnlyHint == true && tool.Tool.ProtocolTool.Annotations?.IdempotentHint == true, "Expected: read-only and idempotent");
    }

    /// <summary>INX3, the control (green on the tree): without an identity the tools are the five reads and no <c>curia_inbox</c>, and the read-only server instructions are the ones without it.</summary>
    [Fact]
    public void R11_104_INX3_Control_WithoutAnIdentityTheToolsAreTheFiveReadsAndNothingNamesTheInbox()
    {
        var names = Registered(Tools(identity: false)).Select(t => t.Name).Order().ToArray();

        Assert.Equal(["curia_boards", "curia_read", "curia_recheck", "curia_search", "curia_verify"], names);
        Assert.DoesNotContain("curia_inbox", ToolText.ServerInstructions(null, ToolCatalogue.WriteToolNames(Tools())), StringComparison.Ordinal);
    }

    /// <summary>INX3. The schema takes exactly the parameters R11.104 names: <c>board</c>, <c>tags</c>, <c>limit</c>, <c>cursor</c>; the names are read from the errata's paragraph (its code spans after "taking"). Red on the tree.</summary>
    [Fact]
    public void R11_104_INX3_TheSchemaTakesExactlyTheParametersTheErrataNames()
    {
        var paragraph = PublishedAcceptBudget.Paragraph("**R11.104**");
        var taking = System.Text.RegularExpressions.Regex.Match(paragraph, @"taking ((?:`\w+`(?:, | and )?)+)", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2));
        Assert.True(taking.Success, $"R11.104 no longer says which parameters the tool takes: {paragraph}");
        var named = System.Text.RegularExpressions.Regex.Matches(taking.Groups[1].Value, @"`(\w+)`", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)).Select(m => m.Groups[1].Value).Order().ToArray();
        Assert.Equal(["board", "cursor", "limit", "tags"], named);

        var tool = Registered(Tools()).SingleOrDefault(t => t.Name == "curia_inbox");
        Assert.True(tool.Tool is not null, "Expected: curia_inbox registered with an identity Actual: absent");
        Assert.True(tool.Tool!.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties), "the schema has no properties");
        Assert.Equal(named, properties.EnumerateObject().Select(p => p.Name).Order().ToArray());
    }

    public static TheoryData<string, string> Refusals => new()
    {
        { "board", "\"\"" },
        { "board", "\"   \"" },
        { "board", "\"a\\u0000b\"" },
        { "cursor", "\"\"" },
        { "cursor", "\"  \"" },
        { "cursor", "\"a\\u0000b\"" },
        { "tags", "[null]" },
        { "tags", "[\"\"]" },
        { "tags", "[\"a,b\"]" },
        { "limit", "0" },
        { "limit", "-1" },
        { "limit", "101" },
        { "agent", "\"someone-else\"" },
    };

    /// <summary>
    /// INX3. An argument the tool refuses is refused before anything is sent (a blank <c>board</c> or <c>cursor</c>, a null or unfilterable tag, a <c>limit</c> outside 1 to 100, a string no store can keep, and an
    /// argument it does not take, named: R11.37, R11.43, R11.57, R11.78). <b>Non-vacuity:</b> the tool is first required to be listed, since a tool that is not there refuses every call. Red on the tree.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task R11_104_INX3_AnArgumentTheToolRefusesIsRefusedBeforeAnythingIsSent(string name, string json)
    {
        ServeInbox();
        await using var session = await SessionAsync(Tools());
        var listed = await session.ListAsync();
        Assert.True(listed.Any(t => t.Name == "curia_inbox"), "Expected: curia_inbox listed Actual: absent");
        _log.Requests.Clear();

        var refused = await session.CallAsync("curia_inbox", new Dictionary<string, object?> { [name] = JsonSerializer.Deserialize<JsonElement>(json) });

        Assert.True(refused.IsError, $"Expected: {name}={json} refused Actual: {refused.Text}");
        Assert.DoesNotContain(_log.Requests, r => r.Contains("/v1/inbox", StringComparison.Ordinal));
        if (name == "agent") Assert.Contains("agent", refused.Text, StringComparison.Ordinal);
    }

    /// <summary>INX3. A clean call sends exactly one inbox request (the account line's counts are the Forum's). Red on the tree.</summary>
    [Fact]
    public async Task R11_104_INX3_ACleanCallSendsOneInboxRequest()
    {
        ServeInbox();
        await using var session = await SessionAsync(Tools());
        Assert.True((await session.ListAsync()).Any(t => t.Name == "curia_inbox"), "Expected: curia_inbox listed Actual: absent");
        _log.Requests.Clear();

        var answered = await session.CallAsync("curia_inbox", new Dictionary<string, object?> { ["board"] = "b", ["limit"] = 5 });

        Assert.False(answered.IsError, answered.Text);
        Assert.Equal(1, _log.Requests.Count(r => r.StartsWith("GET /v1/inbox", StringComparison.Ordinal)));
    }

    /// <summary>A handler that answers <c>GET /v1/inbox</c> with a 429 problem document and every other request as the stub does.</summary>
    private sealed class RefusingInbox(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The response is handed to the caller, which owns and disposes it.")]
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri?.AbsolutePath == "/v1/inbox"
                ? Task.FromResult(new HttpResponseMessage((HttpStatusCode)429)
                {
                    Content = new StringContent("{\"type\":\"curia/rate/limited\",\"title\":\"Too many requests\",\"detail\":\"slow down\"}", Encoding.UTF8, "application/json"),
                })
                : base.SendAsync(request, cancellationToken);
    }

    /// <summary>INX3. A refusal of the Forum's reaches the model worded by its cause, as <c>curia_budget</c>'s does: an error result carrying the problem's title and detail (as display literals), not a transport fault. Red on the tree (the tool is absent).</summary>
    [Fact]
    public async Task R11_104_INX3_AForumRefusalIsRelayedWordedByItsCause()
    {
        ServeInbox();
        using var refusing = new RefusingInbox(_log.Handler);
        await using var session = await SessionAsync(ToolsOver(refusing));
        Assert.True((await session.ListAsync()).Any(t => t.Name == "curia_inbox"), "Expected: curia_inbox listed Actual: absent");

        var answered = await session.CallAsync("curia_inbox", []);

        Assert.True(answered.IsError, answered.Text);
        // Refusal.Summary quotes the problem document's title and detail as display literals (R10.63) and not its type, as curia_budget's refusal does.
        Assert.Contains("Too many requests", answered.Text, StringComparison.Ordinal);
        Assert.Contains("slow down", answered.Text, StringComparison.Ordinal);
    }

    // ---- INX4 ---------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// INX4. The description says an enrolled agent identity and no tier above enrolment is needed (R11.104's phrase), says a count of answers is not a filter, says a row is a title and that <c>curia_read</c>
    /// reads the question (ruling 17), and contains none of <c>Requires no credential</c> and <c>anonymous callers</c> (the words a Table 10 span composed from <c>thread | search</c> would print of a route that
    /// refuses a call with no token: <c>INX6</c> is the route's own proof). Red on the tree.
    /// </summary>
    [Fact]
    public void R11_104_INX4_TheDescriptionStatesTheRoutesRealRequirementAndNotTable10sSpan()
    {
        var paragraph = PublishedAcceptBudget.Paragraph("**R11.104**");
        Assert.Contains("an enrolled agent identity and no tier above enrolment", paragraph, StringComparison.Ordinal);
        var tool = Registered(Tools()).SingleOrDefault(t => t.Name == "curia_inbox");
        Assert.True(tool.Tool is not null, "Expected: curia_inbox registered with an identity Actual: absent");
        var description = tool.Description;

        Assert.Contains("enrolled agent identity", description, StringComparison.Ordinal);
        Assert.Contains("no tier above enrolment", description, StringComparison.Ordinal);
        Assert.Contains("not a filter", description, StringComparison.Ordinal);
        Assert.Contains("title", description, StringComparison.Ordinal);
        Assert.Contains("curia_read", description, StringComparison.Ordinal);
        Assert.DoesNotContain("Requires no credential", description, StringComparison.Ordinal);
        Assert.DoesNotContain("anonymous callers", description, StringComparison.Ordinal);
    }

    // ---- INX5 ---------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static string Text(McpAnswer answer, int block) => answer.Blocks[block];

    /// <summary>
    /// INX5 (ruling 17). The result is one first block of the adapter's own words outside every post's frame (a text block, no delimiter), then one block per question, in number equal to the posts the test
    /// served (three, one of which does not verify), each carrying the provenance envelope's lines and the standing warning. The first block carries the account line byte for byte as the CLI writes it
    /// (INX1's literal), and says the rows are oldest first and that no channel ranked them. Red on the tree.
    /// </summary>
    [Fact]
    public async Task R11_104_INX5_TheResultIsOneFirstBlockOfTheAdaptersWordsThenOneBlockPerQuestion()
    {
        ServeInbox();
        await using var session = await SessionAsync(Tools());
        Assert.True((await session.ListAsync()).Any(t => t.Name == "curia_inbox"), "Expected: curia_inbox listed Actual: absent");

        var answered = await session.CallAsync("curia_inbox", []);

        Assert.False(answered.IsError, answered.Text);
        Assert.Null(answered.Uris[0]);
        var first = Text(answered, 0);
        Assert.DoesNotContain(Datamarking.OpenDelimiter, first, StringComparison.Ordinal);
        Assert.Contains("7 open before exclusions, of which 1 yours and 2 already answered by you; 3 listed.", first, StringComparison.Ordinal);
        Assert.Contains("oldest first", first, StringComparison.Ordinal);
        Assert.Contains("no channel ranked these", first, StringComparison.Ordinal);

        var rows = Enumerable.Range(1, answered.Blocks.Count - 1).Select(i => (Uri: answered.Uris[i], Text: answered.Blocks[i])).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.All(rows, r => Assert.NotNull(r.Uri));
        for (var i = 0; i < 3; i++)
        {
            Assert.Contains(Ids[i], rows[i].Text, StringComparison.Ordinal);
            Assert.Contains("signature", rows[i].Text, StringComparison.Ordinal);
            Assert.Contains(Provenance.StandardWarning, rows[i].Text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// INX5 (ruling 17), the size harm: the body marker the test planted in every question's body appears nowhere in the result, and the title of a post that verifies appears as the verified span (its own block,
    /// as content). Red on the tree.
    /// </summary>
    [Fact]
    public async Task R11_104_INX5_NoBodyIsInTheResultAndAVerifiedTitleIsIn()
    {
        ServeInbox();
        await using var session = await SessionAsync(Tools());
        Assert.True((await session.ListAsync()).Any(t => t.Name == "curia_inbox"), "Expected: curia_inbox listed Actual: absent");

        var answered = await session.CallAsync("curia_inbox", []);

        Assert.False(answered.IsError, answered.Text);
        Assert.DoesNotContain(Marker, answered.Text, StringComparison.Ordinal);
        Assert.Contains(Titles[0], answered.Blocks[1], StringComparison.Ordinal);
        Assert.Contains(Titles[1], answered.Blocks[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// INX5, the control inside it (FINX8's harm: a title taken from the Forum's span or without the verdict): a question whose signature does not cover the form the Forum served is still listed, its block
    /// present, its title absent (neither the title it served nor the title of the form it served), and the reason line present (G19: <c>(no title: the signature did not verify ...)</c>). Red on the tree.
    /// </summary>
    [Fact]
    public async Task R11_104_INX5_AQuestionThatDoesNotVerifyIsListedWithoutATitleAndSaysWhy()
    {
        ServeInbox();
        await using var session = await SessionAsync(Tools());
        Assert.True((await session.ListAsync()).Any(t => t.Name == "curia_inbox"), "Expected: curia_inbox listed Actual: absent");

        var answered = await session.CallAsync("curia_inbox", []);

        Assert.False(answered.IsError, answered.Text);
        var row = Assert.Single(answered.Blocks.Skip(1), b => b.Contains(Ids[2], StringComparison.Ordinal));
        Assert.DoesNotContain(Titles[2], row, StringComparison.Ordinal);
        Assert.DoesNotContain(ForeignTitle, row, StringComparison.Ordinal);
        Assert.Contains("(no title:", row, StringComparison.Ordinal);
        Assert.Contains("did not verify", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// INX5, ruling 32: each row's standing warning is the adapter's, appended after the row's closing span (or after its <c>no title</c> line when there is no span) and never inside a span. A row that verified carries
    /// one open delimiter, one close delimiter and the warning after both; the row that did not verify carries no delimiter and the warning after its reason line; each row carries the warning once. The CLI's titled
    /// page is the control: <c>TitledIndex.Write</c> is unchanged, so every existing <c>--titles</c> suite passes unedited and none prints the warning per row.
    /// </summary>
    [Fact]
    public async Task R11_104_INX5_TheStandingWarningFollowsEachRowsSpanAndIsNeverInsideOne()
    {
        ServeInbox();
        await using var session = await SessionAsync(Tools());
        Assert.True((await session.ListAsync()).Any(t => t.Name == "curia_inbox"), "Expected: curia_inbox listed Actual: absent");

        var answered = await session.CallAsync("curia_inbox", []);

        Assert.False(answered.IsError, answered.Text);
        var rows = answered.Blocks.Skip(1).ToArray();
        Assert.Equal(3, rows.Length);
        for (var i = 0; i < rows.Length; i++)
        {
            var warning = rows[i].IndexOf(Provenance.StandardWarning, StringComparison.Ordinal);
            Assert.True(warning >= 0 && rows[i].IndexOf(Provenance.StandardWarning, warning + 1, StringComparison.Ordinal) < 0, $"row {i} does not carry the standing warning exactly once");
            if (i < 2)
            {
                var open = rows[i].IndexOf(Datamarking.OpenDelimiter, StringComparison.Ordinal);
                var close = rows[i].IndexOf(Datamarking.CloseDelimiter, StringComparison.Ordinal);
                Assert.True(open >= 0 && close > open, $"row {i} has no span to place the warning after");
                Assert.True(warning > close, $"row {i}'s warning is not after its closing delimiter");
            }
            else
            {
                Assert.DoesNotContain(Datamarking.OpenDelimiter, rows[i], StringComparison.Ordinal);
                Assert.True(warning > rows[i].IndexOf("(no title:", StringComparison.Ordinal), "the unverified row's warning is not after its reason line");
            }
        }
    }

    /// <summary>INX5: the server instructions name <c>curia_inbox</c> when an identity is configured, from the catalogue's own list (R11.104: "the server instructions SHALL name it"). Red on the tree.</summary>
    [Fact]
    public void R11_104_INX5_TheServerInstructionsNameTheInbox()
    {
        var instructions = ToolText.ServerInstructions("https://agents.example/alice", ToolCatalogue.WriteToolNames(Tools()));

        Assert.Contains("curia_inbox", instructions, StringComparison.Ordinal);
    }
}
