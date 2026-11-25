using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Application.Credentials;
using Curia.Application.Projections;
using Curia.Application.Tests.OperatorActs;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Credentials;
using CsCheck;
using Xunit;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// SRS8 to SRS11 (errata G80, R4.48, R6.64 (rev.); register item 299): the standing reads an <c>agent.enrolled</c> row only if the lifecycle fold honours it as the root of its own stream. The oracles are
/// not the implementation's: SRS8's property (every identity's posture is readable, and its credential history holds one enrolment when the vector's own <c>expected.json</c> leaves the stream's root honoured and none
/// when it skips it) is stated by the errata and by the vectors' construction, and SRS9's expectations are written from the scene. SRS11 is a statement of the one-function ruling (the feed's drop list and the fold's
/// <c>agent.enrolled</c> skips are the same decision made at two sites), and its independence is limited to that: the independent half of the rule is SRS8, SRS9 and the Api probes.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SecondRootSkippedTests
{
    private const string Reason = "Enrollment accepted: agent key registered with the Registrar";
    private const string X = "agent://curia.example/tuesdaycrowd/x";
    private const string Y = "agent://curia.example/tuesdaycrowd/y";
    private const string Z = "agent://curia.example/tuesdaycrowd/z";

    private static readonly string[] SecondEnrolmentCases =
    [
        "second-enrolment-then-binding", "second-enrolment-after-pair", "second-enrolment-names-another-agent",
        "second-enrolment-payload-not-an-object", "second-enrolment-in-a-rootless-stream", "second-enrolment-between-root-and-pair",
    ];

    /// <summary>A vector's log as a route would have written it: every <c>agent.enrolled</c> payload that is an object carries the <c>reason</c> the route stamps (the vectors' enrolment rows hold only <c>agent_id</c> and <c>kid</c>, which the standing does not read as an enrolment).</summary>
    private static List<AppendedEvent> WithReasons(IReadOnlyList<AppendedEvent> log) =>
    [
        .. log.Select((e, i) => e.Event.Type.Value == AgentStandingProjector.EnrolledType && e.Event.Payload is JsonValue.Object o && !o.Members.Any(m => m.Key == AgentStandingProjector.ReasonField)
            ? LogEvents.At(i, e.AggregateId.Value, e.ServerTimestamp.Value, e.Event.Id.Value, e.Event.Type.Value, e.Event.Actor!.Value.Value, new JsonValue.Object([.. o.Members, new(AgentStandingProjector.ReasonField, new JsonValue.String(Reason))]))
            : e),
    ];

    private static AppendedEvent Enrolled(long index, string aggregate, string names, DateTimeOffset at) =>
        LogEvents.At(index, aggregate, at, "e-" + index, AgentStandingProjector.EnrolledType, aggregate, new JsonValue.Object(
        [
            new(AgentStandingProjector.AgentIdField, new JsonValue.String(names)),
            new(AgentStandingProjector.KeyIdField, new JsonValue.String("k-" + index)),
            new(AgentStandingProjector.ReasonField, new JsonValue.String(Reason)),
        ]));

    private static int Enrolments(AgentStanding? standing) =>
        standing?.CredentialHistory.Count(t => t.Trigger == CredentialTrigger.SuccessfulEnrollment) ?? 0;

    /// <summary>The blocks of an <c>expected.json</c>: the stream's own, and each <c>also</c> agent's.</summary>
    private static IEnumerable<(string Agent, HashSet<long> Skipped)> Blocks(ConformanceLog log, JsonElement expected)
    {
        static HashSet<long> SkippedOf(JsonElement block) => [.. block.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("verdict").GetString() == "skipped").Select(r => r.GetProperty("log_index").GetInt64())];
        yield return (log.Agent, SkippedOf(expected));
        if (expected.TryGetProperty("also", out var also))
            foreach (var other in also.EnumerateArray()) yield return (other.GetProperty("agent").GetString()!, SkippedOf(other));
    }

    /// <summary>SRS8: over every honoured vector, every identity's posture is readable and its credential history holds one enrolment exactly when the vector leaves its stream's first <c>agent.enrolled</c> honoured.</summary>
    [Fact]
    public void R4_48_SRS8_EveryHonouredVectorsStandingIsReadableAndHoldsTheHonouredRootOnly()
    {
        var failures = new List<string>();
        var asked = 0;
        var withRoot = 0;
        foreach (var name in ConformanceLog.Names("honoured"))
        {
            var log = ConformanceLog.Load("honoured", name);
            using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(log.Directory, "expected.json")));
            var events = WithReasons(log.Log);
            var standings = ConformanceLog.Verifier().OfLog(events).Standings();
            var blocks = Blocks(log, expected.RootElement).ToArray();

            // No identity holds a standing the vector does not give a root: a row that names an identity from another stream, or that names one whose stream has no honoured root, makes none.
            foreach (var held in standings.Keys.Where(k => blocks.All(b => b.Agent != k)))
                failures.Add($"{name}: {held}: Expected: no standing (no stream of the vector gives it a root)  Actual: {Enrolments(standings[held])} enrolment(s)");
            foreach (var (agent, skipped) in blocks)
            {
                asked++;
                var firstRoot = events.Select((e, i) => (e, i)).FirstOrDefault(x => x.e.AggregateId.Value == agent && x.e.Event.Type.Value == AgentStandingProjector.EnrolledType);
                var honouredRoot = firstRoot.e is not null && !skipped.Contains(firstRoot.i);
                if (honouredRoot) withRoot++;
                var posture = AgentStandingProjector.PostureOf(standings, Typed.Agent(agent));
                if (!posture.IsOk)
                {
                    failures.Add($"{name}: {agent}: Expected: a readable posture  Actual: {posture.Match(_ => "ok", e => e.Type)}");
                    continue;
                }

                var held = Enrolments(standings.GetValueOrDefault(agent));
                if (held != (honouredRoot ? 1 : 0))
                    failures.Add($"{name}: {agent}: Expected: {(honouredRoot ? 1 : 0)} enrolment(s) in the credential history  Actual: {held}");
            }
        }

        Assert.True(failures.Count == 0, $"{failures.Count} differ. The first: {failures.FirstOrDefault()}{Environment.NewLine}All:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");

        // The pin looked at something: at least one identity per honoured root and at least the six second-enrolment cases among the vectors.
        Assert.True(withRoot >= 60, $"{withRoot} identities with an honoured root");
        Assert.Contains("second-enrolment-after-pair", ConformanceLog.Names("honoured"));
    }

    /// <summary>SRS9: a well-formed enrolment in X's stream naming Y does not enter Y's standing, in either order against Y's own root, and the two readers of a standing (the dictionary and the stream's own) agree.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void R4_48_SRS9_ARowInAnotherStreamNamingYMovesNeitherYsHistoryNorYsTenure(bool rowBeforeYsRoot)
    {
        var t0 = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var forgedAt = t0.AddDays(-3);
        var ownRootAt = t0;
        var log = new List<AppendedEvent> { Enrolled(0, X, X, t0.AddHours(-5)) };
        if (rowBeforeYsRoot)
        {
            log.Add(Enrolled(1, X, Y, forgedAt));
            log.Add(Enrolled(2, Y, Y, ownRootAt));
        }
        else
        {
            log.Add(Enrolled(1, Y, Y, ownRootAt));
            log.Add(Enrolled(2, X, Y, forgedAt));
        }

        var verified = ConformanceLog.Verifier().OfLog(log);
        var all = verified.Standings().GetValueOrDefault(Y);
        var own = verified.StandingOf(Typed.Agent(Y));
        Assert.Equal("1 enrolment; tenure 2026-08-01T12:00:00.0000000+00:00; own reader agrees True", $"{Enrolments(all)} enrolment{(Enrolments(all) == 1 ? string.Empty : "s")}; tenure {all?.EnrolledAt:o}; own reader agrees {all is not null && own is not null && all.Equals(own)}");
        Assert.True(AgentStandingProjector.PostureOf(verified.Standings(), Typed.Agent(Y)).IsOk, "Y's posture is readable");
        Assert.Equal(1, Enrolments(verified.Standings().GetValueOrDefault(X)));
    }

    /// <summary>SRS10: the fold's skipped rows over the six second-enrolment vectors are exactly the vectors' skipped rows (type and predicate at each index).</summary>
    [Theory]
    [MemberData(nameof(SecondEnrolmentCaseData))]
    public void R6_64_SRS10_TheFoldSkipsExactlyTheRowsTheSecondEnrolmentVectorsSay(string name)
    {
        var log = ConformanceLog.Load("honoured", name);
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(log.Directory, "expected.json")));
        var want = string.Join(";", expected.RootElement.GetProperty("rows").EnumerateArray()
            .Where(r => r.GetProperty("verdict").GetString() == "skipped")
            .Select(r => $"{r.GetProperty("log_index").GetInt64()} {r.GetProperty("type").GetString()} {r.GetProperty("predicate").GetString()}"));
        var got = string.Join(";", ConformanceLog.Verifier().OfLog(log.Log).Skipped().Select(r => $"{r.LogIndex} {r.Type} {r.Predicate}"));
        Assert.Equal(want, got);
    }

    public static TheoryData<string> SecondEnrolmentCaseData => [.. SecondEnrolmentCases];

    /// <summary>The log indices <paramref name="verified"/>'s feed leaves out, and the indices of the <c>agent.enrolled</c> rows its fold skips plus the operator-act rows it skips.</summary>
    private static (string LeftOut, string Listed) Agreement(List<AppendedEvent> log)
    {
        var verified = ConformanceLog.Verifier().OfLog(log);
        var kept = verified.HonouredEvents.Events.Select(e => e.Seq.Value).ToHashSet();
        var leftOut = Enumerable.Range(0, log.Count).Where(i => !kept.Contains(log[i].Seq.Value)).Select(i => (long)i).Order();
        var listed = verified.ActSkipped().Select(r => r.LogIndex)
            .Concat(verified.Skipped().Where(r => r.Type == AgentStandingProjector.EnrolledType).Select(r => r.LogIndex))
            .Distinct().Order();
        return (string.Join(",", leftOut), string.Join(",", listed));
    }

    /// <summary>SRS11: in both directions, the indices the feed leaves out are the operator-act skips plus the <c>agent.enrolled</c> rows the fold lists; over the honoured and operator-acts corpora and over generated logs of roots, later roots and cross-stream rows.</summary>
    [Fact]
    public void R4_48_SRS11_TheFeedsDropListIsTheFoldsSkipsOfActsAndEnrolments()
    {
        var failures = new List<string>();
        foreach (var name in ConformanceLog.Names("honoured"))
        {
            var (leftOut, listed) = Agreement(WithReasons(ConformanceLog.Load("honoured", name).Log));
            if (leftOut != listed) failures.Add($"honoured/{name}: Expected: feed leaves out [{listed}]  Actual: [{leftOut}]");
        }

        foreach (var name in ActVectors.Cases())
        {
            var (leftOut, listed) = Agreement(WithReasons(ActVectors.LogOf(name)));
            if (leftOut != listed) failures.Add($"operator-acts/{name}: Expected: feed leaves out [{listed}]  Actual: [{leftOut}]");
        }

        Assert.True(failures.Count == 0, $"{failures.Count} differ. The first: {failures.FirstOrDefault()}{Environment.NewLine}All:{Environment.NewLine}{string.Join(Environment.NewLine, failures)}");
    }

    /// <summary>SRS11's generated half: roots, later roots and cross-stream rows placed before and after the named agent's root, in streams with and without their own root, some payloads not objects.</summary>
    [Fact]
    [SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "A seeded generator of test logs; the seed is the case's name when it fails.")]
    public void R4_48_SRS11_TheFeedsDropListAgreesWithTheFoldOverGeneratedLogs()
    {
        string[] agents = [X, Y, Z];
        var disagreements = new List<string>();
        var droppedSomething = 0;
        Gen.Int[0, 1_000_000].Sample(seed =>
        {
            var random = new Random(seed);
            var log = new List<AppendedEvent>();
            var at = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
            var count = random.Next(1, 13);
            for (var i = 0; i < count; i++)
            {
                var aggregate = agents[random.Next(agents.Length)];
                at = at.AddMinutes(random.Next(-30, 60));
                var shape = random.Next(10);
                log.Add(shape switch
                {
                    0 => LogEvents.At(i, aggregate, at, "g-" + i, AgentStandingProjector.EnrolledType, aggregate, new JsonValue.String("not-an-object")),
                    1 => LogEvents.At(i, aggregate, at, "g-" + i, AgentStandingProjector.KeyBoundType, aggregate, new JsonValue.Object([])),
                    < 6 => Enrolled(i, aggregate, aggregate, at),
                    _ => Enrolled(i, aggregate, agents[random.Next(agents.Length)], at),
                });
            }

            var (leftOut, listed) = Agreement(log);
            if (leftOut.Length > 0) droppedSomething++;
            if (leftOut != listed) disagreements.Add($"seed {seed}: feed leaves out [{leftOut}], the fold lists [{listed}]");
        }, iter: 300);

        Assert.True(disagreements.Count == 0, $"{disagreements.Count} generated logs differ. The first: {disagreements.FirstOrDefault()}");
        Assert.True(droppedSomething > 20, $"the generator dropped something in {droppedSomething} of 300 logs: the property looked at nothing");
    }
}
