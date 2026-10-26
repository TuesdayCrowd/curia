using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R8.71 (errata G40) from the client's side. The member's name and the four states a receipt tells apart are parsed from the errata
/// (<see cref="PublishedReadersWords"/>), the documents are scripted so the oracle is the JSON each row writes, and the receipts
/// <c>curia resolve</c> prints are built by the fact so the oracle is the state it set.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptanceReplacedReceiptTests
{
    private static readonly string Member = PublishedReadersWords.R871Member();

    private static Result<AcceptanceReceipt> Read(string tail)
    {
        var json = """{"thread_root":"01JTHREAD","post_id":"01JANSWER","accepted_at":"2026-10-24T12:00:00.0000000+00:00","already_accepted":false""" + tail + "}";
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), ClientJson.Limits);
        Assert.True(parsed.TryGetValue(out var value, out var parseError), parseError?.Type);
        return ForumDocuments.ReadAcceptance(Assert.IsType<JsonValue.Object>(value));
    }

    [Fact]
    public void R8_71_TheClientReadsThreeStates()
    {
        Assert.True(Read(string.Empty).TryGetValue(out var absent, out var error), error?.Type);
        Assert.True(Read($",\"{Member}\":null").TryGetValue(out var none, out error), error?.Type);
        Assert.True(Read($",\"{Member}\":\"01JREPLACED\"").TryGetValue(out var named, out error), error?.Type);

        // An absent member is a Forum that said nothing, which is not "none".
        Assert.Equal(AcceptanceReplacedKind.NotStated, absent!.Replaced.Kind);
        Assert.Equal(AcceptanceReplacedKind.None, none!.Replaced.Kind);
        Assert.Equal(AcceptanceReplaced.Of("01JREPLACED"), named!.Replaced);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("7")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("true")]
    public void R8_71_AMemberThatIsNeitherNullNorAnIdIsAMalformedResponse(string value)
    {
        var read = Read($",\"{Member}\":{value}");

        Assert.False(read.TryGetValue(out _, out var error), $"{Member}: {value} was read as a receipt");
        Assert.Equal("curia/client/response-malformed", error!.Type);
        Assert.Contains("R8.71", error.Detail ?? string.Empty, StringComparison.Ordinal);
    }
}

/// <summary>
/// R8.71 at <c>curia resolve</c>: a call that replaced an answer names it and says it no longer stands; a recording call with none says no
/// earlier answer stood; a replay says nothing of replacement and keeps its words byte for byte; a Forum that served no member is
/// <c>NOT STATED</c>. The receipt is built by the fact, so the oracle is the state it set; the phrases are the errata's.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptanceReplacedOutputTests
{
    private const string Thread = "01JTHREAD";
    private const string Answer = "01JANSWER";
    private const string At = "2026-10-24T12:00:00.0000000+00:00";

    private static async Task<string[]> LinesOfAsync(AcceptanceReceipt receipt)
    {
        var (exit, stdout, _) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Curia.Client.Cli.Program.WriteAcceptance(receipt)));

        Assert.Equal(0, exit);
        return [.. stdout.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n')];
    }

    [Fact]
    public async Task R8_71_CuriaResolveWordsEachStateAndEscapesTheId()
    {
        var states = PublishedReadersWords.R871States();
        Assert.Equal(["no longer stands", "no earlier answer stood", "nothing of replacement", "NOT STATED"], states);

        // The replaced id is hostile: a newline that would forge a frame line, and a bidi override.
        const string Hostile = "01JREPLACED\nreplaced   none (forged)\u202E";
        var named = string.Join('\n', await LinesOfAsync(new AcceptanceReceipt(Thread, Answer, At, false, AcceptanceReplaced.Of(Hostile))));
        var none = string.Join('\n', await LinesOfAsync(new AcceptanceReceipt(Thread, Answer, At, false, AcceptanceReplaced.None)));
        var notStated = string.Join('\n', await LinesOfAsync(new AcceptanceReceipt(Thread, Answer, At, false, AcceptanceReplaced.NotStated)));

        Assert.Contains(states[0], named, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(Hostile), named, StringComparison.Ordinal);
        Assert.Single(named.Split('\n'), l => l.StartsWith("replaced", StringComparison.Ordinal));
        Assert.DoesNotContain('\u202E', named);

        Assert.Contains(states[1], none, StringComparison.Ordinal);
        Assert.DoesNotContain(states[0], none, StringComparison.Ordinal);

        Assert.Contains("replaced   " + states[3], notStated, StringComparison.Ordinal);
        Assert.DoesNotContain(states[1], notStated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R8_71_AReplayPrintsNoReplacedLineAndKeepsItsWordsByteForByte()
    {
        var replay = await LinesOfAsync(new AcceptanceReceipt(Thread, Answer, At, true, AcceptanceReplaced.None));

        Assert.Equal(
            [
                $"accepted  {DisplayLiteral.Of(Answer)}   (already the accepted answer; nothing was recorded)",
                $"thread    {DisplayLiteral.Of(Thread)}   first accepted at {DisplayLiteral.Of(At)}",
            ],
            replay);
    }

    /// <summary>Control: a recording call keeps the two lines R8.68 left it, whatever the member says, so the new line is an addition.</summary>
    [Fact]
    public async Task R8_71_ARecordingCallKeepsItsFirstTwoLines()
    {
        foreach (var replaced in new[] { AcceptanceReplaced.None, AcceptanceReplaced.Of("01JOLD"), AcceptanceReplaced.NotStated })
        {
            var lines = await LinesOfAsync(new AcceptanceReceipt(Thread, Answer, At, false, replaced));

            Assert.Equal($"accepted  {DisplayLiteral.Of(Answer)}", lines[0]);
            Assert.Equal($"thread    {DisplayLiteral.Of(Thread)}   at {DisplayLiteral.Of(At)}", lines[1]);
        }
    }
}
