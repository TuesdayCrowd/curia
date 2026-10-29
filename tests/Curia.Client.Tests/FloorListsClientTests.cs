using System.Diagnostics.CodeAnalysis;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.ConsoleCapture;

namespace Curia.Client.Tests;

/// <summary>
/// PR 3 (errata G47, R11.63), the CLI's half of CHN50, CHN58 and CHN59: <c>curia search</c>'s floor line states both kind lists as the Forum served them
/// (already, on the unmodified tree: <c>applies to ...; not to ...</c>), says <c>not stated</c> for a member the Forum did not serve, and reads a member that
/// is no array of strings as a malformed search page and not as an empty list. The names and the word are R11.63's code spans, read from the errata by
/// <see cref="PublishedChainWords"/>; the page is served by the stub, whose <see cref="StubLog.FloorListsJson"/> knob writes each row's lists, so the
/// oracle is the JSON the row wrote. CHN58 is a control (green on the unmodified tree) that holds the CLI's words byte for byte through the extraction
/// Task 8 makes.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FloorListsClientTests
{
    private static async Task<(int Exit, string Stdout, string Stderr)> SearchAsync(string? listsJson)
    {
        using var log = new StubLog { FloorListsJson = listsJson };
        var args = Args.Parse(["search", "x"], 1);
        return await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, log.Client(), MarkingMode.None, null, TestContext.Current.CancellationToken));
    }

    private static string FloorLine(string stdout) =>
        stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Single(l => l.StartsWith("floor     ", StringComparison.Ordinal));

    /// <summary>CHN58 (a control). For a document serving both lists the line is byte for byte what it printed before: <c>applies to ...; not to ...</c>.</summary>
    [Fact]
    public async Task R11_63_CHN58_TheFloorLineForBothListsIsUnchanged()
    {
        var (exit, stdout, _) = await SearchAsync(null);

        Assert.Equal(0, exit);
        Assert.Equal(
            "floor     min_verification \"V0\" (\"published\", \"mcp-search\"); applies to \"answer\", \"finding\"; not to \"question\", \"comment\"; removed by floor not stated; disputed 0",
            FloorLine(stdout));
    }

    /// <summary>
    /// CHN50, the CLI's half. A floor with no <c>not_applicable_to</c> (or no <c>applies_to</c>) prints <c>not stated</c> for it, never an empty list. M18
    /// records what the unmodified tree prints (predicted: an empty list, the reader turns an absent member into an empty array).
    /// </summary>
    [Theory]
    [InlineData("\"applies_to\":[\"answer\"]", "not to")]
    [InlineData("\"not_applicable_to\":[\"question\"]", "applies to")]
    [InlineData("", "applies to")]
    public async Task R11_63_CHN50_AnAbsentListIsNotStatedInTheFloorLine(string servedMembers, string absentLabel)
    {
        var notStated = PublishedChainWords.FloorLists().NotStated;

        var (exit, stdout, _) = await SearchAsync(servedMembers);
        var line = FloorLine(stdout);

        Assert.Equal(0, exit);
        Assert.True(line.Contains($"{absentLabel} {notStated}", StringComparison.Ordinal), $"Expected: \"{absentLabel} {notStated}\" Actual: {line}");
    }

    /// <summary>CHN59. A list that is a string, an object, <c>null</c>, or an array holding a non-string is a malformed search page, never an empty list.</summary>
    [Theory]
    [InlineData("\"applies_to\":\"answer\",\"not_applicable_to\":[]")]
    [InlineData("\"applies_to\":{},\"not_applicable_to\":[]")]
    [InlineData("\"applies_to\":null,\"not_applicable_to\":[]")]
    [InlineData("\"applies_to\":[\"answer\",7],\"not_applicable_to\":[]")]
    [InlineData("\"applies_to\":[],\"not_applicable_to\":\"question\"")]
    [InlineData("\"applies_to\":[],\"not_applicable_to\":null")]
    public async Task R11_63_CHN59_AListThatIsNoArrayOfStringsIsAMalformedSearchPage(string lists)
    {
        var (exit, stdout, _) = await SearchAsync(lists);

        Assert.True(exit != 0, $"Expected: a failure Actual: exit 0 and a floor line: {FloorLine(stdout)}");
    }
}
