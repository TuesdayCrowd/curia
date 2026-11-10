using Curia.Domain.Search;
using Curia.Infrastructure.Embedder.Tokenization;

namespace Curia.Infrastructure.Embedder;

/// <summary>The three int64 inputs of the graph for a batch of one, and whether the text has anything the model knows (Ruling 9).</summary>
internal sealed record BgeInput(long[] InputIds, long[] AttentionMask, long[] TokenTypeIds, bool Placeable)
{
    /// <summary>
    /// The tokenized input of <paramref name="text"/> under <paramref name="role"/> (errata G65, Ruling 1): <paramref name="queryPrefix"/> first in the query role,
    /// the head <paramref name="maxSequenceLength"/> tokens with <c>[CLS]</c> and <c>[SEP]</c> counted, the prefix's tokens counted against the budget, and a text all of
    /// whose content tokens are unknown not placeable. The prefix is tokenized apart from the text, which is the same as tokenizing them joined because the prefix ends at a word
    /// boundary; apart, the placeable test can be about the text alone.
    /// </summary>
    internal static BgeInput Build(WordPieceTokenizer tokenizer, string text, EmbeddingRole role, string queryPrefix, int maxSequenceLength)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(queryPrefix);

        var budget = Math.Max(0, maxSequenceLength - 2);
        var prefix = role == EmbeddingRole.Query ? tokenizer.Tokenize(queryPrefix, budget).ContentIds : [];
        var content = tokenizer.Tokenize(text, Math.Max(0, budget - prefix.Count)).ContentIds;

        var ids = new long[2 + prefix.Count + content.Count];
        ids[0] = tokenizer.ClsId;
        var at = 1;
        foreach (var id in prefix) ids[at++] = id;
        foreach (var id in content) ids[at++] = id;
        ids[at] = tokenizer.SepId;

        var placeable = content.Any(id => id != tokenizer.UnknownId);
        var mask = new long[ids.Length];
        Array.Fill(mask, 1L);
        return new BgeInput(ids, mask, new long[ids.Length], placeable);
    }
}
