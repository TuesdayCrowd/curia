using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// INX8 (NDT13's and MBC8's form): R9.69 to R9.71, R10.100 and R10.101 (errata G76) parsed from the errata carry the obligations the probes pin, each quoted beside the sentence it pins, and the words
/// the probes read are each the one code span or phrase, paired by hand. <b>Green once G76 is pasted</b> (it is): it carries information only through FSPEC1 (R9.69's last sentence deleted from a patched
/// copy) and the rows that edit the paragraphs it reads. Whitespace in a paragraph is folded to one space by <see cref="PublishedAcceptBudget.Paragraph(string)"/>, so a code span written with two spaces
/// (<c>answers  2 served</c>) is read with one.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AnswersSpecTests
{
    private static void Holds(string paragraph, params string[] phrases)
    {
        foreach (var phrase in phrases)
            Assert.True(paragraph.Contains(phrase, StringComparison.Ordinal), $"Expected: the paragraph holds \"{phrase}\"  Actual: {paragraph}");
    }

    /// <summary>
    /// The anchor of G76's third arm is unique (ruling 26): <c>**R11.30 (rev. 2)**</c> begins exactly one paragraph. <see cref="PublishedAcceptBudget.Paragraph(string)"/> takes the first match and
    /// would stay green if a later entry reused the anchor, so this holds the count and the filter helpers INX7 and INX8 first carried are not needed.
    /// </summary>
    [Fact]
    public void INX8_TheAnchorOfTheThirdArmBeginsExactlyOneParagraph()
    {
        var starting = PublishedAcceptBudget.ReadErrata().Split("\n\n", StringSplitOptions.None).Count(p => p.StartsWith("**R11.30 (rev. 2)**", StringComparison.Ordinal));

        Assert.Equal(1, starting);
    }

    [Fact]
    public void INX8_R969StatesTheCountItsOneFunctionAndWhatIsNotCounted()
    {
        Holds(
            PublishedAcceptBudget.Paragraph("**R9.69**"),
            "`provenance.answers`",
            "`{\"served\": <n>}`",
            "a non-negative integer",
            "SHALL be made by the one function that lists a duplicate refusal's answers",   // ANC2
            "A revision of an answer, a comment and an answer the Forum withholds SHALL NOT be counted",   // ANC1
            "No post of another kind SHALL carry the member",                                // ANC3
            "this entry adds no validator rule",                                             // ANC4 (ruling 29: R9.11 owns the validator; the member only changes the bytes it is taken from)
            "it counts posts and says nothing of whether any answer is correct or responsive");
    }

    [Fact]
    public void INX8_R970StatesTheRootAndNotTheParentOrThePrev()
    {
        Holds(
            PublishedAcceptBudget.Paragraph("**R9.70**"),
            "`provenance.revises`",
            "`{\"post_id\": \"<id>\", \"kind\": \"question\"}`",
            "naming the chain's root",                                                       // RVT1
            "SHALL NOT be computed from the revision's own `parent`",                        // RVT1
            "SHALL NOT name the revision's `prev` where that is another revision",           // RVT1, the two-hop row
            "No other post SHALL carry the member");                                         // RVT2
    }

    [Fact]
    public void INX8_R971StatesTheInstantTheTwoRelationsAndTheLogOrder()
    {
        Holds(
            PublishedAcceptBudget.Paragraph("**R9.71**"),
            "SHALL carry `accepted_at`",                                                      // ACA2
            "written as the receipt of that acceptance writes it",                            // ACA2, byte for byte
            "`\"before_acceptance\"` or `\"after_acceptance\"`",                              // ACA3's closed set
            "the first revision in log order",                                                // ACA3's fork
            "`provenance.question_revised`, which SHALL NOT be absent",                                  // ACA5's null
            "\"answer_posted\": \"before\" | \"after\"",                                      // ACA5's closed set
            "SHALL be decided by the order of the two events in the log",                     // ACA1
            "SHALL NOT be decided by comparing two instants");                                // ACA1
    }

    [Fact]
    public void INX8_R10100StatesTheLineTheCountTheOrderAndThePointer()
    {
        var paragraph = PublishedAcceptBudget.Paragraph("**R10.100**");
        Holds(
            paragraph,
            "`answers 2 served, none accepted`",                                              // ANC8's example (one space: whitespace is folded)
            "before the acceptance clause",                                                   // ANC8's order
            "writing a count the Forum did not serve as `NOT STATED` and never as 0",         // ANC8
            "posts the Forum serves and not of good answers",                                 // ANC8's gloss
            "SHALL say \"since revised\" only of `after_acceptance`",                         // ACA4
            "the asker accepted a text its author had already revised",                       // ACA4
            "naming a command that reads that thread, with the id written as a shell word",   // RVT4
            "where `post_id` is absent it SHALL say the question is not served",              // RVT3
            "SHALL NOT claim a relevance",                                                    // ACA6
            "SHALL write every value the Forum supplied as a display literal",                // RVT4, G17
            "SHALL refuse as malformed a member that is on a post of another kind",           // ANC7
            "a relation word outside its closed set");                                        // ACA4, ACA6

        // The words the probes read are each the one phrase, paired by hand with the obligation they carry.
        Assert.Equal("posts the Forum serves", G64Words.Phrase(paragraph, "posts the Forum serves"));
        Assert.Equal("already revised", G64Words.Phrase(paragraph, "already revised"));
    }

    [Fact]
    public void INX8_R10101StatesWhatChoseTheRowsAndTheRowCount()
    {
        Holds(
            PublishedAcceptBudget.Paragraph("**R10.101**"),
            "SHALL state what chose its rows and in what order, and that it ranks nothing",    // INX5
            "on each row that is a question, R10.100's count",                                // INX2
            "that the Forum did not serve as `not stated` and never as 0");                   // INX9
    }

    [Fact]
    public void INX8_R11104AndTheThirdArmStateTheToolAndTheAdmission()
    {
        Holds(
            PublishedAcceptBudget.Paragraph("**R11.104**"),
            "`curia_inbox` when an agent identity is configured and not otherwise",           // INX3
            "taking `board`, `tags`, `limit` and `cursor`",                                   // INX3
            "outside every post's frame",                                                     // INX5
            "a question's block SHALL be the titled form of R10.71 and not its body",         // INX5 (ruling 17)
            "SHALL still be listed, without a title and saying why",                          // INX5's control
            "SHALL NOT be composed from a Table 10 span that says no credential is required", // INX4
            "SHALL say that a count of answers is not a filter",                              // INX4
            "the server instructions SHALL name it");                                         // INX5
        Holds(
            PublishedAcceptBudget.Paragraph("**R11.30 (rev. 2)**"),
            "a read whose result is defined by what the caller has already done",
            "which a model with no memory between sessions cannot supply to any other read",
            "`curia_inbox` is admitted on that arm and on no other");
    }
}
