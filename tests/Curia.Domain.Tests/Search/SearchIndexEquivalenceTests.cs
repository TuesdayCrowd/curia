using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>
/// Errata G88, R9.74 and R9.75 on the write side (SI1's half that S1 can hold, and SI16): for every post of the seeded corpus and every query term, the three counts the index would hand a statement
/// (title and body frequency of the term's key, tag frequency of the folded term's key) are the three counts <see cref="LexicalSearch.Rank"/> explains for that post. The oracle is the lexical channel as it
/// stands; <c>tokenize.jsonl</c> says the oracle is the rule, and this says the index is the oracle. <b>SI16:</b> the corpus's class counts are read from the posts, and a class it lacks fails by name.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchIndexEquivalenceTests
{
    private static readonly TagFoldTable Fold = TagFoldTable.Parse(File.ReadAllText(SearchIndexFiles.Retrieval("tagfold-v1.tsv"), Encoding.UTF8)).Match(t => t, e => throw new InvalidOperationException(e.Type));

    private static readonly ImmutableArray<SearchablePost> Corpus = SearchIndexCorpus.Build();

    [Fact]
    public void R14_14_SI16_TheCorpusHoldsEveryClassTheEntryNamesCountedFromThePosts()
    {
        var manifest = SearchIndexCorpus.Manifest(Corpus, Fold);

        var lacking = SearchIndexCorpus.Required.Where(name => !manifest.TryGetValue(name, out var count) || count < 1).ToArray();
        Assert.True(lacking.Length == 0, "the generated corpus has no post of the class(es): " + string.Join(", ", lacking));
        Assert.True(Corpus.Length >= 500, "the corpus has " + Corpus.Length + " posts");
    }

    [Fact]
    [SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "The query side lower-cases every token; a tag read as a term is lower-cased the same way on purpose.")]
    public void R9_74_SI1_TheIndexedCountsAreTheCountsTheLexicalChannelExplains()
    {
        var checks = 0;
        foreach (var post in Corpus)
        {
            var indexed = SearchIndex.IndexTerms(post, Fold);
            var byKey = indexed.Terms.ToDictionary(t => t.Key, StringComparer.Ordinal);
            var capped = indexed.TermsTruncated;

            // The terms a query could name that touch this post: its own tokens, each tag read as a term, a letter-only tag lower-cased, and one term the post does not hold.
            var terms = LexicalSearch.Tokenize(post.Title).Concat(LexicalSearch.Tokenize(post.Body)).Concat(post.Tags.SelectMany(t => LexicalSearch.Tokenize(t)))
                .Concat(post.Tags.SelectMany(t => LexicalSearch.Tokenize(t.ToLowerInvariant()))).Append("absentword").Distinct(StringComparer.Ordinal).ToArray();

            foreach (var term in terms)
            {
                var hits = LexicalSearch.Rank([post], new LexicalQuery(), new QueryTerms([term], [], true));
                var oracle = hits.IsEmpty ? (0, 0, 0) : (hits[0].Why.TitleMatches, hits[0].Why.BodyMatches, hits[0].Why.TagMatches);

                var token = byKey.GetValueOrDefault(SearchIndex.Key(term));
                var tag = byKey.GetValueOrDefault(SearchIndex.Key(Fold.Fold(term)));
                var index = (token?.TitleTf ?? 0, token?.BodyTf ?? 0, tag?.TagTf ?? 0);

                // A post over the cap is the index of its first 4,096 keys: a term it dropped has no row (the one stated difference, R9.77); every term it kept equals the oracle.
                if (capped && token is null && tag is null && oracle != (0, 0, 0)) continue;

                Assert.True(oracle == index, $"post {post.PostId}, term of {term.Length} unit(s) starting {(int)term[0]:X4}: the lexical channel explains (title, body, tag) {oracle}, the index holds {index}");
                checks++;
            }
        }

        Assert.True(checks > 5_000, "only " + checks + " comparisons were made");
    }

    [Fact]
    public void R9_77_TheOverCapPostsDropOnlyTheKeysPastTheCapAndSaySo()
    {
        var wide = Corpus.Single(p => p.Title == "Wide");
        var exact = Corpus.Single(p => p.Title == "Exact");

        var indexedWide = SearchIndex.IndexTerms(wide, Fold);
        var indexedExact = SearchIndex.IndexTerms(exact, Fold);

        Assert.Equal((SearchIndex.MaximumTerms, true), (indexedWide.Terms.Length, indexedWide.TermsTruncated));
        Assert.Equal(["wide", "WIDE", "w00000"], indexedWide.Terms.Take(3).Select(t => t.Key).ToArray());   // the title's token, the tag's folded key, then the body's first
        Assert.DoesNotContain(indexedWide.Terms, t => t.Key == "w04099");
        Assert.Equal((SearchIndex.MaximumTerms, false), (indexedExact.Terms.Length, indexedExact.TermsTruncated));
    }
}
