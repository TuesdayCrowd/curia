using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Curia.Domain.Content;
using Curia.Domain.Primitives;

namespace Curia.Domain.Search;

/// <summary>
/// One row of <c>search_terms</c> (errata G88, R9.75): a key and the frequency of that key in the post's title, tags and body. The key is <see cref="SearchIndex.Key"/>'s, so a token and a
/// folded tag may share one row (a tag <c>2024</c> and the token <c>2024</c>); each arm of a statement reads its own columns only.
/// </summary>
public sealed record IndexedTerm(string Key, int TitleTf, int TagTf, int BodyTf);

/// <summary>
/// What the index holds for one discussion post under one index version: the filters (<c>search_docs</c>) and the terms (<c>search_terms</c>). A function of the post and of nothing else
/// (R9.75): R9.72's list of ignored words is a query's and is never applied here.
/// </summary>
/// <param name="TagsFolded">The distinct, ordinally sorted keys of the post's folded tags: a set, because <see cref="LexicalSearch.Matches"/>'s tag filter is "every named tag present".</param>
/// <param name="TermsTruncated">Whether a distinct key was dropped at <see cref="SearchIndex.MaximumTerms"/> (R9.77).</param>
public sealed record IndexedPost(
    long Seq,
    string PostId,
    string Digest,
    string Board,
    string Kind,
    string Author,
    ImmutableArray<string> TagsFolded,
    bool TermsTruncated,
    ImmutableArray<IndexedTerm> Terms);

/// <summary>
/// The tag fold of index version 1 (errata G88, R9.75): a committed table, <c>conformance/retrieval/tagfold-v1.tsv</c>, one line per scalar whose representative is not itself,
/// <c>&lt;scalar hex&gt; TAB &lt;representative hex&gt;</c>, sorted by scalar. The representative of an <c>OrdinalIgnoreCase</c> class is its least scalar value. <c>OrdinalIgnoreCase</c> is a
/// comparison and has no per-unit function to hash, and it is neither <c>ToUpperInvariant</c> nor <c>ToLowerInvariant</c> (26 and 30 pairs differ), so the index folds through the table and the
/// runtime is the table's oracle: a test regenerates it and a runtime whose table moved turns the equivalence red on purpose.
/// </summary>
public sealed class TagFoldTable
{
    private readonly Dictionary<int, int> _representative;

    private TagFoldTable(Dictionary<int, int> representative) => _representative = representative;

    /// <summary>How many scalars the table maps to a representative other than themselves.</summary>
    public int Count => _representative.Count;

    /// <summary>The table's text: <c>&lt;hex&gt; TAB &lt;hex&gt;</c> lines, <c>\n</c>-terminated, sorted by scalar and with no scalar twice.</summary>
    public static Result<TagFoldTable> Parse(string tsv)
    {
        ArgumentNullException.ThrowIfNull(tsv);

        var map = new Dictionary<int, int>();
        var previous = -1;
        foreach (var line in tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var scalar)
                || !int.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var representative)
                || !IsScalar(scalar) || !IsScalar(representative) || representative >= scalar || scalar <= previous)
                return Result<TagFoldTable>.Fail(new Error(
                    "curia/search/tagfold-malformed", "The tag fold table is not <scalar> TAB <least scalar of its class>, sorted, once each", "line=" + line.Length.ToString(CultureInfo.InvariantCulture)));

            previous = scalar;
            map[scalar] = representative;
        }

        return Result<TagFoldTable>.Ok(new TagFoldTable(map));
    }

    /// <summary>The tag with every scalar replaced by its class's representative. No mapping changes a length: ordinal casing is one scalar to one scalar.</summary>
    public string Fold(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);

        var folded = new StringBuilder(tag.Length);
        foreach (var rune in tag.EnumerateRunes())
            folded.Append(new Rune(_representative.TryGetValue(rune.Value, out var representative) ? representative : rune.Value).ToString());
        return folded.ToString();
    }

    private static bool IsScalar(int value) => value is >= 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF);
}

/// <summary>
/// Errata G88, R9.74 to R9.77: what the keyword channel's index holds for a post. <see cref="IndexTerms"/> is the one function that decides a row; the store's append, the reconcile and the audit
/// all call it, and nothing else writes the tables (a fence holds it).
/// </summary>
public static class SearchIndex
{
    /// <summary>
    /// The index version this build writes. Any change to <see cref="LexicalSearch.Tokenize"/>, <see cref="IndexTerms"/>, <see cref="Key"/>, <see cref="TagFoldTable"/>'s table, the cap or the frequency semantics
    /// is a new version written beside the old (R11.114); a change to R9.72's list never is.
    /// </summary>
    public const short Version = 1;

