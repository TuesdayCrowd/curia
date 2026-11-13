using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Infrastructure.Embedder;
using Xunit;

namespace Curia.Embedding.Tests;

/// <summary>
/// Errata G65 (D10a), SEM27, SEM44 and the directory's own control, which take no <see cref="RealModelFixture"/>: the real files hash to the manifest, the fetch script says so and refuses a changed
/// byte, and the suite's directory check fails loudly. They read the files and the manifest and not the embedder, so a build whose start-up check refuses (a changed tokenizer, a changed manifest
/// digest) cannot make them red through the fixture's constructor instead of through their own assertions (Task 6, Amendment 3 ruling 48). Each still fails, naming the variable, when the directory is absent.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ModelFilesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string ModelDir => ModelDirectory.FromEnvironment();

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

        Assert.Equal(ModelDir, ModelDirectory.Resolve(ModelDir));
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
            await using var stream = File.OpenRead(Path.Combine(ModelDir, name));
            var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, Ct));
            Assert.True(actual == expected, $"{name} hashes to {actual} but the manifest holds '{expected}'");
        }

        var (exit, output, error) = await Probe.RunAsync("python3", [FetchScript(), "--verify-only", "--out", ModelDir], Ct);
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
            File.CreateSymbolicLink(Path.Combine(copy, "model.onnx"), Path.Combine(ModelDir, "model.onnx"));
            var vocab = await File.ReadAllBytesAsync(Path.Combine(ModelDir, "vocab.txt"), Ct);
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
}
