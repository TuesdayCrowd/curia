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

    /// <summary>
    /// The verdict the verifier gives each listed row (Amendment 7, ruling 88): <c>skipped</c> only for a row it reports skipped at that index <b>and</b> whose type is the expected one, <c>honoured</c> only for an event
    /// of the log at that index of the expected type whose event id is among the honoured entries. A row the fold drops silently, or an index that is no event of the stream, is neither: it is said so.
    /// </summary>
    internal static string Rows(VerifiedLifecycle verified, IEnumerable<(long Index, string Type)> rows, ConformanceLog log) =>
        "[" + string.Join(", ", rows.Select(row =>
        {
            var (i, type) = row;
            if (verified.Skipped.FirstOrDefault(r => r.LogIndex == i) is { } skipped)
                return skipped.Type == type ? $"{i} skipped {skipped.Predicate}" : $"{i} skipped {skipped.Predicate} but of type {skipped.Type}, not {type}";

            if (i < 0 || i >= log.Log.Count) return $"{i} is no event of the stream";
            var appended = log.Log[(int)i];
            if (appended.Event.Type.Value != type) return $"{i} is of type {appended.Event.Type.Value}, not {type}";
            return verified.Honoured.Entries.Any(e => e.EventId == appended.Event.Id.Value) ? $"{i} honoured" : $"{i} neither honoured nor skipped";
        })) + "]";

    /// <summary>The differences between what the verifier decides of <paramref name="agent"/> in <paramref name="log"/> and one expected block, or none.</summary>
    internal static IEnumerable<string> Differences(ConformanceLog log, string agent, JsonElement expected)
    {
        var verified = ConformanceLog.Verifier().OfLog(log.Log).For(Typed.Agent(agent));

        var rows = expected.GetProperty("rows").EnumerateArray().ToArray();
        var indices = rows.Select(r => r.GetProperty("log_index").GetInt64()).ToArray();
        var typed = rows.Select(r => (r.GetProperty("log_index").GetInt64(), r.GetProperty("type").GetString()!)).ToArray();
        var expectedRows = "[" + string.Join(", ", rows.Select(r => r.GetProperty("verdict").GetString() == "skipped"
            ? $"{r.GetProperty("log_index").GetInt64()} skipped {r.GetProperty("predicate").GetString()}"
            : $"{r.GetProperty("log_index").GetInt64()} honoured")) + "]";
        var actualRows = Rows(verified, typed, log);
        var extra = verified.Skipped.Where(s => !indices.Contains(s.LogIndex)).Select(s => s.LogIndex).ToArray();
        if (expectedRows != actualRows || extra.Length > 0)
            yield return $"{log.Name} rows: Expected rows: {expectedRows}  Actual: {actualRows}{(extra.Length > 0 ? " and skipped beyond the expected: " + string.Join(", ", extra) : string.Empty)}";

        var keys = expected.GetProperty("keys").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        var actualKeys = keys.Keys.ToDictionary(k => k, k => Word(verified.Honoured.StatusNow(k)), StringComparer.Ordinal);
        // The enrolment names a kid and binds none (ruling 55): a key an honoured row names is one a pair, a rotation, a retirement or a declaration names. The enrolment's own entry is dropped for the one vector that
        // has an enrolment with no pair after it (vector 50); every other vector's enrolment is followed by its pair, which names the same kid, and dropping it there changes nothing a present vector can see (FLRV-N1 (b), Task 7); it is kept to the one vector so that a later vector whose enrolment and pair disagree is compared (Amendment 7, ruling 88).
        var pairless = log.Name == "enrolment-without-its-pair";
        var named = verified.Honoured.Entries.Where(e => !pairless || e.Type != AgentStandingProjector.EnrolledType).SelectMany(e => e.Kid is null ? [] : new[] { e.Kid }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!keys.OrderBy(k => k.Key, StringComparer.Ordinal).SequenceEqual(actualKeys.OrderBy(k => k.Key, StringComparer.Ordinal)) || !keys.Keys.Order(StringComparer.Ordinal).SequenceEqual(named))
            yield return $"{log.Name} keys: Expected: {{{string.Join(", ", keys.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + " " + k.Value))}}}  Actual: {{{string.Join(", ", named.Select(k => k + " " + Word(verified.Honoured.StatusNow(k))))}}}";

        var bound = expected.GetProperty("bound").EnumerateArray().Select(b => b.GetString()!).ToArray();
        // `bound` is the kids the fold binds, by a pair or a rotation, and every binding is compared: the kid-only binding of R4.31 (no key: Jwk null) is the consumers' reading of an enrolment and is not the fold's,
        // so it is allowed in exactly one place, vector 50, where the Jwk-less set is the enrolment's kid and nothing else (ruling 65 (a); Amendment 7, ruling 88).
        var actualBound = verified.Binding?.Keys.Where(k => k.Jwk is not null).Select(k => k.Kid).ToArray() ?? [];
        if (!bound.SequenceEqual(actualBound))
            yield return $"{log.Name} bound: Expected: [{string.Join(", ", bound)}]  Actual: [{string.Join(", ", actualBound)}]";

        var jwkLess = verified.Binding?.Keys.Where(k => k.Jwk is null).Select(k => k.Kid).ToArray() ?? [];
        var enrolmentKid = log.Log.Where(e => e.AggregateId.Value == agent && e.Event.Type.Value == AgentStandingProjector.EnrolledType).Select(e => e.Event.Payload is Curia.Canon.Json.JsonValue.Object o ? o.Members.FirstOrDefault(m => m.Key == "kid").Value as Curia.Canon.Json.JsonValue.String : null).FirstOrDefault()?.Value;
        string[] expectedJwkLess = pairless && enrolmentKid is not null ? [enrolmentKid] : [];
        if (!expectedJwkLess.SequenceEqual(jwkLess))
            yield return $"{log.Name} kid-only bindings: Expected: [{string.Join(", ", expectedJwkLess)}]  Actual: [{string.Join(", ", jwkLess)}]";
    }

    /// <summary>Adds one block's differences to <paramref name="failures"/>; a throw is a failure line that begins with the case's name, as every other does. False when it threw.</summary>
    private static bool Compared(List<string> failures, ConformanceLog log, string agent, JsonElement expected)
    {
        var found = new List<string>();
        var thrown = Record.Exception(() => found.AddRange(Differences(log, agent, expected)));
        failures.AddRange(found);
        if (thrown is null) return true;

        failures.Add($"{log.Name} threw: Expected: For does not throw  Actual: {thrown.GetType().Name}: {thrown.Message}");
        return false;
    }

    [Fact]
    public void LRV3_EveryHonouredVectorsRowsKeysAndBoundAreWhatItsExpectedJsonSays()
    {
        var failures = new List<string>();
        var cases = ConformanceLog.Names("honoured");
        Assert.True(cases.Length == 54, $"Expected: 54 honoured vectors  Actual: {cases.Length} (the pin is counted in six places: conformance/index.json, the README, HonouredFamilyTests, here, vectors.rs and honoured.rs)");
        Assert.Contains("enrolment-without-its-pair", cases);
        foreach (var name in cases)
        {
            var log = ConformanceLog.Load("honoured", name);
            using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(log.Directory, "expected.json")));
            // The act under test is the verifier deciding the case: what it does, a throw included, is recorded under the case's name, and the next case is still compared (Amendment 9, ruling 113 (1)).
            if (!Compared(failures, log, log.Agent, expected.RootElement)) continue;
            if (expected.RootElement.TryGetProperty("also", out var also))
                foreach (var other in also.EnumerateArray())
                    if (!Compared(failures, log, other.GetProperty("agent").GetString()!, other)) break;
        }

        Assert.True(failures.Count == 0, $"{failures.Select(x => x.Split(' ')[0]).Distinct(StringComparer.Ordinal).Count()} of {cases.Length} cases differ ({failures.Count} differences). The first: {failures.FirstOrDefault()}{Environment.NewLine}All:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }
}
