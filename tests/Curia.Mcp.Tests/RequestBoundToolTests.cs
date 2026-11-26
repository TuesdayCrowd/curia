using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// CLW5 (errata G84, R11.111): <c>curia_ask</c> against a Forum that answers the typed <c>413</c>. The tool error carries R11.111's sentence, read from the errata, once; it mentions neither <c>curia_resend</c> nor a digest;
/// and <c>curia_acts</c> lists nothing, because the act was not kept. Compared to the errata and not to <c>ForumResult</c>, so a copy of the words in the adapter is caught.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RequestBoundToolTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly List<string> _homes = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private ForumTools AliceTools(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var home = Directory.CreateTempSubdirectory("curia-mcp-g84-").FullName;
        _homes.Add(home);
        _owned.Add(agent!);
        var session = new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System);
        return new ForumTools(log.Client(), MarkingMode.Datamark, new HeadStore(home), new ForumWriter(agent!, session, TimeProvider.System));
    }

    [Fact]
    public async Task R11_111_CLW5_ATooLargeActIsRelayedAsTheErratasSentenceAndIsNotKept()
    {
        using var log = new StubLog();
        log.RefusesSubmissionsAs = StubIngestRefusal.TooLarge;
        var tools = AliceTools(log);
        string relayed;
        try
        {
            var result = await tools.AskAsync("b", "A title?", "A body.", null, null, Ct);
            relayed = (result.IsError == true ? "error: " : "not an error: ") + Text(result);
        }
        catch (ModelContextProtocol.McpException refused)
        {
            relayed = "error: " + refused.Message;
        }

        var acts = Text(await tools.ActsAsync(Ct));
        var sentence = PublishedRequestBound.Sentence();
        var problems = new List<string>();
        if (!relayed.StartsWith("error: ", StringComparison.Ordinal)) problems.Add("not relayed as an error");
        var at = relayed.IndexOf(sentence, StringComparison.Ordinal);
        if (at < 0 || relayed.IndexOf(sentence, at + 1, StringComparison.Ordinal) >= 0) problems.Add($"R11.111's sentence is not on the relayed text exactly once: {relayed}");
        if (relayed.Contains("curia_resend", StringComparison.Ordinal) || relayed.Contains("sha256:", StringComparison.Ordinal)) problems.Add("the relayed text offers curia_resend or names a digest");
        if (!acts.Contains("no act is kept", StringComparison.Ordinal)) problems.Add($"curia_acts lists an act: {acts}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
