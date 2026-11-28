using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests.ReadCost;

/// <summary>
/// RC14 (errata G87): the matrix is the errata's. The published read sets are parsed from the errata at test time, as Table 10 is from the white paper, and compared with the Forum's own endpoint table: a
/// route with no row fails the build and never denies (vacuity question 3), and a row naming no route is stale. The classes are asserted by hand with the sentence beside them (the criteria's structure is not
/// parsed): a route classed A reads its caller's own rows, so no row of it may be a read of the log whole.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed partial class ReadSetsTableTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static readonly string[] Classes = ["A", "B", "C", "W", "A+B", "A+C", "-"];

    // The vocabulary a class A read set is written in: the notation's own terms, and the words that join them.
    private static readonly string[] Vocabulary = ["S(a)", "K", "P(a)", "M(P)", "R", "G", "A(a)", "F", "D(p)"];

    [GeneratedRegex(@"^\|\s*`(?<route>[^`]+)`\s*\|\s*(?<class>[A-Z+\-]+)\s*\|\s*(?<set>.*?)\s*\|\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex Row();

    private static string RepositoryFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln")))
            dir = dir.Parent;

        return dir is null ? throw new InvalidOperationException("Curia.sln not found") : Path.Combine(dir.FullName, relative);
    }

    private static Dictionary<string, (string Class, string Set)> Published()
    {
        var text = File.ReadAllText(RepositoryFile("curia-whitepaper-ERRATA-AND-ADDENDUM.md"));
        var start = text.IndexOf("### Published read sets (classes A, B, C and W)", StringComparison.Ordinal);
        Assert.True(start >= 0, "the errata has no published read sets (G87)");
        var end = text.IndexOf("### Non-properties", start, StringComparison.Ordinal);
        var rows = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach (var line in text[start..end].Split('\n'))
            if (Row().Match(line.TrimEnd()) is { Success: true } m)
                rows[m.Groups["route"].Value] = (m.Groups["class"].Value, m.Groups["set"].Value);

        return rows;
    }

    [Fact]
    public void RC14_EveryRouteOfTheEndpointTableHasARowInTheErrataAndEveryRowARoute()
    {
        var published = Published();
        var routes = forum.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"]).Select(m => $"{m} {e.RoutePattern.RawText}"))
            .Where(r => r.StartsWith("GET /", StringComparison.Ordinal) || r.StartsWith("POST /", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // The positive control: the table has the routes the endpoint table is known to hold, so an empty parse or an empty enumeration is red and not quiet.
        Assert.True(routes.Length >= 25, $"the endpoint table holds {routes.Length} routes");
        Assert.True(published.Count >= 25, $"the errata publishes {published.Count} rows");

        var missing = routes.Where(r => !published.ContainsKey(r)).ToArray();
        var stale = published.Keys.Where(r => !routes.Contains(r)).ToArray();
        Assert.True(missing.Length == 0, "routes with no published read set: " + string.Join(", ", missing));
        Assert.True(stale.Length == 0, "published rows naming no route: " + string.Join(", ", stale));
    }

    [Fact]
    public void RC14_AClassARowIsWrittenInTheNotationAndNamesNoWholeRead()
    {
        var problems = new List<string>();
        foreach (var (route, (cls, set)) in Published())
        {
            if (!Classes.Contains(cls)) problems.Add($"{route}: class {cls} is none of {string.Join(", ", Classes)}");
            if (cls != "A") continue;

            // Every term of the read set is one of the notation's, so a class A route says which rows it reads; "the log, whole" is no class A read.
            if (set.Contains("whole", StringComparison.OrdinalIgnoreCase)) problems.Add($"{route}: class A names a whole read: {set}");
            if (!Vocabulary.Any(v => set.Contains('`' + v + '`', StringComparison.Ordinal))) problems.Add($"{route}: class A names no term of the notation: {set}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
