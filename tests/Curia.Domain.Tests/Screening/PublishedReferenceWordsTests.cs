using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// The extractor the probes of errata G60 read their lists and sentences with (<see cref="PublishedReferenceWords"/>): the Markdown code-span rule it
/// reads by, and the counts and content it asserts before it returns a list (traps 141 and 142). A list that silently lost a form, or a span read by the
/// wrong rule, would make every probe built on it agree with a smaller or a wrong set.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PublishedReferenceWordsTests
{
    /// <summary>A span opened by n backticks closes at the next run of exactly n, so a form that holds a backtick is read whole.</summary>
    [Theory]
    [InlineData("a `x` b", new[] { "x" })]
    [InlineData("a `` `--password=$DB_PASS` `` b", new[] { "`--password=$DB_PASS`" })]
    [InlineData("`` If (`$A`, `${A}`) or `<b>`: yes. `` and `y`", new[] { "If (`$A`, `${A}`) or `<b>`: yes.", "y" })]
    [InlineData("an unclosed `span", new string[0])]
    [InlineData("```` a `` b ````", new[] { "a `` b" })]
    public void R10_96_TheCodeSpanRuleIsMarkdowns(string text, string[] spans)
    {
        ArgumentNullException.ThrowIfNull(text);
        Assert.Equal(spans, PublishedReferenceWords.Spans(text));
    }

    /// <summary>The four lists have the counts the PR is built to, and each holds a form it is known to hold (the extractor asserts both before it returns).</summary>
    [Fact]
    public void R10_96_TheFourListsHaveTheirCountsAndTheirContent()
    {
        Assert.Equal(
            (16, 10, 2, 5),
            (PublishedReferenceWords.Benign().Length, PublishedReferenceWords.Refused().Length, PublishedReferenceWords.FalsePositives().Length, PublishedReferenceWords.Evasions().Length));
        Assert.Contains("`--password=$DB_PASS`", PublishedReferenceWords.Benign());
        Assert.Contains("--password=$DB_PASS", PublishedReferenceWords.Benign());
    }

    /// <summary>R11.84's two sentences are each one two-backtick span, read whole with the single backticks inside them.</summary>
    [Fact]
    public void R11_84_TheTwoSentencesAreReadWhole()
    {
        Assert.StartsWith("If the ConnectionStringPassword hit is a reference to a secret and not the secret,", PublishedReferenceWords.RefusalSentence(), StringComparison.Ordinal);
        Assert.Contains("(`$DB_PASS`, `${DB_PASS}` or `%DB_PASS%`)", PublishedReferenceWords.RefusalSentence(), StringComparison.Ordinal);
        Assert.StartsWith("A reference to a secret is not a credential:", PublishedReferenceWords.DescriptionSentence(), StringComparison.Ordinal);
        Assert.EndsWith("so never write it.", PublishedReferenceWords.DescriptionSentence(), StringComparison.Ordinal);
    }
}
