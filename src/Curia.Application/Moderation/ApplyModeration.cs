using System.Collections.Immutable;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Application.Moderation;

/// <summary>What the log recorded when a moderation record was accepted.</summary>
/// <param name="Adjudicates">The flags the record names (R10.60): every flag of its category on the post, derived, never typed.</param>
/// <param name="At">The store's <c>server_ts</c> for the append.</param>
public sealed record ModerationRecorded(
    string PostId,
    string Digest,
    ModerationEffect Effect,
    FlagKind Category,
    ImmutableArray<string> Adjudicates,
    ActorId Moderator,
    DateTimeOffset At);

/// <summary>
/// R10.59's human arm: an operator's moderation record, appended out of band.
///
/// <para><b>No HTTP route reaches this.</b> Table 10 grants <c>moderation</c>|<c>apply</c> only to a
/// delegated T3 agent (Phase 4); an operator endpoint would need a pair that does not exist, and
/// <c>ResourceActionModel.RowFor</c> reports an unmodelled pair as a failure. The operator tool calls
/// this over the event store's append-only grant, as it calls <c>AttestOwner</c> (errata G5).</para>
///
/// <para><b>What a record carries (R10.60).</b> The post and its envelope digest (R6.25), the
/// moderator kind and actor, the effect and category, a screened rationale, and the flags it
/// adjudicates — derived here from the flag directory, never typed by the moderator, because the
/// record is the only place a reader of the public log learns which flags were reviewed.</para>
///
/// <para><b>A record that changes nothing is refused.</b> R10.39 counts records. A record that
/// changes neither servability nor any flag's upheld state, and names no flag no earlier record
/// named, would inflate the counts while recording no decision.</para>
/// </summary>
public sealed class ApplyModeration
{
    /// <summary>The actor namespace R10.59 gives the human arm (plan D4: a convention the domain cannot enforce, so it is enforced here).</summary>
    public const string OperatorPrefix = "operator:";

    private readonly IEventStore _events;
    private readonly IFlagDetailStore _details;
    private readonly TimeProvider _clock;
    private readonly UlidGenerator _ids;

    public ApplyModeration(IEventStore events, IFlagDetailStore details, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _details = details;
        _clock = clock;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>Records <paramref name="effect"/> on <paramref name="postId"/> in <paramref name="category"/>, or reports why not.</summary>
    public async Task<Result<ModerationRecorded>> RecordAsync(
        string postId,
        ModerationEffect effect,
        FlagKind category,
        string rationale,
        ActorId moderator,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);

        if (moderator.Value is null
            || !moderator.Value.StartsWith(OperatorPrefix, StringComparison.Ordinal)
            || moderator.Value.Length == OperatorPrefix.Length)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.NotAnOperator());

        if (string.IsNullOrWhiteSpace(rationale))
            return Result<ModerationRecorded>.Fail(ModerationErrors.RationaleRequired());

        // R10.60: the rationale lands in a leaf R6.51 serves verbatim, under the same two-regime
        // table a flag's rationale is screened with.
        var screened = ContentScreener.ScreenText(Encoding.UTF8.GetBytes(rationale));
        if (!screened.TryGetValue(out var screening, out var screeningError))
            return Result<ModerationRecorded>.Fail(screeningError!);

