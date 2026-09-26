using System.Collections.Immutable;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>
/// One flag, as the log and the private store together record it.
///
/// <para><b>There is no rationale here, and that is the design.</b> The rationale is
/// attacker-controlled text; a read model carrying it would let a serving path echo it, and a
/// rationale reading "this post leaks AKIA…" would republish the credential it reported. It stays in
/// the private store, where the operator's out-of-band listing reads it (R10.59).</para>
/// </summary>
/// <param name="FlagId">The flag's event id: what a moderation record's <c>adjudicates</c> names (R10.60).</param>
public sealed record RaisedFlag(string FlagId, string PostId, string RaisedBy, FlagKind Kind, ServerTimestamp At);

/// <summary>
/// R7.18's flags, joined from the two places R10.62 puts them.
///
/// <para><b>Two shapes.</b> A <c>flag.committed</c> entry names its kind and a commitment, and the
/// private row supplies the post and the raiser; the row is believed only if it still opens the
/// entry's commitment. A legacy <c>flag.raised</c> event — written before R10.62 — carries all of it
/// in the log, publicly and permanently, and is read as it stands.</para>
///
/// <para><b>Skips are counted, never silent</b> (R11.31's shape, for the join this stage creates). A
/// committed entry with no row, or with a row that no longer opens it, is listed in
/// <see cref="Skipped"/> by reason; a directory that dropped it quietly would read as a Forum where
/// that flag was never raised.</para>
///
/// <para><b>No clock</b>, for the reason every projector here gives: a rebuild that read "now" would
/// make R11.9's drill tautological.</para>
/// </summary>
public sealed record FlagDirectory(ImmutableArray<RaisedFlag> Flags, ImmutableSortedDictionary<string, int> Skipped)
{
    public const string SkippedNoDetail = "flag.committed: no detail";
    public const string SkippedCommitmentMismatch = "flag.committed: commitment mismatch";
    public const string SkippedUnreadableCommitted = "flag.committed: unreadable";
    public const string SkippedUnreadableLegacy = "flag.raised: unreadable";

    /// <summary>Joins a seq-ordered log with the private store's rows.</summary>
    public static FlagDirectory Join(IReadOnlyList<AppendedEvent> eventsInSeqOrder, IReadOnlyList<FlagDetail> details)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);
        ArgumentNullException.ThrowIfNull(details);

        var byEvent = new Dictionary<string, FlagDetail>(StringComparer.Ordinal);
        foreach (var detail in details)
            byEvent.TryAdd(detail.EventId, detail);

        var flags = ImmutableArray.CreateBuilder<RaisedFlag>();
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
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

            var type = appended.Event.Type.Value;
            if (type != FlagProjector.FlagRaisedType && type != FlagProjector.FlagCommittedType) continue;

            var fields = appended.Event.Payload is JsonValue.Object payload
                ? FlagProjector.Members(payload)
                : new Dictionary<string, JsonValue>(StringComparer.Ordinal);

            var (flag, reason) = type == FlagProjector.FlagRaisedType
                ? Legacy(fields, appended)
                : Committed(fields, appended, byEvent);

            if (flag is not null) flags.Add(flag);
            else skipped[reason!] = skipped.GetValueOrDefault(reason!) + 1;
        }

        return new FlagDirectory(flags.ToImmutable(), skipped.ToImmutableSortedDictionary(StringComparer.Ordinal));
    }

    private static (RaisedFlag? Flag, string? Reason) Legacy(Dictionary<string, JsonValue> fields, AppendedEvent appended) =>
        FlagProjector.Str(fields, FlagProjector.PostIdField, out var postId)
        && FlagProjector.Str(fields, FlagProjector.RaisedByField, out var raisedBy)
        && FlagProjector.Str(fields, FlagProjector.KindField, out var kindWire)
        && FlagKinds.Parse(kindWire).TryGetValue(out var kind, out _)
            ? (new RaisedFlag(appended.Event.Id.Value, postId, raisedBy, kind, appended.ServerTimestamp), null)
            : (null, SkippedUnreadableLegacy);

    private static (RaisedFlag? Flag, string? Reason) Committed(
        Dictionary<string, JsonValue> fields, AppendedEvent appended, Dictionary<string, FlagDetail> byEvent)
    {
        if (!FlagProjector.Str(fields, FlagProjector.KindField, out var kindWire)
            || !FlagKinds.Parse(kindWire).TryGetValue(out var kind, out _)
            || !FlagProjector.Str(fields, FlagProjector.CommitmentField, out var commitment))
            return (null, SkippedUnreadableCommitted);

        if (!byEvent.TryGetValue(appended.Event.Id.Value, out var detail))
            return (null, SkippedNoDetail);

        var recomputed = FlagCommitment.Of(detail.PostId, detail.RaisedBy, detail.Rationale, detail.Salt);
        if (!recomputed.TryGetValue(out var expected, out _) || !string.Equals(expected, commitment, StringComparison.Ordinal))
            return (null, SkippedCommitmentMismatch);

        return (new RaisedFlag(appended.Event.Id.Value, detail.PostId, detail.RaisedBy, kind, appended.ServerTimestamp), null);
    }
}
