using System.Collections.Immutable;
using Curia.Domain;

namespace Curia.Application.Reads;

/// <summary>
/// One clause of a read set (errata G87, R11.113): a closed list, so a route cannot name a type or a payload member the list does not, and a fourth shape breaks every adapter (CS-11).
/// There is no clause that reads "every row of this type" for a route: <see cref="Keys"/> names the three published-key types and nothing else, and a payload clause names one member of
/// the enum below, each of which names the index that serves it. A clause with no index cannot be built, because an unindexed lookup would not fail: it would be a sequential scan that
/// reads as fast on a small log, and the silence would be the defect.
/// </summary>
public abstract class ReadClause
{
    private ReadClause()
    {
    }

    /// <summary>Every row of each named aggregate (<c>(aggregate_id, seq)</c>).</summary>
    public static ReadClause Aggregates(IEnumerable<string> ids) => new AggregatesClause([.. ids.Distinct(StringComparer.Ordinal)]);

    /// <summary>The three published-key types, in full: <c>log.key</c>, <c>log.declaration-key</c>, <c>log.operator-key</c> (<c>(event_type, seq)</c>). Operator-bounded.</summary>
    public static ReadClause Keys() => new KeysClause();

    /// <summary>Every post (<c>post.accepted</c>) whose <c>actor_id</c> is <paramref name="actor"/>, its author (<c>(event_type, seq)</c> with the actor as a filter: no index on <c>actor_id</c> is built, its measured cost being small).</summary>
    public static ReadClause PostsBy(string actor) => new PostsByClause(actor);

    /// <summary>Every acceptance (<c>answer.accepted</c>) whose <c>actor_id</c> is <paramref name="actor"/>, the asker who accepted (<c>(event_type, seq)</c>, the actor as a filter).</summary>
    public static ReadClause AcceptancesBy(string actor) => new AcceptancesByClause(actor);

    /// <summary>Every post whose digest is one of <paramref name="digests"/> (<c>events_post_digest</c>).</summary>
    public static ReadClause PostsWithDigest(IEnumerable<string> digests) => new PayloadClause(PayloadMember.Digest, [.. digests.Distinct(StringComparer.Ordinal)]);

    /// <summary>Every vote and verification whose signed envelope targets one of <paramref name="digests"/> (<c>events_post_signal_target</c>).</summary>
    public static ReadClause SignalsOn(IEnumerable<string> digests) => new PayloadClause(PayloadMember.SignalTarget, [.. digests.Distinct(StringComparer.Ordinal)]);

    /// <summary>The rows whose event id is one of <paramref name="eventIds"/> (the unique index on <c>event_id</c>): the flag entries a private-store row names.</summary>
    public static ReadClause Entries(IEnumerable<string> eventIds) => new EntriesClause([.. eventIds.Distinct(StringComparer.Ordinal)]);

    /// <summary>Every <c>flag.raised</c> row, the form a flag had before R10.62 (<c>(event_type, seq)</c>): none in a log written since.</summary>
    public static ReadClause LegacyFlags() => new LegacyFlagsClause();

    /// <summary>
    /// Every lifecycle entry (<c>agent.enrolled</c>, <c>agent.key-bound</c>, <c>agent.key-retired</c>) of any identity that names <paramref name="kid"/> (R4.37, errata G16: a kid is bound once).
    /// <b>Served by <c>(event_type, seq)</c> with the kid as a filter: no index on the payload's <c>kid</c> exists in <c>db/0007</c>, so this clause reads the rows of three types, which grow with
    /// identities.</b> It is the one clause that does not name a payload index, it is made for a rotation alone (rate-limited, R7.23), and its cost is stated as a lead, not hidden.
    /// </summary>
    public static ReadClause LifecycleNaming(string kid) => new LifecycleNamingClause(kid);

    /// <summary>The one way to read a clause: every case, so a new one breaks every caller.</summary>
    public abstract T Match<T>(
        Func<IReadOnlyList<string>, T> aggregates,
        Func<T> keys,
        Func<string, T> postsBy,
        Func<string, T> acceptancesBy,
        Func<PayloadMember, IReadOnlyList<string>, T> payload,
        Func<IReadOnlyList<string>, T> entries,
        Func<T> legacyFlags,
        Func<string, T> lifecycleNaming);

    /// <summary>The payload members a clause can name; each names its index in <c>db/0007</c>.</summary>
    public enum PayloadMember
    {
        /// <summary><c>payload-&gt;&gt;'digest'</c> of a <c>post.accepted</c> row: <c>events_post_digest</c>.</summary>
        Digest,

        /// <summary>The <c>target</c> member of the signed envelope of a vote or verification: <c>events_post_signal_target</c>.</summary>
        SignalTarget,
    }

