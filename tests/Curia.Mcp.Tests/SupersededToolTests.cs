using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// Errata G32 at the adapter: <c>curia_read</c>, <c>curia_search</c> and <c>curia_verify</c> print the Forum's word on a post's supersession (R10.80,
/// R11.44), and <c>curia_revise</c>'s description, which says the original is "reported superseded", names the line that reports it. <see cref="StubLog"/>
/// serves the member when it is set (<see cref="StubLog.SupersededBy"/>), and not at all for a Forum that predates it (<see cref="StubLog.OmitSupersededBy"/>).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SupersededToolTests : IDisposable
{
    private static readonly string Revision = "sha256:" + new string('7', 64);
    private const string RevisionId = "01REVISIONID00000000000001";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-superseded-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ForumTools Tools() => new(_log.Client(), MarkingMode.None, new HeadStore(_home));

    private static string[] Passages(CallToolResult result)
    {
        var passages = result.Content.OfType<EmbeddedResourceBlock>().Select(b => ((TextResourceContents)b.Resource).Text).ToArray();
        Assert.NotEmpty(passages);
        return passages;
    }

    private static string Text(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    [Fact]
    public async Task R10_80_CuriaReadAndCuriaSearchPrintTheLineAndNotStatedWhereNoneIsServed()
    {
        var ct = TestContext.Current.CancellationToken;

        // The Forum names a revision: every passage of both tools holds the line, naming it.
        _log.SupersededBy = [new Successor(Revision, RevisionId)];
        var read = Passages(await Tools().ReadAsync(StubLog.PostId, ct));
        var search = Passages(await Tools().SearchAsync(new SearchCriteria(), ct));
        foreach (var passage in read.Concat(search))
        {
            Assert.Contains("revision  SUPERSEDED", passage, StringComparison.Ordinal);
            Assert.Contains(Revision, passage, StringComparison.Ordinal);
            Assert.Contains(RevisionId, passage, StringComparison.Ordinal);
        }

        // A Forum that does not serve the member: every passage says so, and none reads as current.
        _log.OmitSupersededBy = true;
        foreach (var passage in Passages(await Tools().ReadAsync(StubLog.PostId, ct)).Concat(Passages(await Tools().SearchAsync(new SearchCriteria(), ct))))
            Assert.Contains("revision  NOT STATED", passage, StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.29 and R11.44: a verification is about the document a read in the session served, so its line is the Forum's word as of that read and says so.
    /// The read is of a post nothing supersedes; the stub then gains a revision; the verification still says none reported, as of the read; a fresh read is told.
    /// </summary>
    [Fact]
    public async Task R11_44_CuriaVerifyStatesItAsOfTheReadItVerifies()
    {
        var ct = TestContext.Current.CancellationToken;
        var tools = Tools();
        _log.SupersededBy = [];

        Assert.NotEmpty(Passages(await tools.ReadAsync(StubLog.PostId, ct)));
        var baseline = await tools.VerifyAsync(StubLog.PostId, null, ct);
        _log.SupersededBy = [new Successor(Revision, RevisionId)];

        var verified = await tools.VerifyAsync(StubLog.PostId, null, ct);
        var text = Text(verified);
        Assert.False(verified.IsError == true, text);
        var line = text.Split('\n').SingleOrDefault(l => l.StartsWith("revision    ", StringComparison.Ordinal));
        Assert.True(line is not null && line.Contains("none reported", StringComparison.Ordinal) && line.Contains("as of the read", StringComparison.Ordinal), $"the verification of a post read as unsuperseded printed no 'revision    none reported ... as of the read' line:\n{text}");
        Assert.DoesNotContain(Revision, text, StringComparison.Ordinal);

        // The closing word and the error flag are the baseline's: the line is not a check.
        Assert.Equal(ClosingWord(Text(baseline)), ClosingWord(text));
        Assert.Equal(baseline.IsError, verified.IsError);

        // A fresh read is told.
        Assert.Contains("revision  SUPERSEDED", string.Join('\n', Passages(await Tools().ReadAsync(StubLog.PostId, ct))), StringComparison.Ordinal);
    }

    private static string ClosingWord(string text) =>
        // The verdict's own paragraph: R6.59's not-checked sentence follows it on a first verification and is not the verdict.
        text.Split('\n').Last(l => l.Length > 0 && !l.StartsWith("Consistency was not checked", StringComparison.Ordinal)).Split(' ')[0];

    private static List<(string Name, string Description)> Registered()
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        var heads = new HeadStore(Path.Combine(Path.GetTempPath(), "curia-tool-descriptions-never-written"));

        using var log = new StubLog();
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        try
        {
            var writer = new ForumWriter(agent!, new ForumSession(client, agent!, log.Store, TimeProvider.System), TimeProvider.System);
            return [.. ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, heads, writer))
                .Select(tool => (tool.ProtocolTool.Name, tool.ProtocolTool.Description ?? string.Empty))];
        }
        finally
        {
            agent!.Dispose();
        }
    }

    public static TheoryData<string> Tools_() => ["curia_revise", "curia_read", "curia_search", "curia_verify"];

    [Theory]
    [MemberData(nameof(Tools_))]
    public void R11_44_ADescriptionThatSaysSupersededNamesTheLineThatReportsIt(string tool)
    {
        var registered = Registered();
        Assert.NotEmpty(registered);
        var description = registered.Single(t => t.Name == tool).Description;
        Assert.NotEmpty(description);

        Assert.True(
            description.Contains("`revision` line", StringComparison.Ordinal),
            $"{tool}'s description does not name the `revision` line that reports a supersession:\n{description}");
    }
}
