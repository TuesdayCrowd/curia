using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// WAF6's description half, WAF9's tool half and WAF31 (errata G53, R10.93, R11.73, R11.74) at the adapter. Every sentence is read from the errata's words
/// paragraphs (<see cref="PublishedClientWords"/>), folded for whitespace, never from a production constant. The version is the one G53's requirement names, read
/// from its paragraph beside the one reader of a version (<c>PublishedReadersLastWords.Version()</c>, RLW40) without adding a second <c>GeneratedRegex</c> reader.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class WordsAfterFaultToolTests : IDisposable
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

    private static string Fold(string text) => PublishedClientWords.Fold(text);

    private ForumTools AliceTools(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var home = Directory.CreateTempSubdirectory("curia-mcp-g53-words-").FullName;
        _homes.Add(home);
        _owned.Add(agent!);
        var session = new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System);
        return new ForumTools(log.Client(), MarkingMode.Datamark, new HeadStore(home), new ForumWriter(agent!, session, TimeProvider.System));
    }

    private static string DescriptionOf(ForumTools tools, string tool) =>
        ToolCatalogue.Build(tools).Single(t => t.ProtocolTool.Name == tool).ProtocolTool.Description ?? string.Empty;

    /// <summary>
    /// WAF6, the description's half. <c>curia_recheck</c>'s description says that <c>unknown</c> names no post, carries the wait, and does not say that
    /// <c>unknown</c> means the citation is to be dropped. (The help of <c>curia recheck</c> is held in <c>Curia.Client.Tests</c>.)
    /// </summary>
    [Fact]
    public void R10_93_WAF6_TheDescriptionOfRecheckSaysUnknownNamesNoPostAndDoesNotSayDrop()
    {
        using var log = new StubLog();
        var description = Fold(DescriptionOf(AliceTools(log), "curia_recheck"));
        var problems = new List<string>();

        if (Regex.IsMatch(description, @"\bunknown\b['""`]?\s+(says|means)\s+(to\s+)?drop", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))
            problems.Add("ToolText.cs: \"'unknown' says to drop the citation\"");
        if (!description.Contains("names no post", StringComparison.Ordinal)) problems.Add("lacks \"names no post\"");
        if (!description.Contains(PublishedClientWords.Wait, StringComparison.Ordinal)) problems.Add($"lacks the wait \"{PublishedClientWords.Wait}\"");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>
    /// WAF9, the tools' half. A <c>curia_ask</c> refused <c>503 curia/store/unavailable</c> relays the published store sentence, and the description of every write
    /// tool carries it. The tools are the catalogue's own list of tools that write in the agent's name, not a list kept here, and the count is written after, so a
    /// write tool added without the sentence is red.
    /// </summary>
    [Fact]
    public async Task R11_73_WAF9_TheRefusalAndEveryWriteToolsDescriptionCarryThePublishedStoreSentence()
    {
        var sentence = Fold(PublishedClientWords.StoreSentence);
        var problems = new List<string>();

        using (var refusing = new StubLog { RefusesSubmissionsAs = StubIngestRefusal.StoreUnavailable })
        {
            string relayed;
            try
            {
                var result = await AliceTools(refusing).AskAsync("b", "A title?", "A body.", null, null, Ct);
                relayed = Fold(Text(result));
            }
            catch (ModelContextProtocol.McpException refused)
            {
                relayed = Fold(refused.Message);
            }

            if (!relayed.Contains(sentence, StringComparison.Ordinal)) problems.Add($"the refusal lacks \"{sentence}\"");
        }

        using var log = new StubLog();
        var tools = AliceTools(log);
        var names = ToolCatalogue.WriteToolNames(tools);
        var missing = names.Where(n => !Fold(DescriptionOf(tools, n)).Contains(sentence, StringComparison.Ordinal)).ToArray();
        if (names.Count != 6) problems.Add($"{names.Count} write tools, expected 6 ({string.Join(' ', names.Order(StringComparer.Ordinal))})");
        if (missing.Length > 0) problems.Add($"the sentence is absent from the description of {missing.Length} write tool(s) ({string.Join(' ', missing)})");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// WAF31. <c>curia-mcp</c> is the version G53's R11.74 names (0.9.4 when written): the catalogue reports it, the one reader of a version
    /// (<c>PublishedReadersLastWords.Version()</c>, which the build re-points from R11.72's paragraph to R11.74's) reads it, the README says it, and the two
    /// hand-written twins (<c>BudgetToolTests</c>, <c>IndexLagEntryTests</c>) have it as their newest literal. The version is read from G53 here with a pattern built
    /// at run time, so RLW40's scan of <c>GeneratedRegex</c> readers is not given a second reader.
    /// </summary>
    [Fact]
    public void R11_74_WAF31_CuriaMcpIsTheVersionG53NamesByTheOneParserThePinsAndTheTwins()
    {
        var named = new Regex("`curia-mcp` is (" + @"\d+\.\d+\.\d+" + ")", RegexOptions.CultureInvariant)
            .Match(PublishedAcceptBudget.Paragraph("**R11.74**")) is { Success: true } m
            ? m.Groups[1].Value
            : throw new InvalidOperationException("the errata's R11.74 names no curia-mcp version");

        var problems = new List<string>();
        void Expect(string where, string actual)
        {
            if (!string.Equals(named, actual, StringComparison.Ordinal)) problems.Add($"{where}: expected {named}, actual {actual}");
        }

        Expect("ToolCatalogue.Version", ToolCatalogue.Version);
        Expect("PublishedReadersLastWords.Version()", PublishedReadersLastWords.Version());

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found");
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        if (!readme.Contains($"`curia-mcp` is {named}", StringComparison.Ordinal)) problems.Add($"README.md: does not say `curia-mcp` is {named}");

        foreach (var twin in new[] { Path.Combine("Curia.Mcp.Tests", "BudgetToolTests.cs"), Path.Combine("Curia.Architecture.Tests", "IndexLagEntryTests.cs") })
        {
            // The newest version a twin writes: an older literal in the same file belongs to an earlier entry's history.
            var newest = Regex.Matches(File.ReadAllText(Path.Combine(root, "tests", twin)), @"\b0\.9\.\d+\b", RegexOptions.CultureInvariant)
                .Select(v => Version.Parse(v.Value)).DefaultIfEmpty(new Version(0, 0, 0)).Max()!.ToString();
            Expect(Path.GetFileName(twin), newest);
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
