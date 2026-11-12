using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Application.Credentials;
using Curia.Application.Projections;
using Curia.Domain.Primitives;
using Xunit;
using static Curia.Application.Tests.Credentials.LifecycleProbeSupport;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// LRV3: the honoured family (<c>conformance/honoured/</c>, errata G68, R6.64) through the C# verifier. Each case's rows, <c>keys</c> and <c>bound</c> equal what its
/// <c>expected.json</c> says; <c>expected.json</c> was written from the case's construction and not by running a verifier. The failure text names every case that differs.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HonouredVectorTests
{
    internal static string Word(Curia.Domain.Primitives.KeyStatus status) => status switch
    {
        Curia.Domain.Primitives.KeyStatus.Active => "active",
        Curia.Domain.Primitives.KeyStatus.Retired => "retired",
        Curia.Domain.Primitives.KeyStatus.Compromised => "compromised",
        Curia.Domain.Primitives.KeyStatus.Unbound => "unbound",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a key status"),
    };

    internal static string Rows(VerifiedLifecycle verified, IEnumerable<long> indices, ConformanceLog log) =>
        verified.Refusal is { } refusal
            ? $"[Refusal {refusal.Type}]"
            : "[" + string.Join(", ", indices.Select(i =>
                verified.Skipped.FirstOrDefault(r => r.LogIndex == i) is { } skipped
                    ? $"{i} skipped {skipped.Predicate}"
                    : $"{i} honoured")) + "]";

    /// <summary>The differences between what the verifier decides of <paramref name="agent"/> in <paramref name="log"/> and one expected block, or none.</summary>
    internal static IEnumerable<string> Differences(ConformanceLog log, string agent, JsonElement expected)
    {
        var verified = ConformanceLog.Verifier().OfLog(log.Log).For(Typed.Agent(agent));

        var rows = expected.GetProperty("rows").EnumerateArray().ToArray();
        var indices = rows.Select(r => r.GetProperty("log_index").GetInt64()).ToArray();
        var expectedRows = "[" + string.Join(", ", rows.Select(r => r.GetProperty("verdict").GetString() == "skipped"
            ? $"{r.GetProperty("log_index").GetInt64()} skipped {r.GetProperty("predicate").GetString()}"
            : $"{r.GetProperty("log_index").GetInt64()} honoured")) + "]";
        var actualRows = Rows(verified, indices, log);
        var extra = verified.Refusal is null ? verified.Skipped.Where(s => !indices.Contains(s.LogIndex)).Select(s => s.LogIndex).ToArray() : [];
        if (expectedRows != actualRows || extra.Length > 0)
            yield return $"{log.Name} rows: Expected rows: {expectedRows}  Actual: {actualRows}{(extra.Length > 0 ? " and skipped beyond the expected: " + string.Join(", ", extra) : string.Empty)}";

        if (verified.Refusal is not null) yield break;

        var keys = expected.GetProperty("keys").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var actualKeys = keys.Keys.ToDictionary(k => k, k => Word(verified.Honoured.StatusNow(k)), StringComparer.Ordinal);
        // The enrolment names a kid and binds none (ruling 55): a key an honoured row names is one a pair, a rotation, a retirement or a declaration names.
        var named = verified.Honoured.Entries.Where(e => e.Type != AgentStandingProjector.EnrolledType).SelectMany(e => e.Kid is null ? [] : new[] { e.Kid }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!keys.OrderBy(k => k.Key, StringComparer.Ordinal).SequenceEqual(actualKeys.OrderBy(k => k.Key, StringComparer.Ordinal)) || !keys.Keys.Order(StringComparer.Ordinal).SequenceEqual(named))
            yield return $"{log.Name} keys: Expected: {{{string.Join(", ", keys.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + " " + k.Value))}}}  Actual: {{{string.Join(", ", named.Select(k => k + " " + Word(verified.Honoured.StatusNow(k))))}}}";

        var bound = expected.GetProperty("bound").EnumerateArray().Select(b => b.GetString()!).ToArray();
        // `bound` is the kids the fold binds, by a pair or a rotation; the kid-only binding of R4.31 (no key: Jwk null) is the consumers' reading of an enrolment and is not the fold's.
        var actualBound = verified.Binding?.Keys.Where(k => k.Jwk is not null).Select(k => k.Kid).ToArray() ?? [];
        if (!bound.SequenceEqual(actualBound))
            yield return $"{log.Name} bound: Expected: [{string.Join(", ", bound)}]  Actual: [{string.Join(", ", actualBound)}]";
    }

    [Fact]
    public void LRV3_EveryHonouredVectorsRowsKeysAndBoundAreWhatItsExpectedJsonSays()
    {
        var failures = new List<string>();
        var cases = ConformanceLog.Names("honoured");
        foreach (var name in cases)
        {
            var log = ConformanceLog.Load("honoured", name);
            using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(log.Directory, "expected.json")));
            failures.AddRange(Differences(log, log.Agent, expected.RootElement));
            if (expected.RootElement.TryGetProperty("also", out var also))
                foreach (var other in also.EnumerateArray())
                    failures.AddRange(Differences(log, other.GetProperty("agent").GetString()!, other));
        }

        Assert.True(failures.Count == 0, $"{failures.Select(x => x.Split(' ')[0]).Distinct(StringComparer.Ordinal).Count()} of {cases.Length} cases differ ({failures.Count} differences). The first: {failures.FirstOrDefault()}{Environment.NewLine}All:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
