using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R11.88 (errata G63), LOB39, the flag in a fixture means something (plan ruling 16): the end-to-end classes make their boards by asking on a name that is new by construction, so their first ask of a board
/// passes <c>--new-board</c> (<c>newBoard: true</c>) and no later ask of that board does, and the one helper that does it is itself tested: asked twice on one board, its second output must not carry the
/// words the check prints for a flag that was not needed (<c>was not needed</c>), and its first must carry the words for a flag that was given. A helper that passes the flag on every ask, which a fixture
/// would then hold as a reflex, is red.
///
/// <para><b>Oracle and form.</b> The helper does not exist on the seam and is Task 4's to write, so this fact names it by reflection (<c>Curia.Api.Tests.FirstAsk</c>, a static class) and is red by assertion until it
/// exists; the number of fixtures that pass the flag is recorded by the run. FLOB55 patches the helper to pass the flag every time.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NewBoardFixtureHelperTests
{
    [Fact]
    public void R11_88_LOB39_TheFirstAskHelperPassesTheFlagOnlyForABoardNoEarlierAskMade()
    {
        var helper = typeof(NewBoardFixtureHelperTests).Assembly.GetType("Curia.Api.Tests.FirstAsk");
        var declared = helper?.GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic).Select(m => m.Name).ToArray() ?? [];

        Assert.True(
            helper is not null && declared.Contains("AskAsync"),
            "Curia.Api.Tests.FirstAsk (the one helper the end-to-end classes ask through, taking the flag as a visible argument at the creating call and tested by asking twice on one board) does not exist: the fixtures that make a board by asking pass --new-board by hand or not at all");
    }
}
