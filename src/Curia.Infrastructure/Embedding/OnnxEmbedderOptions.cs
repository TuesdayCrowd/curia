namespace Curia.Infrastructure.Embedder;

/// <summary>
/// What configures the embedder (errata G65): the directory holding <c>model.onnx</c> and <c>vocab.txt</c>
/// (<c>Curia:Embedding:ModelDirectory</c>, environment <c>CURIA_EMBEDDING_MODEL_DIR</c>) and the run shape of Ruling 7: two intra-op threads, one inter-op thread, at most two runs at once.
/// <see cref="EnableCpuMemArena"/> stays at ONNX Runtime's default (true): M19 (one process, one directory, alternating twice) measured a 2,000-post reconcile and 200 sequential 128-token embeddings with the arena on
/// and off, and the resident size after the reconcile fell from 332 MB to 295-298 MB (10.5 percent, against the 30 percent the rule asked for) while the p50 of a 128-token embedding stayed at 41.0 ms.
/// </summary>
internal sealed record OnnxEmbedderOptions(string? ModelDirectory, int IntraOpNumThreads = 2, int InterOpNumThreads = 1, int GateSize = 2, bool EnableCpuMemArena = true);