    /// <summary>R9.77: a post indexes at most this many distinct keys, title and tags first.</summary>
    public const int MaximumTerms = 4096;

    /// <summary>R9.77: a key longer than this many UTF-8 bytes is keyed by its hash.</summary>
    public const int MaximumKeyBytes = 64;

    /// <summary>
    /// R9.77: the key of a token, a folded tag or a query term. The text itself when it is at most <see cref="MaximumKeyBytes"/> UTF-8 bytes and does not begin with <c>#</c>; otherwise <c>#</c> and the
    /// unpadded base64url of its SHA-256. A token cannot begin with <c>#</c> (not a letter or digit) but a tag can, and a tag spelled as another key's hash would be that key: an author-chosen divergence
    /// from <see cref="LexicalSearch.Matches"/>, which is why a key that starts with <c>#</c> is always hashed.
    /// </summary>
    public static string Key(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length <= MaximumKeyBytes && !text.StartsWith('#')) return text;

        return "#" + Convert.ToBase64String(SHA256.HashData(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// The row set of one discussion post. Keys are, in order, the title's tokens, the tags' folded keys and the body's tokens; the first <see cref="MaximumTerms"/> distinct keys are kept, and a kept
    /// key counts <i>every</i> occurrence anywhere in the post, including after the cutoff, so a post over the cap differs from <see cref="LexicalSearch.Rank"/> only by terms that would score 0.
    /// A tag counts one per occurrence (<c>Rust</c> and <c>rust</c> are two), as <c>Explain</c> counts them.
    /// </summary>
    public static IndexedPost IndexTerms(SearchablePost post, TagFoldTable fold)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(fold);

        var order = new List<string>();
        var counts = new Dictionary<string, int[]>(StringComparer.Ordinal);

        void Count(string key, int field)
        {
            if (!counts.TryGetValue(key, out var frequencies))
            {
                frequencies = new int[3];
                counts[key] = frequencies;
                order.Add(key);
            }

            frequencies[field]++;
        }

        foreach (var token in LexicalSearch.Tokenize(post.Title)) Count(Key(token), 0);

        var tags = post.Tags.IsDefault ? [] : post.Tags;
        var tagKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            var key = Key(fold.Fold(tag));
            tagKeys.Add(key);
            Count(key, 1);
        }

        foreach (var token in LexicalSearch.Tokenize(post.Body)) Count(Key(token), 2);

        var kept = order.Take(MaximumTerms).Select(k => new IndexedTerm(k, counts[k][0], counts[k][1], counts[k][2])).ToImmutableArray();

        return new IndexedPost(
            post.Sequence,
            post.PostId,
            post.Digest,
            post.Board,
            PostKinds.Wire(post.Kind),
            post.Author,
            [.. tagKeys],
            order.Count > MaximumTerms,
            kept);
    }
}

/// <summary>
/// R11.116: the tokenizer is defined by the runtime (<c>char.IsLetterOrDigit</c> and <c>char.ToLowerInvariant</c> over UTF-16 units), so an upgrade that assigns a new letter changes it under rows
/// already written. The pin is a SHA-256 over every unit from 0x0000 to 0xFFFF (surrogates included): one byte <c>IsLetterOrDigit</c> (0 or 1), then two bytes big-endian <c>ToLowerInvariant</c>.
/// It is a drift detector and not a derivation: it restates the runtime, so it says "unchanged" and never "right" (that is <c>tokenize.jsonl</c>'s).
/// </summary>
public static class TokenizerPin
{
    /// <summary>The digest of the running runtime's letter and case tables.</summary>
    public static byte[] Compute()
    {
        var table = new byte[65_536 * 3];
        for (var unit = 0; unit < 65_536; unit++)
        {
            var c = (char)unit;
            var lower = char.ToLowerInvariant(c);
            table[unit * 3] = char.IsLetterOrDigit(c) ? (byte)1 : (byte)0;
            table[(unit * 3) + 1] = (byte)(lower >> 8);
            table[(unit * 3) + 2] = (byte)(lower & 0xFF);
        }

        return SHA256.HashData(table);
    }
}
