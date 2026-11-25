using System.Text.Json;
using Xunit;

namespace Curia.Canon.Tests.Vectors;

/// <summary>
/// R6.45 for <c>conformance/enroll-proof/</c> (errata G79, R4.46): the runner that enumerates the family's cases and holds each to the shape its README states, so that a case
/// the Forum's validator or <c>curia-testis</c> never ran, or a malformed one, fails by name. The verdicts themselves are judged by the Forum's enrollment-proof validator
/// (EPP1); this holds that every case is there, is well formed, and names only the seven refusals R4.46 publishes.
/// </summary>
public sealed class EnrollProofFamilyTests
{
    /// <summary>The number of cases the generator writes (<c>--enroll-proof-cases</c>); the generator's count is the oracle, and this literal is the control.</summary>
    private const int Cases = 56;

    /// <summary>R4.46's seven refusals, by the slugs the Forum publishes and no others.</summary>
    private static readonly string[] Types =
    [
        "curia/enroll/proof-required", "curia/enroll/proof-malformed", "curia/enroll/proof-mismatch", "curia/enroll/proof-not-possessed",
        "curia/enroll/proof-window", "curia/enroll/proof-nonce-stale", "curia/enroll/proof-replayed",
    ];

    private static string Root => Path.Combine(VectorLoader.ConformanceRoot, "enroll-proof");

    private static IEnumerable<string> CaseDirectories() => Directory.EnumerateDirectories(Root).Order(StringComparer.Ordinal);

    [Fact]
    public void ThisRunnerLoadsEveryEnrollProofCaseTheIndexDeclares()
    {
        Assert.True(Directory.Exists(Root), "no enroll-proof family: conformance/enroll-proof/ does not exist");
        var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(VectorLoader.ConformanceRoot, "index.json"))).RootElement;
        var entry = index.GetProperty("directories").EnumerateArray().Single(e => e.GetProperty("name").GetString() == "enroll-proof");

        Assert.Equal("enroll-proof", entry.GetProperty("shape").GetString());
        Assert.Equal(Cases, entry.GetProperty("count").GetInt32());
        Assert.Equal(Cases, CaseDirectories().Count());
    }

    [Fact]
    public void EveryCaseDeclaresItsProfileRequirementAndStep()
    {
        foreach (var dir in CaseDirectories())
        {
            var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "meta.json"))).RootElement;
            var name = Path.GetFileName(dir);
            Assert.True(meta.GetProperty("profile").GetString() == "enroll-proof", $"{name}: profile");
            Assert.True(meta.GetProperty("requirement").GetString() == "R4.46", $"{name}: requirement");
            Assert.True(meta.GetProperty("step").GetString() is "1" or "2" or "3" or "4" or "5" or "6" or "7" or "all", $"{name}: step");
            Assert.False(string.IsNullOrWhiteSpace(meta.GetProperty("note").GetString()), $"{name}: note");
        }
    }

    [Fact]
    public void EveryInputHoldsTheRequestAndTheContextTheVerdictIsDecidedIn()
    {
        foreach (var dir in CaseDirectories())
        {
            var name = Path.GetFileName(dir);
            var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "input.json"))).RootElement;
            var request = input.GetProperty("request");
            foreach (var member in new[] { "agent_id", "alg", "kid", "public_key" })
                Assert.Equal(JsonValueKind.String, request.GetProperty(member).ValueKind);
            if (request.TryGetProperty("proof", out var proof)) Assert.Equal(JsonValueKind.String, proof.ValueKind);

            var context = input.GetProperty("context");
            Assert.Equal(JsonValueKind.Number, context.GetProperty("now").ValueKind);
            Assert.StartsWith("https://", context.GetProperty("audience").GetString(), StringComparison.Ordinal);
            Assert.All(context.GetProperty("current_nonces").EnumerateArray(), n => Assert.Equal(JsonValueKind.String, n.ValueKind));
            Assert.All(context.GetProperty("spent_jtis").EnumerateArray(), j => Assert.Equal(JsonValueKind.String, j.ValueKind));
            Assert.True(request.TryGetProperty("proof", out _) || name == "proof-absent", $"{name}: only proof-absent leaves out the proof member");
        }
    }

    [Fact]
    public void EveryExpectedIsOkOrOneOfTheSevenPublishedRefusals()
    {
        foreach (var dir in CaseDirectories())
        {
            var name = Path.GetFileName(dir);
            var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "expected.json"))).RootElement;
            var verdict = expected.GetProperty("verdict").GetString();
            Assert.True(verdict is "ok" or "refused", $"{name}: verdict {verdict}");
            Assert.True(expected.TryGetProperty("type", out var type) == (verdict == "refused"), $"{name}: a refusal names its type and an acceptance names none");
            if (verdict == "refused") Assert.Contains(type.GetString(), Types);
            if (expected.TryGetProperty("detail", out var detail))
                Assert.True(detail.GetString()!.StartsWith("member=", StringComparison.Ordinal) || detail.GetString()!.StartsWith("claim=", StringComparison.Ordinal), $"{name}: detail names a member or a claim");
        }
    }

    [Fact]
    public void EveryPublishedRefusalIsDecidedByAtLeastOneCase()
    {
        var seen = CaseDirectories()
            .Select(d => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(d, "expected.json"))).RootElement)
            .Where(e => e.TryGetProperty("type", out _))
            .Select(e => e.GetProperty("type").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal([.. Types.Order(StringComparer.Ordinal)], [.. seen.Order(StringComparer.Ordinal)]);
    }
}
