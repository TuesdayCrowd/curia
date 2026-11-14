using System.Text.Json;
using Xunit;

namespace Curia.Canon.Tests.Vectors;

/// <summary>
/// R6.45 for <c>conformance/operator-acts/</c> (errata G70, R6.67): the runner that enumerates the family's cases and holds each to the shape its README states, so
/// that a case the Forum's act fold or <c>curia-testis</c> never ran, or a malformed one, fails by name. The verdicts themselves are run by the fold's own tests (the
/// OAS8 probe) and by the Rust verifier; this holds that every case is there, is well formed, and names only the predicates R6.67 publishes.
/// </summary>
public sealed class OperatorActsFamilyTests
{
    /// <summary>The number of cases, counted from the plan's table (vectors 1 to 33), Amendment 1 (34 to 38) and Amendment 4 (39 to 60); the generator's count is the oracle, and this literal is the control.</summary>
    private const int Cases = 60;

    /// <summary>R6.67's predicates, by the names the Forum publishes and no others (the eight slugs of the procedure's steps).</summary>
    private static readonly string[] Predicates =
    [
        "act-unsigned", "act-statement-mismatch", "act-unknown-kind", "act-broken-link", "act-typ-mismatch", "act-key-unpublished", "act-key-not-first",
        "act-signature-invalid",
    ];

    private static string Root => Path.Combine(VectorLoader.ConformanceRoot, "operator-acts");

    private static IEnumerable<string> CaseDirectories() => Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal);

    [Fact]
    public void ThisRunnerLoadsEveryOperatorActsCaseTheIndexDeclares()
    {
        Assert.True(Directory.Exists(Root), "no operator-acts family: conformance/operator-acts/ does not exist");
        var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(VectorLoader.ConformanceRoot, "index.json"))).RootElement;
        var entry = index.GetProperty("directories").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "operator-acts");

        Assert.Equal("operator-acts", entry.GetProperty("shape").GetString());
        Assert.Equal(Cases, entry.GetProperty("count").GetInt32());
        Assert.Equal(Cases, CaseDirectories().Count());
    }

    [Fact]
    public void EveryCaseDeclaresItsProfileAndRequirementAndNamesItsStream()
    {
        Assert.True(Directory.Exists(Root), "no operator-acts family: conformance/operator-acts/ does not exist");
        foreach (var dir in CaseDirectories())
        {
            var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "meta.json"))).RootElement;
            var name = Path.GetFileName(dir);
            Assert.True(meta.GetProperty("profile").GetString() == "operator-acts-log", $"{name}: profile");
            Assert.True(meta.GetProperty("requirement").GetString() == "R6.67", $"{name}: requirement");
            Assert.True(meta.GetProperty("stream").GetString() is "agent" or "post", $"{name}: stream");
            Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("subject").GetString()), $"{name}: subject");
            Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("note").GetString()), $"{name}: note");
        }
    }

    [Fact]
    public void EveryCasesEntriesAreTheWholeLogFromZeroWithoutAGap()
    {
        Assert.True(Directory.Exists(Root), "no operator-acts family: conformance/operator-acts/ does not exist");
        foreach (var dir in CaseDirectories())
        {
            var files = Directory.EnumerateFiles(Path.Combine(dir, "entries"), "*.json").ToList();
            Assert.NotEmpty(files);
            for (var index = 0; index < files.Count; index++)
            {
                var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "entries", $"{index}.json"))).RootElement;
                Assert.True(document.GetProperty("log_index").GetInt64() == index, $"{Path.GetFileName(dir)}: entries/{index}.json names another log index");
                Assert.StartsWith("sha256:", document.GetProperty("leaf_hash").GetString(), StringComparison.Ordinal);
                Assert.Equal(JsonValueKind.Object, document.GetProperty("entry").ValueKind);
            }
        }
    }

    [Fact]
    public void EveryExpectedNamesOnlyPublishedPredicatesAndAStateOfItsStreamsKind()
    {
        Assert.True(Directory.Exists(Root), "no operator-acts family: conformance/operator-acts/ does not exist");
        foreach (var dir in CaseDirectories())
        {
            var name = Path.GetFileName(dir);
            var stream = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "meta.json"))).RootElement.GetProperty("stream").GetString();
            var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "expected.json"))).RootElement;
            var entries = Directory.EnumerateFiles(Path.Combine(dir, "entries"), "*.json").Count();

            var last = -1L;
            Assert.NotEmpty(expected.GetProperty("rows").EnumerateArray());
            foreach (var row in expected.GetProperty("rows").EnumerateArray())
            {
                var at = row.GetProperty("log_index").GetInt64();
                Assert.True(at > last, $"{name}: rows are not in log order");
                Assert.True(at < entries, $"{name}: a row names a log index the case does not hold");
                last = at;
                var type = row.GetProperty("type").GetString();
                Assert.True(type == (stream == "agent" ? "agent.owner-attested" : "moderation.applied"), $"{name}: row type {type} in a {stream} stream");
                var verdict = row.GetProperty("verdict").GetString();
                Assert.True(verdict is "honoured" or "skipped", $"{name}: verdict {verdict}");
                Assert.True(
                    row.TryGetProperty("predicate", out var predicate) == (verdict == "skipped"),
                    $"{name}: a skipped row names its predicate and an honoured row names none");
                if (verdict == "skipped") Assert.Contains(predicate.GetString(), Predicates);
            }

            var state = expected.GetProperty("state");
            if (stream == "agent")
            {
                Assert.True(state.GetProperty("owner").ValueKind is JsonValueKind.String or JsonValueKind.Null, $"{name}: owner");
                Assert.True(state.GetProperty("verified").ValueKind is JsonValueKind.True or JsonValueKind.False, $"{name}: verified");
                if (state.GetProperty("owner").ValueKind == JsonValueKind.Null) Assert.Equal(JsonValueKind.False, state.GetProperty("verified").ValueKind);
            }
            else
            {
                Assert.True(state.GetProperty("served").ValueKind is JsonValueKind.True or JsonValueKind.False, $"{name}: served");
            }
        }
    }

    [Fact]
    public void EveryPublishedPredicateIsDecidedByAtLeastOneCase()
    {
        Assert.True(Directory.Exists(Root), "no operator-acts family: conformance/operator-acts/ does not exist");
        var seen = CaseDirectories()
            .SelectMany(d => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(d, "expected.json"))).RootElement.GetProperty("rows").EnumerateArray())
            .Where(r => r.TryGetProperty("predicate", out _))
            .Select(r => r.GetProperty("predicate").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal([.. Predicates.Order(StringComparer.Ordinal)], [.. seen.Order(StringComparer.Ordinal)]);
    }
}
