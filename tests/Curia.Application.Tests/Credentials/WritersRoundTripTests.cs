using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;
using static Curia.Application.Tests.Credentials.KeyActHarness;
using static Curia.Application.Tests.Credentials.LifecycleProbeSupport;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// LRV1, the probe that guards the fail-open (errata G68, non-property (f)): a verifier that skips a genuine row leaves a compromised key live and says nothing. Each
/// writer is run with real signers on the in-memory adapters, the log it wrote is handed to the verifier, and every row it appended is honoured with the status it intended.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class WritersRoundTripTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ActorId Operator = Require(ActorId.Create("operator:ops"));

    /// <summary>Publishes <paramref name="signer"/>'s key as the operator's publish-declaration-key does (<c>log.declaration-key</c>, R6.62).</summary>
    private static async Task PublishAsync(KeyActHarness harness, InMemoryLifecycleStatementSigner signer)
    {
        var acta = Require(ActaLog.Fold(await LogAsync(harness).ConfigureAwait(true)));
        var payload = new JsonValue.Object(
        [
            new(LogEntries.KidMember, new JsonValue.String(signer.Kid)),
            new(LogEntries.JwkMember, signer.PublicJwk()),
            new(LogEntries.ValidFromMember, new JsonValue.String(LogLeaf.RenderServerTimestamp(ServerTimestamp.At(harness.Clock.GetUtcNow())))),
        ]);
        Require(await harness.Events.AppendAsync(
            Require(AggregateId.Create(LogEntries.DeclarationKeysAggregate)),
            Require(AggregateVersion.From(acta.DeclarationKeys.Length)),
            [new DomainEvent(Require(EventId.Create("01K4CQ1V9P3R5T7W9Y1A3C5EKY")), Require(EventType.Create(LogEntries.DeclarationKeyType)), Operator, payload)],
            Ct).ConfigureAwait(true));
    }

    /// <summary>
    /// Alice enrolls, rotates (<c>alice-1</c> to <c>alice-2</c>) and retires <c>alice-1</c>; Bob enrolls, rotates, and Bob's first key is declared compromised from its binding.
    /// Every row any writer appended is honoured, and each key has the status its writer intended.
    /// </summary>
    [Fact]
    public async Task LRV1_EveryRowTheWritersAppendedIsHonouredWithTheStatusItIntended_Control()
    {
        var harness = new KeyActHarness();
        var (a1, a2) = (TestIdentity.New("alice-1"), TestIdentity.New("alice-2"));
        var (b1, b2) = (TestIdentity.New("bob-1"), TestIdentity.New("bob-2"));
        await harness.EnrollAsync(Alice, a1).ConfigureAwait(true);
        await harness.EnrollAsync(Bob, b1).ConfigureAwait(true);
        var written = new List<long>
        {
            Require(await harness.RotateAsync(Alice, a1, a2).ConfigureAwait(true)).LogIndex,
            Require(await harness.RotateAsync(Bob, b1, b2).ConfigureAwait(true)).LogIndex,
        };
        written.Add(Require(await harness.RetireAsync(Alice, a1).ConfigureAwait(true)).LogIndex);

        using var signer = new InMemoryLifecycleStatementSigner();
        await PublishAsync(harness, signer).ConfigureAwait(true);
        var bindingOfBob1 = (await LogAsync(harness).ConfigureAwait(true)).Select((e, i) => (e, i)).First(x => x.e.AggregateId.Value == Bob && x.e.Event.Type.Value == AgentStandingProjector.KeyBoundType).i;
        var declared = Require(await new DeclareCompromise(harness.Events, VerifierOf(harness), harness.Clock)
            .DeclareAsync(Typed.Agent(Bob), Typed.Kid(b1.Kid), fromIndex: bindingOfBob1, CompromiseReason.OwnerReported, signer, Operator, Ct).ConfigureAwait(true));
        written.Add(declared.LogIndex);

        var log = await LogAsync(harness).ConfigureAwait(true);
        var verified = VerifierOf(harness).OfLog(log);
        var alice = verified.For(Typed.Agent(Alice));
        var bob = verified.For(Typed.Agent(Bob));
        var skipped = verified.Skipped().Select(r => r.LogIndex).Concat(alice.Skipped.Select(r => r.LogIndex)).Concat(bob.Skipped.Select(r => r.LogIndex)).ToArray();

        var actual =
            $"skipped [{string.Join(", ", skipped)}]; "
            + $"alice-1 {alice.Honoured.StatusNow(a1.Kid)}, alice-2 {alice.Honoured.StatusNow(a2.Kid)}, bob-1 {bob.Honoured.StatusNow(b1.Kid)}, bob-2 {bob.Honoured.StatusNow(b2.Kid)}; "
            + $"honoured rows {alice.Honoured.Entries.Length}+{bob.Honoured.Entries.Length}";
        WholeText.Same(
            "skipped []; alice-1 Retired, alice-2 Active, bob-1 Compromised, bob-2 Compromised; honoured rows 4+4",
            actual);
        _ = written;
    }
    /// <summary>
    /// A store that replaces one member of the payload of every entry it appends (the writers' signature member) before delegating: the row lands in the log with a signature no key made, which is a row the
    /// fold skips, however well the writer built it.
    /// </summary>
    private sealed class SignatureTamperingStore(IEventStore inner, string member) : IEventStore
    {
        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
            inner.ReadByAggregateAsync(aggregateId, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) =>
            inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);

        private IReadOnlyList<DomainEvent> Tamper(IReadOnlyList<DomainEvent> events) =>
        [
            .. events.Select(e => e.Payload is JsonValue.Object o && o.Members.Any(m => m.Key == member)
                ? e with { Payload = new JsonValue.Object([.. o.Members.Select(m => m.Key == member ? new KeyValuePair<string, JsonValue>(member, new JsonValue.String("x")) : m)]) }
                : e),
        ];

        public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(aggregateId, expectedVersion, Tamper(events), cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition condition, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(aggregateId, expectedVersion, Tamper(events), condition, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition? condition, AppendGuard guard, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(aggregateId, expectedVersion, Tamper(events), condition, guard, cancellationToken);
    }

    /// <summary>
    /// LRV43 (errata G68, ruling 81, F-H2): a writer never reports success for a row the Forum skips. Each writer's entry is stored with its signature member replaced by <c>x</c>; the writer must return
    /// <c>curia/keys/not-honoured</c> naming the row's log index and <c>lifecycle-typ-mismatch</c>, the row is in the log (the writer says so), and the verifier skips it at that index. Red on the tip: each writer
    /// returned success with the index.
    /// </summary>
    [Theory]
    [InlineData("rotate")]
    [InlineData("retire")]
    [InlineData("declare")]
    public async Task LRV43_AWriterThatAppendedARowTheForumSkipsSaysSo(string writer)
    {
        var harness = new KeyActHarness();
        var (a1, a2) = (TestIdentity.New("alice-1"), TestIdentity.New("alice-2"));
        await harness.EnrollAsync(Alice, a1).ConfigureAwait(true);

        // A retirement needs a second live key to be retired from: the rotation that binds it is honest.
        if (writer == "retire") Require(await harness.RotateAsync(Alice, a1, a2).ConfigureAwait(true));
        using var signer = new InMemoryLifecycleStatementSigner();
        await PublishAsync(harness, signer).ConfigureAwait(true);
        var expectedIndex = (await LogAsync(harness).ConfigureAwait(true)).Count;
        var member = writer == "declare" ? "declaration" : "authorization";
        harness.Store = new SignatureTamperingStore(harness.Events, member);

        var outcome = writer switch
        {
            "rotate" => (await harness.RotateAsync(Alice, a1, a2).ConfigureAwait(true)).Map(_ => 0L),
            "retire" => (await harness.RetireAsync(Alice, a1).ConfigureAwait(true)).Map(_ => 0L),
            _ => (await new DeclareCompromise(harness.Writer, VerifierOf(harness), harness.Clock)
                .DeclareAsync(Typed.Agent(Alice), Typed.Kid(a1.Kid), fromIndex: 1, CompromiseReason.OwnerReported, signer, Operator, Ct).ConfigureAwait(true)).Map(_ => 0L),
        };

        var log = await LogAsync(harness).ConfigureAwait(true);
        var skipped = VerifierOf(harness).OfLog(log).For(Typed.Agent(Alice)).Skipped.Select(r => $"{r.LogIndex} {r.Predicate}").ToArray();
        var actual = outcome.Match(_ => "ok", e => $"{e.Type} {e.Detail}") + $"; log {log.Count}; skipped [{string.Join(", ", skipped)}]";

        WholeText.Same(
            $"curia/keys/not-honoured the row was appended at log index {expectedIndex} and the Forum skips it (lifecycle-typ-mismatch); it ends, binds and marks no key; log {expectedIndex + 1}; skipped [{expectedIndex} lifecycle-typ-mismatch]",
            actual);
    }

    /// <summary>LRV43's control: with no decorator each writer returns success and its entry is among the honoured ones.</summary>
    [Fact]
    public async Task LRV43_Control_AWriterWhoseRowIsHonouredReturnsSuccess()
    {
        var harness = new KeyActHarness();
        var (a1, a2) = (TestIdentity.New("alice-1"), TestIdentity.New("alice-2"));
        await harness.EnrollAsync(Alice, a1).ConfigureAwait(true);

        var rotated = Require(await harness.RotateAsync(Alice, a1, a2).ConfigureAwait(true));

        var honoured = VerifierOf(harness).OfLog(await LogAsync(harness).ConfigureAwait(true)).For(Typed.Agent(Alice)).Honoured.Entries.Length;
        Assert.Equal("rotation at 2; honoured 3", $"rotation at {rotated.LogIndex}; honoured {honoured}");
    }
}
