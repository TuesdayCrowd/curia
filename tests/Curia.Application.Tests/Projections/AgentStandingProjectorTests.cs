using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// The projection that replaced <c>Curia.Api.AgentDirectory</c>: an agent's standing -- when it
/// enrolled, whether its owner is verified, and when it first met T1 -- folded out of the
/// append-only log rather than remembered by a process.
///
/// <para><b>Every <see cref="AppendedEvent"/> below comes from a real append through
/// <see cref="InMemoryEventStore"/>, never fabricated</b> --
/// <c>Curia.Architecture.Tests.EventStoreWriteSurfaceTests</c> (CS-15) scans this assembly's IL
/// for exactly that, and the in-memory store is the only type in this project on the intended
/// write surface.</para>
///
/// <para>Run against the in-memory adapter rather than Postgres because these are the projector's
/// own rules -- what a fold produces, and what a replay from zero reproduces. The restart-shaped
/// version, against the real composition root and a real database, is
/// <c>Curia.Api.Tests.AgentStandingDurabilityTests</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs (R4.21, R11.9) they enforce verbatim, " +
        "mirroring this solution's existing convention.")]
public sealed class AgentStandingProjectorTests
{
    private const string Agent = "https://agents.example/aurelia";
    private const string Kid = "aurelia-1";
    private const string Other = "https://agents.example/other";
    private const string Owner = "owner:example";
    private const string Operator = "operator:reviewer";

    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(
        InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

    /// <summary>
    /// R4.30's attestation, as an operator records it: the same use case the operator tool calls,
    /// so these tests exercise the deployment path rather than a hand-built event.
    /// </summary>
    private static Task<Result<OwnerAttestation>> AttestAsync(
        InMemoryEventStore store,
        ManualTimeProvider clock,
        string agent,
        CancellationToken ct,
        bool verified = true,
        string owner = Owner,
        string by = Operator,
        OwnerVerificationMethod method = OwnerVerificationMethod.Manual) =>
        new AttestOwner(store, clock).RecordAsync(
            agent,
            Require(OwnerId.Create(owner)),
            verified,
            method,
            "reviewed by an operator",
            Require(ActorId.Create(by)),
            ct);

    /// <summary>Enrolment followed by attestation -- what an agent needs before Table 11's T1 row can hold.</summary>
    private static async Task EnrollVerifiedAsync(
        InMemoryEventStore store, ManualTimeProvider clock, string agent, string kid, CancellationToken ct)
    {
        Require(await new EnrollAgent(store, clock).RecordAsync(agent, kid, ct).ConfigureAwait(false));
        Require(await AttestAsync(store, clock, agent, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Appends a <c>post.accepted</c> event shaped the way <c>IngestPipeline.PersistAsync</c>
    /// shapes one -- only the members this projection reads, since a projection that needed the
    /// whole payload would be a second read model rather than a posture fold.
    /// </summary>
    private static async Task AcceptPostAsync(
        InMemoryEventStore store, string author, string kind, string postId, CancellationToken ct)
    {
        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(postId)),
            new("author", new JsonValue.String(author)),
            new("kind", new JsonValue.String(kind)),
        ]);

        var appended = await store.AppendAsync(
            Require(AggregateId.Create(postId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(postId)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create(author)),
                payload)],
            ct).ConfigureAwait(false);

        Require(appended);
    }

    /// <summary>
    /// R4.21: enrollment is an append-only event, and the credential state is a projection of it.
    /// The instant is the store's <c>server_ts</c>, not anything the request supplied -- and
    /// neither is the owner's verification, which enrollment cannot set (R4.30).
    /// </summary>
    [Fact]
    public async Task R4_21_EnrollmentFoldsIntoAnActiveCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        var recorded = Require(await enroll.RecordAsync(Agent, Kid, ct));

        Assert.Equal(Start, recorded.EnrolledAt);
        Assert.False(recorded.WasAlreadyEnrolled);
        Assert.False(recorded.OwnerVerified);

