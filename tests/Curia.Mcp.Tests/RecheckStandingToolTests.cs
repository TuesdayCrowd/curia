using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// PR 3 (errata G47, R10.89), the adapter's half of CHN41 and CHN43: <c>curia_recheck</c> prints the same <c>superseded</c> line <c>curia recheck</c> does
/// (it says <c>still served</c> and not <c>stands</c>, and names a branch without a post id <c>not served</c> with <c>recheck</c> of its digest) and its
/// description defines the words R10.89 requires it to define, which are the code spans of that sentence of the errata, read by
/// <see cref="PublishedChainWords"/>. The CLI's half is <c>SupersededLineWordsTests</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RecheckStandingToolTests : IDisposable
{
    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-g47-recheck-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string TextOf(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private async Task<string> SupersededLineAsync(params Successor[] successors)
    {
        _log.SupersededBy = [.. successors];
        var result = await new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home)).RecheckAsync([_log.Submission.PrefixedDigest], TestContext.Current.CancellationToken);
        return TextOf(result).Split('\n').Single(l => l.StartsWith("superseded", StringComparison.Ordinal));
    }

    /// <summary>CHN41, the adapter's half. The line for a served revision says <c>still served</c> and not <c>stands</c>; for a branch without a post id it says <c>not served</c> and points at <c>recheck</c> of the digest.</summary>
    [Fact]
    public async Task R10_89_CHN41_CuriaRecheckToolPrintsTheSameWordsTheVerbDoes()
    {
        var (say, notSay) = PublishedChainWords.SupersededWords();
        var served = new Successor("sha256:" + new string('b', 64), "01CHNREVISION00000000001A");
        var unserved = new Successor("sha256:" + new string('c', 64), null);

        var one = await SupersededLineAsync(served);
        var fork = await SupersededLineAsync(served, unserved);

        var problems = new List<string>();
        foreach (var (name, line) in new[] { ("one served revision", one), ("a fork with a branch not served", fork) })
        {
            if (!line.Contains(say, StringComparison.Ordinal)) problems.Add($"{name}: lacks \"{say}\"");
            if (Regex.IsMatch(line, $@"\b{Regex.Escape(notSay)}\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))) problems.Add($"{name}: says \"{notSay}\"");
        }

        foreach (var needed in new[] { "not served", PublishedChainWords.RecheckVerb(), "withheld" })
            if (!fork.Contains(needed, StringComparison.Ordinal)) problems.Add($"a fork with a branch not served: lacks \"{needed}\"");
        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Lines: {one} | {fork}");
    }

    /// <summary>CHN43, the adapter's half. <c>curia_recheck</c>'s description defines each word R10.89 names: the word, then <c>means</c>, <c>is</c>, a colon, a dash or an equals sign.</summary>
    [Fact]
    public void R10_89_CHN43_TheDescriptionDefinesTheWordsWhereTheyAreMet()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using var owned = agent;
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        var description = ToolCatalogue.Build(new ForumTools(_log.Client(), MarkingMode.Datamark, new HeadStore(_home), writer))
            .Single(t => t.ProtocolTool.Name == "curia_recheck").ProtocolTool.Description ?? string.Empty;

        var undefined = PublishedChainWords.DefinedWords()
            .Where(w => !Regex.IsMatch(description, Regex.Escape(w) + @"\s*(means|is|:|--|=)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)))
            .ToArray();

        Assert.True(undefined.Length == 0, $"curia_recheck's description does not define {string.Join(", ", undefined.Select(u => $"\"{u}\""))}");
    }
}
