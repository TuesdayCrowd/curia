using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>Errata G88, R9.79: a query names at most <see cref="LexicalSearch.MaximumQueryTerms"/> distinct terms (R9.72's, after the list); it is refused, not truncated, and the refusal echoes nothing of the text.</summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class QueryTermCapTests
{
    private static string Words(int count, string prefix = "w") => string.Join(' ', Enumerable.Range(0, count).Select(i => prefix + i.ToString("D3", CultureInfo.InvariantCulture)));

    [Fact]
    public void R9_79_ThirtyTwoDistinctTermsAreServedAndThirtyThreeAreRefused()
    {
        Assert.Null(LexicalSearch.RefuseLongQuery(Words(32)));
        var refusal = LexicalSearch.RefuseLongQuery(Words(33));

        Assert.NotNull(refusal);
        Assert.Equal("curia/search/query-too-many-terms", refusal.Type);
        Assert.Equal("terms=33; limit=32; search for the words that matter most, or run several searches", refusal.Detail);
    }

    [Fact]
    public void R9_79_ARepeatedTermCountsOnceAndAnIgnoredWordNotAtAll()
    {
        // 32 distinct terms, each said three times, and the whole stop list besides: the cost is the distinct terms' postings, so this is a query of 32.
        var text = string.Join(' ', Enumerable.Repeat(Words(32), 3)) + " " + string.Join(' ', StopWords.Words);

        Assert.Null(LexicalSearch.RefuseLongQuery(text));
        Assert.Null(LexicalSearch.RefuseLongQuery(null));
        Assert.Null(LexicalSearch.RefuseLongQuery(""));
    }

    [Fact]
    public void R9_79_TheRefusalEchoesNothingOfTheText()
    {
        var refusal = LexicalSearch.RefuseLongQuery(Words(40, "secretword"))!;

        Assert.DoesNotContain("secretword", refusal.Title + refusal.Detail, StringComparison.Ordinal);
    }
}
