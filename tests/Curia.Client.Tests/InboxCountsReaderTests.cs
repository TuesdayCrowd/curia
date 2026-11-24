using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R10.101 (errata G76), probes INX9 and INX1 (the words), and ANC2's CLI leg (ruling 22). <b>Oracles.</b> The JSON each row writes (the inbox page's members and the refusal document), the account
/// line's hand-written words (the INX1 control, whose literals are the words on the tree: the CLI's <c>Summary</c> and the empty-inbox sentences, which Task 4 moves to <c>Curia.Client</c> and must not edit),
/// and the hand count of the answers the refusal's document lists.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class InboxCountsReaderTests : IDisposable
{
    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Curia.Canon.Json.JsonValue.Object Parse(string json)
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), ClientJson.Limits);
        Assert.True(parsed.TryGetValue(out var value, out var error), error?.Type);
        return Assert.IsType<Curia.Canon.Json.JsonValue.Object>(value);
    }

    private static string Page(string members) => "{\"results\":[]" + (members.Length == 0 ? string.Empty : "," + members) + "}";

    // ---- INX1: the words the CLI inbox already has (a control; green on the tree) -------------------------------------------------------------------------------------------

    /// <summary>
    /// INX1. The account line, byte for byte, for a page that states all three counts: <c>5 open before exclusions, of which 1 yours and 2 already answered by you; 0 listed.</c> These literals are the
    /// words on the tree. Task 4 moves the definition to <c>Curia.Client</c> (<c>InboxWording</c>) and this probe stays green <b>unedited</b>: it is a control for the words and says nothing of the counts (ruling 18).
    /// </summary>
    [Fact]
    public void R10_101_INX1_Control_TheAccountLineIsWordForWord()
    {
        var read = ForumDocuments.ReadInbox(Parse(Page("\"open_before_exclusions\":5,\"excluded_as_own\":1,\"excluded_as_already_answered\":2")));

        Assert.True(read.TryGetValue(out var page, out var error), error?.Type);
        Assert.Equal("5 open before exclusions, of which 1 yours and 2 already answered by you; 0 listed.", Program.Summary(page!).ToString());
    }

    /// <summary>
    /// INX1: the empty-inbox sentences, word for word: <c>no open questions match these filters.</c> (the Forum's word that a filter named nothing, a board it did not list, as the unmatched list says),
    /// and with it <c>Nothing here needs an answer -- look elsewhere.</c> where every filter matched.
    /// </summary>
    [Fact]
    public void R10_101_INX1_Control_TheEmptyInboxSentencesAreWordForWord()
    {
        Assert.Equal("no open questions match these filters.", NameWording.InboxEmptySentence("b", null));
        Assert.Null(NameWording.InboxEmptySentence("nosuchboard", ["board"]));
        Assert.Equal("no open questions match these filters. Nothing here needs an answer -- look elsewhere.", NameWording.InboxEmptySentence("b", []));
    }

    // ---- INX9 ---------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static readonly string[] Members = ["open_before_exclusions", "excluded_as_own", "excluded_as_already_answered"];

    /// <summary>INX9, the account line where a count is absent: each of the three counts the Forum did not serve reads <c>not stated</c> in its place and never <c>0</c> (R10.101). Red on the tree (prints 0).</summary>
    [Theory]
    [InlineData("open_before_exclusions")]
    [InlineData("excluded_as_own")]
    [InlineData("excluded_as_already_answered")]
    public void R10_101_INX9_AnAbsentCountIsNotStatedAndNeverZero(string absent)
    {
        var served = string.Join(",", Members.Where(m => m != absent).Select(m => $"\"{m}\":4"));
        var read = ForumDocuments.ReadInbox(Parse(Page(served)));

        Assert.True(read.TryGetValue(out var page, out var error), error?.Type);
        var line = Program.Summary(page!).ToString();
        Assert.Contains("not stated", line, StringComparison.Ordinal);
        Assert.Contains("4", line, StringComparison.Ordinal);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(line, "not stated"));
    }

    /// <summary>INX9, no count at all: the account line states all three as not stated.</summary>
    [Fact]
    public void R10_101_INX9_NoCountsAtAllReadsNotStatedThreeTimes()
    {
        var read = ForumDocuments.ReadInbox(Parse(Page(string.Empty)));

        Assert.True(read.TryGetValue(out var page, out _));
        Assert.Equal(3, System.Text.RegularExpressions.Regex.Count(Program.Summary(page!).ToString(), "not stated"));
    }

    /// <summary>INX9, the valid rows (green on the tree): <c>0</c> and <c>7</c> are read as themselves.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("7")]
    public void R10_101_INX9_Control_AStatedCountIsPrintedAsItself(string count)
    {
        var read = ForumDocuments.ReadInbox(Parse(Page($"\"open_before_exclusions\":{count},\"excluded_as_own\":0,\"excluded_as_already_answered\":0")));

        Assert.True(read.TryGetValue(out var page, out _));
        Assert.StartsWith($"{count} open before exclusions", Program.Summary(page!).ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// INX9 (ruling 18): a count present and not a non-negative integer that fits an <c>int</c> is a malformed response naming the member: a string, <c>null</c>, a boolean, an object, a negative, a value above
    /// <c>int.MaxValue</c>. Never coerced to 0 and never read as absent. Red on the tree (each is accepted: a string, null, a boolean and an object read as 0, a negative is kept, a large value wraps).
    /// </summary>
    [Theory]
    [InlineData("open_before_exclusions", "\"7\"")]
    [InlineData("open_before_exclusions", "null")]
    [InlineData("open_before_exclusions", "true")]
    [InlineData("open_before_exclusions", "{}")]
    [InlineData("open_before_exclusions", "-3")]
    [InlineData("open_before_exclusions", "2147483648")]
    [InlineData("excluded_as_own", "\"7\"")]
    [InlineData("excluded_as_own", "-3")]
    [InlineData("excluded_as_own", "2147483648")]
    [InlineData("excluded_as_already_answered", "\"7\"")]
    [InlineData("excluded_as_already_answered", "-3")]
    [InlineData("excluded_as_already_answered", "2147483648")]
    public void R10_101_INX9_ACountThatIsNotANonNegativeIntegerThatFitsIsMalformed(string member, string value)
    {
        var read = ForumDocuments.ReadInbox(Parse(Page($"\"{member}\":{value}")));

        Assert.False(read.TryGetValue(out _, out _), $"Expected: {member}={value} refused as malformed Actual: the page was read");
    }

    // ---- ANC2's CLI leg (ruling 22) --------------------------------------------------------------------------------------------------------------------------------------

    private static string Frame(JsonObject document, StubLog log)
    {
        var refusal = new Refusal(RefusalKind.Conflict, 409, new Curia.Domain.Primitives.Error("curia/posts/duplicate-question", "duplicate"), Parse(document.ToJsonString()));
        var duplicate = refusal.AsDuplicate;
        Assert.NotNull(duplicate);
        return Output.DuplicateFrame(duplicate, []);
    }

    /// <summary>
    /// ANC2, the CLI leg. The refusal's line names the same number as the question's count and says <c>served</c> beside it: two answers (the hand count: the document lists two) print
    /// <c>answers      2 served</c>, an empty list prints <c>answers      0 served -- read the thread: ...</c>, so a reader sees one noun and one unit across the refusal and the page it points at. Red on the
    /// tree (the refusal prints a bare number, and <c>none yet</c> for none).
    /// </summary>
    [Fact]
    public void R9_69_ANC2_TheRefusalLineSaysServedBesideTheSameNumber()
    {
        var two = JsonNode.Parse(_log.DuplicateRefusalJson())!.AsObject();
        two["answers"]!.AsArray().Add(JsonNode.Parse(two["answers"]![0]!.ToJsonString()));
        two["detail"] = "cosine=0.920 lexical_overlap=0.740 model=hashed-ngram@1 answers=2";
        var none = JsonNode.Parse(_log.DuplicateRefusalJson())!.AsObject();
        none["answers"] = new JsonArray();
        none["detail"] = "cosine=0.920 lexical_overlap=0.740 model=hashed-ngram@1 answers=0";

        var withTwo = Frame(two, _log).Split('\n').Single(l => l.StartsWith("answers", StringComparison.Ordinal));
        var withNone = Frame(none, _log).Split('\n').Single(l => l.StartsWith("answers", StringComparison.Ordinal));

        Assert.StartsWith("answers      2 served", withTwo, StringComparison.Ordinal);
        Assert.StartsWith("answers      0 served -- read the thread: ", withNone, StringComparison.Ordinal);
    }
}
