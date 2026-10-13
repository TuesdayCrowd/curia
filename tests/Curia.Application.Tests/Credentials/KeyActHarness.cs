using System.Security.Cryptography;
using System.Text;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Application.Tests.Ingest;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Credentials;

/// <summary>A real P-256 key a test can sign with: the material the Forum holds and the key that signs.</summary>
internal sealed record TestIdentity(PublicKeyMaterial Material, SigningKey Signing)
{
    internal string Kid => Material.Kid;

    internal static TestIdentity New(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new TestIdentity(
            new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo()),
            new SigningKey("ES256", kid, ecdsa.ExportPkcs8PrivateKey()));
    }
}

/// <summary>
/// The key-act tests' composition (Tasks 1.4): an in-memory store behind the permissive gate, an in-memory
/// key registry, real ES256 signatures under each lifecycle <c>typ</c>, and the two use cases built over
/// whatever registry and store the test has put in the harness.
/// </summary>
internal sealed class KeyActHarness
{
    internal const string Alice = "https://agents.example/alice";
    internal const string Bob = "https://agents.example/bob";

    internal static readonly DateTimeOffset Start = new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly TestEs256 _crypto = new();

    internal KeyActHarness()
    {
        Clock = new ManualTimeProvider(Start);
        Events = new InMemoryEventStore(Clock, new PermissiveLifecycleAppendGate());
        Registry = new InMemoryAuthorKeyRegistry();
    }

    internal ManualTimeProvider Clock { get; }

    internal InMemoryEventStore Events { get; }

    internal IAuthorKeyRegistry Registry { get; set; }

    internal IEventStore? Store { get; set; }

    internal IReadOnlyDictionary<string, IContentVerifier> Verifiers => new Dictionary<string, IContentVerifier> { ["ES256"] = _crypto };

