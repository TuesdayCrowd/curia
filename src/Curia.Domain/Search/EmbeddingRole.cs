namespace Curia.Domain.Search;

/// <summary>
/// The role a text is embedded in (R9.63, errata G65). A model with an instruction on one side is a model whose vector means
/// something different on each: a search query is embedded as a <see cref="Query"/>, a stored post and a question compared with
/// the posts of its board as a <see cref="Passage"/>. An embedder with no such instruction ignores the role.
/// </summary>
public enum EmbeddingRole
{
    /// <summary>A search query: the text a reader typed, compared with stored passages.</summary>
    Query = 0,

    /// <summary>A stored post, and a submitted question compared with stored posts (R8.17).</summary>
    Passage = 1,
}
