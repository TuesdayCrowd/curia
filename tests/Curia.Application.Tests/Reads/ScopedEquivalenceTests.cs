using System.Diagnostics.CodeAnalysis;
using Curia.Application.Reads;
using Curia.Application.Tests.Credentials;
using Curia.Application.Tests.InMemory;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Reads;

/// <summary>
/// RC1 (errata G87, R11.113): a request's own read decides what the whole-log fold decides of the same log, decision for decision. (i) every case of the honoured and lifecycle corpora, compared on the
/// verdict and the skip slug of each row, the binding and the keys' statuses; (ii) generated logs of every row type a posture reads. The whole-log fold is the oracle and the scoped read is held to it.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ScopedEquivalenceTests
{
    private static string Describe(Curia.Application.Credentials.VerifiedLifecycle v) =>
        string.Join(";", v.Honoured.Entries.Select(e => e.EventId + ":" + e.Type)) + "|"
        + string.Join(";", v.Skipped.Select(s => $"{s.LogIndex}:{s.Type}:{s.Predicate}")) + "|"
        + string.Join(";", v.Binding?.Keys.Select(k => k.Kid + (k.Jwk is null ? "?" : "")) ?? []);

    [Theory]
    [InlineData("honoured")]
    [InlineData("lifecycle")]
    public async Task RC1_i_EveryCorpusCaseIsDecidedByTheScopedReadAsTheWholeLogDecidesIt(string family)
    {
        var ct = TestContext.Current.CancellationToken;
        var failures = new List<string>();
        var skipped = 0;
        var cases = ConformanceLog.Names(family);
        Assert.NotEmpty(cases);

        foreach (var name in cases)
        {
            // A reader vector's entries have gaps where the log keys sit (the readers fold them from the key set, not from entries): there is no whole log of it to compare against.
            ConformanceLog log;
            try
            {
                log = ConformanceLog.Load(family, name);
            }
            catch (InvalidOperationException e) when (e.Message.Contains("not contiguous", StringComparison.Ordinal))
            {
                skipped++;
                continue;
            }

            var verifier = ConformanceLog.Verifier();
            var agents = new List<string> { log.Agent };
            using (var expected = System.Text.Json.JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(log.Directory, "expected.json"), ct)))
                if (expected.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && expected.RootElement.TryGetProperty("also", out var also))
                    agents.AddRange(also.EnumerateArray().Select(a => a.GetProperty("agent").GetString()!));

            var whole = verifier.OfLog(log.Log);
            foreach (var agent in agents)
            {
                var typed = Typed.Agent(agent);
                // A log with a gap before it, as a burnt identity value leaves: the reference answers over the whole list, so its index is the position; the oracle is the whole fold.
                var read = await AgentRead.ReadAsync(new ListReader(log.Log), verifier, typed, AgentReadOptions.Stream, ct);
                var a = Describe(whole.For(typed));
                var b = Describe(read.Lifecycle);
                if (a != b) failures.Add($"{family}/{name} {agent}: whole {a}  scoped {b}");
            }
        }

        Assert.True(cases.Length - skipped >= 40, $"{skipped} of {cases.Length} {family} cases had no whole log to compare; only {cases.Length - skipped} were compared");
        Assert.True(failures.Count == 0, $"{failures.Count} of {cases.Length} cases differ. The first: {failures.FirstOrDefault()}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public async Task RC1_ii_AGeneratedLogIsDecidedByAnAgentsOwnReadAsTheWholeLogDecidesIt(int seed)
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(TimeProvider.System, new PermissiveLifecycleAppendGate());
        await ReadScene.BuildAsync(store, seed, burn: null, ct);

        var differences = await ReadScene.DifferencesAsync(store, store, seed, ct);

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }

    /// <summary>
    /// Vacuity question 1: an equivalence over logs that never reach a fact compares nothing. Across the generated scenes, some agent holds a verified finding, an upheld flag, an accepted answer and a
    /// clean question, so each arm of Table 11's facts is one the comparison above has decided at least once.
    /// </summary>
    [Fact]
    public async Task RC1_ii_TheGeneratedLogsReachEveryArmOfThePosture()
    {
        var ct = TestContext.Current.CancellationToken;
        int verified = 0, upheld = 0, accepted = 0, clean = 0;
        for (var seed = 1; seed <= 8; seed++)
        {
            var store = new InMemoryEventStore(TimeProvider.System, new PermissiveLifecycleAppendGate());
            await ReadScene.BuildAsync(store, seed, burn: null, ct);
            var all = (await Curia.Application.Ports.EventReaderExtensions.ReadAllAsync(store, ct)).Match(v => v, e => throw new InvalidOperationException(e.Type));
            var whole = ReadScene.Verifier.OfLog(all);
            foreach (var name in ReadScene.AgentsOf(seed))
            {
                var facts = Curia.Application.Projections.PostureQuery.Of(whole, Typed.Agent(name)).Match(v => v, e => throw new InvalidOperationException(e.Type));
                verified += facts.VerifiedFindings;
                upheld += facts.UpheldFlags;
                accepted += facts.AcceptedAnswers;
                clean += facts.QuestionsWithoutUpheldFlags;
            }
        }

        Assert.True(verified > 0, "no generated agent holds a verified finding: the P6 clause is never compared");
        Assert.True(upheld > 0, "no generated agent has an upheld flag: P4 is never compared");
        Assert.True(accepted > 0, "no generated agent has an accepted answer: P5 is never compared");
        Assert.True(clean > 0, "no generated agent has a clean question");
    }

    /// <summary>The reference semantics over a fixed list, for a corpus case whose log is a list and not a store.</summary>
    private sealed class ListReader(IReadOnlyList<Curia.Domain.AppendedEvent> log) : Curia.Application.Ports.IPositionedEventReader
    {
        public Task<LogSlice> ReadAsync(ReadPlan plan, CancellationToken cancellationToken = default) => Task.FromResult(WholeLogPlanReader.Answer(log, plan));
    }
}
