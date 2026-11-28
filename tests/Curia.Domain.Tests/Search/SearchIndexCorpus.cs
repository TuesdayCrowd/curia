using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Curia.Domain.Content;
using Curia.Domain.Search;

namespace Curia.Domain.Tests.Search;

/// <summary>
/// The generated corpus of errata G88 (R14.14): seeded, committed with its seed, and built from classes of post the rule has to survive (case twins and the five tags whose letters do not match the term the
/// tokenizer makes of them, supplementary letters, <c>#</c> and hash-key tags, 64- and 65-byte keys, a number that is a tag and a token, the cap). <see cref="Manifest"/> counts every class <b>from the posts, by
/// its own reading and never from a flag of the generator</b>, so a generator that quietly stops producing a class turns <c>SearchIndexEquivalenceTests</c> red and not greener.
/// Non-ASCII text is built from code points by number, never typed.
/// </summary>
[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "A seeded generator of test posts: the seed is committed so the corpus is the same on every machine, and nothing here is a secret.")]
internal static class SearchIndexCorpus
{
    public const int Seed = 20_261_127;

    private static string Cp(int scalar) => char.ConvertFromUtf32(scalar);

    private static readonly string[] Words =
    [
        "rust", "cargo", "borrow", "checker", "agent", "forum", "post", "question", "answer", "token", "index", "search", "the", "is", "and", "of", "to", "a", "how", "why",
        "Rust", "CARGO", "Borrow", "Index", "ICE", "Istanbul", "2024", "007", "x1", "json", "jcs", "canonical",
    ];

    private static readonly string[] OddWords =
    [
        "caf" + Cp(0xE9), Cp(0xC9) + "COLE", Cp(0x130) + "stanbul", Cp(0x131) + "ce", Cp(0x212A) + "elvin", "stra" + Cp(0xDF) + "e", "stra" + Cp(0x1E9E) + "e", Cp(0x39F) + Cp(0x3A3),
        "don't", "a_b", "a.b", "cafe" + Cp(0x301), "ok" + Cp(0x1F600) + "fine", "a" + Cp(0x10400) + "b", Cp(0x20000), "x" + Cp(0x1D7CE) + "y", Cp(0xFF11) + Cp(0xFF12), "x" + Cp(0x2166), "x" + Cp(0xB2),
        Cp(0x3BC) + "m", Cp(0x39C) + "m", Cp(0xB5) + "m", "s" + Cp(0x17F) + "s", Cp(0x3F4), Cp(0x2126), Cp(0x212B), new string('x', 64), new string('x', 65), new string('y', 10_000),
    ];

    private static readonly string[] Tags =
    [
        "Rust", "rust", "RUST", "Cargo", "#rust", "c++", "2024", "x1", "jcs", Cp(0x3F4), Cp(0x1E9E), Cp(0x2126), Cp(0x212A), Cp(0x212B), Cp(0x17F), Cp(0xDF), Cp(0x130), Cp(0x131),
        Cp(0x10400), Cp(0x10428), Cp(0x16EA0), Cp(0x16EBB), Cp(0xE9), Cp(0xC9), Cp(0x3BC), Cp(0x39C), Cp(0xB5), new string('a', 65), new string('a', 64), "i" + Cp(0x131),
    ];

    /// <summary>The tag a hostile author types to try to be a long token's key: the key itself.</summary>
    public static string ForgedKeyTag => SearchIndex.Key(new string('x', 65));

    public static ImmutableArray<SearchablePost> Build()
    {
        var random = new Random(Seed);
        string Pick(string[] pool) => pool[random.Next(pool.Length)];
        string Text(int tokens)
        {
            var text = new StringBuilder();
            for (var i = 0; i < tokens; i++)
            {
                text.Append(random.Next(5) == 0 ? Pick(OddWords) : Pick(Words)).Append(random.Next(6) == 0 ? ", " : " ");
            }

            return text.ToString();
        }

        var posts = new List<SearchablePost>();
        SearchablePost Make(string? title, string body, params string[] tags) =>
            new($"p{posts.Count + 1}", $"sha-256:{posts.Count + 1}", "general", PostKind.Question, title, body, [.. tags], "https://agents.example/a", posts.Count + 1);

        // Classes, each deliberately, so none depends on the random draw.
        posts.Add(Make("Rust and rust", "the borrow checker", "Rust", "rust"));                       // case twins: tag_tf 2
        posts.Add(Make(null, "rust rust rust", "RUST"));
        foreach (var scalar in new[] { 0x3F4, 0x1E9E, 0x2126, 0x212A, 0x212B, 0x17F, 0x130, 0x131 })      // tags whose letters do not match the term the tokenizer makes of them
            posts.Add(Make("Letter " + Cp(scalar), "word " + Cp(scalar) + " word", Cp(scalar)));
        posts.Add(Make("Micro", Cp(0xB5) + " " + Cp(0x3BC) + " " + Cp(0x39C), Cp(0xB5), Cp(0x3BC), Cp(0x39C)));   // one OrdinalIgnoreCase class of three
        posts.Add(Make("Deseret", "a" + Cp(0x10400) + "b " + Cp(0x10428), Cp(0x10400), Cp(0x10428)));      // supplementary tags, a class by scalar
        posts.Add(Make("Medefaidrin", "x " + Cp(0x16EA0), Cp(0x16EA0), Cp(0x16EBB)));
        posts.Add(Make("Hash tag", "x", "#rust", "#"));                                                  // a tag that begins with '#'
        posts.Add(Make("Long token", new string('x', 65) + " " + new string('x', 64), ForgedKeyTag));      // a tag spelled as the long token's key
        posts.Add(Make("Long tag", "x", new string('a', 65), new string('A', 65)));                        // over 64 bytes, case twins of one another
        posts.Add(Make("2024", "2024 007", "2024"));                                                     // a number that is a tag and a token: one key, two columns
        posts.Add(Make("Every field", "alpha alpha", "alpha", "ALPHA"));
        posts.Add(Make(null, "the the the is and of to a how why"));                                      // only words the query list ignores
        posts.Add(Make("", "   ", []));                                                                  // nothing to index
        posts.Add(Make("Wide", string.Join(' ', Enumerable.Range(0, SearchIndex.MaximumTerms + 100).Select(i => "w" + i.ToString("D5", CultureInfo.InvariantCulture))), "wide"));   // over the cap
        posts.Add(Make("Exact", string.Join(' ', Enumerable.Range(0, SearchIndex.MaximumTerms - 2).Select(i => "e" + i.ToString("D5", CultureInfo.InvariantCulture))), "exact"));  // 4,094 + title + tag = 4,096

        for (var i = 0; i < 500; i++)
        {
            var tags = Enumerable.Range(0, random.Next(5)).Select(_ => Pick(Tags)).ToArray();
            posts.Add(Make(random.Next(4) == 0 ? null : Text(random.Next(7)), Text(random.Next(45)), tags));
        }

        return [.. posts];
    }

