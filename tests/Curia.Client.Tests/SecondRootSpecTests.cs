using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// SRS20 (NDT13's and MBC8's form): errata G80's three paragraphs (R4.48, R6.54 (add.), R6.64 (rev.)) parsed from the errata carry the phrases that say a later
/// <c>agent.enrolled</c> of a stream is skipped, <c>broken-link</c>. <b>Green once G80 is pasted</b> (it is): it carries information only through FSPEC2 to FSPEC4
/// (each phrase altered in a patched copy of the errata, the code unchanged). Whitespace is folded to one space.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SecondRootSpecTests
{
    private static void Holds(string paragraph, params string[] phrases)
    {
        foreach (var phrase in phrases)
            Assert.True(paragraph.Contains(phrase, StringComparison.Ordinal), $"Expected: the paragraph holds \"{phrase}\"  Actual: {paragraph}");
    }

    private static string[] Starting(string anchor) =>
        [.. PublishedAcceptBudget.ReadErrata().Split("\n\n", StringSplitOptions.None).Where(p => p.StartsWith(anchor, StringComparison.Ordinal))];

    /// <summary>
    /// The paragraph that begins with the anchor. Not <see cref="PublishedAcceptBudget.Paragraph(string)"/>: its first match is the first occurrence anywhere, and R6.64 (add.)
    /// mentions "**R6.54 (add.)**" mid-paragraph before R6.54 (add.)'s own paragraph.
    /// </summary>
    private static string Paragraph(string anchor) => string.Join(' ', Starting(anchor).Single().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Each anchor begins exactly one paragraph.</summary>
    [Theory]
    [InlineData("**R4.48**")]
    [InlineData("**R6.54 (add.)**")]
    [InlineData("**R6.64 (rev.)**")]
    public void SRS20_TheAnchorBeginsExactlyOneParagraph(string anchor)
    {
        Assert.Single(Starting(anchor));
    }

    [Fact]
    public void SRS20_R448SkipsARowThatIsNotTheFirstEnrolmentOfItsStream()
    {
        Holds(
            Paragraph("**R4.48**"),
            "one that is not the first `agent.enrolled` of its stream");                                                   // FSPEC2
    }

    [Fact]
    public void SRS20_R654AddFailsALaterEnrolmentBrokenLink()
    {
        Holds(
            Paragraph("**R6.54 (add.)**"),
            "at an `agent.enrolled` entry that is not its chain's first, `broken-link`");                                  // FSPEC3
    }

    [Fact]
    public void SRS20_R664RevSkipsALaterRootBrokenLink()
    {
        Holds(
            Paragraph("**R6.64 (rev.)**"),
            "and then skipped, `broken-link`");                                                                            // FSPEC4
    }
}
