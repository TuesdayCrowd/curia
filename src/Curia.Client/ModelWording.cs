namespace Curia.Client;

/// <summary>
/// R11.93 (errata G65): what the vector model a Forum served can and cannot do, from the model id the Forum served and from one definition. The banner above a result set
/// and the titled index's header take it. <b>Seam stub (D10a Task 1):</b> says nothing; Task 3 maps <c>hashed-ngram@1</c>, <c>bge-small-en-v1.5@1</c> and any other id.
/// </summary>
public static class ModelWording
{
    /// <summary>The sentence for <paramref name="modelId"/> (null when the Forum served none). For an id it does not know: the id and that its limits are not stated.</summary>
    public static string Sentence(string? modelId) => string.Empty;
}
