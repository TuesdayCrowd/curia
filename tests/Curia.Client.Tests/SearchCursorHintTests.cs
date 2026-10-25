using System.Diagnostics.CodeAnalysis;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.ConsoleCapture;

namespace Curia.Client.Tests;

/// <summary>
/// R9.35 (errata G36) at <c>curia search</c>: the line that offers the next page says the cursor belongs to the terms, flags and <c>--limit</c> that
/// made it, in the plain form and the titled one, and the verb's help says so beside <c>--cursor</c>. The stub serves the page and its cursor, so the
/// oracle is what this fact set on it and the words the errata gives the rule (the bound members are parsed from R9.35, not from the hint).
/// Weak by construction: the hint is a sentence, and the Forum's refusal is the property (the endpoint and use-case probes carry it).
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchCursorHintTests
{
    private static async Task<string> SearchOutputAsync(bool titles)
    {
        using var log = new StubLog();
        var args = Args.Parse(titles ? ["search", "x", "--titles"] : ["search", "x"], 1);
        var (exit, stdout, _) = await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, log.Client(), MarkingMode.None, null, TestContext.Current.CancellationToken));

        Assert.True(exit is 0 or 9, $"curia search exited {exit}");
        return stdout;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task R9_35_TheMoreResultsHintSaysTheCursorBelongsToTheQuery(bool titles)
    {
        var stdout = await SearchOutputAsync(titles);

        // Control: the hint is printed, so what follows is about its words and not about a missing line.
        var hint = stdout.Split('\n').SingleOrDefault(l => l.StartsWith("more results:", StringComparison.Ordinal));
        Assert.NotNull(hint);

        Assert.Contains("same terms, flags and --limit", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void R9_35_TheHelpForCursorSaysItBelongsToTheQueryThatMadeIt()
    {
        var help = Help.For("search")!;

        // Control: the usage line names the flag, so what follows is about a sentence that explains it.
        Assert.Contains("[--cursor c]", help, StringComparison.Ordinal);
        Assert.NotEmpty(PublishedSearchPage.BoundMembers());

        var explanation = help.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("--cursor", StringComparison.Ordinal));
        Assert.True(explanation is not null, $"the verb's help has no line that explains --cursor:\n{help}");
        Assert.Contains("belongs", explanation, StringComparison.Ordinal);
        Assert.Contains("--limit", explanation, StringComparison.Ordinal);
    }
}
