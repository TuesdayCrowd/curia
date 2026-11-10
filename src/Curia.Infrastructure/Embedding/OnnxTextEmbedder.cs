using System.Runtime.InteropServices;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder.Tokenization;

namespace Curia.Infrastructure.Embedder;

/// <summary>
/// The Forum's embedder (errata G65): <c>bge-small-en-v1.5@1</c> run by ONNX Runtime behind the port. Total, bounded by a first-in-first-out gate, stopped when its caller is gone,
/// and checked before the Forum accepts a request (R11.91, R11.92). <see cref="Check"/> verifies the files, builds the session, reproduces every canary and warms the session; until it
/// has passed, every text is answered <see cref="EmbeddingErrors.ModelNotReady"/>.
/// </summary>
internal sealed class OnnxTextEmbedder : ITextEmbedder, IStartupChecked, IDisposable
{
    private const string ConfigurationKey = EmbeddingComposition.ConfigurationKey;
    private const string EnvironmentVariable = EmbeddingComposition.EnvironmentVariable;
    private const double CanaryTolerance = 1e-4;
    private const int WarmUpRuns = 3;

    /// <summary>The fewest canaries a manifest may hold (SEM4's published floor; the manifest holds fifteen). A check over fewer proves little, so the start refuses it.</summary>
    internal const int MinimumCanaries = 14;

    /// <summary>The identity every vector of this embedder carries.</summary>
    internal static EmbeddingModel BgeModel { get; } = new("bge-small-en-v1.5", "1", 384);

    private readonly EmbeddingManifest _manifest;
    private readonly IModelLoader _loader;
    private readonly object _checkLock = new();
    private WordPieceTokenizer? _tokenizer;
    private IModelRunner? _runner;
    private volatile bool _ready;

    internal OnnxTextEmbedder(OnnxEmbedderOptions options, EmbeddingManifest manifest, IModelLoader loader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(loader);
        Options = options;
        _manifest = manifest;
        _loader = loader;
        Gate = new FifoGate(options.GateSize);
    }

    /// <summary>Opens the embedder over a model directory with the embedded manifest and the real loader. Nothing is read until <see cref="Check"/>.</summary>
    internal static OnnxTextEmbedder Open(string? modelDirectory) =>
        new(new OnnxEmbedderOptions(modelDirectory), EmbeddingManifest.Embedded(), new OnnxRuntimeLoader());

    public EmbeddingModel Model => BgeModel;

    /// <summary>The shape the embedder runs in (SEM39).</summary>
    internal OnnxEmbedderOptions Options { get; }

    /// <summary>The gate around a run: at most <see cref="OnnxEmbedderOptions.GateSize"/> callers inside, admitted in arrival order (SEM11, SEM45).</summary>
    internal FifoGate Gate { get; }

    /// <summary>The tokenizer over the vocabulary of the verified directory (SEM29 holds it to the reference). Available once <see cref="Check"/> has read the vocabulary.</summary>
    internal WordPieceTokenizer Tokenizer =>
        _tokenizer ?? throw new InvalidOperationException("The embedder has not been checked: its vocabulary is not read.");

    /// <summary>
    /// The start-up check (R11.91): the options, both digests, the session, every committed canary (token ids exactly, each component within 1e-4) and the warm-up. A failure is an
    /// <see cref="InvalidOperationException"/> naming the configuration key, the file or the canary, never a path, an exception message or an input.
    /// </summary>
    public void Check()
    {
        lock (_checkLock)
        {
            if (_ready) return;

            RequireManifestIsThisBuildsIdentity();

            var verified = ModelFiles.Verify(Options.ModelDirectory, _manifest);
            if (!verified.TryGetValue(out var files, out var refusal))
                throw new InvalidOperationException($"{refusal!.Title} named by {ConfigurationKey} (environment {EnvironmentVariable}); {refusal.Type}.");

            string[] vocabulary;
            try
            {
                vocabulary = File.ReadAllLines(files!.VocabularyPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"{_manifest.Vocabulary.Name} could not be read ({ex.GetType().Name}) from the directory named by {ConfigurationKey}.");
            }

            var tokenizer = new WordPieceTokenizer(vocabulary);
            if (tokenizer.UnknownId < 0 || tokenizer.ClsId < 0 || tokenizer.SepId < 0)
                throw new InvalidOperationException($"{_manifest.Vocabulary.Name} has no [CLS], [SEP] or [UNK] token.");

            var runner = LoadRunner(files.ModelPath);
            try
            {
                ReproduceCanaries(tokenizer, runner);
                WarmUp(tokenizer, runner);
            }
            catch (InvalidOperationException)
            {
                runner.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A run that threw: its message may hold a path or the input, so only the type is named.
                runner.Dispose();
                throw new InvalidOperationException($"The embedding model could not be run at start ({ex.GetType().Name}). The Forum does not start without its embedding model.");
            }

            _tokenizer = tokenizer;
            _runner = runner;
            _ready = true;
        }
    }

