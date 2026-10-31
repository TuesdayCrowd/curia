using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// Errata G51 (R11.70) at the adapter (the D92 PR): PIL27 the receipt's four lines in the tools, PIL28 a pending post is not an error and is not resent,
/// PIL29 the first block's <c>vector_pending</c> member, PIL30 the sentence in the descriptions and PIL31 the version. The words are the entry's
/// (<see cref="PublishedIndexLag"/>); a printed line is compared whitespace-folded, as the entry says; the documents are served by the stub's
/// <c>ReceiptVectorIndexJson</c> and <c>SearchVectorPendingJson</c> knobs. The CLI's half is <c>IndexWordsTests</c> in <c>Curia.Client.Tests</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IndexWordsToolTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-g51-").FullName;

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Fold(string s) => PublishedIndexLag.Fold(s);

    private ForumTools ToolsOver(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var writer = new ForumWriter(agent!, new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System), TimeProvider.System);
        return new ForumTools(log.Client(), MarkingMode.None, new HeadStore(_home), writer);
    }

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>The four tools whose receipt is a post's: the ones that print <c>WriteTools.Posted</c>.</summary>
    private static readonly string[] PostingTools = ["curia_ask", "curia_answer", "curia_comment", "curia_revise"];

    private static async Task<CallToolResult> CallAsync(ForumTools tools, string tool) => tool switch
    {
        "curia_ask" => await tools.AskAsync("b", "A title?", "A body.", null, null, Ct),
        "curia_answer" => await tools.AnswerAsync(StubLog.PostId, "An answer.", null, Ct),
        "curia_comment" => await tools.CommentAsync(StubLog.PostId, "A comment.", null, Ct),
        "curia_revise" => await tools.ReviseAsync(StubLog.PostId, "A corrected body.", null, null, null, Ct),
        _ => throw new InvalidOperationException($"no call for {tool}"),
    };

    /// <summary>One tool call against a stub whose receipts carry <paramref name="vectorIndexJson"/>, and the POSTs of posts the stub saw.</summary>
    private async Task<(CallToolResult Result, int Posts)> CallServedAsync(string tool, string? vectorIndexJson)
    {
        using var log = new StubLog();
        log.ReceiptVectorIndexJson = vectorIndexJson;
        var result = await CallAsync(ToolsOver(log), tool);
        return (result, log.Requests.Count(r => r == "POST /v1/posts"));
    }

    private static string? LineLabelled(CallToolResult result, string label) =>
        Text(result).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(l => l.StartsWith(label + "  ", StringComparison.Ordinal))
            .Select(Fold)
            .FirstOrDefault();

    /// <summary>
    /// PIL27, the tools' half. For each tool that sends a post and each of the four receipt states, the receipt's <c>search</c> line is the entry's, with
    /// <c>NOT STATED</c> for a receipt that said nothing and never <c>by words and by meaning</c>.
    /// </summary>
    [Fact]
    public async Task R11_70_PIL27_EachToolPrintsTheReceiptsLineInThePublishedWords()
    {
        var (indexed, pending, notApplicable) = PublishedIndexLag.Values();
        var rows = new (string? Json, string Words)[]
        {
            ($"\"{indexed}\"", PublishedIndexLag.ReceiptLine(indexed)),
            ($"\"{pending}\"", PublishedIndexLag.ReceiptLine(pending)),
            ($"\"{notApplicable}\"", PublishedIndexLag.ReceiptLine(notApplicable)),
            (null, PublishedIndexLag.NotStatedLine()),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var tool in PostingTools)
            foreach (var (json, words) in rows)
            {
                var (result, _) = await CallServedAsync(tool, json);
                expected.Add(Fold($"{tool} {json ?? "absent"}: \"{words}\""));
                actual.Add(Fold($"{tool} {json ?? "absent"}: \"{LineLabelled(result, "search") ?? "no search line"}\""));
            }

        Said(string.Join("; ", expected), string.Join("; ", actual));
    }

    /// <summary>
    /// PIL28, the tools' half. Against a Forum that answers <c>201</c> with <c>pending</c>, each tool's result is not an error (the post is stored), no more POSTs of the post were sent than for an indexed receipt, and the text carries the line that says not to send it again. The result kind and the POST count are controls; the line is
    /// the red.
    /// </summary>
    [Fact]
    public async Task R11_70_PIL28_APendingReceiptIsNotAnErrorAndTheToolSendsOnce()
    {
        var pending = PublishedIndexLag.Values().Pending;
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var tool in PostingTools)
        {
            // The baseline is the same call against a receipt that says indexed: a client sends the post once (and again after a DPoP nonce challenge, which
            // the stub counts as a second POST), so a pending receipt that makes it send more is a resend.
            var (_, baseline) = await CallServedAsync(tool, $"\"{PublishedIndexLag.Values().Indexed}\"");
            var (result, posts) = await CallServedAsync(tool, $"\"{pending}\"");
            expected.Add(Fold($"{tool}: not an error; {baseline} POSTs as for an indexed receipt; \"{PublishedIndexLag.ReceiptLine(pending)}\""));
            actual.Add(Fold($"{tool}: {(result.IsError == true ? "an error" : "not an error")}; {posts} POSTs as for an indexed receipt; \"{LineLabelled(result, "search") ?? "no search line"}\""));
        }

        Said(string.Join("; ", expected), string.Join("; ", actual));
    }

    /// <summary>PIL29, the tool's half. <c>curia_search</c>'s first block carries <c>vector_pending=&lt;n&gt;</c> for a count (0 and a positive one) and <c>vector_pending=not stated</c> where the Forum served none.</summary>
    [Fact]
    public async Task R11_70_PIL29_TheSearchToolsFirstBlockStatesTheCount()
    {
        var expected = new[] { PublishedIndexLag.ToolCountLine(0), PublishedIndexLag.ToolCountLine(7), PublishedIndexLag.ToolNotStatedLine() };
        var actual = new List<string>();
        foreach (var json in new string?[] { "0", "7", null })
        {
            using var log = new StubLog();
            log.SearchVectorPendingJson = json;
            var result = await new ForumTools(log.Client(), MarkingMode.None, new HeadStore(_home)).SearchAsync(new SearchCriteria(), Ct);
            var first = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
            var member = first.Split([' ', '\n', ';'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(w => w.StartsWith("vector_pending=", StringComparison.Ordinal));
            actual.Add(member is null ? "no vector_pending member" : member == "vector_pending=not" ? "vector_pending=not stated" : member);
        }

        Said(string.Join(" | ", expected), string.Join(" | ", actual));
    }

    /// <summary>
    /// PIL30, the tools' half. The descriptions of the tools that send a post and of <c>curia_search</c> carry the entry's sentence, parsed from the entry.
    /// (The entry says "the five write tools"; four tools print a post's receipt, so the fact holds those four and records the count in its message.)
    /// </summary>
    [Fact]
    public void R11_70_PIL30_TheDescriptionsOfTheSendingToolsAndOfSearchCarryTheSentence()
    {
        using var log = new StubLog();
        var catalogue = ToolCatalogue.Build(ToolsOver(log)).ToDictionary(t => t.ProtocolTool.Name, t => Fold(t.ProtocolTool.Description ?? string.Empty), StringComparer.Ordinal);
        var sentence = PublishedIndexLag.Sentence();
        string[] tools = [.. PostingTools, "curia_search"];
        var missing = tools.Where(t => !catalogue.TryGetValue(t, out var d) || !d.Contains(sentence, StringComparison.Ordinal)).ToArray();

        Said(
            $"the sentence is in the description of {tools.Length} tools ({string.Join(' ', tools)})",
            missing.Length == 0
                ? $"the sentence is in the description of {tools.Length} tools ({string.Join(' ', tools)})"
                : $"the sentence is absent from the description of {missing.Length} tools ({string.Join(' ', missing)})");
    }

    /// <summary>
    /// PIL31. The version: the one parser of a version out of an errata paragraph, <c>PublishedReadersLastWords.Version()</c>, reads R11.70's (it is re-pointed
    /// from R11.66's in the commit that bumps), the catalogue reports it, and the two places the README states it say it. The six facts that read the parser and
    /// the hand-written twin move in that commit; this fact is the version's own.
    /// </summary>
    [Fact]
    public void R11_70_PIL31_TheVersionIsR1170sThroughTheOneParserAndTheReadmeSaysIt()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var readme = File.ReadAllText(Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found"), "README.md"));
        var version = PublishedReadersLastWords.G51Version();

        var expected = $"the parser reads {version}; the catalogue reports {version}; the README says `curia-mcp` is {version} and `curia-mcp` {version}.";
        var actual = $"the parser reads {PublishedReadersLastWords.Version()}; the catalogue reports {ToolCatalogue.Version}; the README says "
            + $"{(readme.Contains($"`curia-mcp` is {version}", StringComparison.Ordinal) ? $"`curia-mcp` is {version}" : "not `curia-mcp` is " + version)} and "
            + $"{(readme.Contains($"`curia-mcp` {version}.", StringComparison.Ordinal) ? $"`curia-mcp` {version}." : "not `curia-mcp` " + version + ".")}";
        Said(expected, actual);
    }
}
