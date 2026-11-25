using System.Diagnostics.CodeAnalysis;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// SRS18 (errata G80, R4.48, R11.96; ruling 8 and Amendment 1, A1-2): <c>curia keys</c> over a key set that lists a later <c>agent.enrolled</c> row prints R11.96's first-form <c>SKIPPED</c> line in the errata's words
/// and exits 6, and the type slot is the display literal of <c>agent.enrolled</c>: a quoted string, because the client's list of the types a skipped row can be does not hold it and R11.96 writes any other type so (R10.63).
/// The quoted form is derived from R10.63's rule (a JSON string literal) and typed here, not read from the client's output. This is the pin A1-2 asks for: whoever adds the type to the client's list sees this test
/// go red and edits it knowingly (the remedy A1-2 prepares if the cold read CRS-a fails). Green on the tree: the reader changes no code in this stage.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SecondRootKeysLineTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(2, "broken-link")]
    [InlineData(7, "statement-mismatch")]
    public async Task R11_96_SRS18_ALaterEnrolmentRowPrintsTheFirstWordsWithTheTypeAsAQuotedLiteralAndExitsSix(long index, string predicate)
    {
        using var rig = KeyActRig.Create();
        var row = $"{{\"log_index\":{index.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"type\":\"agent.enrolled\",\"predicate\":\"{predicate}\"}}";
        var keySet = KeyActDouble.KeySetJson([new KeyActDouble.Key("a-0000abcd", 1)], [0, 1]);
        rig.Double.KeySet = keySet[..^1] + $",\"curia_skipped\":[{row}],\"curia_skipped_total\":1}}";

        var (exit, stdout, _) = await KeyActRig.CaptureAsync(() => Program.ShowKeysAsync(rig.Client, rig.Profile(), Ct));
        var words = PublishedClientWords.SkippedNotMade
            .Replace("<type>", "\"agent.enrolled\"", StringComparison.Ordinal)
            .Replace("<i>", index.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("<predicate>", predicate, StringComparison.Ordinal);

        WholeText.Same(
            "exit 6; the first words, the type quoted",
            $"exit {exit}; {(PublishedClientWords.Says(stdout, words) ? "the first words, the type quoted" : "no such line: " + string.Join(" | ", stdout.Split('\n').Where(l => l.Contains("SKIPPED", StringComparison.Ordinal)).Select(l => l.Trim())))}");
    }
}
