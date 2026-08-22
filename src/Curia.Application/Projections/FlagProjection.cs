using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>
/// One flag, as the log recorded it.
///
/// <para><b>There is no rationale here, and that is the design.</b> R10.35 requires a flag to carry
/// one — a flag nobody can review is not reviewable — but the rationale is attacker-controlled text,
/// and R10.28's argument at ingest applies unchanged: a projection that carried it would let the
/// serving path echo it, and a rationale reading "this post leaks AKIA…" would republish the very
/// credential the flag was reporting. The rationale stays in the log, where a moderator reads it;
/// it never reaches a read model that anything serves from. This is the same shape as
/// <c>RiskFlag</c>, which records a category and an offset and never the matched text.</para>
/// </summary>
public sealed record RaisedFlag(string PostId, string RaisedBy, FlagKind Kind, ServerTimestamp At);

/// <summary>
/// What §10.10 knows about one post: the flags raised against it and the moderation actions taken
/// on it, in order.
/// </summary>
public sealed record PostModeration(
    string PostId,
    ImmutableArray<RaisedFlag> Flags,
    ImmutableArray<ModerationAction> History)
{
    /// <summary>R10.36, delegated to the domain: whether the serving path may still serve this post.</summary>
    public bool MayServe => ModerationPolicy.MayServe(History);

    /// <summary>
    /// Table 11's "no upheld flags", for one post: whether any flag raised against it has been
    /// upheld by a moderator. False for a post nobody has adjudicated, however many flags it carries
    /// — see <see cref="ModerationPolicy.IsUpheld"/> for why the alternative hands every agent a
    /// demotion primitive.
    /// </summary>
    public bool HasUpheldFlag => Flags.Any(f => ModerationPolicy.IsUpheld(f.Kind, History));

    /// <summary>
    /// Structural equality, spelled out for the reason <c>AgentStanding</c> records:
    /// <see cref="ImmutableArray{T}"/>'s own <c>Equals</c> compares the underlying array
    /// <i>reference</i>, so the compiler-generated record equality reports two folds of the very
    /// same events as different. R11.9's rebuild drill is asserted by comparing a projection against
    /// its own rebuild, so leaving this to the compiler makes the drill unassertable — and green.
    /// </summary>
    public bool Equals(PostModeration? other) =>
        other is not null
        && string.Equals(PostId, other.PostId, StringComparison.Ordinal)
        && Flags.SequenceEqual(other.Flags)
        && History.SequenceEqual(other.History);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(PostId, Flags.Length, History.Length);
}

/// <summary>
/// Folds §10.10's two event types into per-post moderation state — the projection that gives
/// <see cref="ModerationPolicy"/> its first caller.
///
/// <para><b>No clock</b>, for the reason <see cref="AggregateSummaryProjector"/> and
/// <see cref="PostProjector"/> both record: a rebuild that consulted "now" would make R11.9's replay
/// drill tautological. Every instant below is one an event already carries.</para>
///
/// <para><b>A post nothing has been raised against is absent</b> rather than present-and-empty.
/// Absence is already what the serving path means by "no moderation history", and materialising an
/// entry per post would make this projection grow with the corpus rather than with the flags.</para>
/// </summary>
public static class FlagProjector
{
    /// <summary>The event <c>RaiseFlag</c> appends. R10.35.</summary>
    public const string FlagRaisedType = "flag.raised";

    /// <summary>
    /// The event a moderation decision appends. R10.37: "Every moderation action SHALL be a signed
    /// log entry with actor, category, and rationale."
    ///
    /// <para><b>Nothing writes this over HTTP yet, deliberately.</b> Table 10 gates
    /// <c>moderation:apply</c> to "T3 (delegated)" and Table 22 puts delegated moderation in Phase 4,
    /// so a route for it now would mean inventing R10.36's delegation-grant machinery ahead of its
    /// phase. The type exists because the serving filter below has to be testable: a
    /// <see cref="PostModeration.MayServe"/> folded over a history that could only ever be empty is
    /// a filter whose silence carries no information, which is the failure this project keeps
    /// finding in its own probes.</para>
    /// </summary>
    public const string ModerationAppliedType = "moderation.applied";

    /// <summary>The post both event types are about.</summary>
    public const string PostIdField = "post_id";

    /// <summary>R10.35's "any credentialed agent": who raised the flag.</summary>
    public const string RaisedByField = "raised_by";

    /// <summary>The flag's type, in R10.35's published spelling.</summary>
    public const string KindField = "kind";

    /// <summary>R10.37's "category" on a moderation action, in R10.35's published spelling.</summary>
    public const string CategoryField = "category";