        var facts = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));

        Assert.Equal(CredentialState.Active, facts.CredentialState);
        Assert.Equal(Start, facts.EnrolledAt);
        Assert.False(facts.OwnerVerified);
    }

    /// <summary>
    /// Errata G5 / plan D2: an enrollment event's own <c>owner_verified</c> member was a claim the
    /// enrolling agent made about itself, and the fold no longer honours it -- while still honouring
    /// the enrollment beside it. Appended raw, shaped exactly as <c>EnrollAgent</c> shaped it before
    /// the member was removed, because the log is append-only and every deployed Forum's history
    /// carries events of this shape forever. The legacy <c>agent.owner-verification-recorded</c>
    /// event, which the same request body produced, is inert for the same reason.
    ///
    /// <para>This one test carries both halves of the landmine: it fails if the projector keeps
    /// reading the flag from the enrollment event, and it fails if the member is left required and
    /// the event is skipped -- which would un-enrol every agent in every existing log, silently
    /// and in the direction that reads as policy.</para>
    /// </summary>
    [Fact]
    public async Task G5_ASelfAssertedOwnerVerificationInTheLogIsInertButTheEnrollmentIsNot()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var aggregate = Require(AggregateId.Create(Agent));

        Require(await store.AppendAsync(aggregate, AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create("legacy-enrolled")),
                Require(EventType.Create(AgentStandingProjector.EnrolledType)),
                Require(ActorId.Create(Agent)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Agent)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String(Kid)),
                    new(AgentStandingProjector.OwnerVerifiedField, new JsonValue.Bool(true)),
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted")),
                ]))],
            ct));

        Require(await store.AppendAsync(aggregate, Require(AggregateVersion.From(1)),
            [new DomainEvent(
                Require(EventId.Create("legacy-verified")),
                Require(EventType.Create(AgentStandingProjector.LegacyOwnerVerificationRecordedType)),
                Require(ActorId.Create(Agent)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Agent)),
                    new(AgentStandingProjector.OwnerVerifiedField, new JsonValue.Bool(true)),
                ]))],
            ct));

        var facts = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));

        Assert.Equal(CredentialState.Active, facts.CredentialState);
        Assert.Equal(Start, facts.EnrolledAt);
        Assert.False(facts.OwnerVerified);
    }

    /// <summary>An agent the log has never heard of has a pending credential and no tenure.</summary>
    [Fact]
    public async Task AnUnknownAgentHasNoStanding()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);

        var facts = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));

        Assert.Equal(CredentialState.Pending, facts.CredentialState);
        Assert.Null(facts.EnrolledAt);
        Assert.Equal(PrincipalTier.Anonymous, TierPolicy.Evaluate(facts, Start).Tier);
    }

    /// <summary>
    /// <b>The invariant the whole move exists to preserve.</b> Table 11 counts "≥ 48 hours" from
    /// enrollment, singular: a client that re-announces its enrollment -- which it legitimately
    /// does whenever it re-authenticates -- must not thereby restart its tenure clock. The guard
    /// is the store's own optimistic concurrency, so this also proves no second enrollment event
    /// reached the log at all.
    /// </summary>
    [Fact]
    public async Task ARepeatEnrollmentDoesNotRestartTheTenureClock()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));

        clock.Advance(TimeSpan.FromDays(8));
        var again = Require(await enroll.RecordAsync(Agent, Kid, ct));

        Assert.Equal(Start, again.EnrolledAt);
        Assert.True(again.WasAlreadyEnrolled);

        var log = await LogAsync(store, ct);
        Assert.Single(log);

        var facts = Require(AgentStandingProjector.PostureOf(AgentStandingProjector.Fold(log), Agent));
        Assert.Equal(Start, facts.EnrolledAt);
        Assert.Equal(CredentialState.Active, facts.CredentialState);
    }

    /// <summary>
    /// R4.30: owner verification enters the log by an operator's attestation, later than
    /// enrollment, and takes effect -- while the enrollment instant beside it does not move. A
    /// repeat enrollment afterwards reports the verified standing and appends nothing.
    /// </summary>
    [Fact]
    public async Task R4_30_OwnerVerificationIsAttestedLaterAndTakesEffect()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));

        clock.Advance(TimeSpan.FromDays(1));
        var attested = Require(await AttestAsync(store, clock, Agent, ct));

        Assert.True(attested.Verified);
        Assert.Equal(Start.AddDays(1), attested.AttestedAt);
        Assert.Equal(OwnerVerificationMethod.Manual, attested.Method);

        var again = Require(await enroll.RecordAsync(Agent, Kid, ct));
        Assert.True(again.OwnerVerified);
        Assert.Equal(Start, again.EnrolledAt);

        var log = await LogAsync(store, ct);
        Assert.Equal(2, log.Count);

        var standing = AgentStandingProjector.Fold(log)[Agent];
        Assert.Equal(Owner, standing.OwnerId);
        Assert.True(standing.OwnerVerified);
        Assert.Equal(Start, standing.EnrolledAt);
    }

    /// <summary>
    /// R4.21 and R4.30 together: the event carries the attesting actor, the owner, the proof and
    /// the reason, so a replay can say who attested what for whom -- the facts that could not be
    /// reconstructed later if the event held only a boolean.
    /// </summary>
    [Fact]
    public async Task R4_30_TheAttestationEventNamesTheActorTheOwnerAndTheProof()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, Kid, ct));
        Require(await AttestAsync(store, clock, Agent, ct, method: OwnerVerificationMethod.Domain));

        var attestation = (await LogAsync(store, ct))[1];
        var payload = Assert.IsType<JsonValue.Object>(attestation.Event.Payload);
        var members = payload.Members.ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);

        Assert.Equal(AgentStandingProjector.OwnerAttestedType, attestation.Event.Type.Value);
        Assert.Equal(Operator, attestation.Event.Actor?.Value);
        Assert.Equal(Owner, Assert.IsType<JsonValue.String>(members[AgentStandingProjector.OwnerIdField]).Value);
        Assert.Equal("domain", Assert.IsType<JsonValue.String>(members[AgentStandingProjector.MethodField]).Value);
        Assert.True(Assert.IsType<JsonValue.Bool>(members[AgentStandingProjector.OwnerVerifiedField]).Value);
        Assert.NotEmpty(Assert.IsType<JsonValue.String>(members[AgentStandingProjector.ReasonField]).Value);
    }

    /// <summary>
    /// R4.30: "SHALL NOT accept owner-verification status from an enrolling agent". The one identity
    /// the domain can recognise without an identifier scheme is the agent's own, and it is refused.
    /// </summary>
    [Fact]
    public async Task R4_30_AnAgentCannotAttestItsOwnOwner()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, Kid, ct));

        var refused = await AttestAsync(store, clock, Agent, ct, by: Agent);

        Assert.False(refused.TryGetValue(out _, out var error));
        Assert.Equal("curia/attest/self-attestation", error!.Type);
        Assert.Single(await LogAsync(store, ct));
    }

    /// <summary>An attestation for an agent the log has never enrolled is refused, and appends nothing.</summary>
    [Fact]
    public async Task R4_30_AnAttestationNeedsAnEnrollment()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);

        var refused = await AttestAsync(store, clock, Agent, ct);

        Assert.False(refused.TryGetValue(out _, out var error));
        Assert.Equal("curia/attest/not-enrolled", error!.Type);
        Assert.Empty(await LogAsync(store, ct));
    }

    /// <summary>
    /// R4.1: the agent-to-owner binding is immutable. A second attestation naming a different owner
    /// is refused by the use case; and should such an event reach the log by any other path, the
    /// fold keeps the first owner -- asserted with a raw append, because the use case refuses to
    /// write one.
    /// </summary>
    [Fact]
    public async Task R4_1_ASecondOwnerIsRefusedByTheUseCaseAndIgnoredByTheFold()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);

        await EnrollVerifiedAsync(store, clock, Agent, Kid, ct);

        var refused = await AttestAsync(store, clock, Agent, ct, owner: "owner:someone-else");
        Assert.False(refused.TryGetValue(out _, out var error));
        Assert.Equal("curia/attest/owner-binding-immutable", error!.Type);

        var aggregate = Require(AggregateId.Create(Agent));
        Require(await store.AppendAsync(aggregate, Require(AggregateVersion.From(2)),
            [new DomainEvent(
                Require(EventId.Create("stray-rehome")),
                Require(EventType.Create(AgentStandingProjector.OwnerAttestedType)),
                Require(ActorId.Create(Operator)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Agent)),
                    new(AgentStandingProjector.OwnerIdField, new JsonValue.String("owner:someone-else")),
                    new(AgentStandingProjector.OwnerVerifiedField, new JsonValue.Bool(false)),
                    new(AgentStandingProjector.MethodField, new JsonValue.String("manual")),
                ]))],
            ct));

        var standing = AgentStandingProjector.Fold(await LogAsync(store, ct))[Agent];
        Assert.Equal(Owner, standing.OwnerId);
        Assert.True(standing.OwnerVerified);
    }

    /// <summary>
    /// Verification can lapse (R4.24's proofs are not permanent), and a lapse is an attestation
    /// like any other: appended, and the latest one wins.
    /// </summary>
    [Fact]
    public async Task R4_30_ALapseIsAttestedAndTakesEffect()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);

        await EnrollVerifiedAsync(store, clock, Agent, Kid, ct);
        Require(await AttestAsync(store, clock, Agent, ct, verified: false));

        var standing = AgentStandingProjector.Fold(await LogAsync(store, ct))[Agent];
        Assert.False(standing.OwnerVerified);
        Assert.Equal(Owner, standing.OwnerId);
        Assert.Equal(3, (await LogAsync(store, ct)).Count);
    }

    /// <summary>
    /// Table 11's T1 row, end to end through the projection: 48 hours, three clean questions,
    /// owner verified. The tier is evaluated at an instant supplied by the caller -- the projection
    /// itself never reads a clock.
    /// </summary>
    [Fact]
    public async Task T1IsReachedOnlyWhenEveryTable11CriterionHolds()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        await EnrollVerifiedAsync(store, clock, Agent, Kid, ct);

        for (var i = 0; i < 3; i++)
            await AcceptPostAsync(store, Agent, "question", $"post-{i}", ct);

        var facts = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));

        Assert.Equal(3, facts.QuestionsWithoutUpheldFlags);

        // Day zero: the questions are in, the owner is verified, and the tenure is not.
        Assert.Equal(PrincipalTier.T0, TierPolicy.Evaluate(facts, Start).Tier);

        // Day eight: every criterion holds.
        Assert.Equal(PrincipalTier.T1, TierPolicy.Evaluate(facts, Start.AddDays(8)).Tier);
    }

    /// <summary>
    /// Answers, and other agents' questions, do not count toward Table 11's "≥ 3 questions".
    /// Asserted because a count that included everything would promote on the same evidence the
    /// row deliberately excludes.
    /// </summary>
    [Fact]
    public async Task OnlyTheAgentsOwnQuestionsCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));
        Require(await enroll.RecordAsync(Other, "other-1", ct));

        await AcceptPostAsync(store, Agent, "question", "mine-1", ct);
        await AcceptPostAsync(store, Agent, "answer", "mine-2", ct);
        await AcceptPostAsync(store, Other, "question", "theirs-1", ct);

        var standings = AgentStandingProjector.Fold(await LogAsync(store, ct));

        Assert.Equal(1, Require(AgentStandingProjector.PostureOf(standings, Agent)).QuestionsWithoutUpheldFlags);
        Assert.Equal(1, Require(AgentStandingProjector.PostureOf(standings, Other)).QuestionsWithoutUpheldFlags);
    }

    /// <summary>
    /// <see cref="TierPolicy.FirstSatisfiedT1At"/> through the fold: the T2 clock starts when T1
    /// was actually first met, which is the later of "48 hours after enrollment" and "the
    /// instant the log-derived criteria first held together" -- not whenever a request happened to
    /// notice the promotion.
    /// </summary>
    [Fact]
    public async Task ReachedT1IsDerivedFromTheLogRatherThanStampedByARequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        await EnrollVerifiedAsync(store, clock, Agent, Kid, ct);

        // The three questions land on day one, so the tenure condition is the binding one.
        clock.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < 3; i++)
            await AcceptPostAsync(store, Agent, "question", $"early-{i}", ct);

        var early = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));

        Assert.Equal(Start.AddHours(TierPolicy.T1MinimumHours), early.ReachedT1At);

        // A second agent, verified only on day twenty: there owner verification is binding, and no
        // amount of later reading moves the answer.
        Require(await enroll.RecordAsync(Other, "other-1", ct));
        for (var i = 0; i < 3; i++)
            await AcceptPostAsync(store, Other, "question", $"other-{i}", ct);

        clock.Advance(TimeSpan.FromDays(19));
        Require(await AttestAsync(store, clock, Other, ct));

        var late = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Other));

        Assert.Equal(Start.AddDays(20), late.ReachedT1At);
    }

    /// <summary>
    /// R11.9: the projection rebuilds from zero to identical state. Asserted against a paged
    /// forward scan as well as a single fold, because a rebuild of a real corpus is paged and a
    /// projector that only worked unpaged would pass the easy half of the drill.
    /// </summary>
    [Fact]
    public async Task R11_9_TheProjectionRebuildsFromZeroIdentically()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));
        await AcceptPostAsync(store, Agent, "question", "q-1", ct);

        clock.Advance(TimeSpan.FromDays(2));
        Require(await AttestAsync(store, clock, Agent, ct));
        await AcceptPostAsync(store, Agent, "question", "q-2", ct);
        await AcceptPostAsync(store, Agent, "question", "q-3", ct);

        var first = AgentStandingProjector.Fold(await LogAsync(store, ct));

        // The rebuild happens at a wall-clock instant far from the first fold's. If anything in
        // the projection read a clock, this is where the two would differ -- which is what makes
        // the assertion worth making rather than assuming.
        clock.Advance(TimeSpan.FromDays(400));

        var paged = new List<AppendedEvent>();
        var afterSeq = EventSequence.Zero;
        while (true)
        {
            var page = Require(await store.ReadForwardAsync(afterSeq, maxCount: 2, cancellationToken: ct));
            if (page.Count == 0) break;
            paged.AddRange(page);
            afterSeq = page[^1].Seq;
        }

        var rebuilt = AgentStandingProjector.Fold(paged);

        Assert.Equal(first, rebuilt);
        Assert.Equal(
            Require(AgentStandingProjector.PostureOf(first, Agent)),
            Require(AgentStandingProjector.PostureOf(rebuilt, Agent)));
    }

    /// <summary>
    /// Events the projection does not model are skipped, not rejected -- the log is the system of
    /// record for everything, and a projection that failed on an unfamiliar type would break every
    /// time an unrelated feature added one.
    /// </summary>
    [Fact]
    public async Task UnmodelledEventTypesAreIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));

        Require(await store.AppendAsync(
            Require(AggregateId.Create("some-other-aggregate")),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create("unrelated-1")),
                Require(EventType.Create("moderation.flag.raised")),
                Actor: null,
                new JsonValue.Object([new("whatever", new JsonValue.String("value"))]))],
            ct));

        var standings = AgentStandingProjector.Fold(await LogAsync(store, ct));

        Assert.Equal([Agent], standings.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Ordering is by <c>seq</c>, and a caller that hands over something other than a store's
    /// forward scan is a bug rather than a modeled outcome (CS-10) -- the same contract
    /// <see cref="PostProjector.Fold"/> states, reached the same way (by reversing a
    /// legitimately-obtained list, never by fabricating an <see cref="AppendedEvent"/>).
    /// </summary>
    [Fact]
    public async Task FoldRefusesEventsOutOfSeqOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ct));
        await AcceptPostAsync(store, Agent, "question", "q-1", ct);

        var reversed = (await LogAsync(store, ct)).Reverse().ToArray();

        var ex = Assert.Throws<ArgumentException>(() => AgentStandingProjector.Fold(reversed));
        Assert.Contains("ascending seq order", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Appends a <c>moderation.applied</c> event upholding a flag of <paramref name="category"/> on
    /// <paramref name="postId"/> — the half of §10.10 that decides, as distinct from the flag that
    /// asks. Raised first, because a moderation action on a post nobody flagged is not what Table 11
    /// counts.
    /// </summary>
    private static async Task UpholdFlagAsync(
        InMemoryEventStore store, string postId, FlagKind category, string reporter, CancellationToken ct)
    {
        await AppendToPostAsync(store, postId, FlagProjector.FlagRaisedType, new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(postId)),
            new(FlagProjector.RaisedByField, new JsonValue.String(reporter)),
            new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String("reported")),
        ]), ct).ConfigureAwait(false);

        await AppendToPostAsync(store, postId, FlagProjector.ModerationAppliedType, new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(postId)),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(ModeratorKind.Human))),
            new(FlagProjector.ActorIdField, new JsonValue.String("https://agents.example/moderator")),
            new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(ModerationEffect.Withhold))),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String("reviewed and confirmed")),
        ]), ct).ConfigureAwait(false);
    }

    /// <summary>Raises a flag and leaves it unadjudicated.</summary>
    private static Task RaiseFlagAsync(
        InMemoryEventStore store, string postId, FlagKind category, string reporter, CancellationToken ct) =>
        AppendToPostAsync(store, postId, FlagProjector.FlagRaisedType, new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(postId)),
            new(FlagProjector.RaisedByField, new JsonValue.String(reporter)),
            new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String("reported")),
        ]), ct);

    private static async Task AppendToPostAsync(
        InMemoryEventStore store, string postId, string type, JsonValue.Object payload, CancellationToken ct)
    {
        var aggregate = Require(AggregateId.Create(postId));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create($"{postId}-{history.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}")),
                Require(EventType.Create(type)),
                Require(ActorId.Create("https://agents.example/moderator")),
                payload)],
            ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Table 11's T1 row reads "≥ 3 questions with <b>no upheld flags</b>", and until §10.10's
    /// moderation events existed this projection counted every accepted question — with a comment
    /// saying so. An upheld flag now takes its question out of the count.
    /// </summary>
    [Fact]
    public async Task R10_39_AnUpheldFlagStopsItsQuestionCountingTowardT1()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);

        await EnrollVerifiedAsync(store, clock, Agent, Kid, ct);
        for (var i = 0; i < 3; i++)
            await AcceptPostAsync(store, Agent, "question", $"post-{i}", ct);

        Assert.Equal(3, Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent)).QuestionsWithoutUpheldFlags);

        await UpholdFlagAsync(store, "post-1", FlagKind.Injection, Other, ct);

        Assert.Equal(2, Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent)).QuestionsWithoutUpheldFlags);
    }

    /// <summary>
    /// R7.8: "demotion SHOULD be immediate" on posture degradation. Nothing caches a tier, so an
    /// upheld flag demotes on the next decision with no invalidation step — the same argument
    /// <c>CredentialLifecycle.Project</c> makes about current state.
    /// </summary>
    [Fact]
    public async Task R7_8_AnUpheldFlagDemotesTheAgentWithoutHumanIntervention()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);

        await EnrollVerifiedAsync(store, clock, Agent, Kid, ct);
        for (var i = 0; i < 3; i++)
            await AcceptPostAsync(store, Agent, "question", $"post-{i}", ct);

        var promoted = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));
        Assert.Equal(PrincipalTier.T1, TierPolicy.Evaluate(promoted, Start.AddDays(8)).Tier);

        await UpholdFlagAsync(store, "post-1", FlagKind.Injection, Other, ct);

        var demoted = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));
        Assert.Equal(PrincipalTier.T0, TierPolicy.Evaluate(demoted, Start.AddDays(8)).Tier);
    }

    /// <summary>
    /// <b>A raised flag is not an upheld flag</b>, and the distinction is what stops flagging being
    /// a demotion primitive: R10.35 lets every T0 agent flag, so if raising were enough, any agent
    /// could demote any other three flags at a time with no moderator involved.
    /// </summary>
    [Fact]
    public async Task R10_35_AnUnadjudicatedFlagDoesNotDemoteAnyone()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);

        await EnrollVerifiedAsync(store, clock, Agent, Kid, ct);
        for (var i = 0; i < 3; i++)
            await AcceptPostAsync(store, Agent, "question", $"post-{i}", ct);

        await RaiseFlagAsync(store, "post-0", FlagKind.Spam, Other, ct);
        await RaiseFlagAsync(store, "post-1", FlagKind.Spam, Other, ct);
        await RaiseFlagAsync(store, "post-2", FlagKind.Spam, Other, ct);

        var facts = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));

        Assert.Equal(3, facts.QuestionsWithoutUpheldFlags);
        Assert.Equal(PrincipalTier.T1, TierPolicy.Evaluate(facts, Start.AddDays(8)).Tier);
    }

    /// <summary>
    /// Table 11's T2 and T3 rows require a "clean record", which <see cref="PostureFacts.HasCleanRecord"/>
    /// reads off <see cref="PostureFacts.UpheldFlags"/> — a field nothing populated until §10.10's
    /// events existed. An upheld flag on an <i>answer</i> counts here even though no question count
    /// changes, which is the point of the two being separate fields.
    /// </summary>
    [Fact]
    public async Task R7_8_AnUpheldFlagOnAnyPostEndsTheCleanRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);

        await EnrollVerifiedAsync(store, clock, Agent, Kid, ct);
        await AcceptPostAsync(store, Agent, "answer", "answer-1", ct);

        Assert.True(Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent)).HasCleanRecord);

        await UpholdFlagAsync(store, "answer-1", FlagKind.MaliciousCode, Other, ct);

        var facts = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));

        Assert.Equal(1, facts.UpheldFlags);
        Assert.False(facts.HasCleanRecord);
    }
}
