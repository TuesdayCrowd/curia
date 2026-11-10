using System.Security.Cryptography;
using System.Text;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder;

namespace Curia.Infrastructure.Tests.Embedder;

/// <summary>A code point as a string, written as a number so that no invisible character sits in this source.</summary>
internal static class Cp
{
    internal static string Of(int codePoint) => char.ConvertFromUtf32(codePoint);

    /// <summary>One UTF-16 code unit, which may be a lone surrogate.</summary>
    internal static string Unit(int codeUnit) => new((char)codeUnit, 1);
}

/// <summary>
/// A stand-in for ONNX Runtime (errata G65): a runner that answers a deterministic hidden state of the right shape and counts, delays or fails as a probe asks, and a loader
/// that hands it out and records what it was asked. Model-free: no probe built on it needs a model file or the native library.
/// </summary>
internal sealed class FakeRunner : IModelRunner
{
    private int _inside;
    private int _maxInside;
    private int _runs;

    /// <summary>What a run does. The default answers <see cref="HiddenStateFor"/>.</summary>
    internal Func<ModelRunInput, CancellationToken, Task<float[]>>? Behaviour { get; set; }

    internal int Runs => Volatile.Read(ref _runs);

    internal int MaxInside => Volatile.Read(ref _maxInside);

    internal List<int> SequenceLengths { get; } = [];

    internal List<long[]> Inputs { get; } = [];

    /// <summary>Set when a run is stopped by its caller's token.</summary>
    internal int StopsObserved;

    internal bool Disposed { get; private set; }

    public async Task<float[]> RunAsync(ModelRunInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        Interlocked.Increment(ref _runs);
        lock (SequenceLengths)
        {
            SequenceLengths.Add(input.InputIds.Length);
            Inputs.Add(input.InputIds);
        }

        var now = Interlocked.Increment(ref _inside);
        int seen;
        while ((seen = Volatile.Read(ref _maxInside)) < now && Interlocked.CompareExchange(ref _maxInside, now, seen) != seen)
        {
        }

        try
        {
            if (Behaviour is { } behaviour)
                return await behaviour(input, cancellationToken).ConfigureAwait(false);
            return HiddenStateFor(input);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref StopsObserved);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inside);
        }
    }

    public void Dispose() => Disposed = true;

    /// <summary>Row 0 is a unit vector that depends on the ids; the other rows are all ones, so a pooling that averages the rows cannot be mistaken for one that reads row 0.</summary>
    internal static float[] HiddenStateFor(ModelRunInput input, int dimensions = 384)
    {
        var length = input.InputIds.Length;
        var hidden = new float[length * dimensions];
        var seed = 0L;
        foreach (var id in input.InputIds) seed = (seed * 31) + id;

        var norm = 0.0;
        for (var j = 0; j < dimensions; j++)
        {
            var v = (float)(((seed + j) % 7) + 1);
            hidden[j] = v;
            norm += (double)v * v;
        }

        norm = Math.Sqrt(norm);
        for (var j = 0; j < dimensions; j++) hidden[j] = (float)(hidden[j] / norm);
        for (var i = 1; i < length; i++)
            for (var j = 0; j < dimensions; j++) hidden[(i * dimensions) + j] = 1f;
        return hidden;
    }
}

internal sealed class FakeLoader(FakeRunner runner) : IModelLoader
{
    internal int LoadCalls { get; private set; }

    internal OnnxEmbedderOptions? LastOptions { get; private set; }

    internal string? LastModelPath { get; private set; }

    /// <summary>Throws instead of loading, as a platform that cannot load the native library does.</summary>
    internal Exception? Throws { get; set; }

    public IModelRunner Load(string modelPath, OnnxEmbedderOptions options)
    {
        LoadCalls++;
        LastModelPath = modelPath;
        LastOptions = options;
        if (Throws is { } ex) throw ex;
        return runner;
    }
}

/// <summary>A model directory of fake files, the manifest that digests them, and the embedder over a fake runner.</summary>
internal sealed class EmbedderRig : IDisposable
{
    internal const string QueryPrefix = "Represent this sentence for searching relevant passages: ";

