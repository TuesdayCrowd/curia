using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder;
using Xunit;

namespace Curia.Embedding.Tests;

/// <summary>
/// Errata G65 (D10a), SEM28 and SEM29: the canaries reproduce the Python reference and the tokenizer reproduces the reference's ids on the committed parity file. Every probe computes its own answer from the bytes or the reference file; none asks the adapter's check.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FilesAndTokenizerTests(RealModelFixture model) : IClassFixture<RealModelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// SEM28. Every canary of the manifest: the token ids the adapter's tokenizer gives are the Python reference's, and every component of the vector the adapter returns is within 1e-4 of it; a canary with no
    /// vector (the emoji-only text) is <c>NoFeatures</c>. Predicted red on the seam: <c>the manifest holds 0 canaries, expected at least 14</c>.
    /// </summary>
    [Fact]
    public async Task R11_91_SEM28_EveryCanaryReproducesTheReferenceThroughThePublicPath()
    {
        var manifest = EmbeddingManifest.Embedded();
        Assert.True(manifest.Canaries.Count >= 14, $"the manifest holds {manifest.Canaries.Count} canaries, expected at least 14");

        var failures = new List<string>();
        foreach (var canary in manifest.Canaries)
        {
            var ids = BgeInput.Build(model.Embedder.Tokenizer, canary.Text, canary.Role, manifest.QueryPrefix, manifest.MaxSequenceLength).InputIds.Select(i => (int)i).ToArray();
            if (!ids.SequenceEqual(canary.TokenIds))
                failures.Add($"{canary.Id}: token ids differ (adapter {Probe.Joined(ids.Take(12))}..., reference {Probe.Joined(canary.TokenIds.Take(12))}...)");

            var result = await model.Embedder.EmbedAsync(canary.Text, canary.Role, Ct);
            if (canary.Vector.Count == 0)
            {
                var type = result.Match(_ => "(a vector)", e => e.Type);
                if (type != EmbeddingErrors.NoFeatures().Type) failures.Add($"{canary.Id}: expected no-features, got {type}");
                continue;
            }

            var vector = result.Match(e => (IReadOnlyList<float>?)e.Vector, _ => null);
            if (vector is null) { failures.Add($"{canary.Id}: no vector"); continue; }
            var worst = Probe.MaxDifference(vector, canary.Vector);
            if (worst > 1e-4) failures.Add($"{canary.Id}: a component differs by {worst:E2}");
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>
    /// The shape of <c>models/bge-small-en-v1.5/tokenizer-parity.jsonl</c> (written by <c>tools/model/reference.py</c>, never by this suite): one JSON object per line, <c>text</c> (the string), <c>ids</c> (the
    /// content token ids of the Hugging Face fast tokenizer, without <c>[CLS]</c> or <c>[SEP]</c>, untruncated) and <c>source</c> (<c>corpus</c> for the 200 strings of Task 0, <c>generated</c> for the rest).
    /// </summary>
    private sealed record ParityLine(string Text, int[] Ids, string Source);

    private static List<ParityLine> ReadParity()
    {
        var path = Path.Combine(ModelDirectory.RepositoryRoot(), "models", "bge-small-en-v1.5", "tokenizer-parity.jsonl");
        Assert.True(File.Exists(path), "models/bge-small-en-v1.5/tokenizer-parity.jsonl is absent: tools/model/reference.py writes it");
        return [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l =>
        {
            using var json = JsonDocument.Parse(l);
            var root = json.RootElement;
            return new ParityLine(root.GetProperty("text").GetString()!, [.. root.GetProperty("ids").EnumerateArray().Select(e => e.GetInt32())], root.GetProperty("source").GetString()!);
        })];
    }

    /// <summary>
    /// SEM29. The adapter's tokenizer gives the reference's ids on all 200 corpus strings and on every generated string of the parity file (at least 2,000, drawn by the reference from a seeded sampler over the
    /// code points assigned in Unicode 15.0 plus U+0378 and the noncharacters, none chosen by whoever wrote the tokenizer). Predicted red on the seam:
    /// <c>models/bge-small-en-v1.5/tokenizer-parity.jsonl is absent</c>.
    /// </summary>
    [Fact]
    public void R9_63_SEM29_TheTokenizerReproducesTheReferencesIdsOnTheCorpusAndTheGeneratedStrings()
    {
        var lines = ReadParity();
        Assert.True(lines.Count(l => l.Source == "corpus") == 200, $"expected 200 corpus strings, the file holds {lines.Count(l => l.Source == "corpus")}");
        Assert.True(lines.Count(l => l.Source == "generated") >= 2000, $"expected at least 2000 generated strings, the file holds {lines.Count(l => l.Source == "generated")}");

        var mismatches = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            var actual = model.Embedder.Tokenizer.Tokenize(lines[i].Text, int.MaxValue).ContentIds;
            if (!actual.SequenceEqual(lines[i].Ids))
                mismatches.Add($"line {i + 1} ({lines[i].Source}): adapter {Probe.Joined(actual.Take(10))}... reference {Probe.Joined(lines[i].Ids.Take(10))}...");
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} of {lines.Count} strings differ: " + string.Join("; ", mismatches.Take(5)));
    }
}
