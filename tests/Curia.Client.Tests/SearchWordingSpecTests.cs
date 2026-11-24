using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// The words of errata G64 (R9.67, R9.68, R10.99, R11.103) as the errata publishes them, read at test time from <c>curia-whitepaper-ERRATA-AND-ADDENDUM.md</c> through
/// <see cref="PublishedAcceptBudget.Paragraph(string)"/> and never from the code a probe is about. A phrase is read by asserting the paragraph holds it exactly where the probe says;
/// an edit that removes it turns the probe red and does not shrink what it compares (NDT13's form).
/// </summary>
internal static partial class G64Words
{
    internal static string R967() => PublishedAcceptBudget.Paragraph("**R9.67**");

    internal static string R968() => PublishedAcceptBudget.Paragraph("**R9.68**");

    internal static string R1099() => PublishedAcceptBudget.Paragraph("**R10.99**");

    internal static string R11103() => PublishedAcceptBudget.Paragraph("**R11.103**");

    /// <summary>The code spans of a paragraph, in order.</summary>
    internal static string[] Spans(string paragraph) => [.. CodeSpan().Matches(paragraph).Select(m => m.Groups[1].Value)];

    /// <summary>The one code span of <paramref name="paragraph"/> that equals <paramref name="span"/>, as written there; it throws when there is none or more than one.</summary>
    internal static string Span(string paragraph, string span)
    {
        var found = Spans(paragraph).Where(s => string.Equals(s, span, StringComparison.Ordinal)).ToArray();
        return found.Length == 1 ? found[0] : throw new InvalidOperationException($"the paragraph holds {found.Length} code spans `{span}`, expected one: {paragraph}");
    }

    /// <summary>A phrase the paragraph holds once, returned as written.</summary>
    internal static string Phrase(string paragraph, string phrase)
    {
        var at = paragraph.IndexOf(phrase, StringComparison.Ordinal);
        return at < 0 || paragraph.IndexOf(phrase, at + 1, StringComparison.Ordinal) >= 0
            ? throw new InvalidOperationException($"the paragraph does not hold \"{phrase}\" exactly once: {paragraph}")
            : phrase;
    }

    /// <summary>R10.99's first clause when no served result was chosen by meaning, as a code span.</summary>
    internal static string NoVectorClause() => Span(R1099(), "the vector channel chose none of them:");

    /// <summary>R10.99's first clause when no served result has a word match, as a code span.</summary>
    internal static string NoWordClause() => Span(R1099(), "no result shares a word with the query:");

    /// <summary>R10.99's gloss of a `words` mark: the phrase that says a common word counts.</summary>
    internal static string CommonWord() => Phrase(R1099(), "a common word such as");

    /// <summary>R10.99's sentence about the score.</summary>
    internal static string NotACosine() => Phrase(R1099(), "not a cosine");

    /// <summary>R10.99's word for the count of posts moved to a later page.</summary>
    /// R10.99 names it twice as a code span (the served count, and the unserved form's words), so the first is read; it still throws when there is none.
    internal static string Diversified() =>
        Spans(R1099()).FirstOrDefault(s => string.Equals(s, "diversified", StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"the paragraph holds no code span `diversified`: {R1099()}");

    [GeneratedRegex("`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex CodeSpan();
}

/// <summary>
/// MBC8 (NDT13's form): R9.67 to R11.103 parsed from the errata carry the obligations, each quoted beside the sentence it pins, and the words the probes read are each the one code
/// span or phrase. <b>Green once G64 is pasted</b> (the errata is the first thing written): it carries information only through FMBC8 (an obligation dropped from the entry),
/// FMBC10 (the `chose none` clause omitted from the build) and FDVW8 (the window rule dropped from the entry), which edit the paragraphs it reads.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchWordingSpecTests
{
    private static void Holds(string paragraph, params string[] phrases)
    {
        foreach (var phrase in phrases)
            Assert.True(paragraph.Contains(phrase, StringComparison.Ordinal), $"Expected: the paragraph holds \"{phrase}\"  Actual: {paragraph}");
    }

    [Fact]
    public void MBC8_R967StatesTheMarkTheCountsAndTheAbsentCase()
    {
        Holds(
            G64Words.R967(),
            "`matched_by`",
            "`words`, `meaning` or `words_and_meaning`",       // the closed set of three wire words
            "never from a score",                              // derived from ranks, not scores
            "`by_channel`, the three counts over the results it serves",
            "has no term under `LexicalSearch.Tokenize`",      // keyed on terms, not on text (Amendment 1, ruling 3)
            "SHALL carry neither member");
    }

    [Fact]
    public void MBC8_R968StatesTheWindowRuleTheTopUpAndTheCount()
    {
        Holds(
            G64Words.R968(),
            "never to the end of the list",
            "next window of `limit` results in which its author and owner are under the cap",
            "earliest-ranked deferred posts",                  // the top-up
            "a page is as long as `limit` unless it is the last",
            "`diversified`",
            "equal on every page of one query",
            "counted before a disputed post is dropped",
            "if and only if its window in the diversified list is later than its window in the admitted list",
            "`limit` changes which posts a search returns");
    }

    [Fact]
    public void MBC8_R1099StatesTheCountLineTheMarkTheGlossAndTheDiversifiedLine()
    {
        Holds(
            G64Words.R1099(),
            "before the first result, how many results each channel chose",
            "every clause printed and an unserved count read as not stated",
            "SHALL mark each result with its channel",
            "shares a whole word with the query",
            "its relation to the topic is not stated",         // a meaning-only hit
            "SHALL state `diversified`, at 0 as at N",
            "marks tallying differently from the counts it was served SHALL say so");

        // The words the probes read are each the one span or phrase, paired by hand with the obligation they carry.
        Assert.Equal("the vector channel chose none of them:", G64Words.NoVectorClause());
        Assert.Equal("no result shares a word with the query:", G64Words.NoWordClause());
        Assert.Equal("a common word such as", G64Words.CommonWord());
        Assert.Equal("not a cosine", G64Words.NotACosine());
        Assert.Equal("diversified", G64Words.Diversified());
    }

    [Fact]
    public void MBC8_R11103StatesTheMcpBlockItsOrderItsDescriptionAndItsVersion()
    {
        Holds(
            G64Words.R11103(),
            "SHALL state what a client's default form states, from the same definitions",
            "the count line before the served model's limits (R11.93) and those limits whole",
            "SHALL say that `limit` moves membership",
            "not a shorter set",
            $"`curia-mcp` is {PublishedReadersLastWords.Version()}");
    }
}
