using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.87 (errata G62), NMN39: the description of a <c>curia-mcp</c> tool states what a tag is (R11.59's seven phrases) if and only if its registered input schema has a top-level
/// <c>tags</c> argument. <c>curia_search</c>'s <c>criteria.tags</c> is a filter (R9.45), a member of the nested record and not of the schema's own <c>properties</c>.
///
/// <para><b>Oracles.</b> Two artifacts that are not the code under test's own list: the registered tool's input schema (<c>properties</c>) and its description. The set of tools that
/// take <c>tags</c> is derived from the schema and asserted non-empty before it is compared (trap 93: a probe that finds no tool with <c>tags</c> is vacuous), and
/// <c>curia_search</c> is asserted absent from it, so a reading that descends into <c>criteria</c> is red. The phrases are parsed from the errata
/// (<see cref="PublishedSmallWords.TagPhrases"/>).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TagSentenceSchemaTests
{
    private static List<(string Name, string Description, bool TakesTags)> Registered()
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        var heads = new HeadStore(Path.Combine(Path.GetTempPath(), "curia-tag-sentence-never-written"));
        using var log = new StubLog();
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using (agent)
        {
            var writer = new ForumWriter(agent!, new ForumSession(client, agent!, log.Store, TimeProvider.System), TimeProvider.System);
            return [.. ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, heads, writer))
                .Select(t => (
                    t.ProtocolTool.Name,
                    t.ProtocolTool.Description ?? string.Empty,
                    t.ProtocolTool.InputSchema.TryGetProperty("properties", out var properties) && properties.TryGetProperty("tags", out _)))];
        }
    }

    /// <summary>
    /// NMN39. For every registered tool the description carries the tag sentence exactly when the schema has a top-level <c>tags</c>. The set that takes <c>tags</c> is
    /// <c>curia_ask</c> and <c>curia_revise</c>, asserted non-empty; <c>curia_search</c> is not in it. Predicted red on the seam: <c>curia_answer</c> and <c>curia_comment</c> carry the
    /// sentence and take no <c>tags</c>.
    /// </summary>
    [Fact]
    public void R11_87_NMN39_ADescriptionStatesWhatATagIsIfAndOnlyIfTheSchemaTakesTags()
    {
        var registered = Registered();
        var takers = registered.Where(t => t.TakesTags).Select(t => t.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.True(takers.Length > 0, "NOT REACHED: no registered tool has a top-level tags argument, so the comparison below would be vacuous");
        Assert.DoesNotContain("curia_search", takers);
        Assert.Equal(["curia_ask", "curia_revise"], takers);

        var phrases = PublishedSmallWords.TagPhrases();
        var expected = registered.Select(t => $"{t.Name}: {(t.TakesTags ? "states" : "does not state")}");
        var actual = registered.Select(t => $"{t.Name}: {(phrases.All(p => t.Description.Contains(p, StringComparison.Ordinal)) ? "states" : "does not state")}");

        var (e, a) = (string.Join(" | ", expected), string.Join(" | ", actual));
        if (!string.Equals(e, a, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {e}{Environment.NewLine}Actual:   {a}");
    }
}
