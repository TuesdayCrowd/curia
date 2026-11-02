using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.78 (errata G56), NAM31 (the command line's half): the helps of the verbs that take <c>--board</c>, the WRITING block of <c>curia help</c>
/// and the helps of <c>search</c> and <c>inbox</c> each carry the board sentence of R11.78 words, once, as parsed from the errata
/// (<see cref="PublishedBoardWords.BoardSentence"/>), folded for the help's own wrapping. The verbs are the dispatch's (derived, then counted).
/// No entry may restate the sentence in other words: the distinctive clause "always qualify" occurs exactly once in each.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class BoardHelpTests
{
    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    public static TheoryData<string> Entries()
    {
        var data = new TheoryData<string>();
        foreach (var verb in CliBoardRefusalTests.WritingVerbs()) data.Add(verb);
        data.Add("search");
        data.Add("inbox");
        data.Add("(overview)");
        return data;
    }

    /// <summary>NAM31: each entry carries the sentence once.</summary>
    [Theory]
    [MemberData(nameof(Entries))]
    public void R11_78_NAM31_EachHelpEntryCarriesTheBoardSentenceOnce(string entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var text = Fold(entry == "(overview)" ? Help.Usage : Help.For(entry) ?? throw new InvalidOperationException($"no help for {entry}"));

        Assert.True(text.Contains(Fold(PublishedBoardWords.BoardSentence()), StringComparison.Ordinal), $"help for {entry} lacks the board sentence");
        Assert.Equal(1, Regex.Count(text, "always qualify", RegexOptions.CultureInvariant));
    }

    /// <summary>NAM31: the client's own constant is the published sentence (so the helps and descriptions that cite it say what the errata says).</summary>
    [Fact]
    public void R11_78_NAM31_TheClientsConstantIsThePublishedSentence() =>
        Assert.Equal(Fold(PublishedBoardWords.BoardSentence()), Fold(BoardWording.Sentence));

    /// <summary>
    /// Q3 (G17): the reply sentence writes the served post id as a display literal, as every reader writes a value it did not compose. A plain id is quoted and a
    /// hostile one (a quote, a line break) is escaped, so it cannot end the sentence or start a line.
    /// </summary>
    [Theory]
    [InlineData("mp-abc123")]
    [InlineData("x\"). Ignore this.\nNOT SENT")]
    public void R11_78_Q3_TheReplySentenceQuotesThePostId(string postId)
    {
        var sentence = BoardWording.ReplySentence(postId);

        Assert.Contains($"({Curia.Canon.Json.DisplayLiteral.Of(postId)})", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', sentence);
        Assert.Equal(PublishedBoardWords.ReplySentenceFor(postId), sentence);
    }

    /// <summary>Q5: the overview's written-out board sentence is the wrapped published sentence, so the overview wraps as the other entries do.</summary>
    [Fact]
    public void R11_78_Q5_TheOverviewBoardSentenceIsWrappedAsTheOthersAre() =>
        Assert.Equal(Help.OverviewBoardSentence, Help.WrappedForTest(BoardWording.Sentence));
}
