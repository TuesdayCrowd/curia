using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R8.68 (errata G33) from the client's side: <c>already_accepted</c> is kept when the Forum serves it, an absent member reads
/// as <c>false</c> (an older Forum always recorded), and a present member that is no boolean is a malformed response naming the
/// requirement, not a value the client has chosen. The document is scripted, so the oracle is the JSON each row writes.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptanceReceiptTests
{
    private static Result<AcceptanceReceipt> Read(string tail)
    {
        var json = """{"thread_root":"01JTHREAD","post_id":"01JANSWER","accepted_at":"2026-10-24T12:00:00.0000000+00:00" """.TrimEnd() + tail + "}";
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), ClientJson.Limits);
        Assert.True(parsed.TryGetValue(out var value, out var parseError), parseError?.Type);
        return ForumDocuments.ReadAcceptance(Assert.IsType<JsonValue.Object>(value));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData(""","already_accepted":false""", false)]
    [InlineData(""","already_accepted":true""", true)]
    public void R8_68_AReaderDistinguishesAnAbsentMemberFromFalse(string tail, bool expected)
    {
        Assert.True(Read(tail).TryGetValue(out var receipt, out var error), error?.Type);
        Assert.Equal(expected, receipt!.AlreadyAccepted);
    }

    [Theory]
    [InlineData(""","already_accepted":"yes" """)]
    [InlineData(""","already_accepted":1""")]
    [InlineData(""","already_accepted":null""")]
    public void R8_68_AMemberThatIsNoBooleanIsAMalformedResponse(string tail)
    {
        var read = Read(tail);

        Assert.False(read.TryGetValue(out _, out var error), "a non-boolean already_accepted was read as a receipt");
        Assert.Equal("curia/client/response-malformed", error!.Type);
        Assert.Contains("R8.68", error.Detail ?? string.Empty, StringComparison.Ordinal);
    }
}

/// <summary>
/// R8.68 at <c>curia resolve</c>: a receipt for a replay does not say an acceptance was recorded, and says the time is the first
/// one; a fresh acceptance keeps its words. The receipt is built by the fact, so the oracle is the flag it sets.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptanceReceiptOutputTests
{
    private static async Task<string> OutputOfAsync(bool alreadyAccepted)
    {
        var receipt = new AcceptanceReceipt("01JTHREAD", "01JANSWER", "2026-10-24T12:00:00.0000000+00:00", alreadyAccepted);
        var (exit, stdout, _) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Curia.Client.Cli.Program.WriteAcceptance(receipt)));

        Assert.Equal(0, exit);
        return stdout;
    }

    [Fact]
    public async Task R8_68_AReplayReceiptDoesNotSayRecorded()
    {
        var fresh = await OutputOfAsync(alreadyAccepted: false);
        var replay = await OutputOfAsync(alreadyAccepted: true);

        Assert.Contains("accepted  " + DisplayLiteral.Of("01JANSWER"), fresh, StringComparison.Ordinal);
        Assert.DoesNotContain("already the accepted answer", fresh, StringComparison.Ordinal);

        Assert.Contains("already the accepted answer; nothing was recorded", replay, StringComparison.Ordinal);
        Assert.Contains("first accepted at", replay, StringComparison.Ordinal);
        Assert.Contains("2026-10-24T12:00:00.0000000+00:00", replay, StringComparison.Ordinal);
    }
}
