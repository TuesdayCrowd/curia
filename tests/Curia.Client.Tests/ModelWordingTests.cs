using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Client.Cli;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G65 (D10a), SEM42 and R11.93: what a client says the vector model can and cannot do is composed from the model id the Forum served, from one definition (<see cref="ModelWording"/>), for a model it
/// knows, for the other one it knows and for one it does not (trap 185: a positive word with no negative neighbour is tested with all three together).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ModelWordingTests
{
    private const string Hashed = "hashed-ngram@1";
    private const string Bge = "bge-small-en-v1.5@1";
    private const string Unknown = "some-future-model@3";

    /// <summary>
    /// SEM42 (words). The sentence for <c>hashed-ngram@1</c> states that it does not find paraphrase; the sentence for <c>bge-small-en-v1.5@1</c> states the 512-token head; an id the definition does not know
    /// prints the id and <c>limits NOT STATED</c>; none is the empty string; the three differ, and neither known sentence makes the other's claim. Predicted red on the seam: <c>the sentence for
    /// hashed-ngram@1 is empty</c>.
    /// </summary>
    [Fact]
    public void R11_93_SEM42_EachModelHasItsOwnSentenceAndAnUnknownIdSaysItsLimitsAreNotStated()
    {
        var hashed = ModelWording.Sentence(Hashed);
        var bge = ModelWording.Sentence(Bge);
        var unknown = ModelWording.Sentence(Unknown);

        Assert.False(string.IsNullOrWhiteSpace(hashed), $"the sentence for {Hashed} is empty");
        Assert.False(string.IsNullOrWhiteSpace(bge), $"the sentence for {Bge} is empty");
        Assert.Contains("paraphrase", hashed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("512", bge, StringComparison.Ordinal);
        Assert.Contains(Unknown, unknown, StringComparison.Ordinal);
        Assert.Contains("limits NOT STATED", unknown, StringComparison.Ordinal);
        Assert.Contains("limits NOT STATED", ModelWording.Sentence(null), StringComparison.Ordinal);

        Assert.Equal(3, new[] { hashed, bge, unknown }.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("512", hashed, StringComparison.Ordinal);
        Assert.DoesNotContain("does not find paraphrase", bge, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("limits NOT STATED", hashed, StringComparison.Ordinal);
        Assert.DoesNotContain("limits NOT STATED", bge, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM42 (the banner). The banner above a result set is composed by <c>ModelWording</c> for the id the Forum served, and none of the three says "until an ONNX model is configured". Predicted red on the seam:
    /// <c>Assert.DoesNotContain() Failure: Sub-string found</c> (the seam's banner is the fixed text, which says "until an ONNX model is configured").
    /// </summary>
    [Theory]
    [InlineData(Hashed)]
    [InlineData(Bge)]
    [InlineData(Unknown)]
    [InlineData(null)]
    public void R11_93_SEM42_TheBannerIsComposedFromTheServedModel(string? modelId)
    {
        var banner = Help.SearchBannerFor(modelId);

        var folded = Regex.Replace(banner, @"\s+", " ");
        Assert.DoesNotContain("until an ONNX model is configured", folded, StringComparison.Ordinal);
        var sentence = ModelWording.Sentence(modelId);
        Assert.False(string.IsNullOrWhiteSpace(sentence), $"NOT REACHED: ModelWording has no sentence for {modelId ?? "(none served)"}");
        Assert.True(folded.Contains(Regex.Replace(sentence, @"\s+", " "), StringComparison.Ordinal), $"the banner for {modelId ?? "(none served)"} does not hold ModelWording's sentence");
    }

    /// <summary>
    /// SEM42b (the ids). The ids <c>ModelWording</c> knows are the ones the documents publish: the identity table of errata G65 (a different artifact from the code) and the register's <c>hashed-ngram@1</c>
    /// row; a model added to the errata without a sentence fails here. Predicted red until Task 8 pastes G65: <c>the errata holds no G65 identity table</c>. This is a known red through Tasks 2 to 7, not
    /// a defect of the code.
    /// </summary>
    [Fact]
    public void R11_93_SEM42b_EveryModelIdThePublishedIdentityTableNamesHasASentence()
    {
        var root = Root();
        var errata = File.ReadAllText(Path.Combine(root, "curia-whitepaper-ERRATA-AND-ADDENDUM.md"));
        var register = File.ReadAllText(Path.Combine(root, "IMPLEMENTATION_PLAN.md"));

        var tables = TableHeader().Matches(errata).Select(m => m.Groups["id"].Value).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(tables.Count > 0, "the errata holds no G65 identity table");
        Assert.Contains("hashed-ngram@1", register, StringComparison.Ordinal);

        foreach (var id in tables.Append(Hashed).Distinct(StringComparer.Ordinal))
            Assert.DoesNotContain("limits NOT STATED", ModelWording.Sentence(id), StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\|\s*Member of `(?<id>[a-z0-9.\-]+@[0-9]+)`\s*\|")]
    private static partial Regex TableHeader();

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "curia-whitepaper-ERRATA-AND-ADDENDUM.md")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root above " + AppContext.BaseDirectory);
    }
}
