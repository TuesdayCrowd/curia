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

    private static readonly DateTimeOffset Start = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static async Task<IReadOnlyList<AppendedEvent>> LogAsync(
        InMemoryEventStore store, CancellationToken ct) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));

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
    /// The instant is the store's <c>server_ts</c>, not anything the request supplied.
    /// </summary>
    [Fact]
    public async Task R4_21_EnrollmentFoldsIntoAnActiveCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        var recorded = Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));

        Assert.Equal(Start, recorded.EnrolledAt);
        Assert.False(recorded.WasAlreadyEnrolled);

        var facts = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));

        Assert.Equal(CredentialState.Active, facts.CredentialState);
        Assert.Equal(Start, facts.EnrolledAt);
        Assert.True(facts.OwnerVerified);
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

        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));

        clock.Advance(TimeSpan.FromDays(8));
        var again = Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));

        Assert.Equal(Start, again.EnrolledAt);
        Assert.True(again.WasAlreadyEnrolled);

        var log = await LogAsync(store, ct);
        Assert.Single(log);

        var facts = Require(AgentStandingProjector.PostureOf(AgentStandingProjector.Fold(log), Agent));
        Assert.Equal(Start, facts.EnrolledAt);
        Assert.Equal(CredentialState.Active, facts.CredentialState);
    }

    /// <summary>
    /// Owner verification is the mutable half: it genuinely changes, so it gets its own event and
    /// the latest one wins -- while the enrollment instant beside it does not move.
    /// </summary>
    [Fact]
    public async Task OwnerVerificationCanBeGrantedLaterAndTakesEffect()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: false, ct));

        clock.Advance(TimeSpan.FromDays(1));
        var verified = Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));

        Assert.True(verified.OwnerVerified);
        Assert.Equal(Start, verified.EnrolledAt);

        var log = await LogAsync(store, ct);
        Assert.Equal(2, log.Count);

        var facts = Require(AgentStandingProjector.PostureOf(AgentStandingProjector.Fold(log), Agent));

        Assert.True(facts.OwnerVerified);
        Assert.Equal(Start, facts.EnrolledAt);
    }

    /// <summary>
    /// Re-announcing the same flag appends nothing. An append-only log cannot take a redundant
    /// fact back, and a client that re-authenticates on every token refresh would otherwise grow
    /// the stream without bound.
    /// </summary>
    [Fact]
    public async Task RecordingAnUnchangedOwnerVerificationAppendsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock);
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));
        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));
        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));

        Assert.Single(await LogAsync(store, ct));
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
        var enroll = new EnrollAgent(store, clock);

        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));

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

        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));
        Require(await enroll.RecordAsync(Other, "other-1", ownerVerified: true, ct));

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

        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));

        // The three questions land on day one, so the tenure condition is the binding one.
        clock.Advance(TimeSpan.FromDays(1));
        for (var i = 0; i < 3; i++)
            await AcceptPostAsync(store, Agent, "question", $"early-{i}", ct);

        var early = Require(AgentStandingProjector.PostureOf(
            AgentStandingProjector.Fold(await LogAsync(store, ct)), Agent));

        Assert.Equal(Start.AddHours(TierPolicy.T1MinimumHours), early.ReachedT1At);

        // A second agent, verified only on day twenty: there owner verification is binding, and no
        // amount of later reading moves the answer.
        Require(await enroll.RecordAsync(Other, "other-1", ownerVerified: false, ct));
        for (var i = 0; i < 3; i++)
            await AcceptPostAsync(store, Other, "question", $"other-{i}", ct);

        clock.Advance(TimeSpan.FromDays(19));
        Require(await enroll.RecordAsync(Other, "other-1", ownerVerified: true, ct));

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

        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: false, ct));
        await AcceptPostAsync(store, Agent, "question", "q-1", ct);

        clock.Advance(TimeSpan.FromDays(2));
        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));
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

        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));

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

        Require(await enroll.RecordAsync(Agent, Kid, ownerVerified: true, ct));
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

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, Kid, ownerVerified: true, ct));
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

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, Kid, ownerVerified: true, ct));
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

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, Kid, ownerVerified: true, ct));
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

        Require(await new EnrollAgent(store, clock).RecordAsync(Agent, Kid, ownerVerified: true, ct));
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
