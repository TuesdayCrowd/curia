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
/// <para><b>The rationale is screened, and that is the part worth reviewing.</b> R10.35 makes a
/// rationale mandatory — a flag nobody can review is not reviewable, cannot be appealed against
/// (R10.38), and cannot be counted honestly in R10.39's upheld rate. But that makes the rationale an
/// ingest path: attacker-controlled text, persisted into an append-only log, with no redaction
/// primitive to take it back. R10.28's argument at ingest applies unchanged — "a scanner that logs
/// what it finds is a credential aggregator" — because a rationale reading "this post leaks AKIA…"
/// republishes the credential the flag was reporting, into the one table nothing can edit.</para>
///
/// <para>So it goes through <see cref="ContentScreener"/> under the same two-regime table the post
/// body does: <c>RiskCategories</c> maps credential material to reject and injection patterns to
/// annotate. Nothing new was built for this; the screener is reused because a second screening rule
/// for a second ingest path is how the two come to disagree.</para>
///
/// <para><b>Why the flag is attributed by token rather than signed.</b> R10.37 requires a
/// <i>moderation action</i> to be a signed log entry (R6.25); R10.35 requires no such thing of a
/// flag. The raiser is therefore the DPoP-bound principal the transport authenticated — the same
/// source <c>SubmitAsync</c> uses for Table 9's author check. Recorded as a decision rather than
/// left to look like an oversight, since the neighbouring requirement does demand signatures.</para>
/// </summary>
public sealed class RaiseFlag
{
    private readonly IEventStore _events;
    private readonly UlidGenerator _ids;

    public RaiseFlag(IEventStore events, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>
    /// Records a flag against <paramref name="postId"/>, or reports why it was not recorded.
    ///
    /// <para>Named <c>RecordAsync</c> to match <c>EnrollAgent</c>, the other use case that appends a
    /// fact about an agent's conduct to the log.</para>
    /// </summary>
    /// <param name="postId">The post being flagged; also the aggregate the flag lands in.</param>
    /// <param name="raisedBy">The authenticated principal raising it.</param>
    /// <param name="kind">One of R10.35's seven types.</param>
    /// <param name="rationale">Required, and screened before it is persisted.</param>
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
        var screened = ContentScreener.Screen(Encoding.UTF8.GetBytes(rationale));
        if (!screened.TryGetValue(out var screening, out var screeningError))
            return Result<FlagRaised>.Fail(screeningError!);

        if (!screening!.MayPersist)
            return Result<FlagRaised>.Fail(FlagErrors.RationaleRejected(screening.Annotations));

        if (!AggregateId.Create(postId).TryGetValue(out var aggregate, out var aggregateError))
            return Result<FlagRaised>.Fail(aggregateError!);

        if (!ActorId.Create(raisedBy).TryGetValue(out var actor, out var actorError))
            return Result<FlagRaised>.Fail(actorError!);

        // The post's own stream, read to establish that there is a post at all. A flag against an
        // identifier the log has never seen is a client error rather than a fact worth recording:
        // an append-only store cannot take back a flag raised against a typo.
        var history = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        if (!history.TryGetValue(out var events, out var readError))
            return Result<FlagRaised>.Fail(readError!);

        if (events!.Count == 0)
            return Result<FlagRaised>.Fail(FlagErrors.NoSuchPost(postId));

        if (!AggregateVersion.From(events.Count).TryGetValue(out var version, out var versionError))
            return Result<FlagRaised>.Fail(versionError!);

        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<FlagRaised>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<FlagRaised>.Fail(eventIdError!);

        if (!EventType.Create(FlagProjector.FlagRaisedType).TryGetValue(out var type, out var typeError))
            return Result<FlagRaised>.Fail(typeError!);

        var payload = new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(postId)),
            new(FlagProjector.RaisedByField, new JsonValue.String(raisedBy)),
            new(FlagProjector.KindField, new JsonValue.String(FlagKinds.Wire(kind))),

            // The rationale is on the event because a moderator has to read it to adjudicate. It is
            // deliberately absent from the projection this feeds -- see RaisedFlag -- so nothing
            // that serves can echo it.
            new(FlagProjector.RationaleField, new JsonValue.String(rationale)),
        ]);

        var appended = await _events
            .AppendAsync(aggregate, version, [new DomainEvent(eventId, type, actor, payload)], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(recorded => new FlagRaised(postId, kind, recorded[0].ServerTimestamp.Value));
    }
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
    /// R10.26/R10.28: the rationale carried credential material and was refused.
    ///
    /// <para>The detail names the category and its position (R10.27) and <b>never the matched
    /// value</b> — which holds structurally rather than by care, because <c>RiskFlag</c> has no
    /// member that can carry content.</para>
    /// </summary>
    public static Error RationaleRejected(RiskAnnotations annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);

        var categories = string.Join(
            ", ",
            annotations.Flags.Select(f => $"{f.Category}@{f.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

        return new Error(
            "curia/flag/rationale-rejected",
            "The flag's rationale was rejected by ingest screening",
            categories);
    }
}
