using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R8.71 (errata G40) at <c>curia_resolve</c>'s receipt: a call that replaced an answer names it and says it no longer stands; a recording call with
/// none says no earlier answer stood; a replay says nothing of replacement; a Forum that served no member is NOT STATED. The stub serves each of the
/// four states as its knob says, so the oracle is what the fact set; the phrases are the errata's.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ResolveReceiptTests : IDisposable
{
    private const string Replaced = "01TESTREPLACED000000000000A";
    private const string ClosingSentence = "accepting another replaces this one";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-resolve-receipt-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<string> ReceiptAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var session = new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System);
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), new ForumWriter(agent!, session, TimeProvider.System));

        var result = await tools.ResolveAsync(StubLog.AnswerPostId, TestContext.Current.CancellationToken);
        var builder = new StringBuilder();
        foreach (var block in result.Content.OfType<TextContentBlock>()) builder.AppendLine(block.Text);
        return builder.ToString();
    }

    private static string[] Lines(string text) => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    [GeneratedRegex(@"\breplaced\b", RegexOptions.CultureInvariant)]
    private static partial Regex ReplacedWord();

    [Fact]
    public async Task R8_71_CuriaResolveToolWordsEachState()
    {
        var states = PublishedReadersWords.R871States();

        _log.AcceptanceAlreadyAccepted = false;
        _log.AcceptanceReplacedAnswerJson = $"\"{Replaced}\"";
        var named = await ReceiptAsync();

        _log.AcceptanceReplacedAnswerJson = "null";
        var none = await ReceiptAsync();

        _log.AcceptanceReplacedAnswerJson = null;
        var notStated = await ReceiptAsync();

        _log.AcceptanceAlreadyAccepted = true;
        _log.AcceptanceReplacedAnswerJson = "null";
        var replay = await ReceiptAsync();

        // Replaced: the displaced answer is named as the Forum named it, and the receipt says it no longer stands.
        Assert.Contains(states[0], named, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(Replaced), named, StringComparison.Ordinal);
        Assert.Contains(Lines(named), l => l.StartsWith("replaced", StringComparison.Ordinal));

        // None on a recording call: no earlier answer stood.
        Assert.Contains(states[1], none, StringComparison.Ordinal);
        Assert.DoesNotContain(states[0], none, StringComparison.Ordinal);

        // A Forum that served no member is NOT STATED, which is not none.
        Assert.Contains("replaced", notStated, StringComparison.Ordinal);
        Assert.Contains(states[3], notStated, StringComparison.Ordinal);
        Assert.DoesNotContain(states[1], notStated, StringComparison.Ordinal);

        // A replay says nothing of replacement: it already says nothing was recorded.
        Assert.Contains("nothing was recorded", replay, StringComparison.Ordinal);
        Assert.DoesNotContain(Lines(replay), l => l.StartsWith("replaced", StringComparison.Ordinal));
        Assert.False(ReplacedWord().IsMatch(replay), $"a replay receipt spoke of replacement:\n{replay}");
        Assert.DoesNotContain(states[1], replay, StringComparison.Ordinal);
    }

    /// <summary>
    /// The control the main fact leans on: the general closing sentence (R8.68's "accepting another replaces this one") is said once on a recording
    /// call, so a receipt that gained words for the replaced answer and repeated it would be a regression this fact names.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("\"01TESTREPLACED000000000000A\"")]
    public async Task R8_71_TheClosingSentenceIsSaidOnceOnARecordingCall(string? member)
    {
        _log.AcceptanceAlreadyAccepted = false;
        _log.AcceptanceReplacedAnswerJson = member;

        var text = await ReceiptAsync();

        Assert.Equal(1, Regex.Count(text, Regex.Escape(ClosingSentence), RegexOptions.CultureInvariant));
    }
}