    /// <summary>The index each payload member is served by, by name.</summary>
    public static string IndexOf(PayloadMember member) => member switch
    {
        PayloadMember.Digest => "events_post_digest",
        PayloadMember.SignalTarget => "events_post_signal_target",
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "Not a payload member"),
    };

    private sealed class AggregatesClause(ImmutableArray<string> ids) : ReadClause
    {
        public override T Match<T>(Func<IReadOnlyList<string>, T> aggregates, Func<T> keys, Func<string, T> postsBy, Func<string, T> acceptancesBy, Func<PayloadMember, IReadOnlyList<string>, T> payload, Func<IReadOnlyList<string>, T> entries, Func<T> legacyFlags, Func<string, T> lifecycleNaming) => aggregates(ids);
    }

    private sealed class KeysClause : ReadClause
    {
        public override T Match<T>(Func<IReadOnlyList<string>, T> aggregates, Func<T> keys, Func<string, T> postsBy, Func<string, T> acceptancesBy, Func<PayloadMember, IReadOnlyList<string>, T> payload, Func<IReadOnlyList<string>, T> entries, Func<T> legacyFlags, Func<string, T> lifecycleNaming) => keys();
    }

    private sealed class PostsByClause(string actor) : ReadClause
    {
        public override T Match<T>(Func<IReadOnlyList<string>, T> aggregates, Func<T> keys, Func<string, T> postsBy, Func<string, T> acceptancesBy, Func<PayloadMember, IReadOnlyList<string>, T> payload, Func<IReadOnlyList<string>, T> entries, Func<T> legacyFlags, Func<string, T> lifecycleNaming) => postsBy(actor);
    }

    private sealed class AcceptancesByClause(string actor) : ReadClause
    {
        public override T Match<T>(Func<IReadOnlyList<string>, T> aggregates, Func<T> keys, Func<string, T> postsBy, Func<string, T> acceptancesBy, Func<PayloadMember, IReadOnlyList<string>, T> payload, Func<IReadOnlyList<string>, T> entries, Func<T> legacyFlags, Func<string, T> lifecycleNaming) => acceptancesBy(actor);
    }

    private sealed class PayloadClause(PayloadMember member, ImmutableArray<string> values) : ReadClause
    {
        public override T Match<T>(Func<IReadOnlyList<string>, T> aggregates, Func<T> keys, Func<string, T> postsBy, Func<string, T> acceptancesBy, Func<PayloadMember, IReadOnlyList<string>, T> payload, Func<IReadOnlyList<string>, T> entries, Func<T> legacyFlags, Func<string, T> lifecycleNaming) => payload(member, values);
    }

    private sealed class EntriesClause(ImmutableArray<string> ids) : ReadClause
    {
        public override T Match<T>(Func<IReadOnlyList<string>, T> aggregates, Func<T> keys, Func<string, T> postsBy, Func<string, T> acceptancesBy, Func<PayloadMember, IReadOnlyList<string>, T> payload, Func<IReadOnlyList<string>, T> entries, Func<T> legacyFlags, Func<string, T> lifecycleNaming) => entries(ids);
    }

    private sealed class LegacyFlagsClause : ReadClause
    {
        public override T Match<T>(Func<IReadOnlyList<string>, T> aggregates, Func<T> keys, Func<string, T> postsBy, Func<string, T> acceptancesBy, Func<PayloadMember, IReadOnlyList<string>, T> payload, Func<IReadOnlyList<string>, T> entries, Func<T> legacyFlags, Func<string, T> lifecycleNaming) => legacyFlags();
    }

    private sealed class LifecycleNamingClause(string kid) : ReadClause
    {
        public override T Match<T>(Func<IReadOnlyList<string>, T> aggregates, Func<T> keys, Func<string, T> postsBy, Func<string, T> acceptancesBy, Func<PayloadMember, IReadOnlyList<string>, T> payload, Func<IReadOnlyList<string>, T> entries, Func<T> legacyFlags, Func<string, T> lifecycleNaming) => lifecycleNaming(kid);
    }
}

/// <summary>
/// A read plan (errata G87, R11.113): at most <see cref="MaxStages"/> stages taken in one snapshot. A stage's clauses are a pure function of the events the earlier stages returned (their log indices are computed once, for the whole read, and a stage does not need them), so a row appended
/// between stage 1 and stage 3 is in none of them. The stage count is a constant of the plan, never of the data.
/// </summary>
public sealed class ReadPlan
{
    /// <summary>The most stages a plan takes: the agent's own rows, what they name, and what that names.</summary>
    public const int MaxStages = 3;

    private readonly ImmutableArray<Func<IReadOnlyList<AppendedEvent>, IReadOnlyList<ReadClause>>> _stages;

    private ReadPlan(ImmutableArray<Func<IReadOnlyList<AppendedEvent>, IReadOnlyList<ReadClause>>> stages) => _stages = stages;

    /// <summary>A plan of one stage whose clauses do not depend on anything read.</summary>
    public static ReadPlan Of(params ReadClause[] clauses) => new([_ => clauses]);

    /// <summary>A plan of up to <see cref="MaxStages"/> stages; each is given every event the earlier ones returned.</summary>
    public static ReadPlan Staged(params Func<IReadOnlyList<AppendedEvent>, IReadOnlyList<ReadClause>>[] stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        if (stages.Length is 0 or > MaxStages)
            throw new ArgumentOutOfRangeException(nameof(stages), stages.Length, $"A read plan has between 1 and {MaxStages} stages.");

        return new([.. stages]);
    }

    /// <summary>The stages, in order.</summary>
    public IReadOnlyList<Func<IReadOnlyList<AppendedEvent>, IReadOnlyList<ReadClause>>> Stages => _stages;
}
