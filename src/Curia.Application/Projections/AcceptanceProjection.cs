using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Projections;

/// <summary>
/// Table 10's <c>answer</c>/<c>accept</c>, folded out of the log: which answer stands as each
/// thread's accepted one.
///
/// <para><b>Latest wins, and there is no un-accept.</b> An asker who changes their mind appends a
/// second acceptance rather than retracting the first — the same argument
/// <c>ModerationPolicy.MayServe</c> makes about servability, and the same one
/// <c>CredentialLifecycle.Project</c> makes about current state. The history is the state, so
/// nothing has to be invalidated and a replay reproduces the outcome exactly.</para>
///
/// <para><b>No clock</b>, for the reason every other projector here records: a rebuild that
/// consulted "now" would make R11.9's replay drill tautological.</para>
/// </summary>
public static class AcceptanceProjector
{
    /// <summary>The event the accept route appends.</summary>
    public const string AnswerAcceptedType = "answer.accepted";

    /// <summary>The thread whose accepted answer this names — the root question's id.</summary>
    public const string ThreadRootField = "thread_root";

    /// <summary>The answer that now stands as the thread's accepted one.</summary>
    public const string AnswerIdField = "answer_id";

    /// <summary>
    /// Who accepted it. Table 10's "(own thread)" means this is the thread's asker, and it is
    /// recorded so the log is self-describing about who exercised the grant rather than leaving that
    /// to be inferred from the aggregate the event landed in.
    /// </summary>
    public const string AcceptedByField = "accepted_by";

    /// <summary>
    /// The acceptance that stands in one thread root's own stream (R8.68): the answer the fold ends on and the
    /// <c>server_ts</c> of the event that named it, or null when nothing has been accepted. It is the same last-wins
    /// reading as <see cref="Fold"/>, <see cref="LatestWins"/>, over one stream and not a keyed list (errata G76, R9.71: one rule), kept with its event
    /// so a replay can answer the first time.
    /// </summary>
    public static StandingAcceptance? Standing(IReadOnlyList<AppendedEvent> rootStreamInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(rootStreamInSeqOrder);

        // The stream is one root's, so every acceptance in it is the same thread whatever its thread_root member says: the key is a constant.
        var latest = LatestWins(rootStreamInSeqOrder, static a => a.AnswerId is null ? null : string.Empty);
        return latest.TryGetValue(string.Empty, out var standing) ? standing : null;
    }

    /// <summary>
    /// R7.26 (errata G46): the stored instants of the acceptances <paramref name="agentId"/> recorded, from the <c>answer.accepted</c> events'
    /// <c>accepted_by</c> and their <c>server_ts</c>. Pure: no clock. An event of another type, one with no string <c>accepted_by</c> and one whose payload
    /// is not an object are counted for nobody. A replay appends no event (R8.68), so it has no instant. The count is
    /// <see cref="Curia.Application.Budget.BudgetReadout.CountIn"/>'s, the one function that counts a budget.
    /// </summary>
    public static IEnumerable<DateTimeOffset> InstantsBy(IReadOnlyList<AppendedEvent> events, AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(agentId);

        var instants = new List<DateTimeOffset>();

        foreach (var appended in events)
        {
            if (appended.Event.Type.Value != AnswerAcceptedType) continue;
            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            foreach (var member in payload.Members)
                if (member.Key == AcceptedByField && member.Value is JsonValue.String by && string.Equals(by.Value, agentId.Value, StringComparison.Ordinal))
                {
                    instants.Add(appended.ServerTimestamp.Value);
                    break;
                }
        }

        return instants;
    }

    /// <summary>
    /// Folds a seq-ordered event list into the accepted answer per thread root.
    ///
    /// <para>A thread nobody has resolved is absent rather than present-and-null, which is what the
    /// serving path already means by "no accepted answer". It is <see cref="FoldWithInstants"/> with the instants dropped, so the
    /// answer a thread's acceptance names and the instant it was recorded at are one reading (errata G76, R9.71).</para>
    /// </summary>
    public static ImmutableDictionary<string, string> Fold(IReadOnlyList<AppendedEvent> eventsInSeqOrder) =>
        FoldWithInstants(eventsInSeqOrder).ToImmutableDictionary(kv => kv.Key, kv => kv.Value.AnswerId, StringComparer.Ordinal);

    /// <summary>
    /// Errata G76 (R9.71): the standing acceptance per thread root with the instant and the log position of the event that made it stand. Latest wins, the position
    /// being the event's <c>seq</c>, the one total order the store offers; the instant is carried to be printed and is never compared (register D94). A thread whose acceptance
    /// event names no root or no answer is absent, as <see cref="Fold"/> always has.
    /// </summary>
    public static ImmutableDictionary<string, StandingAcceptance> FoldWithInstants(IReadOnlyList<AppendedEvent> eventsInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);

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
        }

        return LatestWins(eventsInSeqOrder, static a => a.Root is not null && a.AnswerId is not null ? a.Root : null)
            .ToImmutableDictionary(StringComparer.Ordinal);
    }

    /// <summary>The one rule: of the acceptance events in order, the last one a thread's key names stands. <paramref name="keyOf"/> returns null for an event that names no thread.</summary>
    private static Dictionary<string, StandingAcceptance> LatestWins(IReadOnlyList<AppendedEvent> events, Func<ParsedAcceptance, string?> keyOf)
    {
        var standing = new Dictionary<string, StandingAcceptance>(StringComparer.Ordinal);

        foreach (var appended in events)
        {
            if (appended.Event.Type.Value != AnswerAcceptedType) continue;
            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            string? root = null;
            string? answer = null;

            foreach (var member in payload.Members)
            {
                if (member.Value is not JsonValue.String s) continue;
                if (member.Key == ThreadRootField) root = s.Value;
                else if (member.Key == AnswerIdField) answer = s.Value;
            }

            var parsed = new ParsedAcceptance(root, answer);
            if (keyOf(parsed) is { } key && answer is not null)
                standing[key] = new StandingAcceptance(answer, appended.ServerTimestamp.Value, appended.Seq);
        }

        return standing;
    }

    private readonly record struct ParsedAcceptance(string? Root, string? AnswerId);
}

/// <summary>The acceptance that stands in a thread: the answer and the instant the Forum recorded naming it.</summary>
public sealed record StandingAcceptance(string AnswerId, DateTimeOffset AcceptedAt, EventSequence Seq = default);
