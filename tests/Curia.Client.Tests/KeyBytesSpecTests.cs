using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// KBO17 (NDT13's and MBC8's form): R4.45 (errata G78) parsed from the errata carries the obligations the key-bytes probes pin, each quoted beside the sentence it pins. <b>Green once G78 is pasted</b> (it is): it
/// carries information only through FSPEC4 (R4.45's standings weakened to "live" in a patched copy) and the rows that edit this paragraph. Whitespace is folded to one space by
/// <see cref="PublishedAcceptBudget.Paragraph(string)"/>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyBytesSpecTests
{
    private static void Holds(string paragraph, params string[] phrases)
    {
        foreach (var phrase in phrases)
            Assert.True(paragraph.Contains(phrase, StringComparison.Ordinal), $"Expected: the paragraph holds \"{phrase}\"  Actual: {paragraph}");
    }

    /// <summary>The anchor begins exactly one paragraph, so <see cref="PublishedAcceptBudget.Paragraph(string)"/>'s first match is the only one.</summary>
    [Fact]
    public void KBO17_TheAnchorOfR445BeginsExactlyOneParagraph()
    {
        var starting = PublishedAcceptBudget.ReadErrata().Split("\n\n", StringSplitOptions.None).Count(p => p.StartsWith("**R4.45**", StringComparison.Ordinal));

        Assert.Equal(1, starting);
    }

    [Fact]
    public void KBO17_R445RefusesAndSkipsARotationNamingBoundBytesInAnyStandingUnderAnyKid()
    {
        Holds(
            PublishedAcceptBudget.Paragraph("**R4.45**"),
            "A rotation SHALL be refused by name, and a rotation row SHALL NOT be honoured",                                  // the route and every fold
            "the public key its statement names (its algorithm and every coordinate, never its `kid`)",                      // the comparison is the key, not the label
            "of any key the honoured prefix of the identity's stream has bound (R6.64)",                                     // the set is the honoured prefix
            "under any `kid`",
            "whether that key is live, retired, declared compromised or compromised by cascade (R6.57)");                   // every standing (FSPEC4)
    }

    [Fact]
    public void KBO17_R445NamesTheRefusalItsPlaceInTheOrderAndWhatARefusalLeaves()
    {
        Holds(
            PublishedAcceptBudget.Paragraph("**R4.45**"),
            "`curia/keys/key-already-bound`, status `409`",
            "whose `detail` names no `kid`, no key and no statement",
            "after the refusal for a `kid` already named (R4.18 (revised))",
            "before the rotation's caps (R7.23) and before the key is registered",
            "so a refused rotation leaves the key store and the log as they were",
            "a retry of the same `kid` and the same bytes is still refused as a `kid` already named");
    }

    [Fact]
    public void KBO17_R445SaysAKeyBoundOnceIsBoundOnceAndGivesTheReason()
    {
        Holds(
            PublishedAcceptBudget.Paragraph("**R4.45**"),
            "A key an identity has bound once is bound once: a retirement or a declaration ends a key, not a name, and the key is had again only by making a new one",
            "The reason: a rule that compares labels guards a label; the key a declaration was written to stop is the bytes");
    }
}
