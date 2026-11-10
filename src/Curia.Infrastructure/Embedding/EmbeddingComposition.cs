using Curia.Application.Ports;

namespace Curia.Infrastructure.Embedder;

/// <summary>
/// The one way the Forum's embedder is made (errata G65, Ruling 6): the ONNX embedder, and nothing else. There is no hashed adapter here and no configuration key that selects another;
/// the composition root registers what <see cref="Create"/> returns and runs its start-up check (<see cref="IStartupChecked"/>) when it has built the host.
/// </summary>
public static class EmbeddingComposition
{
    /// <summary>The configuration key of the model directory.</summary>
    public const string ConfigurationKey = "Curia:Embedding:ModelDirectory";

    /// <summary>The environment variable that names the model directory where the key is unset.</summary>
    public const string EnvironmentVariable = "CURIA_EMBEDDING_MODEL_DIR";

    /// <summary>
    /// The embedder over <paramref name="configuredDirectory"/> (the value of <see cref="ConfigurationKey"/>), or over <see cref="EnvironmentVariable"/>'s where that is null.
    /// Nothing is read here: a missing or wrong directory is the start-up check's refusal, which names the key.
    /// </summary>
    public static ITextEmbedder Create(string? configuredDirectory) =>
        OnnxTextEmbedder.Open(configuredDirectory ?? Environment.GetEnvironmentVariable(EnvironmentVariable));
}
