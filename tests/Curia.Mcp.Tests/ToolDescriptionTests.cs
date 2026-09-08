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

        // A head store under a path that is never written to: a description is composed at
        // registration and touches neither the Forum nor the filesystem.
        var heads = new HeadStore(Path.Combine(Path.GetTempPath(), "curia-tool-descriptions-never-written"));
        var tools = new ForumTools(new ForumClient(http, forum), MarkingMode.Datamark, heads);

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
    /// The Table 10 pair a tool's name maps to, and the tier sentence composed from it.
    ///
    /// <para>The map is the one thing here written by hand, and it is the mapping that genuinely
    /// lives in <see cref="ToolCatalogue"/>. It is safe from the usual objection because
    /// <c>TheCatalogueRegistersTheToolsThisStageBuilds</c> fails when the tool set changes, so a
    /// further tool cannot slip past these theories by being absent from the map — that test names
    /// it first. One map, used by every assertion below, so a tool cannot satisfy one of them by
    /// being measured against another tool's span.</para>
    /// </summary>
    private static (ResourceKind Resource, ActionKind Action) PairFor(string name) => name switch
    {
        "curia_search" => (ResourceKind.Thread, ActionKind.Search),
        "curia_read" => (ResourceKind.Thread, ActionKind.Read),

        // curia_verify reads the post it is asked about; the Acta's routes are anonymous. There is
        // no "verify" pair in Table 10 and inventing one would claim an authority boundary the
        // Forum does not enforce (R11.26).
        "curia_verify" => (ResourceKind.Thread, ActionKind.Read),
        _ => throw new InvalidOperationException(
            $"{name} has no Table 10 pair in this test's map. Add it, rather than letting the " +
            "row pass without checking what the description claims about authority."),
    };

    private static string SpanFor(string name)
    {
        var (resource, action) = PairFor(name);
        return TierSpan.For(resource, action);
    }

    /// <summary>
    /// R11.27: the tier sentence is the one substituted span, and it is <i>composed</i> from
    /// Table 10 rather than written into the description. A transcribed tier goes stale silently —
    /// F1 moved T1's tenure from seven days to forty-eight hours, and the agent-facing prose outside
    /// this repository still says "≥ 7 days".
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public void R11_27_TheDescriptionEndsWithTheComposedTierSpan(string name, string description) =>
        Assert.EndsWith(SpanFor(name), description, StringComparison.Ordinal);

    /// <summary>
    /// The notice sits outside the substituted span, so a tier sentence that changes cannot carry
    /// the notice away with it.
    ///
    /// <para><b>Sliced by each tool's OWN span, which it was not.</b> This measured every
    /// description against the length of the <i>read</i> tier sentence. That sentence is short
    /// ("Requires no credential…"); a tool whose pair is not anonymous composes one roughly three
    /// times longer. For such a tool the slice took an arbitrary suffix of the tier sentence, found
    /// no notice in it, and passed — for a reason unrelated to what the test is named for. It was
    /// invisible while all three registered tools happened to share the anonymous span.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public void R11_27_TheNoticeIsOutsideTheSubstitutedSpan(string name, string description)
    {
        var span = SpanFor(name);

        // Non-vacuity: the description really does end with this tool's own composed span, so the
        // slice below is the span rather than some suffix of the prose above it.
        Assert.EndsWith(span, description, StringComparison.Ordinal);

        Assert.DoesNotContain(ToolText.UntrustedDataNotice, span, StringComparison.Ordinal);
        Assert.Contains(ToolText.UntrustedDataNotice, description, StringComparison.Ordinal);
    }

    /// <summary>
    /// The catalogue is not empty. Every other assertion in this file is a theory over the
    /// registered tools, and a theory over nothing passes — which is precisely the vacuity this
    /// project keeps finding. This row fails when the catalogue is empty and the rest go quiet.
    /// </summary>
    [Fact]
    public void TheCatalogueRegistersTheToolsThisStageBuilds()
    {
        Assert.Equal(
            ["curia_read", "curia_search", "curia_verify"],
            Registered().Select(t => t.Name).Order());
    }
}
