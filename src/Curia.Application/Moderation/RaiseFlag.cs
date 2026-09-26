using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Application.Moderation;

/// <summary>What the log recorded when a flag was accepted.</summary>
/// <param name="PostId">The post flagged.</param>
/// <param name="Kind">The flag's type.</param>
/// <param name="RaisedAt">The store's <c>server_ts</c> for the append — R6.5's Forum observation.</param>
public sealed record FlagRaised(string PostId, FlagKind Kind, DateTimeOffset RaisedAt);

/// <summary>
/// R10.35's "any credentialed agent MAY flag content", as a use case.
///
/// <para><b>The flag is committed, not published (R10.62).</b> R6.46 and R6.47 make every event a
/// leaf and R6.51 serves every leaf's input to anyone, so a flag written as an event carrying its
/// raiser and rationale was published — executed in errata G13. The post, the raiser, the rationale
/// and a fresh salt go to the private store (R11.32); the log receives <c>flag.committed</c> on the
/// flag's own aggregate, with no actor, naming the kind and a salted commitment to the rest. The
/// kind and instant stay public so R10.39's volume by category is auditable; which post it concerns
/// becomes public only when a moderation record adjudicates it (R10.60).</para>
///
/// <para><b>The private row is written first.</b> A row whose entry never lands is read by nothing,
/// since every read starts from the log. An entry whose row never landed would be a public
/// commitment nobody can open; <see cref="FlagDirectory"/> counts one if it ever exists, but the order
/// here means it should not.</para>
///
/// <para><b>The rationale is screened</b> under the two-regime table the post body uses: credential
/// material is refused (R10.26) and injection patterns are annotated. It persists in a store with no
/// redaction primitive behind it, and R10.28's argument applies unchanged.</para>
///
/// <para><b>Attributed by token rather than signed.</b> R10.37 requires a <i>moderation action</i> to
/// be a signed log entry; R10.35 requires no such thing of a flag. The raiser is the DPoP-bound
/// principal the transport authenticated.</para>
/// </summary>
public sealed class RaiseFlag
{
    /// <summary>
    /// A flag's own aggregate. Not its post's stream: the aggregate id is a member of the leaf
    /// (R6.46), and the post must not be (R10.62).
    /// </summary>
    public const string FlagAggregatePrefix = "flag:";

    private readonly IEventStore _events;
    private readonly IFlagDetailStore _details;
    private readonly UlidGenerator _ids;
    private readonly Func<string> _newSalt;

    /// <param name="newSalt">The salt source. <see cref="FlagSalt.New"/> in production; a fixed value only in tests that pin a commitment.</param>
    public RaiseFlag(IEventStore events, IFlagDetailStore details, TimeProvider clock, Func<string>? newSalt = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _details = details;
        _ids = new UlidGenerator(clock);
        _newSalt = newSalt ?? FlagSalt.New;
    }

    /// <summary>Records a flag against <paramref name="postId"/>, or reports why it was not recorded.</summary>
    /// <param name="postId">The post being flagged. Recorded privately, never in the log (R10.62).</param>
    /// <param name="raisedBy">The authenticated principal raising it. Recorded privately.</param>
    /// <param name="kind">One of R10.35's seven types. Public.</param>
    /// <param name="rationale">Required, screened, and recorded privately.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<FlagRaised>> RecordAsync(
        string postId,
        string raisedBy,
        FlagKind kind,
        string rationale,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        ArgumentException.ThrowIfNullOrWhiteSpace(raisedBy);

        if (string.IsNullOrWhiteSpace(rationale))
            return Result<FlagRaised>.Fail(ModerationErrors.RationaleRequired());

        // SCREEN, before anything is written. The screener takes a span, which cannot be stored in
        // a field, so this phase structurally cannot retain what it screened.
        var screened = ContentScreener.ScreenText(Encoding.UTF8.GetBytes(rationale));
        if (!screened.TryGetValue(out var screening, out var screeningError))
            return Result<FlagRaised>.Fail(screeningError!);

        if (!screening!.MayPersist)
            return Result<FlagRaised>.Fail(FlagErrors.RationaleRejected(screening.Annotations));

        if (!AggregateId.Create(postId).TryGetValue(out var post, out var postError))
            return Result<FlagRaised>.Fail(postError!);

        // The post's own stream, read to establish that there is a post at all: an append-only store
        // cannot take back a flag raised against a typo.
        var history = await _events.ReadByAggregateAsync(post, cancellationToken).ConfigureAwait(false);
        if (!history.TryGetValue(out var events, out var readError))
            return Result<FlagRaised>.Fail(readError!);

        if (events!.Count == 0)
            return Result<FlagRaised>.Fail(FlagErrors.NoSuchPost(postId));

        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<FlagRaised>.Fail(idError!);

        var id = ulid.ToString();
        if (!EventId.Create(id).TryGetValue(out var eventId, out var eventIdError))
            return Result<FlagRaised>.Fail(eventIdError!);

        if (!AggregateId.Create(FlagAggregatePrefix + id).TryGetValue(out var aggregate, out var aggregateError))
            return Result<FlagRaised>.Fail(aggregateError!);

        if (!EventType.Create(FlagProjector.FlagCommittedType).TryGetValue(out var type, out var typeError))
            return Result<FlagRaised>.Fail(typeError!);

        var salt = _newSalt();
        if (!FlagCommitment.Of(postId, raisedBy, rationale, salt).TryGetValue(out var commitment, out var commitmentError))
            return Result<FlagRaised>.Fail(commitmentError!);

        // The private row first (see the remarks above).
        var stored = await _details
            .AppendAsync(new FlagDetail(id, postId, raisedBy, rationale, salt), cancellationToken)
            .ConfigureAwait(false);
        if (!stored.TryGetValue(out _, out var storeError)) return Result<FlagRaised>.Fail(storeError!);

        var payload = new JsonValue.Object(
        [
            new(FlagProjector.CommitmentField, new JsonValue.String(commitment!)),
            new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),
        ]);

        var appended = await _events
            .AppendAsync(aggregate, AggregateVersion.New, [new DomainEvent(eventId, type, null, payload)], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(recorded => new FlagRaised(postId, kind, recorded[0].ServerTimestamp.Value));
    }
}

/// <summary>The salt a flag's commitment is taken with (R10.62): 32 random bytes, base64url.</summary>
public static class FlagSalt
{
    public const int Bytes = 32;

    /// <summary>A fresh salt; 43 characters.</summary>
    public static string New() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(Bytes));
}

/// <summary>RFC 9457 problem-type slugs the flag path emits.</summary>
public static class FlagErrors
{
    /// <summary>
    /// A flag against a post the log has no record of. Named as its own condition rather than
    /// reported as a generic 404, so a client can tell "there is no such post" from "this route does
    /// not exist" — which return the same status and are very different problems.
    /// </summary>
    public static Error NoSuchPost(string postId) => new(
        "curia/flag/no-such-post",
        "No such post",
        $"post={postId}");

    /// <summary>
    /// R10.26/R10.28: the rationale carried credential material and was refused. The detail names
    /// the category and its position (R10.27) and never the matched value — structurally, because
    /// <c>RiskFlag</c> has no member that can carry content.
    /// </summary>
    public static Error RationaleRejected(RiskAnnotations annotations) => RationaleRefusal.Of(
        "curia/flag/rationale-rejected",
        "The flag's rationale was rejected by ingest screening",
        annotations);
}
