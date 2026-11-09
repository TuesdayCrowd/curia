namespace Curia.Infrastructure.Embedder;

/// <summary>
/// What configures the embedder (errata G65): the directory holding <c>model.onnx</c> and <c>vocab.txt</c>
/// (<c>Curia:Embedding:ModelDirectory</c>, environment <c>CURIA_EMBEDDING_MODEL_DIR</c>) and the run shape of Ruling 7: two intra-op threads, one inter-op thread, at most two runs at once.
/// </summary>
internal sealed record OnnxEmbedderOptions(string? ModelDirectory, int IntraOpNumThreads = 2, int InterOpNumThreads = 1, int GateSize = 2);
