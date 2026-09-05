using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>The <see cref="ITextEmbedder"/> over <see cref="HashedNGramEmbedding"/>: the beta's embedder, and every test's.</summary>
public sealed class HashedNGramEmbedder : ITextEmbedder
{
    public EmbeddingModel Model => HashedNGramEmbedding.Model;

    public Result<Embedding> Embed(string text) =>
        HashedNGramEmbedding.Embed(text).Map(vector => new Embedding(Model, vector));
}
