using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.78 (errata G56), NAM22: <see cref="SearchRequestRules"/>, the one function <c>curia</c> and <c>curia-mcp</c> ask whether a search criterion is
/// present and blank and whether a request names a criterion at all. The test is <see cref="string.IsNullOrWhiteSpace(string?)"/>. The expected
/// verdicts are written here by hand, row by row; the same table is put to the command line (<c>CliBlankCriterionTests</c>) and to the tool
/// (<c>BoardNameToolTests</c>), whose verdicts must equal these.
///
/// <para>The names the function returns are fixed here as <c>query</c>, <c>board</c>, <c>author</c> and <c>min_verification</c>: the tool's own spelling
/// (<c>criteria.query</c> and the rest), which the command line maps to its flags. White-space spellings are built at run time.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchRequestRulesTests
{
    private static string U(int scalar) => char.ConvertFromUtf32(scalar);

    /// <summary>The blank spellings: empty, a space, a tab, a line feed, a no-break space (U+00A0) and an ideographic space (U+3000).</summary>
    internal static string[] Blanks() => ["", " ", "\t", "\n", U(0xA0), U(0x3000), "  \t "];

    public static TheoryData<int> BlankRows()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Blanks().Length; i++) data.Add(i);
        return data;
    }

    /// <summary>NAM22: each blank spelling is the named criterion's blank, whichever of the four positions holds it, the others valid.</summary>
    [Theory]
    [MemberData(nameof(BlankRows))]
    public void R11_78_NAM22_AMemberHoldingABlankIsNamed(int row)
    {
        var blank = Blanks()[row];

        Assert.Equal("query", SearchRequestRules.BlankCriterion(blank, "b", "a", "V1"));
        Assert.Equal("board", SearchRequestRules.BlankCriterion("x", blank, "a", "V1"));
        Assert.Equal("author", SearchRequestRules.BlankCriterion("x", "b", blank, "V1"));
        Assert.Equal("min_verification", SearchRequestRules.BlankCriterion("x", "b", "a", blank));
    }

    /// <summary>NAM22: an absent criterion (null) is not blank, and a value with edge white space that holds a character is not blank.</summary>
    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("x", null, null, null)]
    [InlineData(null, "b", null, null)]
    [InlineData(null, null, "a", null)]
    [InlineData(null, null, null, "V1")]
    [InlineData(" x ", " b ", " a ", " V1 ")]
    [InlineData("x", "b", "a", "V1")]
    public void R11_78_NAM22_AnAbsentOrNonBlankCriterionIsNotBlank(string? terms, string? board, string? author, string? minVerification) =>
        Assert.Null(SearchRequestRules.BlankCriterion(terms, board, author, minVerification));

    /// <summary>NAM22: where several are blank the first in the published order is named: query, board, author, min_verification.</summary>
    [Fact]
    public void R11_78_NAM22_TheFirstBlankInThePublishedOrderIsNamed()
    {
        Assert.Equal("query", SearchRequestRules.BlankCriterion("", "", "", ""));
        Assert.Equal("board", SearchRequestRules.BlankCriterion(null, " ", "", ""));
        Assert.Equal("board", SearchRequestRules.BlankCriterion("x", " ", " ", " "));
        Assert.Equal("author", SearchRequestRules.BlankCriterion("x", "b", " ", " "));
        Assert.Equal("min_verification", SearchRequestRules.BlankCriterion("x", "b", "a", " "));
    }

    /// <summary>NAM22: a request names a criterion when some member holds a character; a blank member is no criterion (the old present test counted it).</summary>
    [Theory]
    [InlineData(null, null, null, null, false)]
    [InlineData("", null, null, null, false)]
    [InlineData(" ", null, null, null, false)]
    [InlineData(null, " ", null, null, false)]
    [InlineData(null, null, "\t", null, false)]
    [InlineData(null, null, null, "  ", false)]
    [InlineData(" ", " ", " ", " ", false)]
    [InlineData("x", null, null, null, true)]
    [InlineData(null, "b", null, null, true)]
    [InlineData(null, null, "a", null, true)]
    [InlineData(null, null, null, "V1", true)]
    [InlineData(" ", "b", null, null, true)]
    public void R11_78_NAM22_HasCriterionAgreesWithTheBlankTest(string? terms, string? board, string? author, string? minVerification, bool expected) =>
        Assert.Equal(expected, SearchRequestRules.HasCriterion(terms, board, author, minVerification));
}
