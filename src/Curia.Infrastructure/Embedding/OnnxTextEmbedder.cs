using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder.Tokenization;

namespace Curia.Infrastructure.Embedder;

/// <summary>
/// The Forum's embedder (errata G65): <c>bge-small-en-v1.5@1</c> run by ONNX Runtime behind the port. Total, bounded by a first-in-first-out gate, stopped when its caller is gone,
/// and checked before the Forum accepts a request (R11.91, R11.92). <b>Seam stub (D10a Task 1):</b> loads nothing, checks nothing, and answers
/// <see cref="EmbeddingErrors.ModelNotReady"/> to every text; Task 2 builds it.
/// </summary>
internal sealed class OnnxTextEmbedder : ITextEmbedder, IStartupChecked, IDisposable
{
    /// <summary>The identity every vector of this embedder carries.</summary>
    internal static EmbeddingModel BgeModel { get; } = new("bge-small-en-v1.5", "1", 384);

    private readonly EmbeddingManifest _manifest;

    internal OnnxTextEmbedder(OnnxEmbedderOptions options, EmbeddingManifest manifest, IModelLoader loader)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(loader);
        Options = options;
        _manifest = manifest;
        Tokenizer = new WordPieceTokenizer([]);
        Gate = new FifoGate(options.GateSize);
    }

    /// <summary>Opens the embedder over a model directory with the embedded manifest and the real loader. Seam stub: the loader is absent.</summary>
    internal static OnnxTextEmbedder Open(string modelDirectory) =>
        new(new OnnxEmbedderOptions(modelDirectory), EmbeddingManifest.Embedded(), new UnavailableLoader());

    public EmbeddingModel Model => BgeModel;

    /// <summary>The shape the embedder runs in (SEM39).</summary>
    internal OnnxEmbedderOptions Options { get; }

    /// <summary>The gate around a run: at most <see cref="OnnxEmbedderOptions.GateSize"/> callers inside, admitted in arrival order (SEM11, SEM45).</summary>
    internal FifoGate Gate { get; }

    /// <summary>The tokenizer over the vocabulary of the verified directory (SEM29 holds it to the reference).</summary>
    internal WordPieceTokenizer Tokenizer { get; }

    public void Check()
    {
        _ = _manifest;
    }

    public Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Task.FromResult(Result<Embedding>.Fail(EmbeddingErrors.ModelNotReady()));
    }

    public void Dispose()
    {
    }

    private sealed class UnavailableLoader : IModelLoader
    {
        public IModelRunner Load(string modelPath, OnnxEmbedderOptions options) =>
            throw new InvalidOperationException("The ONNX loader is not built yet (D10a Task 2).");
    }
}
