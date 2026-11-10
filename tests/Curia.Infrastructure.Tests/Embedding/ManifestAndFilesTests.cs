using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder;
using Xunit;

namespace Curia.Infrastructure.Tests.Embedder;

/// <summary>
/// Errata G65 (D10a), SEM4 and SEM5, model-free: the manifest the build carries and the checks that the model directory is the one it names (R9.63, R11.91).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ManifestAndFilesTests
{
    /// <summary>The canaries the manifest must hold (Amendment 1, ruling 22): the classes the tokenizer is most likely to get wrong, each a text the reference run records the ids and the vector of.</summary>
    internal static readonly string[] RequiredCanaries =
    [
        "plain", "plain-query", "mixed-case", "accents-composed", "accents-decomposed", "cjk", "hangul", "emoji", "emoji-only", "url", "code-symbols",
        "zero-width-soft-hyphen", "unassigned-code-point", "special-token-spelling", "over-512-tokens",
    ];

    /// <summary>
    /// SEM4. The embedded manifest parses; its id is <c>bge-small-en-v1.5@1</c> with 384 dimensions; both digests are 64 lower-case hex digits; at least fourteen canaries (here fifteen) cover the
    /// classes above; every vector has 384 finite floats of L2 norm 1 within 1e-5, except the emoji-only canary, which has none (it has no features, Ruling 9); the manifest names the reference
    /// tools. Predicted red on the seam: <c>Expected: "bge-small-en-v1.5@1" Actual: ""</c> (the seam's manifest reads nothing).
    /// </summary>
    [Fact]
    public void R11_91_SEM4_TheEmbeddedManifestNamesTheIdentityTheFilesAndTheCanaries()
    {
        var manifest = EmbeddingManifest.Embedded();

        Assert.Equal("bge-small-en-v1.5@1", manifest.Id);
        Assert.Equal(384, manifest.Dimensions);
        Assert.Matches("^[0-9a-f]{64}$", manifest.Model.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", manifest.Vocabulary.Sha256);
        Assert.Equal("Represent this sentence for searching relevant passages: ", manifest.QueryPrefix);
        Assert.Equal(512, manifest.MaxSequenceLength);

        Assert.True(manifest.Canaries.Count >= 14, $"expected at least 14 canaries, the manifest holds {manifest.Canaries.Count}");
        var missing = RequiredCanaries.Except(manifest.Canaries.Select(c => c.Id), StringComparer.Ordinal).ToArray();
        Assert.True(missing.Length == 0, "the manifest holds no canary for: " + string.Join(", ", missing));

        foreach (var canary in manifest.Canaries)
        {
            Assert.NotEmpty(canary.TokenIds);
            if (canary.Id == "emoji-only")
            {
                Assert.Empty(canary.Vector);
                continue;
            }

            Assert.Equal(384, canary.Vector.Count);
            Assert.All(canary.Vector, v => Assert.True(float.IsFinite(v), $"canary {canary.Id} holds a non-finite component"));
            var norm = Math.Sqrt(canary.Vector.Sum(v => (double)v * v));
            Assert.True(Math.Abs(norm - 1.0) <= 1e-5, string.Create(CultureInfo.InvariantCulture, $"canary {canary.Id} has L2 norm {norm}, expected 1 within 1e-5"));
        }

        foreach (var tool in new[] { "python", "transformers", "tokenizers", "onnxruntime" })
            Assert.True(manifest.ReferenceTools.ContainsKey(tool), $"the manifest does not name the reference tool '{tool}'");
    }

    public static TheoryData<string> FileCases() => new()
    {
        "missing-directory",
        "missing-model",
        "missing-vocabulary",
        "truncated-model",
        "flipped-bit-in-model",
        "vocabulary-swapped",
    };

    private static string RefusalFor(string name) => name switch
    {
        "missing-directory" => EmbedderRefusals.DirectoryMissing,
        "missing-model" => EmbedderRefusals.ModelFileMissing,
        "missing-vocabulary" => EmbedderRefusals.VocabularyFileMissing,
        "truncated-model" => EmbedderRefusals.ModelDigestMismatch,
        "flipped-bit-in-model" => EmbedderRefusals.ModelDigestMismatch,
        "vocabulary-swapped" => EmbedderRefusals.VocabularyDigestMismatch,
        _ => throw new ArgumentException("no such case", nameof(name)),
    };

    private static string Prepare(EmbedderRig rig, string name)
    {
        switch (name)
        {
            case "missing-directory":
                return Path.Combine(rig.Directory, "no-such-subdirectory");
            case "missing-model":
                File.Delete(rig.PathOf("model.onnx"));
                break;
            case "missing-vocabulary":
                File.Delete(rig.PathOf("vocab.txt"));
                break;
            case "truncated-model":
                File.WriteAllBytes(rig.PathOf("model.onnx"), rig.ModelBytes[..(rig.ModelBytes.Length / 2)]);
                break;
            case "flipped-bit-in-model":
                var flipped = (byte[])rig.ModelBytes.Clone();
                flipped[^1] ^= 0x01;
                File.WriteAllBytes(rig.PathOf("model.onnx"), flipped);
                break;
            case "vocabulary-swapped":
                File.WriteAllText(rig.PathOf("vocab.txt"), "[PAD]\n[UNK]\n[CLS]\n[SEP]\n[MASK]\nsomething\nelse\n");
                break;
        }

        return rig.Directory;
    }

    /// <summary>
    /// SEM5. A directory that is missing, lacks a file, holds a truncated or a one-bit-different model, or a different vocabulary is refused with that cause's own fixed type, whose text holds
    /// no path; the right bytes pass; and no session is built for any refused directory (the digest is checked before the loader is asked). Predicted red on the seam:
    /// <c>Assert.False() Failure</c> / "the check verified a directory with a missing model" (the seam's check accepts any directory).
    /// </summary>
    [Theory]
    [MemberData(nameof(FileCases))]
    public void R11_91_SEM5_ADirectoryThatIsNotTheManifestsIsRefusedByItsOwnCauseAndNoSessionIsBuilt(string name)
    {
        using var rig = EmbedderRig.Create();
        var directory = Prepare(rig, name);

        var verified = ModelFiles.Verify(directory, rig.Manifest);
        Assert.False(verified.IsOk, $"{name}: the check verified a directory it should have refused");
        var error = verified.Match(_ => throw new InvalidOperationException("not reached"), e => e);
        Assert.Equal(RefusalFor(name), error.Type);
        foreach (var text in new[] { error.Type, error.Title, error.Detail ?? string.Empty })
            Assert.DoesNotContain(rig.Directory, text, StringComparison.Ordinal);

        using var embedder = rig.Over(rig.Manifest, rig.Loader, new OnnxEmbedderOptions(directory));
        var refusal = Assert.Throws<InvalidOperationException>(embedder.Check);
        Assert.DoesNotContain(rig.Directory, refusal.Message, StringComparison.Ordinal);
        Assert.Equal(0, rig.Loader.LoadCalls);
    }

    /// <summary>SEM5, the served side: the right bytes pass the check and the loader is asked once. Control for the row above: a check that refused everything would pass that and fail this.</summary>
    [Fact]
    public void R11_91_SEM5_TheRightBytesPassAndTheLoaderIsAskedOnce()
    {
        using var rig = EmbedderRig.Create();

        Assert.True(ModelFiles.Verify(rig.Directory, rig.Manifest).IsOk);
        rig.Embedder.Check();

        Assert.Equal(1, rig.Loader.LoadCalls);
        Assert.Equal(rig.PathOf("model.onnx"), rig.Loader.LastModelPath);
    }

    /// <summary>SEM5, the causes are told apart: the five fixed types are five different strings.</summary>
    [Fact]
    public void R11_91_SEM5_TheRefusalTypesAreDistinct()
    {
        string[] types =
        [
            EmbedderRefusals.DirectoryMissing, EmbedderRefusals.ModelFileMissing, EmbedderRefusals.VocabularyFileMissing,
            EmbedderRefusals.ModelDigestMismatch, EmbedderRefusals.VocabularyDigestMismatch,
        ];
        Assert.Equal(types.Length, types.Distinct(StringComparer.Ordinal).Count());
        Assert.All(types, t => Assert.StartsWith("curia/embedding/", t, StringComparison.Ordinal));
    }
}
