using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// INX7 (NDT13's form): R11.30's third arm and R11.104's obligations (errata G76) are parsed from the errata, quoted here, and the catalogue's <c>curia_inbox</c> registration is held against what the
/// paragraph says: <b>the admission test the tool passes is the one the specification states, not the one the builder remembered</b> (ruling 10). The paragraph half is green once G76 is pasted (it carries
/// information only through FSPEC2, which deletes the third arm from a patched copy); the catalogue half is red until the tool exists.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class InboxAdmissionSpecTests : IDisposable
{
    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-inbox-spec-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ForumTools Tools(bool identity)
    {
        ForumWriter? writer = null;
        if (identity)
        {
            Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
            _owned.Add(agent!);
            writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        }

        return new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
    }

    /// <summary>INX7, the paragraph half: R11.30 (rev. 2)'s third arm is the one sentence, quoted, and G11's decision 3 is said to be superseded. Green once G76 is pasted.</summary>
    [Fact]
    public void R11_30_INX7_TheThirdArmIsStatedWhereTheAdmissionTestIsRead()
    {
        var arm = PublishedAcceptBudget.Paragraph("**R11.30 (rev. 2)**");

        Assert.Contains("A tool is also admitted where it is a read whose result is defined by what the caller has already done, which a model with no memory between sessions cannot supply to any other read.", arm, StringComparison.Ordinal);
        Assert.Contains("`curia_inbox` is admitted on that arm and on no other", arm, StringComparison.Ordinal);
        Assert.Contains("G11's decision 3", arm, StringComparison.Ordinal);
        Assert.Contains("superseded", arm, StringComparison.Ordinal);
    }

    /// <summary>
    /// INX7, the catalogue half: R11.104 says the tool is offered "when an agent identity is configured and not otherwise"; the catalogue registers it exactly then. The sentence is read from the paragraph (the
    /// probe fails if it stops saying so), and the registration is read from <see cref="ToolCatalogue"/> in both configurations. Red on the tree (absent with an identity).
    /// </summary>
    [Fact]
    public void R11_104_INX7_TheCatalogueRegistersTheToolWhereTheParagraphSaysItIs()
    {
        Assert.Contains("`curia_inbox` when an agent identity is configured and not otherwise", PublishedAcceptBudget.Paragraph("**R11.104**"), StringComparison.Ordinal);

        var with = ToolCatalogue.Build(Tools(identity: true)).Select(t => t.ProtocolTool.Name).ToArray();
        var without = ToolCatalogue.Build(Tools(identity: false)).Select(t => t.ProtocolTool.Name).ToArray();

        Assert.Contains("curia_inbox", with);
        Assert.DoesNotContain("curia_inbox", without);
    }
}