    internal static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} {e.Detail}"));

    internal static Error Refusal<T>(Result<T> result) =>
        result.Match(v => throw new InvalidOperationException($"expected a refusal, got {v}"), e => e);

    internal IEventStore Writer => Store ?? Events;

    internal LogBoundKeys KeysOver() => new(new RegistryResolver(Registry), Registry, Events);

    internal RotateKey Rotator() => new(Writer, Registry, KeysOver(), Verifiers, Clock);

    internal RetireKey Retirer() => new(Writer, KeysOver(), Verifiers, Clock);

    /// <summary>Enrolls <paramref name="agent"/> with <paramref name="first"/> through the real use case.</summary>
    internal async Task EnrollAsync(string agent, TestIdentity first)
    {
        var enroll = new EnrollIdentity(Events, Registry, new EnrollAgent(Events, Clock), Clock);
        Require(await enroll.EnrollAsync(Typed.Agent(agent), Copy(first.Material), TestContext.Current.CancellationToken).ConfigureAwait(false));
    }

    internal static PublicKeyMaterial Copy(PublicKeyMaterial key) => new(key.Alg, key.Kid, key.Public.ToArray());

    internal async Task<IReadOnlyList<AppendedEvent>> StreamAsync(string agent) =>
        Require(await Events.ReadByAggregateAsync(Require(AggregateId.Create(agent)), TestContext.Current.CancellationToken).ConfigureAwait(false));

    internal async Task<long> LatestIndexAsync(string agent)
    {
        var all = Require(await Events.ReadAllAsync(TestContext.Current.CancellationToken).ConfigureAwait(false));
        var lifecycle = Require(KeyLifecycle.Of(all, Typed.Agent(agent)));
        return Require(ActaLog.Fold(all)).IndexOf(lifecycle.Entries[^1].EventId)!.Value;
    }

    internal static string RotationStatement(string agent, string authorizedBy, PublicKeyMaterial key, long prev) =>
        Canonical(new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent)),
            new("authorized_by", new JsonValue.String(authorizedBy)),
            new("jwk", Require(PublicJwk.Of(key))),
            new("kid", new JsonValue.String(key.Kid)),
            new("prev", new JsonValue.Number(prev)),
            new("typ", new JsonValue.String(KeyStatements.RotationStatementTyp)),
        ]));

    internal static string RetirementStatement(string agent, string kid, long prev) =>
        Canonical(new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent)),
            new("kid", new JsonValue.String(kid)),
            new("prev", new JsonValue.Number(prev)),
            new("typ", new JsonValue.String(KeyStatements.RetirementStatementTyp)),
        ]));

    internal static string Canonical(JsonValue.Object statement) =>
        Encoding.UTF8.GetString(Require(CanonicalJson.CanonicalizeWithNfc(statement)).Span);

    /// <summary>A detached JWS over <paramref name="statement"/> under header <c>typ</c> <paramref name="typ"/>, signed with <paramref name="key"/> (its <c>kid</c> is the header's).</summary>
    internal DetachedSignature Sign(string statement, string typ, SigningKey key)
    {
        var bytes = Require(JsonReader.Parse(Encoding.UTF8.GetBytes(statement), AdmitLimits.Default));
        var canonical = Require(CanonicalJson.CanonicalizeWithNfc(bytes));
        var jws = new DetachedJws(new Dictionary<string, IContentSigner> { ["ES256"] = _crypto }, Verifiers, typ);
        return Require(DetachedSignature.Parse(Require(jws.Sign(canonical, key)).Compact));
    }

    /// <summary>
    /// A well-formed rotation of <paramref name="agent"/> to <paramref name="next"/>, authorized by
    /// <paramref name="authorizer"/> with its <c>prev</c> the latest lifecycle index unless given.
    /// </summary>
    internal async Task<(StatementText Statement, DetachedSignature Authorization, DetachedSignature Possession)> RotationAsync(
        string agent, TestIdentity authorizer, TestIdentity next, long? prev = null)
    {
        var text = RotationStatement(agent, authorizer.Kid, next.Material, prev ?? await LatestIndexAsync(agent).ConfigureAwait(false));
        return (
            Require(StatementText.Parse(text)),
            Sign(text, KeyStatements.RotationHeaderTyp, authorizer.Signing),
            Sign(text, KeyStatements.RotationHeaderTyp, next.Signing));
    }

    internal async Task<Result<KeyActRecorded>> RotateAsync(string agent, TestIdentity authorizer, TestIdentity next, long? prev = null)
    {
        var (statement, authorization, possession) = await RotationAsync(agent, authorizer, next, prev).ConfigureAwait(false);
        return await Rotator().RotateAsync(Typed.Agent(agent), statement, authorization, possession, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>A well-formed retirement of <paramref name="victim"/>, signed by <paramref name="signer"/> under the retirement <c>typ</c>.</summary>
    internal async Task<Result<KeyActRecorded>> RetireAsync(string agent, TestIdentity victim, TestIdentity? signer = null, long? prev = null)
    {
        var text = RetirementStatement(agent, victim.Kid, prev ?? await LatestIndexAsync(agent).ConfigureAwait(false));
        return await Retirer().RetireAsync(
            Typed.Agent(agent),
            Typed.Kid(victim.Kid),
            Require(StatementText.Parse(text)),
            Sign(text, KeyStatements.RetirementHeaderTyp, (signer ?? victim).Signing),
            TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    /// <summary>The store's resolving half over the in-memory registry: the key under the agent and kid, from its <c>NotBefore</c>.</summary>
    internal sealed class RegistryResolver(IAuthorKeyRegistry registry) : IAuthorKeyResolver
    {
        public async Task<Result<PublicKeyMaterial>> ResolveAsync(AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken cancellationToken = default)
        {
            var held = (await registry.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(k => k.Key.Kid == kid.Value);
            return held is not null && at.Value >= held.NotBefore
                ? Result<PublicKeyMaterial>.Ok(held.Key)
                : Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NotRegisteredToAgent(agentId.Value, kid.Value));
        }
    }

    /// <summary>A registry that runs <paramref name="afterAdd"/> once, right after a successful <c>AddAsync</c>.</summary>
    internal sealed class HookedRegistry(IAuthorKeyRegistry inner, Func<Task> afterAdd) : IAuthorKeyRegistry
    {
        private bool _fired;

        public Task<Result<RegisteredKey>> EnrollAsync(AgentId agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default) =>
            inner.EnrollAsync(agentId, key, notBefore, cancellationToken);

        public async Task<Result<RegisteredKey>> AddAsync(AgentId agentId, PublicKeyMaterial key, DateTimeOffset validFrom, CancellationToken cancellationToken = default)
        {
            var added = await inner.AddAsync(agentId, key, validFrom, cancellationToken).ConfigureAwait(false);
            if (added.IsOk && !_fired)
            {
                _fired = true;
                await afterAdd().ConfigureAwait(false);
            }

            return added;
        }

        public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(AgentId agentId, CancellationToken cancellationToken = default) =>
            inner.KeysForAsync(agentId, cancellationToken);
    }

    /// <summary>A store that runs <paramref name="beforeFirstLogRead"/> once, just before the first whole-log read, so an entry can join between a stream read and a log read.</summary>
    internal sealed class InterceptingStore(IEventStore inner, Func<Task> beforeFirstLogRead) : IEventStore
    {
        private bool _fired;

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
            inner.ReadByAggregateAsync(aggregateId, cancellationToken);

        public async Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
        {
            if (!_fired)
            {
                _fired = true;
                await beforeFirstLogRead().ConfigureAwait(false);
            }

            return await inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken).ConfigureAwait(false);
        }

        public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition condition, CancellationToken cancellationToken = default) =>
            inner.AppendAsync(aggregateId, expectedVersion, events, condition, cancellationToken);
    }
}
