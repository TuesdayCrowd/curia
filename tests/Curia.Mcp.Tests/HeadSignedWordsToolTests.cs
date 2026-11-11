using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// PR H (errata G67, R11.94), HSU28's MCP half: <c>curia_verify</c> over a post newer than the latest signed head says when that head was signed, in the closing and in the
/// inclusion line, from the head's own signed timestamp. The stub signs every head with the timestamp <c>1970-01-01T00:00:00Z</c>, so the instant is known and the age is
/// the wall clock's; the end-to-end words, with a real head and a clock the test owns, are <c>HeadsSignedUnattendedTests</c> in the Api suite.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class HeadSignedWordsToolTests : IDisposable
{
    private readonly StubLog _log = new(treeSize: 12, postIndex: 9);
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-headwords-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"\(tree size 6, signed at 1970-01-01T00:00:00\.000Z, \d+ s before this check began\), so its inclusion could not be checked")]
    private static partial Regex ClosingClause();

    [GeneratedRegex(@"That head was signed at 1970-01-01T00:00:00\.000Z, \d+ s before this check began\.")]
    private static partial Regex InclusionClause();

    private static string Flatten(CallToolResult result) =>
        Regex.Replace(string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text)), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(5));

    /// <summary>
    /// HSU28 (MCP half). A post at leaf 9 under a head of tree size 6: the closing names the head's tree size and, in the same parenthesis, when it was signed; the inclusion line holds the
    /// sentence after its first. Red on the seam: the closing ends <c>(tree size 6), so its inclusion</c>.
    /// </summary>
    [Fact]
    public async Task R11_94_HSU28_CuriaVerifyPastTheHeadSaysWhenTheHeadWasSigned()
    {
        _log.HeadTreeSize = 6;
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home));

        var text = Flatten(await tools.VerifyAsync(StubLog.PostId, null, TestContext.Current.CancellationToken));

        var problems = new List<string>();
        if (!ClosingClause().IsMatch(text)) problems.Add("the closing does not state when the head was signed");
        if (!InclusionClause().IsMatch(text)) problems.Add("the inclusion line does not state when the head was signed");
        Assert.True(problems.Count == 0, string.Join("; ", problems) + " :: " + text);
    }

    private static string Hostile() => string.Concat(Enumerable.Range(0, 72).Select(i => (char)(0x0391 + (i * 5 % 17))));

    /// <summary>
    /// HSU27 (MCP half; Amendment 3 ruling 43 (c)). A head whose signed timestamp is a hostile string (Greek capitals, which the published words do not contain): <c>curia_verify</c> says NOT
    /// STATED in the closing and the inclusion line and no 8-character window of the signed string occurs in its text. Red under FHSU42.
    /// </summary>
    [Fact]
    public async Task R11_94_HSU27_CuriaVerifyEchoesNoWindowOfAHostileSignedString()
    {
        var signed = Hostile();
        _log.HeadTreeSize = 6;
        _log.HeadTimestamp = signed;
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home));

        var text = Flatten(await tools.VerifyAsync(StubLog.PostId, null, TestContext.Current.CancellationToken));

        var echoed = Enumerable.Range(0, signed.Length - 7).Select(i => signed.Substring(i, 8)).FirstOrDefault(w => text.Contains(w, StringComparison.Ordinal));
        var problems = new List<string>();
        if (echoed is not null) problems.Add($"the text holds the window of the signed string that starts \"{echoed}\"");
        if (!text.Contains("signed at NOT STATED", StringComparison.Ordinal)) problems.Add("the text does not say NOT STATED");
        Assert.True(problems.Count == 0, string.Join("; ", problems) + " :: " + text);
    }

    /// <summary>
    /// HSU43 (MCP half; Amendment 3 ruling 43 (d)). The text of <c>curia_verify</c> past the head, for the stub's readable timestamp and for an unreadable one, holds no cadence phrase.
    /// The controls are the matcher's own: three sentences it must catch.
    /// </summary>
    [Fact]
    public async Task R11_94_HSU43_CuriaVerifyStatesNoCadence()
    {
        Assert.Equal([true, true, true], NoCadence.Controls.Select(NoCadence.Says).ToArray());

        var problems = new List<string>();
        foreach (var stamp in new[] { "1970-01-01T00:00:00Z", "yesterday" })
        {
            _log.HeadTreeSize = 6;
            _log.HeadTimestamp = stamp;
            var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home));
            var text = Flatten(await tools.VerifyAsync(StubLog.PostId, null, TestContext.Current.CancellationToken));
            if (NoCadence.Says(text)) problems.Add($"{stamp}: the text states a cadence :: {text}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
