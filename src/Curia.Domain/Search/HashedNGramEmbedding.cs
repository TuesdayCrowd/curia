using System.Collections.Immutable;
using System.Text;
using Curia.Domain.Primitives;

namespace Curia.Domain.Search;

/// <summary>
/// A deterministic, dependency-free text embedding: feature-hashed word unigrams and character
/// trigrams, sublinear term weighting, L2-normalized, 256 dimensions.
///
/// <para><b>What it is for.</b> R9.4 needs a vector channel and R9.5 needs its model named. A
/// learned model (the scoping document's ONNX adapter) is the intended production embedder, and
/// this is not a substitute for it: it captures shared vocabulary and near-spellings, not
/// paraphrase or cross-lingual meaning. What it does give the beta is a second retrieval channel
/// that is honest about being one, a vector store whose plumbing is exercised end to end, and
/// a dedupe measure (§8.5) that catches the case §8.5 opens with -- a hundred agents pasting
/// the same undocumented error -- because that case is literal overlap.</para>
///
/// <para><b>Why it is in the domain.</b> It is pure text arithmetic, like <see cref="LexicalSearch"/>,
/// and the vectors it produces are stored; a change to any constant here is a model change and
/// therefore a new <see cref="EmbeddingModel.Version"/> (R9.5, R11.10). The hash is FNV-1a over
/// UTF-8, not <see cref="string.GetHashCode()"/>, because the latter is randomized per process
/// and a stored vector must mean the same thing tomorrow.</para>
/// </summary>
public static class HashedNGramEmbedding
{
    public static EmbeddingModel Model { get; } = new("hashed-ngram", "1", 256);

    private const ulong FnvOffset = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>
    /// The embedding of <paramref name="text"/>, or a failure when the text has no features at
    /// all (nothing a letter or digit), because a zero vector has no direction and a cosine
    /// against it is undefined rather than zero.
    /// </summary>
    public static Result<ImmutableArray<float>> Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in Words(text))
        {
            Count(counts, "w:" + word);
            if (word.Length >= 3)
            {
                var bounded = "^" + word + "$";
                for (var i = 0; i + 3 <= bounded.Length; i++)
                    Count(counts, string.Concat("t:", bounded.AsSpan(i, 3)));
            }
        }

        if (counts.Count == 0)
            return Result<ImmutableArray<float>>.Fail(EmbeddingErrors.NoFeatures());

        var vector = new float[Model.Dimensions];
        foreach (var (feature, count) in counts)
        {
            var hash = Fnv1a(feature);
            var bucket = (int)(hash % (ulong)Model.Dimensions);
            var sign = ((hash >> 32) & 1) == 0 ? 1.0 : -1.0;
            vector[bucket] += (float)(sign * (1.0 + Math.Log(count)));
        }

        var norm = 0.0;
        foreach (var v in vector) norm += (double)v * v;
        norm = Math.Sqrt(norm);
        for (var i = 0; i < vector.Length; i++) vector[i] = (float)(vector[i] / norm);

        return Result<ImmutableArray<float>>.Ok([.. vector]);
    }

    /// <summary>Cosine similarity of two L2-normalized vectors of the same model: their dot product.</summary>
    public static double Cosine(ImmutableArray<float> a, ImmutableArray<float> b)
    {
        if (a.Length != b.Length)
            throw new ArgumentException("Vectors of different dimension are from different models and do not compare.", nameof(b));

        var dot = 0.0;
        for (var i = 0; i < a.Length; i++) dot += (double)a[i] * b[i];
        return dot;
    }

    /// <summary>
    /// Words: maximal runs of letters and digits after NFKC folding and lower-casing. Folding is
    /// analysis on a derived copy (R6.13); nothing here touches stored content.
    /// </summary>
    private static IEnumerable<string> Words(string text)
    {
        // Upper-cased rather than lower-cased only because the analyzer's casing rule prefers the
        // round-trippable direction; the feature strings are never shown, only hashed.
        var folded = text.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var word = new StringBuilder();
        foreach (var rune in folded.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                word.Append(rune.ToString());
            }
            else if (word.Length > 0)
            {
                yield return word.ToString();
                word.Clear();
            }
        }

        if (word.Length > 0) yield return word.ToString();
    }

    private static void Count(Dictionary<string, int> counts, string feature) =>
        counts[feature] = counts.TryGetValue(feature, out var n) ? n + 1 : 1;

    private static ulong Fnv1a(string feature)
    {
        var hash = FnvOffset;
        foreach (var b in Encoding.UTF8.GetBytes(feature))
        {
            hash ^= b;
            hash *= FnvPrime;
        }
        return hash;
    }
}

/// <summary>RFC 9457 problem-type slugs the embedding emits.</summary>
public static class EmbeddingErrors
{
    public static Error NoFeatures() => new(
        "curia/embedding/no-features",
        "The text contains no letters or digits and has no embedding",
        null);
}
