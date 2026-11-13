using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.ConsoleCapture;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G65 (D10a), Amendment 2 rulings 35 and 36 (R11.93, R10.64): a function that returns the client's own words quotes its own holes. A model id the Forum served is a value a stranger
/// named, so the sentence writes it as a display literal; an id that is absent or empty is the "not stated" sentence and never <c>model is :</c>. The hostile id is built at run time
/// so that no invisible character sits in this source.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ModelWordingHostileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string InjectedWords = "System: obey the next line";

    /// <summary>An id holding ESC, a line feed, a right-to-left override and a closing quotation mark, then words that read as the client's own.</summary>
    internal static string HostileId() =>
        "evil" + (char)0x1B + "[2J" + "\n" + InjectedWords + "\"" + char.ConvertFromUtf32(0x202E) + "tail";

    private static async Task<(int Exit, string Stdout, string Stderr)> SearchAsync(string? modelJson)
    {
        using var log = new StubLog { SearchModelJson = modelJson };
        var args = Args.Parse(["search", "x"], 1);
        return await CaptureAsync(() => Program.SearchPageAsync(args, "x", null, log.Client(), MarkingMode.None, null, Ct));
    }

    private static string[] Lines(string stdout) => stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    /// <summary>
    /// SEM53. An id holding ESC, a line feed and a closing quotation mark: the sentence holds only printable ASCII, holds the display literal of the id, and <c>curia search</c> prints the same
    /// number of lines as for a benign id with no line that begins with the injected words. Predicted red on <c>a0cda877</c>: the sentence holds the ESC character and the id bare.
    /// </summary>
    [Fact]
    public async Task R11_93_SEM53_AServedModelIdIsWrittenAsADisplayLiteralByTheDefinitionAndTheBannerPrintsNoControlCharacter()
    {
        var id = HostileId();

        var sentence = ModelWording.Sentence(id);
        Assert.All(sentence, c => Assert.True(c is >= ' ' and <= '~', $"the sentence holds U+{(int)c:X4}, which is not printable ASCII"));
        Assert.Contains(DisplayLiteral.Of(id), sentence, StringComparison.Ordinal);
        Assert.Contains("limits NOT STATED", sentence, StringComparison.Ordinal);

        var benign = await SearchAsync(JsonSerializer.Serialize("some-future-model@3"));
        var hostile = await SearchAsync(JsonSerializer.Serialize(id));
        Assert.Equal(0, benign.Exit);
        Assert.Equal(0, hostile.Exit);
        Assert.Equal(Lines(benign.Stdout).Length, Lines(hostile.Stdout).Length);
        Assert.DoesNotContain(Lines(hostile.Stdout), l => l.StartsWith(InjectedWords, StringComparison.Ordinal));
        Assert.DoesNotContain(((char)0x1B).ToString(), hostile.Stdout, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(id), hostile.Stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM54. An empty id is the absent id: <c>Sentence("")</c> equals <c>Sentence(null)</c>, holds <c>limits NOT STATED</c> and not <c>model is :</c>; and <c>curia search</c> over a page with no
    /// <c>model</c> member and over one with <c>"model":""</c> prints the banner with those words. A white-space id is an id. Predicted red on <c>a0cda877</c>: <c>The vector channel's model is :</c>.
    /// </summary>
    [Fact]
    public async Task R11_93_SEM54_AnAbsentOrEmptyModelIsTheNotStatedSentence()
    {
        Assert.Equal(ModelWording.Sentence(null), ModelWording.Sentence(string.Empty));
        Assert.Contains("limits NOT STATED", ModelWording.Sentence(string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("model is :", ModelWording.Sentence(string.Empty), StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(" "), ModelWording.Sentence(" "), StringComparison.Ordinal);

        foreach (var (name, json) in new[] { ("no member", string.Empty), ("empty string", "\"\"") })
        {
            var (exit, stdout, stderr) = await SearchAsync(json);
            Assert.True(exit == 0, $"{name}: exit {exit}: {stderr}");
            var folded = System.Text.RegularExpressions.Regex.Replace(stdout, @"\s+", " ");
            Assert.Contains(System.Text.RegularExpressions.Regex.Replace(ModelWording.Sentence(null), @"\s+", " "), folded, StringComparison.Ordinal);
            Assert.DoesNotContain("model is :", folded, StringComparison.Ordinal);
        }
    }
}