        if (!screening!.MayPersist)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.RationaleRejected(screening.Annotations));

        if (!AggregateId.Create(postId).TryGetValue(out var aggregate, out var aggregateError))
            return Result<ModerationRecorded>.Fail(aggregateError!);

        var read = await _events.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var log, out var readError))
            return Result<ModerationRecorded>.Fail(readError!);

        var post = PostProjector.Fold(log!).FirstOrDefault(p => string.Equals(p.PostId, postId, StringComparison.Ordinal));
        if (post is null)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.NoSuchPost(postId));

        var rows = await _details.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!rows.TryGetValue(out var details, out var detailError))
            return Result<ModerationRecorded>.Fail(detailError!);

        // R10.60: every flag of this category raised against the post so far — derived, never typed.
        ImmutableArray<string> adjudicates =
        [
            .. FlagDirectory.Join(log!, details!).Flags
                .Where(f => string.Equals(f.PostId, postId, StringComparison.Ordinal) && f.Kind == category)
                .Select(f => f.FlagId),
        ];

        ImmutableArray<ModerationAction> before = FlagProjector.Fold(log!).TryGetValue(postId, out var moderation)
            ? moderation.History
            : [];

        var action = new ModerationAction(
            postId, ModeratorKind.Human, moderator.Value, effect, category, rationale, ServerTimestamp.At(_clock.GetUtcNow()), adjudicates);

        if (!ModerationPolicy.Authorize(action).TryGetValue(out _, out var authorizeError))
            return Result<ModerationRecorded>.Fail(authorizeError!);

        ImmutableArray<ModerationAction> after = [.. before, action];
        var noOp = ModerationPolicy.MayServe(before) == ModerationPolicy.MayServe(after)
            && ModerationPolicy.UpheldFlags(before).SetEquals(ModerationPolicy.UpheldFlags(after))
            && ModerationPolicy.AdjudicatedFlags(before).IsSupersetOf(adjudicates);
        if (noOp)
            return Result<ModerationRecorded>.Fail(ModerationRecordErrors.NoOp(postId, effect, category));

        // The expected version comes from the read the decision was made on, not from a second read.
        // A record another operator appended to this post since then fails this append, rather than
        // standing beside a record decided without it (R10.39 counts records). A flag committed since
        // is on its own aggregate and is not caught here; it stays open until a later record names it.
        if (!AggregateVersion.From(log!.Count(e => e.AggregateId == aggregate)).TryGetValue(out var version, out var versionError))
            return Result<ModerationRecorded>.Fail(versionError!);

        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<ModerationRecorded>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<ModerationRecorded>.Fail(eventIdError!);

        if (!EventType.Create(FlagProjector.ModerationAppliedType).TryGetValue(out var type, out var typeError))
            return Result<ModerationRecorded>.Fail(typeError!);

        var payload = new JsonValue.Object(
        [
            new(FlagProjector.PostIdField, new JsonValue.String(postId)),
            new(FlagProjector.DigestField, new JsonValue.String(post.Digest)),
            new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(action.Moderator))),
            new(FlagProjector.ActorIdField, new JsonValue.String(moderator.Value)),
            new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(effect))),
            new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(category))),
            new(FlagProjector.RationaleField, new JsonValue.String(rationale)),
            new(FlagProjector.AdjudicatesField, new JsonValue.Array([.. adjudicates.Select(id => (JsonValue)new JsonValue.String(id))])),
        ]);

        var appended = await _events
            .AppendAsync(aggregate, version, [new DomainEvent(eventId, type, moderator, payload)], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(events => new ModerationRecorded(
            postId, post.Digest, effect, category, adjudicates, moderator, events[0].ServerTimestamp.Value));
    }
}

/// <summary>RFC 9457 problem-type slugs the moderation writer emits.</summary>
public static class ModerationRecordErrors
{
    /// <summary>R10.59: the human arm is an operator's, named <c>operator:&lt;name&gt;</c>.</summary>
    public static Error NotAnOperator() => new(
        "curia/moderation/not-an-operator",
        "Only an operator records a human moderator's action (R10.59)",
        "the actor must be named operator:<name>");

    public static Error NoSuchPost(string postId) => new(
        "curia/moderation/no-such-post",
        "No such post",
        $"post={postId}");

    /// <summary>R10.60: categories and offsets only (R10.27), never the matched value.</summary>
    public static Error RationaleRejected(RiskAnnotations annotations) => RationaleRefusal.Of(
        "curia/moderation/rationale-rejected",
        "The moderator's rationale was rejected by screening; it would land in a public leaf (R10.60)",
        annotations);

    /// <summary>A record that would change nothing (spec Decision 11).</summary>
    public static Error NoOp(string postId, ModerationEffect effect, FlagKind category) => new(
        "curia/moderation/no-op",
        "That record would change nothing, and R10.39 counts records",
        $"post={postId} effect={ModerationEffects.Wire(effect)} category={FlagKinds.Wire(category)}");
}
