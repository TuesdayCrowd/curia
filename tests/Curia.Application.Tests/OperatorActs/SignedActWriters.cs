using Curia.Application.Credentials;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Application.Tests.Credentials;
using Curia.Application.Tests.Ingest;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Tests.OperatorActs;

/// <summary>
/// PR N2 (errata G70, Amendment 5 ruling 60 (a)): the operator-act writers sign, and take a signer per call and a verifier at construction. A test that only needs an act recorded builds one of these:
/// a thin wrapper whose <c>RecordAsync</c> has the writer's old parameter list and signs with one process-wide test key (a test key guards nothing), over a verifier for ES256.
///
/// <para>The key is published into the same store through <see cref="PublishOperatorKey"/>, once, when the writer first refuses for want of it and the log holds no operator key at all; the act is then
/// made again. That is at the first act and not at construction, so that a test that counts what a refused act reads (R10.68's order) or what it appended sees exactly what the act did and nothing a setup
/// added. When the refusal is stale instead (the log held no key at the writer's read and holds one now, because another writer on the store published it in between), the wrapper answers as the store
/// answers a writer whose view another append overtook: <c>curia/domain/concurrency-conflict</c>, which is what a test that races two writers on one store is about.</para>
/// </summary>
internal static class SignedActWriters
{
    private static readonly ActorId KeyPublisher = Require(ActorId.Create("operator:test-publisher"));

    /// <summary>The one operator key every wrapper signs with. Never disposed: it lives as long as the test process.</summary>
    internal static readonly InMemoryOperatorActSigner Key = new("test-operator-key");

    internal static LifecycleVerifier Verifier { get; } = new(new Dictionary<string, IContentVerifier> { ["ES256"] = new TestEs256() });

    internal static SignedAttestOwner Attest(IEventStore events, TimeProvider clock) => new(events, clock);

    internal static SignedApplyModeration Moderate(IEventStore events, IFlagDetailStore details, TimeProvider clock) => new(events, details, clock);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    /// <summary>
    /// What to do with a first outcome: null to return it as it stands; otherwise the retry. Publishes <see cref="Key"/> when the log holds no operator key and the refusal is for want of one (true: act
    /// again); a stale refusal (the log holds a key now) is the overtaken view's concurrency conflict (false, with <paramref name="stale"/> set).
    /// </summary>
    private static async Task<(bool Retry, Error? Stale)> AfterRefusalAsync(IEventStore events, TimeProvider clock, Error refusal, CancellationToken ct)
    {
        if (refusal.Type != KeyActErrors.OperatorKeyNotPublishedType)
            return (false, null);

        var log = Require(await events.ReadAllAsync(ct).ConfigureAwait(false));
        if (!PublishedKeys.OfLog(log).OperatorKeys.IsEmpty)
        {
            var aggregate = Require(AggregateId.Create(LogEntries.OperatorKeysAggregate));
            return (false, DomainErrors.ConcurrencyConflict(aggregate, AggregateVersion.New, Require(AggregateVersion.From(1))));
        }

        Require(await new PublishOperatorKey(events, clock).PublishAsync(Key, KeyPublisher, ct).ConfigureAwait(false));
        return (true, null);
    }

    internal sealed class SignedAttestOwner(IEventStore events, TimeProvider clock)
    {
        private readonly AttestOwner _inner = new(events, Verifier, clock);

        internal async Task<Result<OwnerAttestation>> RecordAsync(
            Result<AgentId> agentId,
            OwnerId owner,
            bool verified,
            OwnerVerificationMethod method,
            string reason,
            ActorId attestedBy,
            CancellationToken cancellationToken = default)
        {
            var first = await _inner.RecordAsync(agentId, owner, verified, method, reason, attestedBy, Key, cancellationToken).ConfigureAwait(false);
            if (first.TryGetValue(out _, out var refusal))
                return first;

            var (retry, stale) = await AfterRefusalAsync(events, clock, refusal!, cancellationToken).ConfigureAwait(false);
            if (retry)
                return await _inner.RecordAsync(agentId, owner, verified, method, reason, attestedBy, Key, cancellationToken).ConfigureAwait(false);

            return stale is { } overtaken ? Result<OwnerAttestation>.Fail(overtaken) : first;
        }
    }

    internal sealed class SignedApplyModeration(IEventStore events, IFlagDetailStore details, TimeProvider clock)
    {
        private readonly ApplyModeration _inner = new(events, details, Verifier, clock);

        internal async Task<Result<ModerationRecorded>> RecordAsync(
            Result<PostId> postId,
            ModerationEffect effect,
            FlagKind category,
            Result<Rationale> rationale,
            ActorId moderator,
            CancellationToken cancellationToken = default)
        {
            var first = await _inner.RecordAsync(postId, effect, category, rationale, moderator, Key, cancellationToken).ConfigureAwait(false);
            if (first.TryGetValue(out _, out var refusal))
                return first;

            var (retry, stale) = await AfterRefusalAsync(events, clock, refusal!, cancellationToken).ConfigureAwait(false);
            if (retry)
                return await _inner.RecordAsync(postId, effect, category, rationale, moderator, Key, cancellationToken).ConfigureAwait(false);

            return stale is { } overtaken ? Result<ModerationRecorded>.Fail(overtaken) : first;
        }
    }
}
