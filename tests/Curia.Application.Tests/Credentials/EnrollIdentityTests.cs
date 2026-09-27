using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// R4.31 at the use case (errata G14): enrollment binds an identity to one key, the log records
/// which, and neither a second key, nor a lost key row, nor a race, nor a <c>kid</c> another identity
/// holds can change that. Every refusal is followed by a look at each store the fact holds -- a read,
/// or, for a store that never writes, whether it was asked -- because a refusal that had already
/// written something is the defect.
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

    /// <summary>
    /// Passes every request on to <paramref name="inner"/>, and counts each request to enroll. R4.33's
    /// refusals come before the key store is asked, so a count above zero is a key registered for an
    /// identifier whose enrollment the log could never record.
    /// </summary>
    private sealed class CountingKeyStore(IAuthorKeyRegistry inner) : IAuthorKeyRegistry
    {
        private int _enrollments;

        /// <summary>How many times enrollment asked this store to register a key.</summary>
        public int Enrollments => Volatile.Read(ref _enrollments);

        public Task<Result<RegisteredKey>> EnrollAsync(
            string agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _enrollments);
            return inner.EnrollAsync(agentId, key, notBefore, cancellationToken);
        }

        public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(string agentId, CancellationToken cancellationToken = default) =>
            inner.KeysForAsync(agentId, cancellationToken);
    }

    private static async Task<IReadOnlyList<AppendedEvent>> StreamAsync(InMemoryEventStore events, string agentId, CancellationToken ct) =>
        Require(await events.ReadByAggregateAsync(Require(AggregateId.Create(agentId)), ct).ConfigureAwait(false));

    /// <summary>What a fresh enrollment appends, in order (R4.34): the record, and the key it binds.</summary>
    private static readonly string[] EnrollmentEntries = [AgentStandingProjector.EnrolledType, AgentStandingProjector.KeyBoundType];

    /// <summary>The type of each entry in <paramref name="stream"/>, in order.</summary>
    private static List<string> Types(IReadOnlyList<AppendedEvent> stream) => [.. stream.Select(e => e.Event.Type.Value)];

    /// <summary>
    /// An <c>agent.key-bound</c> entry for <paramref name="key"/>, appended to Alice's stream after
    /// whatever it holds -- the entry R4.18's rotation will append, written here by hand because no
    /// writer in this solution produces a second one yet.
    /// </summary>
    private static async Task BindAnotherKeyAsync(InMemoryEventStore events, PublicKeyMaterial key, CancellationToken ct)
    {
        var stream = await StreamAsync(events, Alice, ct).ConfigureAwait(false);
        Require(await events.AppendAsync(
            Require(AggregateId.Create(Alice)),
            Require(AggregateVersion.From(stream.Count)),
            [new DomainEvent(
                Require(EventId.Create("rotation-shaped-binding-" + key.Kid)),
                Require(EventType.Create(AgentStandingProjector.KeyBoundType)),
                Require(ActorId.Create(Alice)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String(key.Kid)),
                    new(AgentStandingProjector.JwkField, Require(PublicJwk.Of(key))),
                ]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>
    /// An event store that takes one append and refuses every later one, changing nothing, as a store
    /// does when a write fails after the first of two appends. What it tells apart is R4.34's "same
    /// append": an enrollment written in one append is taken whole, and one written in two is not.
    /// </summary>
    private sealed class OneAppendEventStore(InMemoryEventStore inner) : IEventStore
    {
        private int _appends;

        public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
            AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken = default) =>
            Interlocked.Increment(ref _appends) == 1
                ? inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken)
                : Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(
                    new Error("test/append-failed", "This store takes one append", "a write failed after the first append")));

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
            inner.ReadByAggregateAsync(aggregateId, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(
            EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) =>
            inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }

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
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));
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
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));
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

        Require(await new EnrollAgent(events, clock).RecordAsync(Alice, bound, ct));
        var keys = new PreG14KeyStore(Alice, [new RegisteredKey(bound, Start, null), new RegisteredKey(unbound, Start.AddHours(1), null)]);

        var again = new PublicKeyMaterial(unbound.Alg, unbound.Kid, unbound.Public.ToArray());
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await Enroll(events, keys, clock).EnrollAsync(Alice, again, ct)).Type);
        Assert.Equal(0, keys.Enrollments);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>The log's record on its own: it will not report success for a kid it did not bind.</summary>
    [Fact]
    public async Task R4_31_TheLogsRecordRefusesAKidItDidNotBind()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var log = new EnrollAgent(events, clock);
        var alice = NewKey("alice-1");

        Require(await log.RecordAsync(Alice, alice, ct));

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await log.RecordAsync(Alice, NewKey("mallory-1"), ct)).Type);
        Assert.True(Require(await log.RecordAsync(Alice, alice, ct)).WasAlreadyEnrolled);
        Assert.Equal(["alice-1"], EnrolledKids(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// The log's record on its own, for R4.31 rev.'s second clause (errata G16): under the <c>kid</c>
    /// it bound, it will not report success for other bytes, since the log now carries the key. The
    /// same key, from a fresh byte array, is still a re-announcement.
    /// </summary>
    [Fact]
    public async Task R4_31_TheLogsRecordRefusesOtherBytesUnderTheKidItBound()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var log = new EnrollAgent(events, clock);
        var alice = NewKey("alice-1");

        Require(await log.RecordAsync(Alice, alice, ct));

        Assert.Equal("curia/keys/material-immutable", Refusal(await log.RecordAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.True(Require(await log.RecordAsync(Alice, new PublicKeyMaterial(alice.Alg, alice.Kid, alice.Public.ToArray()), ct)).WasAlreadyEnrolled);
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));
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
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await new EnrollAgent(events, clock).RecordAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.Empty(await keys.KeysForAsync(Alice, ct));
        Assert.Single(await StreamAsync(events, Alice, ct));
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

    /// <summary>
    /// R4.34 (errata G16): a fresh enrollment appends the record and the key it binds, in that order,
    /// under one instant, and the binding carries the key as RFC 7518's <c>EC</c> JWK. The coordinates
    /// are read out of the key's DER here, independently of the renderer the Forum uses. Under this
    /// frozen clock two appends would share an instant too; that the two are one append is
    /// <see cref="R4_34_AnEnrollmentAndItsBindingAreOneAppend"/>'s to show.
    /// </summary>
    [Fact]
    public async Task R4_34_AnEnrollmentBindsItsKeyInTheLogBesideItsRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var key = NewKey("alice-1");

        Require(await Enroll(events, new InMemoryAuthorKeyRegistry(), clock).EnrollAsync(Alice, key, ct));

        var stream = await StreamAsync(events, Alice, ct);
        Assert.Equal(EnrollmentEntries, Types(stream));
        Assert.Equal(stream[0].ServerTimestamp, stream[1].ServerTimestamp);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key.Public.Span, out _);
        var q = ecdsa.ExportParameters(includePrivateParameters: false).Q;
        var payload = (JsonValue.Object)stream[1].Event.Payload;
        var jwk = (JsonValue.Object)payload.Members.Single(m => m.Key == AgentStandingProjector.JwkField).Value;

        Assert.Equal(
            $"agent_id={Alice} kid=alice-1 jwk=alg:ES256,crv:P-256,kid:alice-1,kty:EC,x:{Base64Url.EncodeToString(q.X)},y:{Base64Url.EncodeToString(q.Y)}",
            $"agent_id={((JsonValue.String)payload.Members.Single(m => m.Key == AgentStandingProjector.AgentIdField).Value).Value}"
            + $" kid={((JsonValue.String)payload.Members.Single(m => m.Key == AgentStandingProjector.KeyIdField).Value).Value}"
            + " jwk=" + string.Join(",", jwk.Members.OrderBy(m => m.Key, StringComparer.Ordinal).Select(m => $"{m.Key}:{((JsonValue.String)m.Value).Value}")));
    }

    /// <summary>
    /// R4.34 (errata G16): the enrollment and its binding are one append, so a write that fails
    /// between two appends leaves both entries or neither, and never <c>agent.enrolled</c> without its
    /// binding: an identity the log would bind by its <c>kid</c> alone, for good. The store here takes
    /// one append and refuses every later one.
    /// </summary>
    [Fact]
    public async Task R4_34_AnEnrollmentAndItsBindingAreOneAppend()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var oneAppend = new OneAppendEventStore(events);
        var enroll = new EnrollIdentity(oneAppend, new InMemoryAuthorKeyRegistry(), new EnrollAgent(oneAppend, clock), clock);

        var answer = await enroll.EnrollAsync(Alice, NewKey("alice-1"), ct);

        Assert.Equal(
            "enrolled; agent.enrolled, agent.key-bound",
            $"{(answer.TryGetValue(out _, out var error) ? "enrolled" : error!.Type)}; {string.Join(", ", Types(await StreamAsync(events, Alice, ct)))}");
    }

    /// <summary>
    /// R4.34 (errata G16): the log's record carries the key as its public JWK, so a key the renderer
    /// refuses is refused before anything is written. Refused after the store, it would leave a key
    /// row no enrollment binds, which every re-send would re-present and meet the same refusal. The
    /// route never lets such a key through; this holds the use case to it on its own.
    /// </summary>
    [Fact]
    public async Task R4_34_AKeyTheLogCannotCarryIsRefusedBeforeAnythingIsWritten()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var keys = new InMemoryAuthorKeyRegistry();

        var refused = Refusal(await Enroll(events, keys, clock).EnrollAsync(Alice, new PublicKeyMaterial("EdDSA", "alice-1", new byte[31]), ct));

        Assert.Equal(PublicJwk.NotRenderableType, refused.Type);
        Assert.Empty(await keys.KeysForAsync(Alice, ct));
        Assert.Empty(await StreamAsync(events, Alice, ct));
    }

    /// <summary>
    /// R4.34 (errata G16): a binding is an entry in the identity's own stream. One written in another
    /// stream binds nothing to Alice though its payload names her, even when the history
    /// <see cref="EnrollmentBinding.Find"/> reads is the whole log rather than her stream.
    /// </summary>
    [Fact]
    public async Task R4_34_OnlyTheIdentitysOwnStreamBindsItsKeys()
    {
        const string Mallory = "https://agents.example/mallory";
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var planted = NewKey("alice-2");

        Require(await new EnrollAgent(events, clock).RecordAsync(Alice, NewKey("alice-1"), ct));
        Require(await new EnrollAgent(events, clock).RecordAsync(Mallory, NewKey("mallory-1"), ct));
        Require(await events.AppendAsync(
            Require(AggregateId.Create(Mallory)),
            Require(AggregateVersion.From(EnrollmentEntries.Length)),
            [new DomainEvent(
                Require(EventId.Create("planted-in-another-stream")),
                Require(EventType.Create(AgentStandingProjector.KeyBoundType)),
                Require(ActorId.Create(Mallory)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String(planted.Kid)),
                    new(AgentStandingProjector.JwkField, Require(PublicJwk.Of(planted))),
                ]))],
            ct));

        var binding = EnrollmentBinding.Find(Require(await events.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct)), Alice);

        Assert.NotNull(binding);
        Assert.Equal("alice-1", string.Join(",", binding.Keys.Select(k => k.Kid)));
    }

    /// <summary>
    /// The conformance vector <c>acta/key-bound-entry</c> pins the shape of R4.34's entry, and this
    /// holds the writer to it: enrolling the vector's identity with <c>envelope/ed25519-minimal</c>'s
    /// key, under that fixture's <c>kid</c>, writes the vector's payload byte for byte once
    /// canonicalized. Without it the vector could pin a shape the Forum never writes (trap 1).
    /// </summary>
    [Fact]
    public async Task R4_34_AnEnrollmentWritesTheConformanceVectorsPayload()
    {
        const string Scriptor = "agent://curia.example/tuesdaycrowd/scriptor";
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);

        Require(await new EnrollAgent(events, clock).RecordAsync(
            Scriptor,
            new PublicKeyMaterial("EdDSA", "conformance-ed25519-minimal", Base64Url.DecodeFromChars("HRzJlnTufZYYTZyCDBpyP5ldQ38JlbCeDOQHgIozgg8")),
            ct));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;
        var vector = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(await File.ReadAllBytesAsync(Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("conformance/ not found above " + AppContext.BaseDirectory),
            "conformance", "acta", "key-bound-entry", "input.json"), ct)));
        var expected = vector.Members.Single(m => m.Key == "payload").Value;

        var written = Assert.Single(await StreamAsync(events, Scriptor, ct), e => e.Event.Type.Value == AgentStandingProjector.KeyBoundType);
        Assert.Equal(
            Encoding.UTF8.GetString(Require(CanonicalJson.Canonicalize(expected)).ToArray()),
            Encoding.UTF8.GetString(Require(CanonicalJson.Canonicalize(written.Event.Payload)).ToArray()));
    }

    /// <summary>
    /// R4.31 rev. (errata G16), the residual errata G14's fourth cost named: the store has lost Alice's
    /// row, and a request presenting her bound <c>kid</c> with other bytes is refused by name, because
    /// the log now carries her key. Nothing is registered and nothing is appended. Her own key, over
    /// the same lost store, is then re-registered, so the refusal is about the bytes.
    /// </summary>
    [Fact]
    public async Task R4_31_ALostRowsRecoveryWithOtherBytesUnderTheBoundKidIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var key = NewKey("alice-1");

        Require(await Enroll(events, new InMemoryAuthorKeyRegistry(), clock).EnrollAsync(Alice, key, ct));

        clock.Advance(TimeSpan.FromDays(1));
        var lost = new InMemoryAuthorKeyRegistry();
        var enroll = Enroll(events, lost, clock);

        Assert.Equal("curia/keys/material-immutable", Refusal(await enroll.EnrollAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.Empty(await lost.KeysForAsync(Alice, ct));
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));

        Assert.True(Require(await enroll.EnrollAsync(Alice, key, ct)).WasAlreadyEnrolled);
        Assert.Equal(Start, Assert.Single(await lost.KeysForAsync(Alice, ct)).NotBefore);
    }

    /// <summary>
    /// R4.31 rev. as R4.35 reads the log (errata G16): an <c>agent.key-bound</c> entry that names a
    /// <c>kid</c> binds it, whatever else it carries. One whose <c>jwk</c> is not a key binds the
    /// <c>kid</c> to no key, and the kid-only clause of the identity's <c>agent.enrolled</c> does not
    /// stand for it, so after a lost row no bytes under that <c>kid</c> are registered. No writer in
    /// this solution produces such an entry; this holds the reader to the log if one is ever there.
    /// </summary>
    [Fact]
    public async Task R4_31_AKeyBindingThatCarriesNoKeyBindsNoBytesUnderItsKid()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);

        Require(await events.AppendAsync(
            Require(AggregateId.Create(Alice)),
            AggregateVersion.New,
            [
                new DomainEvent(
                    Require(EventId.Create("enrolled-beside-a-binding-without-a-key")),
                    Require(EventType.Create(AgentStandingProjector.EnrolledType)),
                    Require(ActorId.Create(Alice)),
                    new JsonValue.Object(
                    [
                        new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                        new(AgentStandingProjector.KeyIdField, new JsonValue.String("alice-1")),
                        new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted")),
                    ])),
                new DomainEvent(
                    Require(EventId.Create("a-binding-without-a-key")),
                    Require(EventType.Create(AgentStandingProjector.KeyBoundType)),
                    Require(ActorId.Create(Alice)),
                    new JsonValue.Object(
                    [
                        new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                        new(AgentStandingProjector.KeyIdField, new JsonValue.String("alice-1")),
                        new(AgentStandingProjector.JwkField, new JsonValue.String("not-a-key")),
                    ])),
            ],
            ct));

        clock.Advance(TimeSpan.FromDays(1));
        var lost = new InMemoryAuthorKeyRegistry();

        Assert.Equal("curia/keys/material-immutable", Refusal(await Enroll(events, lost, clock).EnrollAsync(Alice, NewKey("alice-1"), ct)).Type);
        Assert.Empty(await lost.KeysForAsync(Alice, ct));
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// An identity enrolled before R4.34 has an <c>agent.enrolled</c> naming its <c>kid</c> and no
    /// <c>agent.key-bound</c>, and the log binds that <c>kid</c> alone: after a lost row, whatever
    /// bytes arrive under it are registered, as errata G14's fourth cost says, while any other
    /// <c>kid</c> is still refused. Pinned so the kid-only branch is a decision a test holds, not an
    /// accident of the code.
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentityEnrolledBeforeR4_34IsBoundByItsKidAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);

        Require(await events.AppendAsync(
            Require(AggregateId.Create(Alice)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create("enrolled-before-key-binding")),
                Require(EventType.Create(AgentStandingProjector.EnrolledType)),
                Require(ActorId.Create(Alice)),
                new JsonValue.Object(
                [
                    new(AgentStandingProjector.AgentIdField, new JsonValue.String(Alice)),
                    new(AgentStandingProjector.KeyIdField, new JsonValue.String("alice-1")),
                    new(AgentStandingProjector.ReasonField, new JsonValue.String("Enrollment accepted")),
                ]))],
            ct));

        clock.Advance(TimeSpan.FromDays(1));
        var lost = new InMemoryAuthorKeyRegistry();
        var enroll = Enroll(events, lost, clock);

        Assert.Equal("curia/enroll/already-enrolled", Refusal(await enroll.EnrollAsync(Alice, NewKey("mallory-1"), ct)).Type);
        Assert.True(Require(await enroll.EnrollAsync(Alice, NewKey("alice-1"), ct)).WasAlreadyEnrolled);
        Assert.Equal(Start, Assert.Single(await lost.KeysForAsync(Alice, ct)).NotBefore);
        Assert.Equal([AgentStandingProjector.EnrolledType], Types(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// The seam R4.31 rev. settles (errata G16): once the log binds a second key to an identity, as
    /// R4.18's rotation will, re-announcing that key is a re-announcement, exactly as re-announcing
    /// the first is, and writes nothing. A <c>kid</c> no entry binds is still refused. The second
    /// binding is appended by hand, since nothing produces one yet; the store holds both keys, as it
    /// will after a rotation.
    /// </summary>
    [Fact]
    public async Task R4_31_AKeyASecondBindingNamesIsReAnnouncedAsTheFirstIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var first = NewKey("alice-1");
        var second = NewKey("alice-2");

        Require(await new EnrollAgent(events, clock).RecordAsync(Alice, first, ct));
        await BindAnotherKeyAsync(events, second, ct);
        var keys = new PreG14KeyStore(Alice, [new RegisteredKey(first, Start, null), new RegisteredKey(second, Start, null)]);
        var enroll = Enroll(events, keys, clock);

        Assert.True(Require(await enroll.EnrollAsync(Alice, new PublicKeyMaterial(second.Alg, second.Kid, second.Public.ToArray()), ct)).WasAlreadyEnrolled);
        Assert.True(Require(await enroll.EnrollAsync(Alice, new PublicKeyMaterial(first.Alg, first.Kid, first.Public.ToArray()), ct)).WasAlreadyEnrolled);
        Assert.Equal("curia/enroll/already-enrolled", Refusal(await enroll.EnrollAsync(Alice, NewKey("alice-3"), ct)).Type);
        Assert.Equal(
            [AgentStandingProjector.EnrolledType, AgentStandingProjector.KeyBoundType, AgentStandingProjector.KeyBoundType],
            Types(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// R4.31 rev. (errata G16, as its review amended it): an identifier the log never enrolled, as
    /// every one enrolled before <c>agent.enrolled</c> existed is, for which the store holds two keys --
    /// its own, and one errata G14's hole wrote beside it. Nothing in the log says which is its own, so
    /// a request presenting either is refused by name before the store is asked to register, and
    /// nothing is appended. Binding whichever arrived would let anyone holding the second key's public
    /// half make it the identity's key and turn its history into failures. With one stored key the
    /// same request enrolls the identifier and binds that key (errata G16's fifth cost), so the refusal
    /// is about the count.
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundWhileTheStoreHoldsSeveralKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);
        var own = NewKey("alice-1");
        var hole = NewKey("hole-1");
        var several = new PreG14KeyStore(Alice, [new RegisteredKey(own, Start, null), new RegisteredKey(hole, Start.AddHours(1), null)]);
        var enroll = Enroll(events, several, clock);

        Assert.Equal("curia/enroll/keys-ambiguous", Refusal(await enroll.EnrollAsync(Alice, new PublicKeyMaterial(hole.Alg, hole.Kid, hole.Public.ToArray()), ct)).Type);
        Assert.Equal("curia/enroll/keys-ambiguous", Refusal(await enroll.EnrollAsync(Alice, new PublicKeyMaterial(own.Alg, own.Kid, own.Public.ToArray()), ct)).Type);
        Assert.Equal(0, several.Enrollments);
        Assert.Empty(await StreamAsync(events, Alice, ct));

        var one = new PreG14KeyStore(Alice, [new RegisteredKey(own, Start, null)]);
        Assert.False(Require(await Enroll(events, one, clock).EnrollAsync(Alice, new PublicKeyMaterial(own.Alg, own.Kid, own.Public.ToArray()), ct)).WasAlreadyEnrolled);
        Assert.Equal(EnrollmentEntries, Types(await StreamAsync(events, Alice, ct)));
    }

    /// <summary>
    /// R4.33's second clause (errata G15): an identifier whose aggregate holds another writer's events,
    /// here a post's as the review enrolled one, and no enrollment of it. It is refused before the key
    /// store is asked. Without the refusal the store registered the key, and the log's append at
    /// <see cref="AggregateVersion.New"/> could never succeed however often the request was sent,
    /// leaving a key no enrollment bound. The stream is left as it was.
    /// </summary>
    [Fact]
    public async Task R4_33_AnIdentifierWhoseStreamHoldsAnotherWritersEventsIsRefusedBeforeTheStoreIsAsked()
    {
        const string PostId = "01M0572TG0X22FT8T07WK7819H";
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(Start);
        var events = new InMemoryEventStore(clock);

        Require(await events.AppendAsync(
            Require(AggregateId.Create(PostId)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create("post-accepted-under-the-identifier")),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create(Alice)),
                new JsonValue.Object(
                [
                    new("post_id", new JsonValue.String(PostId)),
                    new("author", new JsonValue.String(Alice)),
                    new("kind", new JsonValue.String("question")),
                ]))],
            ct));

        var keys = new CountingKeyStore(new InMemoryAuthorKeyRegistry());
        var answer = (await Enroll(events, keys, clock).EnrollAsync(PostId, NewKey("post-named"), ct)).Match(_ => "enrolled", e => e.Type);

        Assert.Equal("curia/enroll/identifier-reserved", answer);
        Assert.Equal(0, keys.Enrollments);
        Assert.Equal(PostProjector.PostAcceptedType, Assert.Single(await StreamAsync(events, PostId, ct)).Event.Type.Value);
    }

    /// <summary>
    /// R4.33's first clause, derived from the writers and not from the refusal's own list. Every public
    /// <c>const string</c> in the domain and the application named <c>...Aggregate</c> (an aggregate a
    /// writer names) or <c>...AggregatePrefix</c> (one it mints under a prefix, enrolled here followed
    /// by a ULID) is refused before the store is asked, on a log that holds nothing under it, so only
    /// the namespace can refuse it. A writer that adds a constant outside every reserved prefix turns
    /// this red. At least three must be found, or the fact is passing over an empty enumeration.
    /// </summary>
    [Fact]
    public async Task R4_33_EveryAggregateNameTheForumMintsIsReserved()
    {
        const string Ulid = "01M0572TG0X22FT8T07WK7819H";
        var ct = TestContext.Current.CancellationToken;

        var minted = new[] { typeof(LogEntries).Assembly, typeof(EnrollIdentity).Assembly }
            .SelectMany(assembly => assembly.GetTypes())
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (Field: field, Prefix: field.Name.EndsWith("AggregatePrefix", StringComparison.Ordinal)))
            .Where(named => named.Prefix || named.Field.Name.EndsWith("Aggregate", StringComparison.Ordinal))
            .Select(named => (
                Name: named.Field.DeclaringType!.Name + "." + named.Field.Name,
                Identifier: (string)named.Field.GetRawConstantValue()! + (named.Prefix ? Ulid : "")))
            .ToList();

        Assert.True(minted.Count >= 3, $"found {minted.Count} aggregate names the Forum mints; the Acta's two and the flag prefix exist, so the reflection is wrong");

        var admitted = new List<string>();
        foreach (var (name, identifier) in minted)
        {
            var clock = new ManualTimeProvider(Start);
            var events = new InMemoryEventStore(clock);
            var keys = new CountingKeyStore(new InMemoryAuthorKeyRegistry());

            var answer = (await Enroll(events, keys, clock).EnrollAsync(identifier, NewKey("minted-" + name), ct)).Match(_ => "enrolled", e => e.Type);
            if (answer != "curia/enroll/identifier-reserved" || keys.Enrollments != 0)
                admitted.Add($"{name} ({identifier}): {answer}, the store asked {keys.Enrollments} time(s)");
        }

        Assert.True(admitted.Count == 0, "not reserved: " + string.Join("; ", admitted));
    }
}
