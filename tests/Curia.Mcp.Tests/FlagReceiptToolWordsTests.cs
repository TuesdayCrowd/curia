using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// PR 3 (errata G47, R9.49, R10.88), the adapter's half of CHN36 and CHN38, and CHN37 and CHN53: <c>curia_flag</c>'s receipt states what R10.88 requires
/// (visible only to the raiser and the author, where the raiser reads it, that no outcome is shown, and the Forum's word on whether the post is served in
/// each of the three states), in the same sentences <c>curia flag</c> prints (both are held to the errata's spans, so they are held to each other); the
/// claim that <c>curia-mcp</c> has no tool that reads flags is true of the tool list the server registers, which is enumerated; and a member the receipt or
/// a post does not know changes nothing the adapter prints. The spans are read from the errata by <see cref="PublishedChainWords"/>; the receipt is served by
/// the stub's <see cref="StubLog.FlagReceiptServedJson"/> knob, so the oracle is the JSON each row wrote.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagReceiptToolWordsTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly List<string> _homes = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private ForumTools AliceTools(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var home = Directory.CreateTempSubdirectory("curia-mcp-g47-flag-").FullName;
        _homes.Add(home);
        _owned.Add(agent!);
        var session = new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System);
        return new ForumTools(log.Client(), MarkingMode.None, new HeadStore(home), new ForumWriter(agent!, session, TimeProvider.System));
    }

    private async Task<string> FlagReceiptAsync(string? servedJson)
    {
        using var log = new StubLog { FlagReceiptServedJson = servedJson };
        var result = await AliceTools(log).FlagAsync(StubLog.PostId, "incorrect", "It is wrong.", Ct);
        return Text(result);
    }

    /// <summary>CHN36 and CHN38, the adapter's half. The receipt states R10.88's three things, in the errata's own spans, for each state of the member.</summary>
    [Theory]
    [InlineData("true", "served")]
    [InlineData("false", "not-served")]
    [InlineData(null, "not-stated")]
    public async Task R10_88_CHN36_CuriaFlagToolStatesTheThreeThings(string? member, string state)
    {
        var (visibility, noOutcome, where) = PublishedChainWords.FlagStatements();
        var words = PublishedChainWords.ServedWords();
        var word = state switch
        {
            "served" => words.Served,
            "not-served" => words.NotServed,
            _ => words.NotStated,
        };

        var receipt = await FlagReceiptAsync(member);

        var missing = new[] { visibility, noOutcome, where, word }.Where(w => !receipt.Contains(w, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"curia_flag ({state}) lacks: {string.Join("; ", missing.Select(m => $"\"{m}\""))}. Actual: {receipt}");
    }

    /// <summary>CHN36. A member that is no boolean is a malformed receipt, so the tool refuses and does not say the post is not served.</summary>
    [Theory]
    [InlineData("\"yes\"")]
    [InlineData("null")]
    [InlineData("1")]
    public async Task R10_88_CHN36_AMemberThatIsNoBooleanIsRefusedByTheTool(string member)
    {
        string outcome;
        try
        {
            outcome = (await FlagReceiptAsync(member)).Contains(PublishedChainWords.ServedWords().NotServed, StringComparison.Ordinal) ? "it says the post is not served" : "a receipt";
        }
        catch (ModelContextProtocol.McpException)
        {
            outcome = "refused";
        }

        Assert.True(outcome == "refused", $"Expected: a malformed receipt, refused Actual: {outcome}");
    }

    /// <summary>
    /// CHN37. The receipt's claim about the tool list is true of the tool list: the registered tools are enumerated, and while none but <c>curia_flag</c>
    /// has <c>flag</c> in its name the receipt says <c>curia-mcp</c> has no tool that reads flags; a tool that does would make the claim false, and the
    /// receipt must then not make it.
    /// </summary>
    [Fact]
    public async Task R10_88_CHN37_TheReceiptsClaimAboutTheToolListIsTrue()
    {
        using var log = new StubLog();
        var tools = ToolCatalogue.Build(AliceTools(log)).Select(t => t.ProtocolTool.Name).ToArray();
        Assert.Contains("curia_flag", tools);
        var readers = tools.Where(n => n != "curia_flag" && n.Contains("flag", StringComparison.OrdinalIgnoreCase)).ToArray();

        var receipt = await FlagReceiptAsync("true");

        var claimsNone = receipt.Contains("no tool", StringComparison.OrdinalIgnoreCase);
        Assert.True(
            claimsNone == (readers.Length == 0),
            $"the tool list holds {readers.Length} tool(s) that read flags ({string.Join(", ", readers)}); the receipt {(claimsNone ? "says" : "does not say")} curia-mcp has none. Actual: {receipt}");
    }

    /// <summary>CHN53, the adapter's half (a control, green now). A member the post, its provenance or the receipt does not know changes nothing <c>curia_read</c> and <c>curia_flag</c> print.</summary>
    [Fact]
    public async Task R10_88_CHN53_AnUnknownMemberChangesNothingTheAdapterPrints()
    {
        using var plainLog = new StubLog();
        using var extraLog = new StubLog { ThreadRouteServesTheThread = false };
        var node = JsonNode.Parse(extraLog.PostJson())!.AsObject();
        node["extra_probe"] = new JsonObject { ["nested"] = new JsonArray(1) };
        node["provenance"]!.AsObject()["extra_probe"] = "x";
        extraLog.ServedPosts[StubLog.PostId] = node.ToJsonString();
        extraLog.ServedThreads[StubLog.PostId] = "[" + node.ToJsonString() + "]";
        plainLog.ServedThreads[StubLog.PostId] = "[" + plainLog.PostJson() + "]";

        var plain = Text(await AliceTools(plainLog).ReadAsync(StubLog.PostId, Ct));
        var extra = Text(await AliceTools(extraLog).ReadAsync(StubLog.PostId, Ct));

        Assert.Equal(plain, extra);
        Assert.DoesNotContain("extra_probe", extra, StringComparison.Ordinal);
    }
}
