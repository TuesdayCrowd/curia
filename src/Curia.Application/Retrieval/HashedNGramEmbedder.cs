using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>The <see cref="ITextEmbedder"/> over <see cref="HashedNGramEmbedding"/>: the test adapter (every model-free test's, and the test fixture's one explicit door). The Forum does not run it: production registers the ONNX embedder and nothing else (errata G65, R11.91), and a fence reads every shipped assembly's IL for a reference to this type.</summary>
public sealed class HashedNGramEmbedder : ITextEmbedder
{
    public EmbeddingModel Model => HashedNGramEmbedding.Model;

    /// <summary>The hashed model has no instruction on either side: the role is ignored and the answer is complete before the task is returned.</summary>
    public Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken) =>
        Task.FromResult(HashedNGramEmbedding.Embed(text).Map(vector => new Embedding(Model, vector)));
}
