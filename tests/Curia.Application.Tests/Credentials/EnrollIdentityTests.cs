using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// R4.31 at the use case (errata G14): enrollment binds an identity to one key, the log records
/// which, and neither a second key, nor a lost key row, nor a race, nor a <c>kid</c> another identity
/// holds can change that. Every refusal is followed by a read of both stores, because a refusal that
/// had already written something is the defect.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EnrollIdentityTests
{
    private const string Alice = "https://agents.example/alice";
    private const string Bob = "https://agents.example/bob";

    private static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static Error Refusal<T>(Result<T> result) =>
        result.Match(v => throw new InvalidOperationException($"expected a refusal, got {v}"), e => e);

    private static PublicKeyMaterial NewKey(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
    }

    private static EnrollIdentity Enroll(InMemoryEventStore events, IAuthorKeyRegistry keys, TimeProvider clock) =>
        new(events, keys, new EnrollAgent(events, clock), clock);

    /// <summary>
    /// Holds every racer at the key store until all <paramref name="racers"/> are accounted for, so
    /// each has read the log -- and found it empty -- before any of them registers. That is the
    /// interleaving in which only the store's own rule stands between a race and a key per racer.
    /// Without it, a racer that happens to start after the winner has written the log is refused by
    /// the log's half, and a broken store rule would pass whenever the scheduler was kind.
    ///
    /// <para><b>A racer is accounted for when it arrives here or when it finishes.</b> Before the
    /// release no arrived racer can finish, so a racer that finishes first never reached the store:
    /// the use case refused it on the way. Counting only arrivals would wait for racers that are
    /// never coming -- which is what a use case that wrote the log before asking the store produces --
    /// and turn a wrong answer into a timeout. The timeout below is a hang guard, and no run reaches
    /// it.</para>
    /// </summary>
    private sealed class ConvergingRegistry(IAuthorKeyRegistry inner, int racers) : IAuthorKeyRegistry
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _accountedFor;

        /// <summary>Runs one racer, and accounts for it when it finishes, however it finishes.</summary>
        public async Task<T> RaceAsync<T>(Func<Task<T>> racer)
        {
            try
            {
                return await racer().ConfigureAwait(false);
            }
            finally
            {
                AccountFor();
            }
        }

        public async Task<Result<RegisteredKey>> EnrollAsync(
            string agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default)
        {
            AccountFor();
            await _all.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return await inner.EnrollAsync(agentId, key, notBefore, cancellationToken).ConfigureAwait(false);
        }

        private void AccountFor()
        {
            if (Interlocked.Increment(ref _accountedFor) == racers) _all.SetResult();
        }

        public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(string agentId, CancellationToken cancellationToken = default) =>
            inner.KeysForAsync(agentId, cancellationToken);
    }

    /// <summary>
    /// A key store as one written before errata G14 can hold it: beside the key <paramref name="holder"/>'s
    /// enrollment bound, a key no enrollment bound -- which is what G14's attack left behind. It
    /// applies the shared rule (<see cref="KeyEnrollment.Decide"/>) to what it holds and never writes,
    /// since an identity that holds a key is never told to register one. It counts every request to
    /// enroll, because the log's binding is meant to refuse before the store is asked at all.
    /// </summary>
    private sealed class PreG14KeyStore(string holder, IReadOnlyList<RegisteredKey> held) : IAuthorKeyRegistry
    {
        private int _enrollments;

        /// <summary>How many times enrollment asked this store to register a key.</summary>
        public int Enrollments => Volatile.Read(ref _enrollments);

        public Task<Result<RegisteredKey>> EnrollAsync(
            string agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _enrollments);

            if (!KeyEnrollment.Decide(agentId, key, HeldBy(agentId)).TryGetValue(out var existing, out var refusal))
                return Task.FromResult(Result<RegisteredKey>.Fail(refusal!));

            return Task.FromResult(Result<RegisteredKey>.Ok(
                existing ?? throw new InvalidOperationException($"this store never writes, and {agentId} holds no key in it")));
        }

        public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult(HeldBy(agentId));

        private IReadOnlyList<RegisteredKey> HeldBy(string agentId) =>
            string.Equals(agentId, holder, StringComparison.Ordinal) ? held : [];
    }

    private static async Task<IReadOnlyList<AppendedEvent>> StreamAsync(InMemoryEventStore events, string agentId, CancellationToken ct) =>
        Require(await events.ReadByAggregateAsync(Require(AggregateId.Create(agentId)), ct).ConfigureAwait(false));

    /// <summary>The <c>kid</c> each <c>agent.enrolled</c> in <paramref name="stream"/> names, in order.</summary>
    private static List<string> EnrolledKids(IReadOnlyList<AppendedEvent> stream) =>
    [
        .. stream
            .Where(e => e.Event.Type.Value == AgentStandingProjector.EnrolledType)
            .Select(e => ((JsonValue.Object)e.Event.Payload).Members
                .Single(m => m.Key == AgentStandingProjector.KeyIdField).Value)
            .Cast<JsonValue.String>()
            .Select(s => s.Value),
    ];

    [Fact]
    public async Task R4_31_AFreshIdentityIsEnrolledWithItsKeyAndOneRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();
        var key = NewKey("alice-1");

        var enrolled = Require(await Enroll(events, keys, clock).EnrollAsync(Alice, key, ct));

        Assert.False(enrolled.WasAlreadyEnrolled);
        Assert.Equal(Start, enrolled.EnrolledAt);
        Assert.Equal("alice-1", Assert.Single(await keys.KeysForAsync(Alice, ct)).Key.Kid);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>A re-announcement a day later: accepted, and neither store gains anything.</summary>
    [Fact]
    public async Task R4_31_ReEnrollingTheBoundKeyWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();
        var key = NewKey("alice-1");
        var enroll = Enroll(events, keys, clock);

        Require(await enroll.EnrollAsync(Alice, key, ct));
        clock.Advance(TimeSpan.FromDays(1));
        var again = Require(await enroll.EnrollAsync(Alice, new PublicKeyMaterial(key.Alg, key.Kid, key.Public.ToArray()), ct));

        Assert.True(again.WasAlreadyEnrolled);
        Assert.Equal(Start, again.EnrolledAt);
        Assert.Equal(Start, Assert.Single(await keys.KeysForAsync(Alice, ct)).NotBefore);
        Assert.Single(await StreamAsync(events, Alice, ct));
    }

    /// <summary>The attack errata G14 records, at the use case: refused, and both stores are as they were.</summary>
    [Fact]
    public async Task R4_31_ASecondKeyForAnEnrolledIdentityIsRefusedAndNothingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();
        var enroll = Enroll(events, keys, clock);

        Require(await enroll.EnrollAsync(Alice, NewKey("alice-1"), ct));

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await enroll.EnrollAsync(Alice, NewKey("mallory-1"), ct)).Type);
        Assert.Equal("alice-1", Assert.Single(await keys.KeysForAsync(Alice, ct)).Key.Kid);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// The log's half on its own: the key store has lost Alice's row (a second, empty store over the
    /// same log), and a request naming another <c>kid</c> is still refused -- before the store is
    /// touched, which is what the empty store afterwards shows. A use case that asked the store first
    /// would register the key and only then be refused by the log's record.
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentityTheLogBoundIsRefusedAnotherKidEvenWhenTheStoreHoldsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);

        Require(await Enroll(events, new InMemoryAuthorKeyRegistry(), clock).EnrollAsync(Alice, NewKey("alice-1"), ct));

        var lost = new InMemoryAuthorKeyRegistry();
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await Enroll(events, lost, clock).EnrollAsync(Alice, NewKey("mallory-1"), ct)).Type);
        Assert.Empty(await lost.KeysForAsync(Alice, ct));
    }

    /// <summary>
    /// R4.31's "even one the store holds": the store holds, beside the key Alice's enrollment bound,
    /// a second key no enrollment bound, as a store written before errata G14 can. Re-presenting that
    /// second key, byte for byte, is refused by name -- and refused by the log's binding before the
    /// store is asked, which would call the key held and leave the refusal to the log's record alone.
    /// The log gains nothing, and the store, which never writes, is not asked to.
    /// </summary>
    [Fact]
    public async Task R4_31_AKidTheLogDidNotBindIsRefusedEvenWhenTheStoreHoldsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var bound = NewKey("alice-1");
        var unbound = NewKey("mallory-1");

        Require(await new EnrollAgent(events, clock).RecordAsync(Alice, bound.Kid, ct));
        var keys = new PreG14KeyStore(Alice, [new RegisteredKey(bound, Start, null), new RegisteredKey(unbound, Start.AddHours(1), null)]);

        var again = new PublicKeyMaterial(unbound.Alg, unbound.Kid, unbound.Public.ToArray());
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await Enroll(events, keys, clock).EnrollAsync(Alice, again, ct)).Type);
        Assert.Equal(0, keys.Enrollments);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// R4.31's one exception, and the positive control for the fact above: over the same lost store,
    /// a day later, the key the log bound is re-registered -- so the refusal there is about the kid,
    /// not a use case refusing everything once the store and the log disagree. It is dated from the
    /// enrollment the log records, not from the re-registration: a key dated today would put every
    /// post the identity signed before the loss outside its window (R6.31).
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentityWhoseKeyRowWasLostCanReRegisterTheKeyItsEnrollmentBound()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var key = NewKey("alice-1");

        Require(await Enroll(events, new InMemoryAuthorKeyRegistry(), clock).EnrollAsync(Alice, key, ct));

        clock.Advance(TimeSpan.FromDays(1));
        var lost = new InMemoryAuthorKeyRegistry();
        var again = Require(await Enroll(events, lost, clock).EnrollAsync(Alice, key, ct));

        Assert.True(again.WasAlreadyEnrolled);
        var rebound = Assert.Single(await lost.KeysForAsync(Alice, ct));
        Assert.Equal("alice-1", rebound.Key.Kid);
        Assert.Equal(Start, rebound.NotBefore);
        Assert.Single(await StreamAsync(events, Alice, ct));
    }

    /// <summary>The log's record on its own: it will not report success for a kid it did not bind.</summary>
    [Fact]
    public async Task R4_31_TheLogsRecordRefusesAKidItDidNotBind()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var log = new EnrollAgent(events, clock);

        Require(await log.RecordAsync(Alice, "alice-1", ct));

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await log.RecordAsync(Alice, "mallory-1", ct)).Type);
        Assert.True(Require(await log.RecordAsync(Alice, "alice-1", ct)).WasAlreadyEnrolled);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// Fail closed on a binding the log cannot state. No writer in this solution has produced an
    /// <c>agent.enrolled</c> without a <c>kid</c>, but the log is append-only and could hold one; an
    /// identity whose binding cannot be read is refused re-enrollment under any kid, rather than
    /// granted it under every kid.
    /// </summary>
    [Fact]
    public async Task R4_31_AnEnrollmentThatNamesNoKidBindsNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);

        Require(await events.AppendAsync(
            Require(AggregateId.Create(Alice)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create("enrolled-without-a-kid")),
                Require(EventType.Create(AgentStandingProjector.EnrolledType)),
                Require(ActorId.Create(Alice)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted")),
                ]))],
            ct));

        var keys = new InMemoryAuthorKeyRegistry();
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await Enroll(events, keys, clock).EnrollAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await new EnrollAgent(events, clock).RecordAsync(Alice, "alice-1", ct)).Type);
        Assert.Empty(await keys.KeysForAsync(Alice, ct));
    }

    /// <summary>
    /// Why the store is asked before the log is written: a <c>kid</c> another identity holds is
    /// refused, and Bob's stream stays empty. Written the other way round, Bob would be enrolled --
    /// permanently, in an append-only log -- bound to a <c>kid</c> he can never register.
    /// </summary>
    [Fact]
    public async Task R4_31_AKidAnotherIdentityHoldsIsRefusedAndNoEnrollmentIsRecorded()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();
        var enroll = Enroll(events, keys, clock);

        Require(await enroll.EnrollAsync(Alice, NewKey("shared-kid"), ct));

        Assert.Equal("curia/enroll/kid-already-registered", Refusal(await enroll.EnrollAsync(Bob, NewKey("shared-kid"), ct)).Type);
        Assert.Empty(await StreamAsync(events, Bob, ct));
        Assert.Empty(await keys.KeysForAsync(Bob, ct));
    }

    /// <summary>
    /// Eight enrollments of one fresh identity under eight <c>kid</c>s, every one past the log's check
    /// before any reaches the store: one key, one record, and the record names the key that was
    /// registered. Every loser is refused by name. This fact is about the store's rule under a race;
    /// the order of store and log is <see cref="R4_31_AKidAnotherIdentityHoldsIsRefusedAndNoEnrollmentIsRecorded"/>'s.
    /// </summary>
    [Fact]
    public async Task R4_31_RacingEnrollmentsOfOneFreshIdentityLeaveOneKeyAndOneRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();
        var converging = new ConvergingRegistry(keys, racers: 8);
        var enroll = Enroll(events, converging, clock);

        var outcomes = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            Task.Run(() => converging.RaceAsync(() => enroll.EnrollAsync(Alice, NewKey($"alice-{i}"), ct)), ct)));

        Assert.Single(outcomes, o => o.IsOk);
        Assert.All(outcomes.Where(o => !o.IsOk), o => Assert.Equal("curia/enroll/already-enrolled", Refusal(o).Type));

        var held = Assert.Single(await keys.KeysForAsync(Alice, ct));
        Assert.Equal([held.Key.Kid], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }
}
