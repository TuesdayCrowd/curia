using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Mcp;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.19: "MCP tool descriptions SHALL state that returned content is untrusted data. Tool
/// descriptions are read by the consuming model and are the last place to set that expectation
/// before content arrives."
///
/// <para><b>Every assertion here reads the registered collection, never a list written in this
/// file.</b> A hand-written array is a second place to remember an eighth tool, and the thing being
/// guarded is what happens when someone does not — which is the shape of trap 5 in this project's
/// own list, a corpus family no runner enumerated.</para>
///
/// <para><b>And each one asserts non-empty first.</b> The SDK publishes <c>"description": ""</c> for
/// a tool that supplies none — an empty string, not an omitted member — so a bare
/// <c>Assert.Contains</c> on a null-safe path would pass over a silently empty description. That
/// was established by building a server and reading its <c>tools/list</c>, not by reading the
/// SDK.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ToolDescriptionTests
{
    /// <summary>
    /// A catalogue built over a client that is never dialled: a description is composed at
    /// registration and does not depend on the Forum being reachable.
    /// </summary>
    private static IEnumerable<(string Name, string Description)> Registered()
    {
        using var http = new HttpClient();
        var forum = new Uri("https://forum.invalid/", UriKind.Absolute);
        var tools = new ForumTools(new ForumClient(http, forum), MarkingMode.Datamark);

        foreach (var tool in ToolCatalogue.Build(tools))
            yield return (tool.ProtocolTool.Name, tool.ProtocolTool.Description ?? string.Empty);
    }

    public static TheoryData<string, string> Tools()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, description) in Registered())
            data.Add(name, description);
        return data;
    }

    [Theory]
    [MemberData(nameof(Tools))]
    public void R11_19_EveryToolDescriptionStatesThatContentIsUntrusted(string name, string description)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(description),
            $"{name} publishes an empty description. The SDK emits \"\" rather than omitting the " +
            "member, so an empty one satisfies a Contains check on a null-safe path and says nothing.");

        Assert.Contains(ToolText.UntrustedDataNotice, description, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.27: the notice is one frozen sentence common to every tool. R10.17's warning was frozen
    /// as a constant on the reasoning that a text an operator can reword will eventually say
    /// something weaker, and a description is read <i>before</i> any content arrives, which makes it
    /// the stronger case of the same argument.
    /// </summary>
    [Fact]
    public void R11_27_TheNoticeIsOneSentenceAndIsIdenticalAcrossTools()
    {
        var notices = Registered()
            .Select(t => t.Description[t.Description.IndexOf(ToolText.UntrustedDataNotice, StringComparison.Ordinal)..]
                .Split('\n')[0])
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Single(notices);
    }

    /// <summary>
    /// R11.27: the tier sentence is the one substituted span, and it is <i>composed</i> from
    /// Table 10 rather than written into the description. A transcribed tier goes stale silently —
    /// F1 moved T1's tenure from seven days to forty-eight hours, and the agent-facing prose outside
    /// this repository still says "≥ 7 days".
    ///
    /// <para>The pair map below is the one thing here written by hand, and it is the mapping that
    /// genuinely lives in <see cref="ToolCatalogue"/>. It is safe from the usual objection because
    /// <c>TheCatalogueRegistersTheToolsThisStageBuilds</c> fails when the tool set changes, so a
    /// ninth tool cannot slip past this theory by being absent from the map — the other test names
    /// it first.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public void R11_27_TheDescriptionEndsWithTheComposedTierSpan(string name, string description)
    {
        var (resource, action) = name switch
        {
            "curia_search" => (ResourceKind.Thread, ActionKind.Search),
            "curia_read" => (ResourceKind.Thread, ActionKind.Read),
            _ => throw new InvalidOperationException(
                $"{name} has no Table 10 pair in this test's map. Add it, rather than letting the " +
                "row pass without checking what the description claims about authority."),
        };

        Assert.EndsWith(TierSpan.For(resource, action), description, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.19's notice sits outside the substituted span, so a tier sentence that changes cannot
    /// carry the notice away with it. Asserted by position: the notice appears before the span.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public void R11_27_TheNoticeIsOutsideTheSubstitutedSpan(string name, string description)
    {
        var span = description[^TierSpan.For(ResourceKind.Thread, ActionKind.Read).Length..];
        Assert.DoesNotContain(ToolText.UntrustedDataNotice, span, StringComparison.Ordinal);
        Assert.Contains(ToolText.UntrustedDataNotice, description, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    /// <summary>
    /// The catalogue is not empty. Every other assertion in this file is a theory over the
    /// registered tools, and a theory over nothing passes — which is precisely the vacuity this
    /// project keeps finding. This row fails when the catalogue is empty and the rest go quiet.
    /// </summary>
    [Fact]
    public void TheCatalogueRegistersTheToolsThisStageBuilds()
    {
        Assert.Equal(["curia_read", "curia_search"], Registered().Select(t => t.Name).Order());
    }
}