    /// <summary>The words after the five specials; ids 0 to 4 are [PAD] [UNK] [CLS] [SEP] [MASK].</summary>
    internal static readonly string[] Words =
    [
        "the", "quick", "brown", "fox", "jumps", "over", "lazy", "dog", "hello", "world", "represent", "this", "sentence", "for", "searching",
        "relevant", "passages", ":", "alpha", "beta", "gamma", "delta", "cafe", "##s", "##ing", "##ed", "a", "b", "c", "d", "x", "y", "z",
        "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "key", "rotate", "post", "board", "agent",
    ];

    internal const int Pad = 0;
    internal const int Unk = 1;
    internal const int Cls = 2;
    internal const int Sep = 3;

    private EmbedderRig(string directory, byte[] model, byte[] vocab)
    {
        Directory = directory;
        ModelBytes = model;
        VocabBytes = vocab;
        Runner = new FakeRunner();
        Loader = new FakeLoader(Runner);
        Manifest = ManifestFor(model, vocab, []);
        Embedder = new OnnxTextEmbedder(new OnnxEmbedderOptions(directory), Manifest, Loader);
    }

    internal string Directory { get; }

    internal byte[] ModelBytes { get; }

    internal byte[] VocabBytes { get; }

    internal FakeRunner Runner { get; }

    internal FakeLoader Loader { get; }

    internal EmbeddingManifest Manifest { get; private set; }

    internal OnnxTextEmbedder Embedder { get; private set; }

    internal static IReadOnlyList<string> Vocabulary { get; } = [.. new[] { "[PAD]", "[UNK]", "[CLS]", "[SEP]", "[MASK]" }, .. Words];

    /// <summary>The id of a word of the synthetic vocabulary.</summary>
    internal static int IdOf(string word) => Array.IndexOf([.. Vocabulary], word) is var i and >= 0 ? i : throw new ArgumentException("not in the synthetic vocabulary: " + word, nameof(word));

    internal static EmbedderRig Create()
    {
        var directory = Path.Combine(Path.GetTempPath(), "curia-sem-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var model = Encoding.ASCII.GetBytes("a fake model file, not an onnx graph " + Guid.NewGuid().ToString("N"));
        var vocab = Encoding.UTF8.GetBytes(string.Join('\n', Vocabulary) + "\n");
        File.WriteAllBytes(Path.Combine(directory, "model.onnx"), model);
        File.WriteAllBytes(Path.Combine(directory, "vocab.txt"), vocab);
        return new EmbedderRig(directory, model, vocab);
    }

    internal static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static EmbeddingManifest ManifestFor(byte[] model, byte[] vocab, IReadOnlyList<Canary> canaries) =>
        new(
            "bge-small-en-v1.5@1",
            384,
            new ManifestFile("model.onnx", model.Length, Sha256(model)),
            new ManifestFile("vocab.txt", vocab.Length, Sha256(vocab)),
            QueryPrefix,
            512,
            canaries,
            new Dictionary<string, string>());

    /// <summary>Rebuilds the embedder over a manifest with <paramref name="canaries"/>.</summary>
    internal OnnxTextEmbedder WithCanaries(IReadOnlyList<Canary> canaries)
    {
        Manifest = ManifestFor(ModelBytes, VocabBytes, canaries);
        Embedder = new OnnxTextEmbedder(new OnnxEmbedderOptions(Directory), Manifest, Loader);
        return Embedder;
    }

    /// <summary>The embedder over the same directory, a manifest and a loader of the caller's.</summary>
    internal OnnxTextEmbedder Over(EmbeddingManifest manifest, IModelLoader loader, OnnxEmbedderOptions? options = null) =>
        new(options ?? new OnnxEmbedderOptions(Directory), manifest, loader);

    internal string PathOf(string name) => Path.Combine(Directory, name);

    public void Dispose()
    {
        Embedder.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // a temporary directory; the next run's names differ
        }
    }

    /// <summary>Whether <paramref name="role"/> made no difference to this fake (it never does); here to name what a probe passes.</summary>
    internal static EmbeddingRole Passage => EmbeddingRole.Passage;
}
