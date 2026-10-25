using System.Diagnostics.CodeAnalysis;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.ConsoleCapture;

namespace Curia.Client.Tests;

/// <summary>
/// R9.33 (errata G33, closing D71) at <c>curia search</c>: the three members R9.31 states are printed, <c>clamped false</c> is
/// printed and is not silence, and a Forum that did not serve them reads <c>not stated</c>; so the three pages differ, which is
/// the property. The stub serves the pages, so the oracle is what each fact set on it.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchClampPrintTests
{
    private static async Task<string> SearchOutputAsync(StubLog log)
    {
        var args = Args.Parse(["search", "x"], 1);
        var (exit, stdout, _) = await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, log.Client(), MarkingMode.None, null, TestContext.Current.CancellationToken));

        Assert.Equal(ExitCode.Ok, exit);
        return stdout;
    }

    [Fact]
    public async Task R9_33_CuriaSearchPrintsTheClampAndFalseIsNotSilence()
    {
        using var clamped = new StubLog { FloorClamped = true, FloorRequestedMinVerification = "V0" };
        using var unclamped = new StubLog();
        using var older = new StubLog { OmitClampMembers = true };

        var clampedOut = await SearchOutputAsync(clamped);
        var unclampedOut = await SearchOutputAsync(unclamped);
        var olderOut = await SearchOutputAsync(older);

        Assert.Contains("CLAMPED", clampedOut, StringComparison.Ordinal);
        Assert.Contains("requested V0", clampedOut, StringComparison.Ordinal);
        Assert.Contains("served V1", clampedOut, StringComparison.Ordinal);
        Assert.Contains("clamped false", unclampedOut, StringComparison.Ordinal);
        Assert.Contains("not stated", olderOut, StringComparison.Ordinal);

        Assert.NotEqual(unclampedOut, olderOut);
        Assert.NotEqual(clampedOut, unclampedOut);
        Assert.NotEqual(clampedOut, olderOut);
    }
}
