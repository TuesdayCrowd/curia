using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text.Json;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder;
using Xunit;

namespace Curia.Embedding.Tests;

/// <summary>
/// Errata G65 (D10a), SEM27 to SEM29, SEM44 and the fixture's own control: the real files hash to the manifest, the fetch script says so and refuses a changed byte, the canaries reproduce the Python
/// reference, and the tokenizer reproduces the reference's ids on the committed parity file. Every probe computes its own answer from the bytes or the reference file; none asks the adapter's check.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FilesAndTokenizerTests(RealModelFixture model) : IClassFixture<RealModelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// R14.13 (control, no SEM number). The suite's fixture refuses, naming the variable and the fetch command, when the variable is unset, names no directory, or names one without both files.
    /// Green on the seam: it is the fixture's own logic.
    /// </summary>
    [Fact]
    public void R14_13_TheFixtureFailsWithTheVariableAndTheFetchCommandAndNeverSkips()
    {
        var empty = Path.Combine(Path.GetTempPath(), "curia-embedding-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            foreach (var value in new string?[] { null, string.Empty, Path.Combine(empty, "absent"), empty })
            {
                var failure = Assert.ThrowsAny<Exception>(() => ModelDirectory.Resolve(value));
                Assert.Contains(ModelDirectory.Variable, failure.Message, StringComparison.Ordinal);
                Assert.Contains("python3 tools/model/fetch-model.py", failure.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(empty);
        }

        Assert.Equal(model.Directory, ModelDirectory.Resolve(model.Directory));
    }

    private static string FetchScript() => Path.Combine(ModelDirectory.RepositoryRoot(), "tools", "model", "fetch-model.py");

    /// <summary>
    /// SEM27. Both files hash, by this test's own SHA-256 over their bytes, to the digests the manifest holds, and <c>fetch-model.py --verify-only</c> agrees (exit 0). Predicted red on the seam:
    /// <c>model.onnx hashes to 828e14... but the manifest holds ''</c> (the seam's manifest is empty).
    /// </summary>
    [Fact]
    public async Task R11_91_SEM27_BothFilesHashToTheManifestsDigestsAndTheFetchScriptAgrees()
    {
        var manifest = EmbeddingManifest.Embedded();

        foreach (var (name, expected) in new[] { ("model.onnx", manifest.Model.Sha256), ("vocab.txt", manifest.Vocabulary.Sha256) })
        {
            await using var stream = File.OpenRead(Path.Combine(model.Directory, name));
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, Ct));
            Assert.True(actual == expected, $"{name} hashes to {actual} but the manifest holds '{expected}'");
        }

        var (exit, output, error) = await Probe.RunAsync("python3", [FetchScript(), "--verify-only", "--out", model.Directory], Ct);
        Assert.True(exit == 0, $"fetch-model.py --verify-only exited {exit}; stderr: {error.Trim()}; stdout: {output.Trim()}");
    }

    /// <summary>
    /// SEM44. The negative side of SEM27: a copy of the directory with one byte of <c>vocab.txt</c> flipped makes the script exit non-zero, naming the file and the digest mismatch on its error stream
    /// (a script that always fails would pass the first half alone, so the words are required). Predicted red on the seam: <c>stderr does not name vocab.txt and a digest mismatch: fetch-model.py is a seam
    /// stub: not implemented</c>.
    /// </summary>
    [Fact]
    public async Task R11_91_SEM44_AChangedByteMakesTheFetchScriptRefuseAndNameTheFile()
    {
        var copy = Path.Combine(Path.GetTempPath(), "curia-sem44-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(copy);
        try
        {
            File.CreateSymbolicLink(Path.Combine(copy, "model.onnx"), Path.Combine(model.Directory, "model.onnx"));
            var vocab = await File.ReadAllBytesAsync(Path.Combine(model.Directory, "vocab.txt"), Ct);
            vocab[vocab.Length / 2] ^= 0x01;
            await File.WriteAllBytesAsync(Path.Combine(copy, "vocab.txt"), vocab, Ct);

            var (exit, _, error) = await Probe.RunAsync("python3", [FetchScript(), "--verify-only", "--out", copy], Ct);

            Assert.NotEqual(0, exit);
            Assert.True(
                error.Contains("vocab.txt", StringComparison.Ordinal) && error.Contains("digest", StringComparison.OrdinalIgnoreCase),
                $"stderr does not name vocab.txt and a digest mismatch: {error.Trim()}");
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

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
