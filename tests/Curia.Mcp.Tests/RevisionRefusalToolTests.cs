using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.61 (errata G45) at the tool: <c>curia_revise</c> relays the Forum's second-revision refusals worded by their cause (RVO33), its description and
/// those of the three other posting tools state the refusals and no longer say a revision landing after the read is not seen (RVO34, RVO50), and
/// <c>curia-mcp</c> is 0.8.9 since errata G46 (RVO35). The golden phrases are <see cref="RevisionWording"/>'s, the same constants <c>Refusal.Summary</c> and the help share.
///
/// <para>RVO33 drives the stub's <see cref="StubLog.RefusesSubmissionsAs"/> knob, which PR 1's Task 0 declared and the stub ignores: the tool posts and
/// reports <c>POSTED</c>, so each fact is red by assertion until Task 4 gives the knob a body.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RevisionRefusalToolTests : IDisposable
{
    /// <summary>Asserts two descriptions are equal and says both in full when they are not: xUnit shortens a long string's difference, and a red text is what these probes leave behind.</summary>
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private readonly List<IDisposable> _owned = [];
    private readonly List<string> _homes = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private ForumTools AliceTools(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var home = Directory.CreateTempSubdirectory("curia-mcp-g45-").FullName;
        _homes.Add(home);
        _owned.Add(agent!);
        var session = new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System);
        return new ForumTools(log.Client(), MarkingMode.Datamark, new HeadStore(home), new ForumWriter(agent!, session, TimeProvider.System));
    }

    private static string Missing(string text, params string[] phrases) =>
        string.Join("; ", phrases.Where(p => !text.Contains(p, StringComparison.OrdinalIgnoreCase)).Select(p => $"missing \"{p}\""));

    /// <summary>RVO33. A Forum refusal of either 409 reaches the model worded by cause, as an error, with no tier span; the revision was refused, not posted.</summary>
    [Theory]
    [InlineData(nameof(StubIngestRefusal.AlreadyRevised))]
    [InlineData(nameof(StubIngestRefusal.RevisionInFlight))]
    public async Task R11_61_AForumRefusalOfARevisionReachesTheModelWordedByItsCause(string named)
    {
        var refusal = Enum.Parse<StubIngestRefusal>(named);
        using var log = new StubLog { RefusesSubmissionsAs = refusal, RefusalNamedPostId = "01REVISIONID00000000000007" };
        var tools = AliceTools(log);

        string outcome;
        try
        {
            var result = await tools.ReviseAsync(StubLog.PostId, "A corrected body.", null, null, null, Ct);
            outcome = (result.IsError == true ? "error: " : "not an error: ") + Text(result);
        }
        catch (ModelContextProtocol.McpException refused)
        {
            outcome = "error: " + refused.Message;
        }

        var phrases = refusal == StubIngestRefusal.AlreadyRevised
            ? new[] { RevisionWording.AlreadyRevisedCause, Curia.Canon.Json.DisplayLiteral.Of("01REVISIONID00000000000007"), RevisionWording.AlreadyRevisedRemedy }
            : [RevisionWording.InFlightCause, RevisionWording.NothingStoredOrSpent, RevisionWording.InFlightRetry];
        var problems = string.Join(
            "; ",
            new[]
            {
                outcome.StartsWith("error: ", StringComparison.Ordinal) ? string.Empty : "the revision was posted, not refused",
                Missing(outcome, phrases),
                outcome.Contains("trust tier", StringComparison.OrdinalIgnoreCase) ? "worded with a tier" : string.Empty,
            }.Where(p => p.Length > 0));

        Said(string.Empty, problems);
    }

    private List<(string Name, string Description)> Registered()
    {
        using var log = new StubLog();
        var tools = AliceTools(log);
        return [.. ToolCatalogue.Build(tools).Select(t => (t.ProtocolTool.Name, t.ProtocolTool.Description ?? string.Empty))];
    }

    /// <summary>RVO34, the tool half. The description of <c>curia_revise</c> states the refusal and no longer says a revision landing after the read is not seen.</summary>
    [Fact]
    public void R11_61_TheReviseDescriptionStatesTheRefusalAndDropsTheRetiredSentence()
    {
        var description = Registered().Single(t => t.Name == "curia_revise").Description;
        Assert.NotEmpty(description);

        var problems = string.Join(
            "; ",
            new[]
            {
                Missing(description, RevisionWording.SecondRevisionRefused),
                description.Contains("a revision landing since is not in", StringComparison.Ordinal) ? "still says a revision landing since is not in the read" : string.Empty,
                Missing(ToolText.ReviseTemplate, RevisionWording.SecondRevisionRefused),
            }.Where(p => p.Length > 0));

        Said(string.Empty, problems);
    }

    /// <summary>RVO50, the tool half. The descriptions of the four posting tools state that a post identical to a recorded one is refused, naming it.</summary>
    [Theory]
    [InlineData("curia_ask")]
    [InlineData("curia_answer")]
    [InlineData("curia_comment")]
    [InlineData("curia_revise")]
    public void R11_61_ThePostingToolDescriptionsStateTheRefusalOfAnIdenticalPost(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var description = Assert.Single(Registered(), t => t.Name == name).Description;
        Assert.NotEmpty(description);

        Said(string.Empty, Missing(description, RevisionWording.IdenticalPostRefused));
    }

    /// <summary>RVO35. <c>curia-mcp</c> was 0.8.8 here was 0.8.9 under errata G46 and was 0.8.10 under errata G47 and is 0.8.11 since errata G48 (the pin follows the version): the descriptions and a refusal's wording change; no tool is added.</summary>
    [Fact]
    public void R11_61_TheVersionIs089()
    {
        Assert.Equal("0.8.11", ToolCatalogue.Version);
    }
}
