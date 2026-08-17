using System.Collections.Immutable;
using System.Globalization;
using Curia.Application.Ports;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Application.Ingest;

/// <summary>
/// §6.4's pipeline, composed from parts that already existed separately: <c>EnvelopeParser</c>
/// (ADMIT), <c>DetachedJws</c> (VERIFY), <c>ContentScreener</c> (SCREEN) and
/// <see cref="IEventStore"/> (PERSIST). What this type adds is the *sequence* and the phase types
/// that make it the only sequence.
///
/// <para><b>The one thing to check when reading this class</b> is that
/// <see cref="PersistAsync"/> writes <c>screened.Canonical</c> and nothing else -- no
/// re-canonicalization, no re-serialization of <see cref="PostEnvelope"/>, no normalization.
/// R6.12 is a byte-equality claim, and the only implementation that can keep it is one where the
/// bytes verified and the bytes written are the same object.</para>
/// </summary>
public sealed class IngestPipeline : IIngestPipeline
{
    private static readonly EventType PostAcceptedType = MustCreate(EventType.Create("post.accepted"));

    private readonly IAuthorKeyResolver _keys;
    private readonly IEventStore _events;
    private readonly DetachedJws _jws;
    private readonly TimeProvider _clock;
    private readonly UlidGenerator _ids;
    private readonly AdmitLimits _limits;

    public IngestPipeline(
        IAuthorKeyResolver keys,
        IEventStore events,
        IReadOnlyDictionary<string, IContentVerifier> verifiersByAlg,
        TimeProvider clock,
        AdmitLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(verifiersByAlg);
        ArgumentNullException.ThrowIfNull(clock);

        _keys = keys;
        _events = events;
        _jws = new DetachedJws(new Dictionary<string, IContentSigner>(), verifiersByAlg);
        _clock = clock;
        _ids = new UlidGenerator(clock);
        _limits = limits ?? AdmitLimits.Default;
    }

    /// <inheritdoc/>
    public Result<AdmittedSubmission> Admit(ReadOnlySpan<byte> wire) =>
        EnvelopeParser.Parse(wire, _limits)
            .Map(submission => new AdmittedSubmission(submission.Envelope, submission.Signature));

    /// <inheritdoc/>
    public async Task<Result<VerifiedSubmission>> VerifyAsync(
        AdmittedSubmission admitted,
        string principalAgentId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(admitted);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalAgentId);

        // R6.16: re-canonicalize from the *parsed* form rather than trusting the wire bytes to
        // already be canonical. A submitter who sends nearly-canonical bytes must not have their
        // signature checked against what they sent; it is checked against what the rules say their
        // document canonicalizes to, and if those differ the signature fails -- correctly.
        var canonical = CanonicalJson.CanonicalizeWithNfc(admitted.Document.Root);
        if (!canonical.TryGetValue(out var canonicalBytes, out var canonicalError))
            return Result<VerifiedSubmission>.Fail(canonicalError!);

        var read = PostEnvelope.Read(admitted.Document.Root);
        if (!read.TryGetValue(out var envelope, out var schemaError))
            return Result<VerifiedSubmission>.Fail(schemaError!);

        // Table 9: `author` "must equal the authenticated principal". A valid signature over
        // another agent's name is a valid signature by the wrong agent, so this is checked before
        // the signature rather than after -- there is no reason to spend a verification on a
        // document that is already disqualified.
        if (!string.Equals(envelope.Author, principalAgentId, StringComparison.Ordinal))
            return Result<VerifiedSubmission>.Fail(ContentErrors.AuthorIsNotThePrincipal());

        var header = DetachedJws.ReadProtectedHeader(admitted.Signature);
        if (!header.TryGetValue(out var protectedHeader, out var headerError))
            return Result<VerifiedSubmission>.Fail(headerError!);

        // R6.31 (errata A12): key validity is evaluated at server_ts. This is the Forum's
        // observation of receipt, taken once, here -- not at PERSIST, because a key that expires
        // between VERIFY and PERSIST must not retroactively invalidate a signature the Forum
        // already accepted, and a key that becomes valid in that window must not retroactively
        // rescue one it rejected.
        var serverTs = ServerTimestamp.At(_clock.GetUtcNow());

        var key = await _keys.ResolveAsync(envelope!.Author, protectedHeader.Kid, serverTs, cancellationToken).ConfigureAwait(false);
        if (!key.TryGetValue(out var publicKey, out var keyError))
            return Result<VerifiedSubmission>.Fail(keyError!);

