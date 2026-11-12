using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
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
            $"skipped [{string.Join(", ", skipped)}]; refusals {alice.Refusal?.Type ?? "none"}/{bob.Refusal?.Type ?? "none"}; "
            + $"alice-1 {alice.Honoured.StatusNow(a1.Kid)}, alice-2 {alice.Honoured.StatusNow(a2.Kid)}, bob-1 {bob.Honoured.StatusNow(b1.Kid)}, bob-2 {bob.Honoured.StatusNow(b2.Kid)}; "
            + $"honoured rows {alice.Honoured.Entries.Length}+{bob.Honoured.Entries.Length}";
        WholeText.Same(
            "skipped []; refusals none/none; alice-1 Retired, alice-2 Active, bob-1 Compromised, bob-2 Compromised; honoured rows 4+4",
            actual);
        _ = written;
    }
}
