using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.85 (errata G61), RPB30 and RPB31, at the adapter: the reply tools take the board of the post they read, so a reply to a post stored on a board the grammar refuses
/// (served as <c>Meta</c>) is signed and sent with that board, the withdrawn R11.78 sentence is printed nowhere, the descriptions say the board is read from the post and the
/// version is the one R11.85 names. The protocol is real (<see cref="McpProtocolSession"/>) over the stub Forum (<see cref="StubLog"/>), which records the bytes it was sent.
///
/// <para><b>Oracles.</b> What was sent is the stub's account: the envelope parsed from the bytes it received, not the tool's report. The version, and the withdrawn sentence, are
/// parsed from the errata (<see cref="PublishedReplyBoardWords"/>, <see cref="PublishedBoardWords"/>).</para>
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ReplyBoardToolTests : IDisposable
{
    private readonly StubLog _log = new() { ServedBoard = "Meta" };
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-replyboard-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    private async Task<McpProtocolSession> StartAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
        return await McpProtocolSession.StartAsync(tools, writer.Agent.Profile.AgentId, agent);
    }

    private static Dictionary<string, object?> Arguments(string tool) => tool switch
    {
        "curia_answer" => new() { ["parentPostId"] = StubLog.PostId, ["body"] = "An answer." },
        "curia_comment" => new() { ["parentPostId"] = StubLog.PostId, ["body"] = "A comment." },
        "curia_revise" => new() { ["postId"] = StubLog.PostId, ["body"] = "A corrected body." },
        _ => throw new ArgumentOutOfRangeException(nameof(tool)),
    };

    private static void None(List<string> problems, string expected)
    {
        if (problems.Count > 0)
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {string.Join("; ", problems)}");
    }

    public static TheoryData<string> Tools() => new() { "curia_answer", "curia_comment", "curia_revise" };

    /// <summary>The boards of the envelopes the stub was sent, one per distinct act, read from the bytes it received.</summary>
    private List<string> BoardsSent() =>
        [.. _log.WrittenBodies.Where(ReceivedAct.IsSubmission)
            .GroupBy(ReceivedAct.DigestOf)
            .Select(g => JsonDocument.Parse(g.First()).RootElement.GetProperty("envelope").GetProperty("board").GetString()!)];

    /// <summary>
    /// RPB30. A reply to a post served on the board <c>Meta</c> (stored before R8.85, a board the grammar refuses for a new post) is signed and sent with that board: the stub
    /// received one act whose envelope board is <c>Meta</c>, and the result is not an error. Predicted red: <c>NOT SENT</c>, the client's refusal and the reply sentence, nothing sent.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tools))]
    public async Task R11_85_RPB30_AReplyToAPostOnAStoredBoardIsSentWithThatBoard(string tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        await using var session = await StartAsync();

        var result = await session.CallAsync(tool, Arguments(tool));

        var sent = BoardsSent();
        var problems = new List<string>();
        if (result.IsError) problems.Add($"the tool answered an error: {Fold(result.Text)}");
        if (sent.Count != 1) problems.Add($"{sent.Count} act(s) were sent, expected 1");
        else if (sent[0] != "Meta") problems.Add($"the envelope's board is \"{sent[0]}\", expected \"Meta\"");
        None(problems, $"{tool}: not an error, one act whose board is \"Meta\", the post's");
    }

    /// <summary>
    /// RPB31. The three tools' descriptions say the board is read from the post and declare no <c>board</c> argument; the sentence the reply tools gave a refused board (R11.78's, withdrawn
    /// by R11.85) is printed in no result and no description; and the catalogue's version is the one R11.85 names. Predicted red: the withdrawn sentence is printed in the result for
    /// <c>Meta</c>, and the version is 0.10.2. Amendment 3 (ruling 37): each description must carry its own span, parsed from R11.85 words (help and descriptions), and not merely the word
    /// <c>board</c>; the descriptions are unchanged by this PR, so the span is green on the tree it was added to.
    /// </summary>
    [Fact]
    public async Task R11_85_RPB31_TheDescriptionsSayTheBoardIsThePostsAndTheWithdrawnSentenceIsPrintedNowhere()
    {
        await using var session = await StartAsync();
        var problems = new List<string>();
        var withdrawn = Fold(PublishedBoardWords.ReplySentenceFor(StubLog.PostId));
        var tools = (await session.ListAsync()).ToDictionary(t => t.Name, StringComparer.Ordinal);

        foreach (var tool in new[] { "curia_answer", "curia_comment", "curia_revise" })
        {
            var description = Fold(tools[tool].Description ?? string.Empty);
            var span = Fold(PublishedReplyBoardWords.ToolSpan(tool));
            if (!description.Contains(span, StringComparison.Ordinal)) problems.Add($"{tool}: the description lacks the published span \"{span}\"");
            if (tools[tool].InputSchema.GetProperty("properties").TryGetProperty("board", out _)) problems.Add($"{tool}: declares a board argument");
            if (description.Contains(withdrawn, StringComparison.Ordinal)) problems.Add($"{tool}: the description carries the withdrawn sentence");

            var result = await session.CallAsync(tool, Arguments(tool));
            if (Fold(result.Text).Contains(withdrawn, StringComparison.Ordinal)) problems.Add($"{tool}: a result prints the withdrawn sentence");
        }

        if (!PublishedReplyBoardWords.RequirementNamesMcpVersion(ToolCatalogue.Version))
            problems.Add($"the catalogue's version is {ToolCatalogue.Version}, and R11.85 does not name it");
        None(problems, "descriptions that say the board is the post's, the withdrawn sentence printed nowhere, and the version R11.85 names");
    }
}