    /// <summary>
    /// Ruling 39: before a file is read, the manifest must name the identity the code stamps on every vector (<see cref="BgeModel"/>) and hold at least <see cref="MinimumCanaries"/> canaries.
    /// A refusal names the member and the build, and holds no path, no value of the manifest and no exception message (R11.91). The manifest is embedded in the assembly, so this guards against
    /// an edit of the manifest that the tests did not see and not against a hostile build; the floor restates SEM4's number, so the start and the test agree by construction.
    /// </summary>
    private void RequireManifestIsThisBuildsIdentity()
    {
        if (!string.Equals(_manifest.Id, BgeModel.Id, StringComparison.Ordinal))
            throw new InvalidOperationException("The embedding manifest's 'id' member is not the model this build stamps on its vectors. The Forum does not start without its embedding model.");
        if (_manifest.Dimensions != BgeModel.Dimensions)
            throw new InvalidOperationException("The embedding manifest's 'dimensions' member is not the width of the vectors this build stamps. The Forum does not start without its embedding model.");
        if (_manifest.Canaries.Count < MinimumCanaries)
            throw new InvalidOperationException("The embedding manifest's 'canaries' member holds fewer canaries than the start-up check requires. The Forum does not start without its embedding model.");
    }

    private IModelRunner LoadRunner(string modelPath)
    {
        try
        {
            return _loader.Load(modelPath, Options);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException or TypeInitializationException)
        {
            throw new InvalidOperationException(
                $"Microsoft.ML.OnnxRuntime cannot load its native library on {RuntimeInformation.OSDescription} ({RuntimeInformation.ProcessArchitecture}): " +
                "the package ships no native for this platform, or it could not be loaded. The Forum does not start without its embedding model.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException($"The embedding model session could not be built ({ex.GetType().Name}). The Forum does not start without its embedding model.");
        }
    }

    private void ReproduceCanaries(WordPieceTokenizer tokenizer, IModelRunner runner)
    {
        foreach (var canary in _manifest.Canaries)
        {
            var input = BgeInput.Build(tokenizer, canary.Text, canary.Role, _manifest.QueryPrefix, _manifest.MaxSequenceLength);
            if (!input.InputIds.Select(i => (int)i).SequenceEqual(canary.TokenIds))
                throw new InvalidOperationException($"Canary '{canary.Id}': the tokenizer's ids differ from the reference's.");

            if (canary.Vector.Count == 0)
            {
                if (input.Placeable) throw new InvalidOperationException($"Canary '{canary.Id}': the reference has no vector for it but the tokenizer finds features in it.");
                continue;
            }

            if (!input.Placeable) throw new InvalidOperationException($"Canary '{canary.Id}': the reference has a vector for it but the tokenizer finds no features in it.");

            var pooled = Run(runner, input);
            if (!pooled.TryGetValue(out var vector, out var fault))
                throw new InvalidOperationException($"Canary '{canary.Id}': the model could not embed it ({fault!.Type}).");
            if (vector.Length != canary.Vector.Count)
                throw new InvalidOperationException($"Canary '{canary.Id}': the vector has {vector.Length} components, the reference {canary.Vector.Count}.");
            for (var j = 0; j < vector.Length; j++)
                if (Math.Abs(vector[j] - canary.Vector[j]) > CanaryTolerance)
                    throw new InvalidOperationException($"Canary '{canary.Id}': component {j} differs from the reference by more than {CanaryTolerance:E0}.");
        }
    }

    /// <summary>Runs the session on the longest input it will be given, so the first request does not pay for the allocations (Ruling 5).</summary>
    private void WarmUp(WordPieceTokenizer tokenizer, IModelRunner runner)
    {
        var length = _manifest.MaxSequenceLength;
        var ids = new long[length];
        Array.Fill(ids, (long)Math.Min(tokenizer.Vocabulary.Count - 1, 1000));
        ids[0] = tokenizer.ClsId;
        ids[^1] = tokenizer.SepId;
        var mask = new long[length];
        Array.Fill(mask, 1L);
        var input = new BgeInput(ids, mask, new long[length], true);
        for (var i = 0; i < WarmUpRuns; i++)
        {
            var pooled = Run(runner, input);
            if (!pooled.IsOk) throw new InvalidOperationException("The embedding model's warm-up run produced no usable vector.");
        }
    }

    private Result<System.Collections.Immutable.ImmutableArray<float>> Run(IModelRunner runner, BgeInput input)
    {
        var hidden = runner.RunAsync(new ModelRunInput(input.InputIds, input.AttentionMask, input.TokenTypeIds), CancellationToken.None).GetAwaiter().GetResult();
        return BgePooling.Pool(hidden, input.InputIds.Length, _manifest.Dimensions);
    }

    public async Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_ready || _runner is not { } runner || _tokenizer is not { } tokenizer)
            return Result<Embedding>.Fail(EmbeddingErrors.ModelNotReady());

        BgeInput input;
        try
        {
            input = BgeInput.Build(tokenizer, text, role, _manifest.QueryPrefix, _manifest.MaxSequenceLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            return Result<Embedding>.Fail(EmbeddingErrors.ModelFault(ex.GetType().Name));
        }

        if (!input.Placeable) return Result<Embedding>.Fail(EmbeddingErrors.NoFeatures());

        using var lease = await Gate.EnterAsync(cancellationToken).ConfigureAwait(false);
        float[] hidden;
        try
        {
            hidden = await runner.RunAsync(new ModelRunInput(input.InputIds, input.AttentionMask, input.TokenTypeIds), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Result<Embedding>.Fail(EmbeddingErrors.ModelFault(ex.GetType().Name));
        }

        return BgePooling.Pool(hidden, input.InputIds.Length, _manifest.Dimensions).Map(vector => new Embedding(BgeModel, vector));
    }

    public void Dispose() => _runner?.Dispose();
}
