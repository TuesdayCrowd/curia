using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;

namespace Curia.Application.Projections;

/// <summary>
/// What §10.10's moderation records say about one post, in order (R10.60, R10.61).
///
/// <para><b>Flags are not here.</b> A flag's log entry names no post (R10.62), so which post a flag
/// concerns is known only to the private store; <see cref="FlagDirectory"/> joins the two. What a
/// post's moderation needs — whether it may be served, and which flags are upheld — is decided by
/// the records alone, which name the flags they adjudicate, so posture and serving read public
/// records only.</para>
/// </summary>
public sealed record PostModeration(string PostId, ImmutableArray<ModerationAction> History)
{
    /// <summary>R10.36, delegated to the domain: whether the serving path may still serve this post.</summary>
    public bool MayServe => ModerationPolicy.MayServe(History);

    /// <summary>R10.61: the flags this post's records currently uphold.</summary>
    public ImmutableHashSet<string> UpheldFlags => ModerationPolicy.UpheldFlags(History);

    /// <summary>
    /// Table 11's "no upheld flags", for one post: whether any flag its records adjudicated is upheld.
    /// False for a post nobody has adjudicated, however many flags it carries — see
    /// <see cref="ModerationPolicy.UpheldFlags"/> for why the alternative hands every agent a
    /// demotion primitive.
    /// </summary>
    public bool HasUpheldFlag => !UpheldFlags.IsEmpty;

    /// <summary>
    /// Structural equality, spelled out: <see cref="ImmutableArray{T}"/>'s own <c>Equals</c> compares
    /// the underlying array by reference, so generated record equality would report two folds of the
    /// same events as different and make R11.9's drill unassertable — and green.
    /// </summary>
    public bool Equals(PostModeration? other) =>
        other is not null
        && string.Equals(PostId, other.PostId, StringComparison.Ordinal)
        && History.SequenceEqual(other.History);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(PostId, History.Length);
}

/// <summary>
/// Folds <c>moderation.applied</c> records into per-post moderation state, and names every §10.10
/// event type and member.
///
/// <para><b>No clock</b>, for the reason <see cref="PostProjector"/> records: a rebuild that
/// consulted "now" would make R11.9's replay drill tautological.</para>
///
/// <para><b>A post no record names is absent</b> rather than present-and-empty. Absence is already
/// what the serving path means by "no moderation history".</para>
/// </summary>
public static class FlagProjector
{
    /// <summary>
    /// A flag as written before R10.62: its post, raiser and rationale in the log, publicly and
    /// permanently. Read by <see cref="FlagDirectory"/>; never written again.
    /// </summary>
    public const string FlagRaisedType = "flag.raised";

    /// <summary>A flag as <c>RaiseFlag</c> writes one (R10.62): its kind and a salted commitment, on its own aggregate, with no actor.</summary>
    public const string FlagCommittedType = "flag.committed";

    /// <summary>
    /// A moderation record. R10.37: "Every moderation action SHALL be a signed log entry with actor,
    /// category, and rationale."
    /// </summary>
    public const string ModerationAppliedType = "moderation.applied";

    /// <summary>The post a record acts on, or a legacy flag concerns.</summary>
    public const string PostIdField = "post_id";

    /// <summary>A legacy flag's raiser. Never written after R10.62.</summary>
    public const string RaisedByField = "raised_by";

    /// <summary>A flag's type, in R10.35's published spelling.</summary>
    public const string KindField = "kind";

    /// <summary>R10.37's "category" on a moderation record, in R10.35's published spelling.</summary>
    public const string CategoryField = "category";

    /// <summary>R10.36's moderator kind, which decides what the record was permitted to be.</summary>
    public const string ModeratorField = "moderator";

    /// <summary>R10.37's "actor".</summary>
    public const string ActorIdField = "actor_id";

    /// <summary>What the record did.</summary>
    public const string EffectField = "effect";

    /// <summary>A moderation record's rationale (R10.37), or a legacy flag's.</summary>
    public const string RationaleField = "rationale";

    /// <summary>R6.25's "a <c>moderation</c> record referencing a digest": the post's envelope digest (R10.60).</summary>
    public const string DigestField = "digest";

    /// <summary>The flags a moderation record adjudicates, by event id (R10.60, R10.61).</summary>
    public const string AdjudicatesField = "adjudicates";

    /// <summary>A committed flag's commitment (R10.62).</summary>
    public const string CommitmentField = "commitment";

    /// <summary>Folds a seq-ordered event list into per-post moderation state.</summary>
    public static ImmutableDictionary<string, PostModeration> Fold(IReadOnlyList<AppendedEvent> eventsInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);

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

            if (appended.Event.Type.Value != ModerationAppliedType) continue;
            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            ApplyModeration(actions, Members(payload), appended);
        }

        return actions.ToImmutableDictionary(
            entry => entry.Key,
            entry => new PostModeration(entry.Key, entry.Value.ToImmutable()),
            StringComparer.Ordinal);
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

        // A record without the member still governs servability and upholds nothing (R10.61). It is
        // read, not dropped: dropping a withholding would serve what a human withheld.
        ImmutableArray<string> adjudicates = fields.TryGetValue(AdjudicatesField, out var named) && named is JsonValue.Array list
            ? [.. list.Items.OfType<JsonValue.String>().Select(s => s.Value)]
            : [];

        if (!actions.TryGetValue(postId, out var builder))
        {
            builder = ImmutableArray.CreateBuilder<ModerationAction>();
            actions[postId] = builder;
        }

        builder.Add(new ModerationAction(
            postId, moderator, actorId, effect, category, rationale, appended.ServerTimestamp, adjudicates));
    }

    internal static Dictionary<string, JsonValue> Members(JsonValue.Object payload)
    {
        var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var member in payload.Members)
            fields[member.Key] = member.Value;

        return fields;
    }

    internal static bool Str(Dictionary<string, JsonValue> fields, string name, out string value)
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
