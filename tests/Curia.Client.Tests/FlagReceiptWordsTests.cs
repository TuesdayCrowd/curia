using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 3 (errata G47, R9.49, R10.88), probes CHN35 and CHN36 (the CLI's half) and CHN53 (the flag receipt's part): what the client reads of a flag
/// receipt's <c>post_served</c>, and what <c>curia flag</c> says of it. The member name, the three statements and the three served words are R9.49's and
/// R10.88's code spans, read from the errata by <see cref="PublishedChainWords"/>; the receipts are scripted, so the oracle is the JSON each row writes.
///
/// <para>CHN35 and CHN36 are red by assertion on the unmodified tree because PR 3's Task 0 declared <see cref="FlagReceipt.Served"/> and
/// <see cref="Program.PrintFlagged"/> as inert seams (every receipt reads <see cref="PostServed.NotStated"/>; the note is the old constant).</para>
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagReceiptWordsTests
{
    private static Result<FlagReceipt> Read(string tail)
    {
        var json = """{"post_id":"01JPOST","kind":"incorrect","raised_at":"2026-10-29T12:00:00.0000000+00:00" """ + tail + "}";
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), ClientJson.Limits);
        Assert.True(parsed.TryGetValue(out var value, out var parseError), parseError?.Type);
        return ForumDocuments.ReadFlagReceipt(Assert.IsType<JsonValue.Object>(value));
    }

    /// <summary>CHN35. The member reads in three states: absent is not stated, a boolean is its value; an unknown member beside it is tolerated.</summary>
    [Theory]
    [InlineData("", PostServed.NotStated)]
    [InlineData(",\"post_served\":true", PostServed.Served)]
    [InlineData(",\"post_served\":false", PostServed.NotServed)]
    [InlineData(",\"extra_probe\":{\"a\":1}", PostServed.NotStated)]
    [InlineData(",\"extra_probe\":1,\"post_served\":false", PostServed.NotServed)]
    public void R10_88_CHN35_TheClientReadsThreeStates(string tail, PostServed expected)
    {
        Assert.True(Read(tail).TryGetValue(out var receipt, out var error), error?.Type);

        Assert.Equal(expected, receipt!.Served);
    }

    /// <summary>CHN35. A member that is not a boolean is a malformed receipt, and an absent one is never read as <c>false</c>.</summary>
    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void R10_88_CHN35_AMemberThatIsNoBooleanIsAMalformedReceipt(string value)
    {
        var read = Read($",\"{PublishedChainWords.ServedMember()}\":{value}");

        Assert.False(read.TryGetValue(out var receipt, out var error), $"post_served: {value} was read as a receipt that is {receipt?.Served}");
        Assert.Equal("curia/client/response-malformed", error!.Type);
    }

    private static async Task<string> FlagNoteAsync(PostServed served)
    {
        var (exit, stdout, _) = await ConsoleCapture.CaptureAsync(
            () => Task.FromResult(Program.PrintFlagged(new FlagReceipt("01JPOST", "incorrect", "2026-10-29T12:00:00.0000000+00:00", served))));
        Assert.Equal(0, exit);
        return stdout;
    }

    /// <summary>
    /// CHN36 (the CLI's half). <c>curia flag</c> states R10.88's three things: visible only to the raiser and the author, where the raiser reads it
    /// (<c>curia flags</c>), that no outcome of a review is shown, and the Forum's word on whether the post is served, in each of the three states.
    /// </summary>
    [Theory]
    [InlineData(PostServed.Served)]
    [InlineData(PostServed.NotServed)]
    [InlineData(PostServed.NotStated)]
    public async Task R10_88_CHN36_CuriaFlagStatesTheThreeThings(PostServed served)
    {
        var (visibility, noOutcome, where) = PublishedChainWords.FlagStatements();
        var words = PublishedChainWords.ServedWords();
        var word = served switch
        {
            PostServed.Served => words.Served,
            PostServed.NotServed => words.NotServed,
            PostServed.NotStated => words.NotStated,
            _ => throw new ArgumentOutOfRangeException(nameof(served), served, "not a state"),
        };

        var note = await FlagNoteAsync(served);

        var missing = new[] { visibility, noOutcome, where, word }.Where(w => !note.Contains(w, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"curia flag ({served}) lacks: {string.Join("; ", missing.Select(m => $"\"{m}\""))}. Actual: {note}");
    }

    /// <summary>
    /// CHN36. The word for one state is not a word for another: a receipt that served <c>true</c> does not say the post is not served, and an older
    /// Forum's receipt says <c>NOT STATED</c> and neither of the others.
    /// </summary>
    [Fact]
    public async Task R10_88_CHN36_TheServedWordIsOnlyForItsState()
    {
        var words = PublishedChainWords.ServedWords();
        var served = await FlagNoteAsync(PostServed.Served);
        var notServed = await FlagNoteAsync(PostServed.NotServed);
        var notStated = await FlagNoteAsync(PostServed.NotStated);

        // The expected word is present first (otherwise the negatives below are silence passing for a statement).
        Assert.True(served.Contains(words.Served, StringComparison.Ordinal), $"Expected: \"{words.Served}\" Actual: {served}");
        Assert.DoesNotContain(words.NotServed, served, StringComparison.Ordinal);
        Assert.DoesNotContain(words.Served, notServed.Replace(words.NotServed, string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain(words.Served, notStated, StringComparison.Ordinal);
        Assert.DoesNotContain(words.NotServed, notStated, StringComparison.Ordinal);
    }

    /// <summary>CHN53, the receipt's part (a control, green now). A member the receipt does not know changes nothing that <c>curia flag</c> prints.</summary>
    [Fact]
    public async Task R10_88_CHN53_AnUnknownMemberOfTheReceiptIsToleratedAndPrintsNothing()
    {
        Assert.True(Read(string.Empty).TryGetValue(out var plain, out var e1), e1?.Type);
        Assert.True(Read(",\"extra_probe\":{\"nested\":[1,2]}").TryGetValue(out var extra, out var e2), e2?.Type);

        var (_, withoutMember, _) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.PrintFlagged(plain!)));
        var (_, withMember, _) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Program.PrintFlagged(extra!)));

        Assert.Equal(withoutMember, withMember);
        Assert.DoesNotContain("extra_probe", withMember, StringComparison.Ordinal);
    }
}
