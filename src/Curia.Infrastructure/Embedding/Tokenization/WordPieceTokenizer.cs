namespace Curia.Infrastructure.Embedder.Tokenization;

/// <summary>What the tokenizer read: the content ids (no <c>[CLS]</c> or <c>[SEP]</c>), how many characters of the text it consumed, and whether it stopped at the cap.</summary>
internal sealed record TokenizeResult(IReadOnlyList<int> ContentIds, int CharactersConsumed, bool StoppedAtCap);

/// <summary>
/// BERT-uncased WordPiece, written in this repository to the behaviour of Hugging Face's tokenizer (errata G65, Amendment 1 ruling 22), streaming: it stops on a word
/// boundary after the <c>maxContentTokens</c>th content token and reads no further. <b>Seam stub (D10a Task 1):</b> reads nothing and answers no tokens; Task 2 writes it.
/// </summary>
internal sealed class WordPieceTokenizer
{
    internal WordPieceTokenizer(IReadOnlyList<string> vocabulary)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        Vocabulary = vocabulary;
    }

    internal IReadOnlyList<string> Vocabulary { get; }

    /// <summary>The id of <c>[UNK]</c>; the ids of the five specials come from the vocabulary.</summary>
    internal int UnknownId
    {
        get
        {
            for (var i = 0; i < Vocabulary.Count; i++)
                if (string.Equals(Vocabulary[i], "[UNK]", StringComparison.Ordinal)) return i;
            return -1;
        }
    }

    internal TokenizeResult Tokenize(string text, int maxContentTokens)
    {
        ArgumentNullException.ThrowIfNull(text);
        _ = Vocabulary;
        return new TokenizeResult([], 0, false);
    }
}