    /// <summary>
    /// The class counts, read from the posts by their own predicates (the tokenizer, the runtime's comparer, string checks): never from how the posts were made. A class a minimum names that the corpus lacks is
    /// a failure of the corpus, reported by name.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Manifest(IReadOnlyList<SearchablePost> posts, TagFoldTable fold)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Count(string name, bool when) { counts.TryAdd(name, 0); if (when) counts[name]++; }

        var longTokens = new HashSet<string>(posts.SelectMany(p => LexicalSearch.Tokenize(p.Body)).Where(t => Encoding.UTF8.GetByteCount(t) > SearchIndex.MaximumKeyBytes).Select(SearchIndex.Key), StringComparer.Ordinal);
        foreach (var post in posts)
        {
            var tags = post.Tags;
            Count("tag-case-twins", tags.Any(a => tags.Any(b => !string.Equals(a, b, StringComparison.Ordinal) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase))));
            Count("tag-whose-letter-does-not-match-its-own-term", tags.Any(t =>
            {
                var term = LexicalSearch.Tokenize(t);
                return term.Length == 1 && !string.Equals(t, term[0], StringComparison.OrdinalIgnoreCase);
            }));
            Count("tag-supplementary", tags.Any(t => t.Any(char.IsSurrogate)));
            Count("tag-hash-sign", tags.Any(t => t.StartsWith('#')));
            Count("tag-equals-a-long-token-key", tags.Any(t => longTokens.Contains(t)));
            Count("tag-over-64-bytes", tags.Any(t => Encoding.UTF8.GetByteCount(t) > SearchIndex.MaximumKeyBytes));
            Count("tag-and-token-share-a-key", tags.Any(t => LexicalSearch.Tokenize(post.Body).Concat(LexicalSearch.Tokenize(post.Title)).Any(w => string.Equals(SearchIndex.Key(w), SearchIndex.Key(fold.Fold(t)), StringComparison.Ordinal))));
            Count("tag-fold-classes-of-three", tags.Count(t => t.Length == 1 && fold.Fold(t) != t) >= 2);
            Count("token-over-64-bytes", LexicalSearch.Tokenize(post.Body).Any(t => Encoding.UTF8.GetByteCount(t) > SearchIndex.MaximumKeyBytes));
            Count("token-exactly-64-bytes", LexicalSearch.Tokenize(post.Body).Any(t => Encoding.UTF8.GetByteCount(t) == SearchIndex.MaximumKeyBytes));
            Count("text-supplementary-letter", (post.Body + post.Title).Any(char.IsSurrogate));
            Count("text-combining-mark", (post.Body + post.Title).Any(c => char.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark));
            Count("text-dotted-or-dotless-i", (post.Body + post.Title).Any(c => c is (char)0x130 or (char)0x131));
            Count("term-in-title-body-and-tag", LexicalSearch.Tokenize(post.Title).Intersect(LexicalSearch.Tokenize(post.Body)).Any(w => tags.Any(t => string.Equals(t, w, StringComparison.OrdinalIgnoreCase))));
            Count("only-ignored-words", LexicalSearch.Tokenize(post.Body).Length > 0 && LexicalSearch.Tokenize(post.Body).All(StopWords.Contains));
            Count("no-title-no-tags", string.IsNullOrWhiteSpace(post.Title) && post.Tags.IsEmpty);
            Count("over-the-cap", post.Tags.Length + LexicalSearch.Tokenize(post.Title).Concat(LexicalSearch.Tokenize(post.Body)).Distinct(StringComparer.Ordinal).Count() > SearchIndex.MaximumTerms);
        }

        return counts;
    }

    public static readonly ImmutableArray<string> Required =
    [
        "tag-case-twins", "tag-whose-letter-does-not-match-its-own-term", "tag-supplementary", "tag-hash-sign", "tag-equals-a-long-token-key", "tag-over-64-bytes", "tag-and-token-share-a-key",
        "tag-fold-classes-of-three", "token-over-64-bytes", "token-exactly-64-bytes", "text-supplementary-letter", "text-combining-mark", "text-dotted-or-dotless-i", "term-in-title-body-and-tag",
        "only-ignored-words", "no-title-no-tags", "over-the-cap",
    ];
}