        return _jws.Verify(canonicalBytes, admitted.Signature, publicKey!)
            .Map(verified => new VerifiedSubmission(verified, envelope!, principalAgentId));
    }

    /// <inheritdoc/>
    public Task<Result<ScreenedSubmission>> ScreenAsync(
        VerifiedSubmission verified,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verified);
        cancellationToken.ThrowIfCancellationRequested();

        var screened = ContentScreener.Screen(verified.Canonical.Span);
        if (!screened.TryGetValue(out var result, out var error))
            return Task.FromResult(Result<ScreenedSubmission>.Fail(error!));

        if (!result!.MayPersist)
            return Task.FromResult(Result<ScreenedSubmission>.Fail(
                IngestErrors.ScreeningRejected(result.Annotations)));

        // Wrapped, unchanged -- `verified` is the same instance, not a rebuild. There is no point
        // at which the content could have changed, so there is nothing to compare to detect it.
        return Task.FromResult(Result<ScreenedSubmission>.Ok(
            new ScreenedSubmission(verified, result.Annotations)));
    }

    /// <inheritdoc/>
    public async Task<Result<PostAccepted>> PersistAsync(
        ScreenedSubmission screened,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(screened);

        var id = _ids.Next();
        if (!id.TryGetValue(out var ulid, out var idError))
            return Result<PostAccepted>.Fail(idError!);

        var postId = ulid.ToString();
        var digest = Digests.Sha256(screened.Canonical);
        var serverTs = ServerTimestamp.At(_clock.GetUtcNow());

        // The canonical bytes go in verbatim (R6.12). They are carried as an opaque string here --
        // the exact UTF-8 the signature was verified over, decoded without transformation -- so
        // that nothing between here and the column can renormalize them. The rest of the payload
        // is Forum-assigned metadata, in fields distinct from the signed form (R6.14).
        var payload = new JsonValue.Object(
        [
            new("post_id", new JsonValue.String(postId)),
            new("canonical", new JsonValue.String(System.Text.Encoding.UTF8.GetString(screened.Canonical.Span))),
            new("digest", new JsonValue.String(digest.ToString())),
            new("author", new JsonValue.String(screened.Inner.AuthorAgentId)),
            new("board", new JsonValue.String(screened.Inner.Envelope.Board)),
            new("kind", new JsonValue.String(PostKinds.Wire(screened.Inner.Envelope.Kind))),
            new("parent", screened.Inner.Envelope.Parent is { } p
                ? new JsonValue.String(p)
                : new JsonValue.Null()),
            new("server_ts", new JsonValue.String(
                serverTs.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture))),
            new("risk_flags", RiskFlagsPayload(screened.Annotations)),
        ]);

        var eventId = EventId.Create(postId);

        // One aggregate per post, rather than a single shared "posts" stream. A shared stream
        // would make every concurrent submission an optimistic-concurrency conflict against every
        // other -- correctness preserved, throughput destroyed, and for nothing: posts are not a
        // consistency boundary with each other. Threading is a read-model concern (the `parent`
        // field), not a write-model one.
        var aggregate = AggregateId.Create(postId);
        var actor = ActorId.Create(screened.Inner.AuthorAgentId);

        if (!eventId.TryGetValue(out var evId, out var e1)) return Result<PostAccepted>.Fail(e1!);
        if (!aggregate.TryGetValue(out var aggId, out var e2)) return Result<PostAccepted>.Fail(e2!);
        if (!actor.TryGetValue(out var actorId, out var e3)) return Result<PostAccepted>.Fail(e3!);

        var domainEvent = new DomainEvent(evId, PostAcceptedType, actorId, payload);

        var appended = await _events
            .AppendAsync(aggId, AggregateVersion.New, [domainEvent], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(_ => new PostAccepted(postId, serverTs, digest.ToString()));
    }

    /// <summary>
    /// R6.14: annotations ride beside the signed content, in a distinct field. They carry no
    /// content -- see <see cref="RiskFlag"/> -- so this payload cannot leak what a detector found.
    /// </summary>
    private static JsonValue.Array RiskFlagsPayload(RiskAnnotations annotations) =>
        new JsonValue.Array(
        [
            .. annotations.Flags.Select(f => (JsonValue)new JsonValue.Object(
            [
                new("category", new JsonValue.String(f.Category.ToString())),
                new("offset", new JsonValue.Number(f.Offset)),
                new("length", new JsonValue.Number(f.Length)),
                new("detector", new JsonValue.String(f.DetectorVersion)),
            ])),
        ]);

    private static T MustCreate<T>(Result<T> result) =>
        result.TryGetValue(out var value, out var error)
            ? value!
            : throw new InvalidOperationException($"Static identifier failed to construct: {error!.Type}");
}
