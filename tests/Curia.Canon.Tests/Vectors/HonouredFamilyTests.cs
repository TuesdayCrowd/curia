using System.Text.Json;
using Xunit;

namespace Curia.Canon.Tests.Vectors;

/// <summary>
/// R6.45 for <c>conformance/honoured/</c> (errata G68, R6.64): the runner that enumerates the family's cases and holds each to the shape its README states, so that a case
/// the Forum's verifier or <c>curia-testis</c> never ran, or a malformed one, fails by name. The verdicts themselves are run by the verifier's own tests; this holds
/// that every case is there, is well formed, and names only the predicates R6.64 publishes.
/// </summary>
public sealed class HonouredFamilyTests
{
    /// <summary>The number of cases, counted from the plan's table (vector 49 included); the generator's count is the oracle, and this literal is the control.</summary>
    private const int Cases = 53;

    /// <summary>R6.64's predicates, by the names the Forum publishes and no others.</summary>
    private static readonly string[] Predicates =
    [
        "statement-mismatch", "unknown-lifecycle-kind", "unknown-reason", "broken-link", "lifecycle-typ-mismatch", "rotation-unauthorized",
        "declaration-key-unpublished", "declaration-key-is-log-key", "declaration-key-not-first", "declaration-signature-invalid",
        "kid-not-bound-to-agent", "declaration-mismatch",
    ];

    private static readonly string[] Statuses = ["active", "retired", "compromised"];

    private static string Root => Path.Combine(VectorLoader.ConformanceRoot, "honoured");

    private static IEnumerable<string> CaseDirectories() => Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal);

    [Fact]
    public void ThisRunnerLoadsEveryHonouredCaseTheIndexDeclares()
    {
        var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(VectorLoader.ConformanceRoot, "index.json"))).RootElement;
        var entry = index.GetProperty("directories").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "honoured");

        Assert.Equal("honoured", entry.GetProperty("shape").GetString());
        Assert.Equal(Cases, entry.GetProperty("count").GetInt32());
        Assert.Equal(Cases, CaseDirectories().Count());
    }

    [Fact]
    public void EveryCaseDeclaresItsProfileAndRequirementAndNamesAnAgent()
    {
        foreach (var dir in CaseDirectories())
        {
            var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "meta.json"))).RootElement;
            var name = Path.GetFileName(dir);
            Assert.True(meta.GetProperty("profile").GetString() == "honoured-log", $"{name}: profile");
            Assert.True(meta.GetProperty("requirement").GetString() == "R6.64", $"{name}: requirement");
            Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("agent").GetString()), $"{name}: agent");
            Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("note").GetString()), $"{name}: note");
        }
    }

    [Fact]
    public void EveryCasesEntriesAreTheWholeLogFromZeroWithoutAGap()
    {
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
    public void EveryExpectedNamesOnlyPublishedPredicatesAndTheStatusesTheProcedureHas()
    {
        foreach (var dir in CaseDirectories())
        {
            var name = Path.GetFileName(dir);
            var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "expected.json"))).RootElement;
            var agents = new List<JsonElement> { expected };
            if (expected.TryGetProperty("also", out var also)) agents.AddRange(also.EnumerateArray());

            foreach (var agent in agents)
            {
                var last = -1L;
                foreach (var row in agent.GetProperty("rows").EnumerateArray())
                {
                    var at = row.GetProperty("log_index").GetInt64();
                    Assert.True(at > last, $"{name}: rows are not in log order");
                    last = at;
                    var verdict = row.GetProperty("verdict").GetString();
                    Assert.True(verdict is "honoured" or "skipped", $"{name}: verdict {verdict}");
                    Assert.True(
                        row.TryGetProperty("predicate", out var predicate) == (verdict == "skipped"),
                        $"{name}: a skipped row names its predicate and an honoured row names none");
                    if (verdict == "skipped") Assert.Contains(predicate.GetString(), Predicates);
                }

                foreach (var key in agent.GetProperty("keys").EnumerateObject()) Assert.Contains(key.Value.GetString(), Statuses);
                var bound = agent.GetProperty("bound").EnumerateArray().Select(b => b.GetString()).ToList();
                if (name is not ("enrolment-without-its-pair" or "enrolment-names-another-agent" or "enrolment-payload-not-an-object")) Assert.NotEmpty(bound); // an enrolment alone binds no kid (ruling 55); a stream with no root binds none (ruling 79)
                Assert.All(bound, kid => Assert.True(agent.GetProperty("keys").TryGetProperty(kid!, out _), $"{name}: bound kid {kid} has no status"));
            }
        }
    }
}
