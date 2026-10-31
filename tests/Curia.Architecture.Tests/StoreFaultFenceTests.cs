using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// ULG6, ULG14, ULG33 (errata G52, R11.71, R7.31), as source fences: no path turns a failed read into an empty one; no <c>catch</c> in <c>src</c> classifies a
/// store fault by naming the driver's types outside the one predicate, and <c>Curia.Api</c> names no Npgsql type; and no test hand-copies a sentence the entry
/// publishes. They read source text, which is where an empty fallback and a <c>catch</c> clause are written; each reports the file and line it finds. ULG6 and ULG14
/// are red on the seam tree by assertion (the swallow and the twelve gate clauses are there), ULG33 is a green control.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class StoreFaultFenceTests
{
    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no repository root above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<(string Relative, string[] Lines)> Sources(string root)
    {
        var repo = Repo();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, root), "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}", StringComparison.Ordinal) || file.Contains($"{sep}bin{sep}", StringComparison.Ordinal)) continue;
            yield return (Path.GetRelativePath(repo, file), File.ReadAllLines(file));
        }
    }

    [GeneratedRegex(@"\bRead(?:All|Forward|ByAggregate)Async\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex ReadCall();

    [GeneratedRegex(@"\?\s*[^:;]+:\s*(?:\[\s*\]|Array\.Empty|Enumerable\.Empty|default\b|ImmutableArray<[^>]+>\.Empty)", RegexOptions.CultureInvariant)]
    private static partial Regex EmptyFallback();

    [GeneratedRegex(@"\.Match\s*\(\s*[^,]+,\s*_\s*=>\s*(?:\[\s*\]|Array\.Empty|Enumerable\.Empty|default\b)", RegexOptions.CultureInvariant)]
    private static partial Regex MatchWithEmptyFailure();

    /// <summary>
    /// ULG6. No path turns a failed or faulted read into an empty one: in <c>src/Curia.Api</c> and <c>src/Curia.Application</c>, no result of a
    /// <c>ReadAllAsync</c>, <c>ReadForwardAsync</c> or <c>ReadByAggregateAsync</c> is consumed with a fallback of <c>[]</c>, <c>Array.Empty</c>, an empty
    /// collection or <c>default</c> within the statements that follow the call.
    /// </summary>
    [Fact]
    public void R7_31_ULG6_NoPathTurnsAFailedReadIntoAnEmptyOne()
    {
        var found = new List<string>();
        foreach (var root in new[] { "src/Curia.Api", "src/Curia.Application" })
        {
            foreach (var (relative, lines) in Sources(root))
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!ReadCall().IsMatch(lines[i])) continue;

                    for (var j = i; j < Math.Min(lines.Length, i + 6); j++)
                    {
                        if (EmptyFallback().IsMatch(lines[j]) || MatchWithEmptyFailure().IsMatch(lines[j]))
                        {
                            found.Add($"{relative}:{j + 1} consumes a read with a fallback of []");
                            break;
                        }
                    }
                }
            }
        }

        Assert.True(found.Count == 0, string.Join('\n', found));
    }

    [GeneratedRegex(@"catch\s*\(\s*(?:Npgsql\.)?(?:NpgsqlException|TimeoutException)\b", RegexOptions.CultureInvariant)]
    private static partial Regex DriverCatch();

    [GeneratedRegex(@"catch\s*\(\s*(?:Npgsql\.)?PostgresException\b", RegexOptions.CultureInvariant)]
    private static partial Regex PostgresCatch();

    /// <summary>The two files that catch <c>PostgresException</c> for a reason of their own, each with the reason (R11.71, ruling 20; the plan's ULG14).</summary>
    private static readonly (string File, string Reason)[] PostgresCatchesWithReasons =
    [
        ("PostgresFlagDetailStore.cs", "a unique violation is the domain refusal FlagDetailRules.Exists; its Unavailable arms are moved onto the one predicate by the same PR"),
        ("PostgresVectorIndex.cs", "a vector's SQLSTATE is the index's own refusal, and D92 made the step total"),
    ];

    /// <summary>
    /// ULG14. No <c>catch</c> in <c>src</c> classifies a store fault by naming <c>NpgsqlException</c> or <c>TimeoutException</c> outside
    /// <c>PostgresStoreFaults</c>; the <c>PostgresException</c> catches are the two listed with their reasons and no other; and <c>Curia.Api</c> names no Npgsql
    /// type.
    /// </summary>
    [Fact]
    public void R11_71_ULG14_NoCatchClassifiesAStoreFaultByNamingTheDriversTypesOutsideTheOnePredicate()
    {
        var found = new List<string>();
        foreach (var (relative, lines) in Sources("src"))
        {
            var file = Path.GetFileName(relative);
            if (file == "PostgresStoreFaults.cs") continue;

            for (var i = 0; i < lines.Length; i++)
            {
                if (DriverCatch().IsMatch(lines[i])) found.Add($"{relative}:{i + 1}");
                if (PostgresCatch().IsMatch(lines[i]) && PostgresCatchesWithReasons.All(r => r.File != file)) found.Add($"{relative}:{i + 1} (a PostgresException catch with no reason listed)");
            }
        }

        foreach (var (relative, lines) in Sources("src/Curia.Api"))
        {
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains("Npgsql", StringComparison.Ordinal) && !lines[i].TrimStart().StartsWith("//", StringComparison.Ordinal) && !lines[i].TrimStart().StartsWith("///", StringComparison.Ordinal))
                    found.Add($"{relative}:{i + 1} names an Npgsql type in Curia.Api");
        }

        Assert.True(found.Count == 0, $"{found.Count} catch clause(s) name a driver fault outside PostgresStoreFaults:\n" + string.Join('\n', found.Select((f, n) => n < 12 ? f : string.Empty).Where(f => f.Length > 0))
            + (found.Count > 12 ? $"\n... and {found.Count - 12} more" : string.Empty));
    }

    /// <summary>
    /// ULG33, a control. No test hand-copies a published sentence: the three client sentences, the token's fixed description and the two titles the entry
    /// introduces are written in no test source but the one that reads them from the errata. A sentence a test had written itself would agree with the code it checks.
    /// </summary>
    [Fact]
    public void R11_72_ULG33_NoTestHandCopiesAPublishedSentence()
    {
        string[] published =
        [
            PublishedStoreFaults.LogUnreadableSentence, PublishedStoreFaults.StoreUnavailableSentence, PublishedStoreFaults.TokenSentence,
            PublishedStoreFaults.TokenDescription, PublishedStoreFaults.FaultTitle, PublishedStoreFaults.StoreUnavailableTitle,
        ];

        var copies = new List<string>();
        foreach (var (relative, lines) in Sources("tests"))
        {
            if (Path.GetFileName(relative) == "PublishedStoreFaults.cs") continue;
            var text = string.Join('\n', lines);
            copies.AddRange(published.Where(p => text.Contains(p, StringComparison.Ordinal)).Select(p => $"{relative} writes \"{p[..Math.Min(60, p.Length)]}\""));
        }

        Assert.True(published.All(p => p.Length > 20), "a published sentence was parsed as a bare token");
        Assert.True(copies.Count == 0, string.Join('\n', copies));
    }

    /// <summary>
    /// ULG32, a control (green on the seam tree: errata G52 is applied; red only under FULG30). <c>PublishedStoreFaults</c> reads the entry: the two types and
    /// titles, the route's type held equal to the append's entry, the token's code, status and description, the unclassified fault's type, title and status, the
    /// depth, the event's name and id, and the three client sentences; a paragraph that is absent fails by name.
    /// </summary>
    [Fact]
    public void R11_71_ULG32_ThePublishedWordsAreReadFromTheErrata()
    {
        var problems = new List<string>();
        void Check(string what, bool holds)
        {
            if (!holds) problems.Add(what);
        }

        Check("the two 503 types differ and begin curia/", PublishedStoreFaults.LogUnreadableType != PublishedStoreFaults.StoreUnavailableType && PublishedStoreFaults.LogUnreadableType.StartsWith("curia/", StringComparison.Ordinal));
        Check("the two titles are not empty and differ", PublishedStoreFaults.LogUnreadableTitle.Length > 0 && PublishedStoreFaults.LogUnreadableTitle != PublishedStoreFaults.StoreUnavailableTitle);
        Check("R9.52 names the type R8.82 does", PublishedStoreFaults.RouteType == PublishedStoreFaults.LogUnreadableType);
        Check("the status of each 503 type is 503", PublishedStoreFaults.UnavailableStatus == 503);
        Check("the token's status is 503 and its code is an OAuth code", PublishedStoreFaults.TokenStatus == 503 && PublishedStoreFaults.TokenCode.Length > 0 && !PublishedStoreFaults.TokenCode.Contains('/', StringComparison.Ordinal));
        Check("the token's own-fault code is not its outage code", PublishedStoreFaults.TokenOwnFaultCode != PublishedStoreFaults.TokenCode);
        Check("the unclassified fault is a 500 of its own type", PublishedStoreFaults.FaultStatus == 500 && PublishedStoreFaults.FaultType.StartsWith("curia/", StringComparison.Ordinal) && PublishedStoreFaults.FaultTitle.Length > 0);
        Check("the depth parses from its word", PublishedStoreFaults.Depth == 8);
        Check("the event has a name and an id after 5001", PublishedStoreFaults.EventName.Length > 0 && PublishedStoreFaults.EventId > 5001 && PublishedStoreFaults.ServerFaultEventId == 5000);
        Check("the three sentences are whole sentences that differ", new[] { PublishedStoreFaults.LogUnreadableSentence, PublishedStoreFaults.StoreUnavailableSentence, PublishedStoreFaults.TokenSentence }.Distinct(StringComparer.Ordinal).Count() == 3);

        var absent = Assert.Throws<InvalidOperationException>(() => PublishedStoreFaults.WordsParagraph("R99.99"));
        Check("an absent paragraph fails by name", absent.Message.Contains("R99.99 words", StringComparison.Ordinal));

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