    /// <summary>R10.36's moderator kind, which decides what the action was permitted to be.</summary>
    public const string ModeratorField = "moderator";

    /// <summary>R10.37's "actor".</summary>
    public const string ActorIdField = "actor_id";

    /// <summary>What the action did.</summary>
    public const string EffectField = "effect";

    /// <summary>
    /// R10.35 and R10.37 both require a rationale. Recorded on the event and deliberately not
    /// projected — see <see cref="RaisedFlag"/>.
    /// </summary>
    public const string RationaleField = "rationale";

    /// <summary>Folds a seq-ordered event list into per-post moderation state.</summary>
    public static ImmutableDictionary<string, PostModeration> Fold(
        IReadOnlyList<AppendedEvent> eventsInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);

        var flags = new Dictionary<string, ImmutableArray<RaisedFlag>.Builder>(StringComparer.Ordinal);
        var actions = new Dictionary<string, ImmutableArray<ModerationAction>.Builder>(StringComparer.Ordinal);
        var lastSeq = EventSequence.Zero;

        foreach (var appended in eventsInSeqOrder)
        {
            if (appended.Seq < lastSeq)
                throw new ArgumentException(
                    "Events must arrive in ascending seq order; every IEventReader this solution " +
                    "ships already guarantees that, so a violation means the caller did not get " +
                    "these from a store's forward scan.",
                    nameof(eventsInSeqOrder));

            lastSeq = appended.Seq;

            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            switch (appended.Event.Type.Value)
            {
                case FlagRaisedType:
                    ApplyFlag(flags, Members(payload), appended);
                    break;

                case ModerationAppliedType:
                    ApplyModeration(actions, Members(payload), appended);
                    break;

                default:
                    break;
            }
        }

        // Every post either half mentions. A post with a moderation action and no surviving flag
        // still belongs here -- MayServe is what the serving path reads, and dropping it because
        // nothing was flagged would serve withheld content.
        return flags.Keys
            .Concat(actions.Keys)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableDictionary(
                postId => postId,
                postId => new PostModeration(
                    postId,
                    flags.TryGetValue(postId, out var f) ? f.ToImmutable() : [],
                    actions.TryGetValue(postId, out var a) ? a.ToImmutable() : []),
                StringComparer.Ordinal);
    }

    private static void ApplyFlag(
        Dictionary<string, ImmutableArray<RaisedFlag>.Builder> flags,
        Dictionary<string, JsonValue> fields,
        AppendedEvent appended)
    {
        if (!Str(fields, PostIdField, out var postId)) return;
        if (!Str(fields, RaisedByField, out var raisedBy)) return;
        if (!Str(fields, KindField, out var kindWire)) return;
        if (!FlagKinds.Parse(kindWire).TryGetValue(out var kind, out _)) return;

        For(flags, postId).Add(new RaisedFlag(postId, raisedBy, kind, appended.ServerTimestamp));
    }

    private static void ApplyModeration(
        Dictionary<string, ImmutableArray<ModerationAction>.Builder> actions,
        Dictionary<string, JsonValue> fields,
        AppendedEvent appended)
    {
        if (!Str(fields, PostIdField, out var postId)) return;
        if (!Str(fields, ActorIdField, out var actorId)) return;
        if (!Str(fields, RationaleField, out var rationale)) return;
        if (!Str(fields, ModeratorField, out var moderatorWire)) return;
        if (!Str(fields, EffectField, out var effectWire)) return;
        if (!Str(fields, CategoryField, out var categoryWire)) return;

        if (!ModeratorKinds.Parse(moderatorWire).TryGetValue(out var moderator, out _)) return;
        if (!ModerationEffects.Parse(effectWire).TryGetValue(out var effect, out _)) return;
        if (!FlagKinds.Parse(categoryWire).TryGetValue(out var category, out _)) return;

        For(actions, postId).Add(new ModerationAction(
            postId, moderator, actorId, effect, category, rationale, appended.ServerTimestamp));
    }

    private static ImmutableArray<T>.Builder For<T>(
        Dictionary<string, ImmutableArray<T>.Builder> builders, string postId)
    {
        if (!builders.TryGetValue(postId, out var builder))
        {
            builder = ImmutableArray.CreateBuilder<T>();
            builders[postId] = builder;
        }

        return builder;
    }

    private static Dictionary<string, JsonValue> Members(JsonValue.Object payload)
    {
        var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var member in payload.Members)
            fields[member.Key] = member.Value;

        return fields;
    }

    private static bool Str(Dictionary<string, JsonValue> fields, string name, out string value)
    {
        if (fields.TryGetValue(name, out var raw) && raw is JsonValue.String s)
        {
            value = s.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
