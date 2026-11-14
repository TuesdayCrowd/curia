using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Credentials;
using Curia.Domain.Primitives;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// LRV43 (d) (errata G68, ruling 81, F-H2; Amendment 8, ruling 99): the operator's refusal handler, given a row the Forum recorded and skips, names the row's log index and the predicate on the writer
/// it is handed and returns the code <c>lifecycle-audit</c> uses for "a row the Forum does not honour". That is all this class shows. The handler is a private method read by reflection, because
/// <c>OperatorCommands</c> builds its store inside each command and a writer that returns <c>not-honoured</c> cannot be provoked through a real store (the fold agrees with the writer in every state a
/// test can build); a rename fails the probe by name. The handler takes stderr alone, so nothing here observes stdout, and nothing here runs <c>curia-operator compromise</c>: that the command prints no
/// <c>compromised</c> line and exits 7 for such a row is exercised by the live run on a build with a seeded defect, not by this class.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorNotHonouredTests
{
    [Fact]
    public async Task LRV43_D_TheRefusalHandlerExitsSevenForANotHonouredRowAndNamesItsIndexAndPredicate()
    {
        var refuse = typeof(OperatorCommands).GetMethod("RefuseAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Curia.OperatorTool.OperatorCommands.RefuseAsync is not found: the refusal handler this probe reads was renamed");
        using var stderr = new StringWriter();

        var exit = await (Task<int>)refuse.Invoke(null, [stderr, KeyActErrors.NotHonoured(7, "lifecycle-typ-mismatch")])!;

        Assert.Equal(
            $"exit {ExitCode.LifecycleRowsSkipped}; stderr names the index and the predicate: True",
            $"exit {exit}; stderr names the index and the predicate: {stderr.ToString().Contains("log index 7", StringComparison.Ordinal) && stderr.ToString().Contains("lifecycle-typ-mismatch", StringComparison.Ordinal)}");
        Assert.Equal(7, ExitCode.LifecycleRowsSkipped);
    }

    /// <summary>The refusal of any other cause keeps its exit code: <see cref="ExitCode.Refused"/>.</summary>
    [Fact]
    public async Task LRV43_D_AnotherRefusalStillExitsRefused()
    {
        var refuse = typeof(OperatorCommands).GetMethod("RefuseAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        using var stderr = new StringWriter();

        var exit = await (Task<int>)refuse.Invoke(null, [stderr, KeyActErrors.BeforeBinding()])!;

        Assert.Equal(ExitCode.Refused, exit);
    }
}
