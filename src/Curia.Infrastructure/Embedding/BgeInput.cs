using Curia.Domain.Search;
using Curia.Infrastructure.Embedder.Tokenization;

namespace Curia.Infrastructure.Embedder;

/// <summary>The three int64 inputs of the graph for a batch of one, and whether the text has anything the model knows (Ruling 9).</summary>
internal sealed record BgeInput(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds, bool Placeable)
{
    /// <summary>
    /// The tokenized input of <paramref name="text"/> under <paramref name="role"/> (errata G65, Ruling 1): <paramref name="queryPrefix"/> first in the query role,
    /// the head <paramref name="maxSequenceLength"/> tokens with <c>[CLS]</c> and <c>[SEP]</c> counted, the prefix's tokens counted against the budget, and a text all of
    /// whose content tokens are unknown not placeable. <b>Seam stub (D10a Task 1):</b> answers an empty, unplaceable input.
    /// </summary>
    internal static BgeInput Build(WordPieceTokenizer tokenizer, string text, EmbeddingRole role, string queryPrefix, int maxSequenceLength)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(queryPrefix);
        return new BgeInput([], [], [], false);
    }
}
