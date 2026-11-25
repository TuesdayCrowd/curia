using System.Collections.Immutable;

namespace Curia.Domain.Search;

/// <summary>
/// R9.72: the published list of 111 common English words the keyword channel ignores in a query (errata G81). A constant with no I/O, and the one copy the product reads;
/// <c>conformance/retrieval/stopwords.txt</c> is the committed list and a probe holds the two equal word for word, with the SHA-256 R9.72 states.
///
/// <para>The list is lower-case ASCII and English only, applied to a query's terms and to nothing else (never a post's text, a tag, a filter, the embedder's input or R8.18's overlap).
/// It is a prior chosen before any query existed and measured once against a blind set (<c>conformance/retrieval/STOPWORD-RULE.md</c>), not a fit.</para>
/// </summary>
public static class StopWords
{
    /// <summary>The list, sorted ordinally.</summary>
    public static ImmutableArray<string> Words { get; } =
    [
        "a", "about", "also", "am", "an", "and", "are", "aren", "as", "at", "be", "because", "been", "being", "but", "by", "can", "cannot", "could",
        "couldn", "did", "didn", "do", "does", "doesn", "don", "for", "from", "had", "hadn", "has", "hasn", "have", "haven", "he", "hello", "her",
        "here", "hey", "hi", "him", "his", "how", "i", "if", "in", "into", "is", "isn", "it", "its", "just", "ll", "may", "me", "might", "must",
        "my", "no", "not", "of", "on", "or", "our", "please", "s", "shall", "she", "should", "shouldn", "so", "such", "t", "than", "thank", "thanks",
        "that", "the", "their", "them", "then", "there", "these", "they", "this", "those", "to", "too", "us", "ve", "very", "was", "wasn", "we",
        "were", "weren", "what", "when", "where", "which", "who", "whom", "whose", "why", "will", "with", "won", "would", "wouldn", "you", "your"
    ];

    private static readonly ImmutableHashSet<string> Set = Words.ToImmutableHashSet(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="token"/> (already lower-cased by <see cref="LexicalSearch.Tokenize"/>) is on the list.</summary>
    public static bool Contains(string token) => Set.Contains(token);
}
