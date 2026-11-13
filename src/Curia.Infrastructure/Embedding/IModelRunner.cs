namespace Curia.Infrastructure.Embedder;

/// <summary>What a session is asked to run: the three int64 inputs of a batch of one.</summary>
internal sealed record ModelRunInput(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds);

/// <summary>
/// One loaded session (errata G65, Ruling 7). <see cref="RunAsync"/> answers <c>last_hidden_state</c> of batch 0, flattened (sequence length times dimensions),
/// and stops the run it started when <c>cancellationToken</c> is cancelled, throwing <see cref="OperationCanceledException"/> (R11.92).
/// </summary>
internal interface IModelRunner : IDisposable
{
    Task<float[]> RunAsync(ModelRunInput input, CancellationToken cancellationToken);
}

/// <summary>Builds a session from a verified model file. The seam where a test double stands in for ONNX Runtime; a loader that cannot load a native library throws what the platform throws.</summary>
internal interface IModelLoader
{
    IModelRunner Load(string modelPath, OnnxEmbedderOptions options);
}
