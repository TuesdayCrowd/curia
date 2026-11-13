using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Credentials;
using Curia.Domain.Primitives;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// LRV43 (d) (errata G68, ruling 81, F-H2): the operator's refusal of a row the Forum recorded and skips. <c>curia-operator compromise</c> prints no <c>compromised</c> line for it, names the row's log
/// index and the predicate on stderr and exits with the code <c>lifecycle-audit</c> uses for "a row the Forum does not honour". No database is involved: a writer that returns <c>not-honoured</c> cannot be
/// provoked through a real store (the fold agrees with the writer in every state a test can build), so the refusal's handler is read directly.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorNotHonouredTests
{
    [Fact]
    public async Task LRV43_D_ANotHonouredRefusalWritesNothingToStdoutNamesTheRowOnStderrAndExitsSeven()
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
