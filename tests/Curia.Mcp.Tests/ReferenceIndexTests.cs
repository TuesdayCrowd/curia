using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.48 (errata G34): a refused <c>refs</c> item is named by its zero-based index, as <c>refs[&lt;n&gt;]</c>, for every item refused and
/// in order, and its value is never echoed. The three write tools that take <c>refs</c> are driven alike, because the rule is one rule and
/// a tool that keeps the old text is the defect. The oracle is the list the test built and the recording stub's request list: a refusal
/// before anything is read leaves no request at all.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReferenceIndexTests : IDisposable
{
    /// <summary>A value of none of the three forms, chosen so that no refusal could contain it by accident.</summary>
    private const string BadValue = "ZQ7BAD";

    private static readonly string Digest = "sha256:" + new string('a', 64);

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-g34-refs-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ForumTools Tools()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var session = new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System);
        return new ForumTools(_log.Client(), MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent!, session, TimeProvider.System));
    }

    private Task<CallToolResult> CallAsync(string tool, string[] refs)
    {
        var tools = Tools();
        return tool switch
        {
            "curia_answer" => tools.AnswerAsync(StubLog.PostId, "A body.", refs, Ct),
            "curia_comment" => tools.CommentAsync(StubLog.PostId, "A body.", refs, Ct),
            "curia_revise" => tools.ReviseAsync(StubLog.PostId, "A body.", null, null, refs, Ct),
            _ => throw new InvalidOperationException($"{tool} takes no refs"),
        };
    }

    private static string RefusedText(CallToolResult result)
    {
        Assert.True(result.IsError, "the references were not refused");
        var text = string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        Assert.StartsWith("NOT SENT", text, StringComparison.Ordinal);
        return text;
    }

    [Theory]
    [InlineData("curia_answer", 0)]
    [InlineData("curia_answer", 1)]
    [InlineData("curia_answer", 2)]
    [InlineData("curia_comment", 0)]
    [InlineData("curia_comment", 1)]
    [InlineData("curia_comment", 2)]
    [InlineData("curia_revise", 0)]
    [InlineData("curia_revise", 1)]
    [InlineData("curia_revise", 2)]
    public async Task R11_48_ARefusedReferenceNamesItsIndexAndNeverItsValue(string tool, int bad)
    {
        string[] refs = [Digest, Digest, Digest];
        refs[bad] = BadValue;

        var text = RefusedText(await CallAsync(tool, refs));

        Assert.Contains($"refs[{bad}]", text, StringComparison.Ordinal);
        foreach (var other in Enumerable.Range(0, 3).Where(i => i != bad))
            Assert.DoesNotContain($"refs[{other}]", text, StringComparison.Ordinal);
        Assert.DoesNotContain(BadValue, text, StringComparison.Ordinal);
        Assert.Empty(_log.Requests);
        Assert.Empty(_log.WrittenBodies);
    }

    [Theory]
    [InlineData("curia_answer")]
    [InlineData("curia_comment")]
    [InlineData("curia_revise")]
    public async Task R11_48_EveryFailingIndexIsNamedInOrder(string tool)
    {
        var text = RefusedText(await CallAsync(tool, [BadValue, Digest, BadValue]));

        Assert.Contains("refs[0], refs[2]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("refs[1]", text, StringComparison.Ordinal);
        Assert.DoesNotContain(BadValue, text, StringComparison.Ordinal);
    }
}
