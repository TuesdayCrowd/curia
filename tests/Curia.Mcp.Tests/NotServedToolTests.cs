using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// PR 3 (errata G50, R10.91), Task 0's probes RLW11 (the tool's half) and RLW15 (the tools' half) at the adapter: <c>curia_read</c>'s <c>accepted</c> line says the
/// definition beside an accepted answer the Forum lists by digest alone, and every tool whose description prints such an entry carries the definition sentence.
/// The words are G50's (<see cref="PublishedReadersLastWords"/>); the stub serves the state each row names. The help's half of RLW15 is in the client's
/// <c>NotServedWordsTests</c>, because the help is internal to <c>curia</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NotServedToolTests : IDisposable
{
    private static readonly string Digest = "sha256:" + new string('a', 64);

    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-not-served-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>RLW11 (the tool's half). <c>curia_read</c>'s <c>accepted</c> line for an accepted answer listed by digest alone carries the pointer beside the digest and never says <c>withheld</c> of it.</summary>
    [Fact]
    public async Task R10_91_RLW11_TheReadToolsAcceptedLineSaysTheSameWords()
    {
        using var log = new StubLog { ThreadRouteServesTheThread = true };
        log.AcceptedAnswer = StubAcceptedAnswer.Standing;
        log.AcceptedAnswerKind = "answer";
        log.AcceptedAnswerSupersededBy = [];
        log.AcceptedAnswerNames = (Digest, null);

        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using var owned = agent;
        var tools = new ForumTools(log.Client(), MarkingMode.None, new HeadStore(_home), new ForumWriter(agent!, new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System), TimeProvider.System));
        var result = await tools.ReadAsync(StubLog.PostId, TestContext.Current.CancellationToken);
        var first = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        var line = first.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').FirstOrDefault(l => l.StartsWith("accepted ", StringComparison.Ordinal)) ?? string.Empty;

        var pointer = PublishedReadersLastWords.PointerTail();
        Assert.True(line.Contains(Curia.Canon.Json.DisplayLiteral.Of(Digest), StringComparison.Ordinal), $"the accepted line does not name the digest: {first}");
        Assert.True(line.Contains(pointer, StringComparison.Ordinal), $"Expected: \"{pointer}\" in the accepted line Actual: {line}");
    }

    private static List<(string Name, string Description)> Registered()
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        var heads = new HeadStore(Path.Combine(Path.GetTempPath(), "curia-tool-descriptions-never-written"));
        using var log = new StubLog();
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using var owned = agent;
        var writer = new ForumWriter(agent!, new ForumSession(client, agent!, log.Store, TimeProvider.System), TimeProvider.System);
        return [.. ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, heads, writer)).Select(t => (t.ProtocolTool.Name, t.ProtocolTool.Description ?? string.Empty))];
    }

    /// <summary>
    /// RLW15 (the tools' half). Every tool whose description contains <c>not served</c>, and every tool that prints a digest-only entry whatever its description
    /// says today (<c>curia_read</c>, <c>curia_search</c>, <c>curia_verify</c>, <c>curia_recheck</c>), carries R10.91's definition sentence; a tool added with the
    /// words and without the sentence is red.
    /// </summary>
    [Fact]
    public void R10_91_RLW15_EveryToolThatPrintsADigestOnlyEntryDefinesIt()
    {
        string[] printers = ["curia_read", "curia_search", "curia_verify", "curia_recheck"];
        var tools = Registered();
        Assert.True(tools.Count >= 10, $"only {tools.Count} tools were registered");
        foreach (var name in printers) Assert.Contains(tools, t => t.Name == name);

        var sentence = PublishedReadersLastWords.DefinitionSentence();
        var problems = tools
            .Where(t => printers.Contains(t.Name, StringComparer.Ordinal) || t.Description.Contains("not served", StringComparison.Ordinal))
            .Where(t => !PublishedReadersLastWords.Folded(t.Description).Contains(sentence, StringComparison.Ordinal))
            .Select(t => $"{t.Name}: Expected: the definition \"{sentence}\" in its description")
            .ToArray();
        Assert.True(problems.Length == 0, string.Join(Environment.NewLine, problems));
    }
}
