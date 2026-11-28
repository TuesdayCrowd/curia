using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>
/// Errata G88, R14.14, SI12 and SI16: <c>conformance/retrieval/tokenize.jsonl</c> against <see cref="LexicalSearch.Tokenize"/>, <see cref="SearchIndex.Key"/>, <see cref="TagFoldTable.Fold"/> and
/// <see cref="SearchIndex.IndexTerms"/>. The file is written from the rule's sentence (a row whose basis is <c>measured</c> says it was read off a run, naming the runtime); no expected value is the
/// output of the function it tests. Inputs and outputs are lists of UTF-16 units in hex, because JSON cannot carry a lone surrogate and a literal invisible code point in a fixture is how fixtures change unseen.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TokenizeVectorsTests
{
    private static readonly TagFoldTable Fold = TagFoldTable.Parse(File.ReadAllText(SearchIndexFiles.Retrieval("tagfold-v1.tsv"), Encoding.UTF8)).Match(t => t, e => throw new InvalidOperationException(e.Type));

    private static string Text(JsonElement units) => new([.. units.EnumerateArray().Select(u => (char)Convert.ToUInt16(u.GetString(), 16))]);

    private static string Generated(JsonElement gen) => new((char)Convert.ToUInt16(gen.GetProperty("unit").GetString(), 16), gen.GetProperty("count").GetInt32());

    private static List<JsonElement> Rows(string op) =>
        [.. File.ReadAllLines(SearchIndexFiles.Retrieval("tokenize.jsonl"), Encoding.UTF8)
            .Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).Where(r => r.GetProperty("op").GetString() == op)];

    [Fact]
    public void R14_14_SI12_TokenizeReadsTheRulesTokens()
    {
        var rows = Rows("tokenize");
        Assert.True(rows.Count >= 20, "the file has " + rows.Count + " tokenize rows");
        foreach (var row in rows)
        {
            var input = row.TryGetProperty("input_gen", out var gen) ? Generated(gen) : Text(row.GetProperty("input_u16"));
            var expected = row.TryGetProperty("expected_gen", out var expectedGen)
                ? [.. expectedGen.EnumerateArray().Select(Generated)]
                : row.GetProperty("expected_u16").EnumerateArray().Select(Text).ToArray();
            Assert.True(expected.SequenceEqual(LexicalSearch.Tokenize(input)), $"{row.GetProperty("id").GetString()}: tokens differ");
        }
    }

    [Fact]
    public void R14_14_SI12_FoldMapsEachTagToTheTablesRepresentative()
    {
        var rows = Rows("fold");
        Assert.True(rows.Count >= 10, "the file has " + rows.Count + " fold rows");
        foreach (var row in rows)
            Assert.True(
                string.Equals(Text(row.GetProperty("expected_u16")), Fold.Fold(Text(row.GetProperty("input_u16"))), StringComparison.Ordinal),
                $"{row.GetProperty("id").GetString()}: fold differs");
    }

    [Fact]
    public void R14_14_SI12_KeyIsTheTextOrItsHash()
    {
        var rows = Rows("key");
        Assert.True(rows.Count >= 6, "the file has " + rows.Count + " key rows");
        foreach (var row in rows)
            Assert.True(
                string.Equals(Text(row.GetProperty("expected_u16")), SearchIndex.Key(Text(row.GetProperty("input_u16"))), StringComparison.Ordinal),
                $"{row.GetProperty("id").GetString()}: key differs");
    }

    private static SearchablePost PostOf(JsonElement input)
    {
        var body = input.TryGetProperty("body_gen", out var gen)
            ? string.Join(' ', Enumerable.Range(0, gen.GetProperty("count").GetInt32()).Select(i => gen.GetProperty("prefix").GetString() + i.ToString("D4", System.Globalization.CultureInfo.InvariantCulture))
                .Concat(gen.GetProperty("then").EnumerateArray().Select(t => t.GetString()!)))
            : Text(input.GetProperty("body_u16"));
        var title = Text(input.GetProperty("title_u16"));
        return new SearchablePost("p1", "digest-1", "general", PostKind.Question, title.Length == 0 ? null : title, body, [.. input.GetProperty("tags_u16").EnumerateArray().Select(Text)], "https://agents.example/a", 1);
    }

    [Fact]
    public void R14_14_SI12_SI17_IndexTermsAreTheRulesRowsAndTheCapIsExact()
    {
        var rows = Rows("index_terms");
        Assert.True(rows.Count >= 10, "the file has " + rows.Count + " index_terms rows");
        foreach (var row in rows)
        {
            var id = row.GetProperty("id").GetString();
            var expected = row.GetProperty("expected");
            var indexed = SearchIndex.IndexTerms(PostOf(row.GetProperty("input")), Fold);

            Assert.True(expected.GetProperty("truncated").GetBoolean() == indexed.TermsTruncated, $"{id}: truncated differs");

            static string Line(string k, int a, int b, int c) => $"{k}|{a}|{b}|{c}";
            static string Spelled(JsonElement r) => Line(Text(r[0]), r[1].GetInt32(), r[2].GetInt32(), r[3].GetInt32());
            var actual = indexed.Terms.Select(t => Line(t.Key, t.TitleTf, t.TagTf, t.BodyTf)).ToArray();

            if (expected.TryGetProperty("terms", out var terms))
            {
                Assert.True(terms.EnumerateArray().Select(Spelled).SequenceEqual(actual), $"{id}: terms differ: {string.Join(" ; ", actual.Take(6))}");
                Assert.True(expected.GetProperty("tags_folded_u16").EnumerateArray().Select(Text).SequenceEqual(indexed.TagsFolded), $"{id}: tags_folded differs");
            }
            else
            {
                Assert.Equal(expected.GetProperty("terms_count").GetInt32(), actual.Length);
                var head = expected.GetProperty("terms_head").EnumerateArray().Select(Spelled).ToArray();
                Assert.True(head.SequenceEqual(actual.Take(head.Length)), $"{id}: head differs: {string.Join(" ; ", actual.Take(4))}");
                Assert.Equal(Spelled(expected.GetProperty("terms_last")), actual[^1]);
                foreach (var absent in expected.GetProperty("absent_u16").EnumerateArray().Select(Text))
                    Assert.DoesNotContain(indexed.Terms, t => t.Key == absent);
            }
        }
    }

    [Fact]
    public void R14_14_SI16_TheFileHoldsEveryClassTheEntryNames()
    {
        // A positive control for the file itself: a row class deleted from it makes the checks above quieter, never red, so each class is counted here from the rows' ids.
        var ids = File.ReadAllLines(SearchIndexFiles.Retrieval("tokenize.jsonl"), Encoding.UTF8).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("id").GetString()!).ToImmutableArray();
        foreach (var needed in new[]
        {
            "tok-deseret-splits", "tok-cjk-ext-b-absent", "tok-bold-digit-absent", "tok-emoji-splits", "tok-combining-mark-splits", "tok-precomposed", "tok-dotted-capital-i-stays", "tok-dotless-i",
            "tok-kelvin-lowers-to-k", "tok-eszett-and-capital", "tok-capital-sigma", "tok-lone-high-surrogate", "tok-apostrophe-splits", "tok-underscore-splits", "tok-dot-splits", "tok-fullwidth-digits",
            "tok-roman-numeral-dropped", "tok-superscript-two-dropped", "tok-long-token",
            "fold-theta-symbol-is-not-theta", "fold-kelvin-is-not-k", "fold-angstrom-is-not-a-ring", "fold-ohm-is-not-omega", "fold-capital-sharp-s-is-not-sharp-s", "fold-long-s-is-not-s", "fold-medefaidrin-capital-stays",
            "key-64-bytes", "key-65-bytes", "key-hash-sign-tag", "key-forged-hash-tag",
            "ix-duplicate-tag-counts-twice", "ix-cap-exact", "ix-cap-over", "ix-cap-kept-key-counts-after-cutoff", "ix-stopword-stored",
        })
            Assert.Contains(needed, ids);
    }
}
