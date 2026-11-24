using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// ANC11's MCP leg (ruling 28): the adapter's duplicate-refusal text says the same count as the reference client, the document's own list, readable and not. The Forum is <see cref="StubLog"/> behind a handler that
/// rewrites the duplicate refusal's <c>answers</c> array; <b>the oracle is the array the probe wrote</b> (two answers the adapter reads and one it cannot). FANC11's MCP form: the line printing the readable count.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DuplicateServedToolTests : IDisposable
{
    private readonly StubLog _log = new() { RefusesAsDuplicate = true };
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-duplicate-served-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Rewrites the body of a <c>409</c> duplicate refusal with <see cref="Edit"/>; every other response passes as it came.</summary>
    private sealed class RefusalEditing(HttpMessageHandler inner, Action<JsonObject> edit) : DelegatingHandler(inner)
    {
        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The response is handed to the caller, which owns and disposes it.")]
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Conflict) return response;

            var document = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
            edit(document);
            response.Content = new StringContent(document.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        }
    }

    private ForumTools ToolsEditing(Action<JsonObject> edit)
    {
        var editing = new RefusalEditing(_log.Handler, edit);
        _owned.Add(editing);
        var http = new HttpClient(editing, disposeHandler: false) { BaseAddress = StubLog.Forum };
        _owned.Add(http);
        var client = new ForumClient(http, StubLog.Forum);
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        return new ForumTools(client, MarkingMode.None, new HeadStore(_home), new ForumWriter(agent!, new ForumSession(client, agent!, _log.Store, TimeProvider.System), TimeProvider.System));
    }

    private static string Preamble(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    /// <summary>
    /// ANC11, the MCP leg. An array of two readable answers and one the adapter cannot read: <c>answers     3 served, 2 shown below, ...</c> and the unreadable line beneath. The number is the array's length, not the
    /// readable count, which would say 2 beside a thread that has three.
    /// </summary>
    [Fact]
    public async Task R9_69_ANC11_TheDuplicateTextCountsTheDocumentsListReadableAndNot()
    {
        var tools = ToolsEditing(document =>
        {
            var answers = document["answers"]!.AsArray();
            answers.Add(JsonNode.Parse(answers[0]!.ToJsonString()));
            answers.Add(JsonNode.Parse("{\"post_id\":7}"));
        });

        var preamble = Preamble(await tools.AskAsync("b", "Seen before?", "Probably.", null, null, TestContext.Current.CancellationToken));

        var lines = preamble.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var count = Assert.Single(lines, l => l.StartsWith("answers ", StringComparison.Ordinal));
        Assert.StartsWith("answers     3 served, 2 shown below", count, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.TrimStart().StartsWith("and 1 this client could not read", StringComparison.Ordinal));
    }

    /// <summary>ANC11's empty branch (ruling 28): a document whose list is empty says <c>answers     0 served.</c> and where to read the thread, in the adapter's own terms.</summary>
    [Fact]
    public async Task R9_69_ANC11_AnEmptyListSaysZeroServedAndWhereToReadTheThread()
    {
        var tools = ToolsEditing(document => document["answers"] = new JsonArray());

        var preamble = Preamble(await tools.AskAsync("b", "Seen before?", "Probably.", null, null, TestContext.Current.CancellationToken));

        var count = Assert.Single(preamble.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), l => l.StartsWith("answers ", StringComparison.Ordinal));
        Assert.StartsWith("answers     0 served. Read the thread with curia_read ", count, StringComparison.Ordinal);
    }
}
