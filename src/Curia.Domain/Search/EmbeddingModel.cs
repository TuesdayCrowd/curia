namespace Curia.Domain.Search;

/// <summary>
/// R9.5: the model a vector came from, recorded beside every vector and named on the wire.
/// Changing the model invalidates every stored vector, and a version field is how that is
/// discovered as a reindex rather than as "mysterious relevance degradation".
/// </summary>
/// <param name="Name">The model family, e.g. <c>hashed-ngram</c>.</param>
/// <param name="Version">The exact revision; two vectors compare only when both match.</param>
/// <param name="Dimensions">The vector length every embedding under this model has.</param>
public sealed record EmbeddingModel(string Name, string Version, int Dimensions)
{
    /// <summary>The identifier stored with each vector and served in <c>why_ranked</c>: <c>name@version</c>.</summary>
    public string Id => Name + "@" + Version;
}
