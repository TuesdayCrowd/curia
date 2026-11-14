using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;
using static Curia.Application.Tests.Credentials.LifecycleProbeSupport;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// Errata G68, R6.64 and R5.24 (rulings 5, 6): the honoured prefix. The chain moves only on honoured rows (LRV5), the binding list is the honoured bindings (LRV6), and the
/// served key set is what the honoured rows say (LRV10). The keys here are ones the attacker can actually produce: it holds its own key, and the genuine key's public half.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LifecycleVerifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A rotation row with signatures that verify: <paramref name="authorizer"/> authorizes and <paramref name="next"/> possesses.</summary>
    private static Curia.Canon.Json.JsonValue.Object SignedRotation(KeyActHarness harness, TestIdentity authorizer, TestIdentity next, long prev)
    {
        var statement = RotationStatement(Alice, authorizer.Kid, next.Material, prev);
        return new Curia.Canon.Json.JsonValue.Object(
        [
            new("agent_id", S(Alice)),
            new("authorization", S(harness.Sign(statement, KeyStatements.RotationHeaderTyp, authorizer.Signing).Value)),
            new("authorized_by", S(authorizer.Kid)),
            new("jwk", Require(Curia.Canon.Jws.PublicJwk.Of(next.Material))),
            new("kid", S(next.Kid)),
            new("possession", S(harness.Sign(statement, KeyStatements.RotationHeaderTyp, next.Signing).Value)),
            new("prev", new Curia.Canon.Json.JsonValue.Number(prev)),
            new("statement", S(statement)),
        ]);
    }

    /// <summary>LRV5 (case 24): after a forged rotation to <c>ka</c>, a rotation authorized by <c>ka</c> with valid signatures is skipped, because <c>ka</c> is not bound by the honoured prefix.</summary>
    [Fact]
    public async Task LRV5_ARotationAuthorizedByAForgedKeyIsSkipped()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var ka = TestIdentity.New("ka");
        var kb = TestIdentity.New("kb");
        await AppendRowAsync(harness, Alice, "agent.key-bound", Rotation(Alice, victim.Kid, ka.Material, 1)).ConfigureAwait(true);
        var chained = await AppendRowAsync(harness, Alice, "agent.key-bound", SignedRotation(harness, ka, kb, 1)).ConfigureAwait(true);

        var verified = VerifierOf(harness).OfLog(await LogAsync(harness).ConfigureAwait(true)).For(Typed.Agent(Alice));

        Assert.True(
            verified.Skipped.Any(r => r.LogIndex == chained && r.Predicate == "rotation-unauthorized"),
            $"Expected: skipped rotation-unauthorized at log_index {chained}  Actual: {Describe(verified)}");
    }

    /// <summary>LRV5 (cases 25, 26): after a forged retirement, a genuine rotation whose <c>prev</c> is the last honoured index is honoured, and the forged row is skipped.</summary>
    [Fact]
    public async Task LRV5_AGenuineRotationAfterAForgedRetirementIsHonouredAtTheLastHonouredPrev()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var second = TestIdentity.New("alice-2");
        var forged = await AppendRowAsync(harness, Alice, "agent.key-retired", Retirement(Alice, victim.Kid, 1, agentId: Alice)).ConfigureAwait(true);
        var genuine = await AppendRowAsync(harness, Alice, "agent.key-bound", SignedRotation(harness, victim, second, 1)).ConfigureAwait(true);

        var verified = VerifierOf(harness).OfLog(await LogAsync(harness).ConfigureAwait(true)).For(Typed.Agent(Alice));

        Assert.True(
            verified.Skipped.Select(r => r.LogIndex).SequenceEqual([forged]) && genuine != forged && verified.Honoured.StatusNow(second.Kid) == KeyStatus.Active && verified.Honoured.StatusNow(victim.Kid) == KeyStatus.Active,
            $"Expected: only row {forged} skipped, {second.Kid} and {victim.Kid} Active  Actual: {Describe(verified)}");
    }

    /// <summary>LRV6: the binding list is the honoured bindings. A forged rotation's key is not in <c>Keys</c> and <c>Binding.For(attackerKid)</c> is null; the enrolment's key and a genuine rotation's are present and held.</summary>
    [Fact]
    public async Task LRV6_TheBindingListIsTheHonouredBindings()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var second = TestIdentity.New("alice-2");
        var attacker = TestIdentity.New("ka-attacker");
        Require(await harness.RotateAsync(Alice, victim, second).ConfigureAwait(true));
        await AppendRowAsync(harness, Alice, "agent.key-bound", Rotation(Alice, victim.Kid, attacker.Material, 2)).ConfigureAwait(true);

        var binding = VerifierOf(harness).OfLog(await LogAsync(harness).ConfigureAwait(true)).For(Typed.Agent(Alice)).Binding;

        Assert.NotNull(binding);
        Assert.True(binding.For(victim.Material)?.Holds(victim.Material) == true && binding.For(second.Material)?.Holds(second.Material) == true, "the enrolment's key and the genuine rotation's key are held");
        Assert.Null(binding.For(Typed.Kid(attacker.Kid)));
        Assert.DoesNotContain(binding.Keys, k => k.Kid == attacker.Kid);
    }

    /// <summary>LRV10: the served key set (<c>KeySetAsync</c>, which <c>GetJwks</c> serves) lists the keys the honoured rows bind, and the lifecycle indices are the honoured rows only.</summary>
    [Fact]
    public async Task LRV10_TheServedKeySetIsTheHonouredRowsOnly()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var attacker = TestIdentity.New("ka-attacker");
        Require(await harness.Registry.AddAsync(Typed.Agent(Alice), Copy(attacker.Material), Start, Ct).ConfigureAwait(true));
        var forged = await AppendRowAsync(harness, Alice, "agent.key-bound", Rotation(Alice, victim.Kid, attacker.Material, 1)).ConfigureAwait(true);
        var verifier = VerifierOf(harness);
        var log = await LogAsync(harness).ConfigureAwait(true);
        var keys = new LogBoundKeys(new RegistryResolver(harness.Registry), harness.Registry, harness.Events, verifier);

        var set = Require(await keys.KeySetAsync(Typed.Agent(Alice), Ct).ConfigureAwait(true));
        var acta = Require(Curia.Application.Projections.ActaLog.Fold(log));
        var lifecycle = verifier.OfLog(log).For(Typed.Agent(Alice)).Honoured.Entries.Select(e => acta.IndexOf(e.EventId)!.Value).ToArray();

        Assert.True(
            set.Bound.Select(b => b.Key.Key.Kid).SequenceEqual([victim.Kid]) && lifecycle.SequenceEqual([0L, 1L]),
            $"Expected: Bound [{victim.Kid}], lifecycle [0, 1]  Actual: Bound [{string.Join(", ", set.Bound.Select(b => b.Key.Key.Kid))}], lifecycle [{string.Join(", ", lifecycle)}] (the forged row is {forged})");
    }
    private static readonly string[] RootVectors = ["declaration-honoured", "rotation-honoured", "enrolment-pair-only"];

    /// <summary>One mutation of the enrolment (index 0) or the pair (index 1) of a vector, by name: the payload as an object, or the payload that replaces it.</summary>
    private static readonly (string Name, int Index, Func<Curia.Canon.Json.JsonValue.Object, Curia.Canon.Json.JsonValue> Mutate)[] RootMutations =
    [
        ("enrolment-payload-not-an-object", 0, _ => S("not-an-enrolment")),
        ("enrolment-agent-id-removed", 0, o => Without(o, "agent_id")),
        ("enrolment-agent-id-another-agent", 0, o => With(o, "agent_id", S("agent://curia.example/tuesdaycrowd/other"))),
        ("enrolment-kid-removed", 0, o => Without(o, "kid")),
        ("enrolment-kid-another-value", 0, o => With(o, "kid", S("another-kid"))),
        ("enrolment-extra-member", 0, o => With(o, "extra", S("x"))),
        ("pair-agent-id-another-agent", 1, o => With(o, "agent_id", S("agent://curia.example/tuesdaycrowd/other"))),
        ("pair-jwk-removed", 1, o => Without(o, "jwk")),
        ("pair-kid-another-value", 1, o => With(o, "kid", S("another-kid"))),
    ];

    public static TheoryData<string, string> RootCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var vector in RootVectors)
            foreach (var mutation in RootMutations)
                data.Add(vector, mutation.Name);
        return data;
    }

    /// <summary>
    /// Amendment 8, ruling 97: LRV41 runs nine mutations over three vectors, 27 rows, and each vector is a directory of <c>conformance/honoured/</c>. A vector or a mutation that left the tables would
    /// otherwise make the theory run fewer rows and stay green.
    /// </summary>
    [Fact]
    public void LRV41_TheTheoryRunsTwentySevenRowsOverThreeHonouredVectors()
    {
        var rows = RootCases().Select(r => ((ITheoryDataRow)r).GetData()).Select(d => (Vector: (string)d[0]!, Mutation: (string)d[1]!)).ToArray();
        var vectors = rows.Select(r => r.Vector).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var distinct = rows.Distinct().Count();

        Assert.Equal(
            "27 rows, 27 distinct, over declaration-honoured, enrolment-pair-only, rotation-honoured",
            $"{rows.Length} rows, {distinct} distinct, over {string.Join(", ", vectors)}");
        foreach (var vector in vectors)
        {
            Assert.Contains(vector, ConformanceLog.Names("honoured"));
            Assert.True(Directory.Exists(Path.Combine(ConformanceLog.Root(), "honoured", vector)), $"Expected: conformance/honoured/{vector} is a directory");
        }
    }

    private static Curia.Canon.Json.JsonValue.Object Without(Curia.Canon.Json.JsonValue.Object o, string member) =>
        new([.. o.Members.Where(m => m.Key != member)]);

    private static Curia.Canon.Json.JsonValue.Object With(Curia.Canon.Json.JsonValue.Object o, string member, Curia.Canon.Json.JsonValue value) =>
        new([.. o.Members.Where(m => m.Key != member).Append(new(member, value))]);

    /// <summary>
    /// LRV41 (errata G68, rulings 79, 80): whatever the enrolment or its pair state, <c>For</c> does not throw, every honoured entry is an event the verifier reports honoured, and no honoured
    /// entry is a row <c>KeyLifecycle.Of</c> would refuse (that is <c>For</c> returning). The inputs are three honoured vectors read from their files and mutated in memory.
    /// </summary>
    [Theory]
    [MemberData(nameof(RootCases))]
    public void LRV41_NoRootThePairOrTheEnrolmentCanStateMakesTheHonouredRowsUnreadable(string vector, string mutation)
    {
        var (_, index, mutate) = RootMutations.Single(m => m.Name == mutation);
        var log = ConformanceLog.Load("honoured", vector);
        var events = log.Log.ToList();
        var original = events[index];
        var payload = (Curia.Canon.Json.JsonValue.Object)original.Event.Payload;
        events[index] = LogEvents.At(index, original.AggregateId.Value, original.ServerTimestamp.Value, original.Event.Id.Value, original.Event.Type.Value, original.Event.Actor!.Value.Value, mutate(payload));

        VerifiedLifecycle? verified = null;
        var thrown = Record.Exception(() => verified = ConformanceLog.Verifier().OfLog(events).For(Typed.Agent(log.Agent)));

        Assert.True(thrown is null, $"Expected: For does not throw  Actual: {thrown?.GetType().Name}: {thrown?.Message}");
        var skipped = verified!.Skipped.Select(r => r.LogIndex).ToHashSet();
        foreach (var entry in verified.Honoured.Entries)
        {
            var at = events.FindIndex(e => e.Event.Id.Value == entry.EventId);
            Assert.True(at >= 0 && !skipped.Contains(at), $"Expected: honoured entry {entry.EventId} is an event the verifier reports honoured  Actual: log index {at}, skipped [{string.Join(", ", skipped)}]");
        }
    }
}
