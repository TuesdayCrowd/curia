using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;
using static Curia.Application.Tests.Credentials.LifecycleProbeSupport;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// Errata G68, R4.40, R5.24, R6.64 (ruling 4: no request is refused because of a row's content): a lifecycle row that does not verify is skipped, counted and inert. LRV4,
/// LRV7 and LRV8. Each shape is built the way the credential can write it, with no use case in front.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ForgedRowsAreInertTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The nine shapes of LRV4, with the predicate R6.64 (and the honoured vector of the same shape) gives each.</summary>
    public static TheoryData<string, string> Shapes => new()
    {
        { "retirement-agent-id-absent", "statement-mismatch" },
        { "payload-not-an-object", "statement-mismatch" },
        { "unknown-statement-typ", "unknown-lifecycle-kind" },
        { "extra-member", "statement-mismatch" },
        { "statement-less-binding-after-history", "broken-link" },
        { "zero-signature-retirement", "lifecycle-typ-mismatch" },
        { "forged-rotation", "lifecycle-typ-mismatch" },
        { "declaration-under-the-log-key", "declaration-key-unpublished" },
        { "wrong-prev", "broken-link" },
    };

    private static async Task<long> AppendShapeAsync(KeyActHarness harness, TestIdentity victim, string shape)
    {
        var attacker = TestIdentity.New("attacker-1");
        switch (shape)
        {
            case "retirement-agent-id-absent":
                return await AppendRowAsync(harness, Alice, "agent.key-retired", Retirement(Alice, victim.Kid, 1, agentId: null)).ConfigureAwait(true);

            case "payload-not-an-object":
                return await AppendRowAsync(harness, Alice, "agent.key-retired", S("not an object")).ConfigureAwait(true);

            case "unknown-statement-typ":
                return await AppendRowAsync(harness, Alice, "agent.key-retired", Retirement(Alice, victim.Kid, 1, "curia.key-retirement/9", Alice)).ConfigureAwait(true);

            case "extra-member":
            {
                var row = Retirement(Alice, victim.Kid, 1, agentId: Alice);
                return await AppendRowAsync(harness, Alice, "agent.key-retired", new JsonValue.Object([.. row.Members, new("surprise", S("x"))])).ConfigureAwait(true);
            }

            case "statement-less-binding-after-history":
            {
                var second = TestIdentity.New("alice-2");
                Require(await harness.RotateAsync(Alice, victim, second).ConfigureAwait(true));
                var binding = new JsonValue.Object(
                [
                    new("agent_id", S(Alice)),
                    new("jwk", Require(PublicJwk.Of(attacker.Material))),
                    new("kid", S(attacker.Kid)),
                ]);
                return await AppendRowAsync(harness, Alice, "agent.key-bound", binding).ConfigureAwait(true);
            }

            case "zero-signature-retirement":
            {
                var row = Retirement(Alice, victim.Kid, 1, agentId: Alice);
                return await AppendRowAsync(
                    harness, Alice, "agent.key-retired",
                    new JsonValue.Object([.. row.Members.Select(m => m.Key == "authorization" ? new KeyValuePair<string, JsonValue>("authorization", S(string.Empty)) : m)])).ConfigureAwait(true);
            }

            case "forged-rotation":
                return await AppendRowAsync(harness, Alice, "agent.key-bound", Rotation(Alice, victim.Kid, attacker.Material, 1)).ConfigureAwait(true);

            case "declaration-under-the-log-key":
            {
                // A genuine-looking JWS, under the declaration header, by a key the log never published as a declaration key.
                var statement = ((JsonValue.String)Declaration(Alice, [victim.Kid], 1, 1, "x").Members.First(m => m.Key == "statement").Value).Value;
                var signature = harness.Sign(statement, KeyStatements.CompromiseHeaderTyp, TestIdentity.New("log-key").Signing).Value;
                return await AppendRowAsync(harness, Alice, "agent.key-compromised", Declaration(Alice, [victim.Kid], 1, 1, signature)).ConfigureAwait(true);
            }

            case "wrong-prev":
                return await AppendRowAsync(harness, Alice, "agent.key-retired", Retirement(Alice, victim.Kid, 41, agentId: Alice)).ConfigureAwait(true);

            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, "not one of LRV4's nine shapes");
        }
    }

    /// <summary>LRV4: a row never refuses. For each shape the victim's lifecycle is returned, the row is skipped with its predicate, and the victim's key is active.</summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task LRV4_AForgedRowIsSkippedAndNeverRefuses(string shape, string predicate)
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var index = await AppendShapeAsync(harness, victim, shape).ConfigureAwait(true);

        var verified = VerifierOf(harness).OfLog(await LogAsync(harness).ConfigureAwait(true)).For(Typed.Agent(Alice));

        Assert.True(
            verified.Skipped.Any(r => r.LogIndex == index && r.Predicate == predicate) && verified.Honoured.StatusNow(victim.Kid) == KeyStatus.Active,
            $"Expected: a lifecycle, row {index} skipped {predicate}, {victim.Kid} Active  Actual: {Describe(verified)} ({victim.Kid} {verified.Honoured.StatusNow(victim.Kid)})");
    }

    private static async Task<(KeyActHarness Harness, TestIdentity Victim, TestIdentity Attacker, LogBoundKeys Keys)> WithAttackerKeyHeldAsync()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var attacker = TestIdentity.New("attacker-1");
        Require(await harness.Registry.AddAsync(Typed.Agent(Alice), Copy(attacker.Material), KeyActHarness.Start, Ct).ConfigureAwait(true));
        return (harness, victim, attacker, new LogBoundKeys(new RegistryResolver(harness.Registry), harness.Registry, harness.Events, VerifierOf(harness)));
    }

    /// <summary>LRV7 (a), the control: a store row for the victim's id and an attacker kid, and no log row naming it, is refused by R4.35 already; the victim's real key resolves live.</summary>
    [Fact]
    public async Task LRV7_AStoreRowWithNoLogRowIsRefused_Control()
    {
        var (harness, victim, attacker, keys) = await WithAttackerKeyHeldAsync().ConfigureAwait(true);
        var at = ServerTimestamp.At(KeyActHarness.Start.AddHours(1));

        var attackerKey = await keys.ResolveLiveAsync(Typed.Agent(Alice), Typed.Kid(attacker.Kid), at, Ct).ConfigureAwait(true);
        var victimKey = await keys.ResolveLiveAsync(Typed.Agent(Alice), Typed.Kid(victim.Kid), at, Ct).ConfigureAwait(true);

        WholeText.Same(
            "attacker: curia/keys/not-bound-by-the-log; victim: LiveKey",
            $"attacker: {attackerKey.Match(_ => "LiveKey", e => e.Type)}; victim: {victimKey.Match(_ => "LiveKey", e => e.Type)}");
        _ = harness;
    }

    /// <summary>LRV7 (b): a store row and a forged statement-bearing <c>agent.key-bound</c> naming the attacker's key. Nothing resolves under it, its status is Unbound, and the victim's key still resolves.</summary>
    [Fact]
    public async Task LRV7_AStoreRowAndAForgedBindingDoNotMakeTheKeyLive()
    {
        var (harness, victim, attacker, keys) = await WithAttackerKeyHeldAsync().ConfigureAwait(true);
        var at = ServerTimestamp.At(KeyActHarness.Start.AddHours(1));
        await AppendRowAsync(harness, Alice, "agent.key-bound", Rotation(Alice, victim.Kid, attacker.Material, 1)).ConfigureAwait(true);

        var attackerKey = await keys.ResolveLiveAsync(Typed.Agent(Alice), Typed.Kid(attacker.Kid), at, Ct).ConfigureAwait(true);
        var status = await keys.StatusAsync(Typed.Agent(Alice), Typed.Kid(attacker.Kid), Ct).ConfigureAwait(true);
        var victimKey = await keys.ResolveLiveAsync(Typed.Agent(Alice), Typed.Kid(victim.Kid), at, Ct).ConfigureAwait(true);

        WholeText.Same(
            "attacker: curia/keys/not-bound-by-the-log; status: Unbound; victim: LiveKey",
            $"attacker: {attackerKey.Match(_ => "LiveKey", e => e.Type)}; status: {status.Match(s => s.ToString(), e => e.Type)}; victim: {victimKey.Match(_ => "LiveKey", e => e.Type)}");
    }

    /// <summary>
    /// LRV8 (a): a forged retirement and a forged declaration for the victim's live kid leave it Active. The declaration's <c>prev</c> is 1, the last honoured index (the skipped retirement moved nothing),
    /// so each row is decided on its own defect and the predicates are asserted: the retirement's signature is bad (<c>rotation-unauthorized</c>) and the declaration's is no JWS (<c>lifecycle-typ-mismatch</c>) (ruling 86).
    /// </summary>
    [Fact]
    public async Task LRV8_AForgedRetirementAndDeclarationLeaveTheVictimsKeyActive()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);

        // The retirement has the right header (the retirement typ and the victim's kid) and another key's signature bytes, so it reaches the signature check: the helper's own "not-a-signature" is no JWS and is skipped
        // at the header (lifecycle-typ-mismatch) before the signature is read.
        var plain = Retirement(Alice, victim.Kid, 1, agentId: Alice);
        var statement = ((JsonValue.String)plain.Members.First(m => m.Key == "statement").Value).Value;
        var byAnotherKey = harness.Sign(statement, KeyStatements.RetirementHeaderTyp, new SigningKey("ES256", victim.Kid, TestIdentity.New("stranger").Signing.Private.ToArray())).Value;
        var forged = new JsonValue.Object([.. plain.Members.Select(m => m.Key == "authorization" ? new KeyValuePair<string, JsonValue>("authorization", S(byAnotherKey)) : m)]);
        var retired = await AppendRowAsync(harness, Alice, "agent.key-retired", forged).ConfigureAwait(true);
        var declared = await AppendRowAsync(harness, Alice, "agent.key-compromised", Declaration(Alice, [victim.Kid], 1, 1, "not-a-signature")).ConfigureAwait(true);
        var keys = new LogBoundKeys(new RegistryResolver(harness.Registry), harness.Registry, harness.Events, VerifierOf(harness));

        var status = await keys.StatusAsync(Typed.Agent(Alice), Typed.Kid(victim.Kid), Ct).ConfigureAwait(true);
        var predicates = VerifierOf(harness).OfLog(await LogAsync(harness).ConfigureAwait(true)).For(Typed.Agent(Alice)).Skipped.ToDictionary(r => r.LogIndex, r => r.Predicate);

        WholeText.Same(
            $"{retired} rotation-unauthorized; {declared} lifecycle-typ-mismatch; Active",
            $"{retired} {predicates.GetValueOrDefault(retired, "none")}; {declared} {predicates.GetValueOrDefault(declared, "none")}; {status.Match(s => s.ToString(), e => "Refused " + e.Type)}");
    }

    /// <summary>A reader whose every read fails, as a store fault does.</summary>
    private sealed class FaultingReader : IEventReader
    {
        private static readonly Error Fault = new("curia/store/unavailable", "The store is unavailable");

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(Fault));

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(Fault));
    }

    /// <summary>LRV8 (b), the control for FLRV19: a read that cannot be made refuses transiently on every path, never Active, Unbound or Retired (ruling 4, R5.24).</summary>
    [Fact]
    public async Task LRV8_AReadThatCannotBeMadeIsUnreadableNeverAnAnswer_Control()
    {
        var (harness, victim) = await EnrolledAsync().ConfigureAwait(true);
        var keys = new LogBoundKeys(new RegistryResolver(harness.Registry), harness.Registry, new FaultingReader(), VerifierOf(harness));
        var at = ServerTimestamp.At(KeyActHarness.Start.AddHours(1));

        var status = await keys.StatusAsync(Typed.Agent(Alice), Typed.Kid(victim.Kid), Ct).ConfigureAwait(true);
        var resolve = await keys.ResolveLiveAsync(Typed.Agent(Alice), Typed.Kid(victim.Kid), at, Ct).ConfigureAwait(true);
        var set = await keys.KeySetAsync(Typed.Agent(Alice), Ct).ConfigureAwait(true);

        WholeText.Same(
            "curia/log/unreadable; curia/log/unreadable; curia/log/unreadable",
            $"{status.Match(s => s.ToString(), e => e.Type)}; {resolve.Match(_ => "LiveKey", e => e.Type)}; {set.Match(_ => "KeySet", e => e.Type)}");
    }
}
