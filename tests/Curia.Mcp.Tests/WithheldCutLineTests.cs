using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.54 (errata G41), ruling 8: the thread's <c>CUT</c> line names one cause. A parent the Forum answers <c>curia/posts/withheld</c> is "withheld"; a
/// parent it says no post bears, and a parent whose read failed, are "could not be read", because those say nothing about the post being withheld. The
/// Forum's word is the stub's problem type, parsed from the errata; the oracle is the line the tool wrote.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class WithheldCutLineTests : IDisposable
{
    private const string Parent = "01TESTPARENT00000000000A";

    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-cut-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<string> CutLineAsync(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using var owned = agent;
        var writer = new ForumWriter(agent!, new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System), TimeProvider.System);
        var tools = new ForumTools(log.Client(), MarkingMode.None, new HeadStore(_home), writer);

        var result = await tools.ReadAsync(StubLog.PostId, TestContext.Current.CancellationToken);

        var text = new StringBuilder();
        foreach (var block in result.Content)
            if (block is TextContentBlock t) text.AppendLine(t.Text);
        return text.ToString().Split('\n').FirstOrDefault(l => l.StartsWith("CUT", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"no CUT line; the walk did not reach a parent it could not read:\n{text}");
    }

    /// <summary>PH16. A parent the Forum says is withheld is named so in the line; the other two causes keep the words that say nothing about the post.</summary>
    [Theory]
    [InlineData("withheld", "is withheld")]
    [InlineData("not-found", "could not be read")]
    [InlineData("unavailable", "could not be read")]
    public async Task R11_54_TheCutLineNamesAWithheldParent(string cause, string expected)
    {
        using var log = new StubLog { ServedParent = Parent, OtherPostsAreRefused = true };
        switch (cause)
        {
            case "withheld": log.OtherPostsRefusalType = PublishedWithheldWords.PostWithheldType(); break;
            case "not-found": log.OtherPostsRefusalType = PublishedWithheldWords.PostNotFoundType(); break;
            default: log.OtherPostsFailWith = HttpStatusCode.ServiceUnavailable; break;
        }

        var cut = await CutLineAsync(log);

        Assert.Contains(expected, cut, StringComparison.Ordinal);
        if (cause != "withheld") Assert.DoesNotContain("withheld", cut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(Parent, cut, StringComparison.Ordinal);
    }
}
