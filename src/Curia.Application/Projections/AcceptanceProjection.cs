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
    /// reading as <see cref="Fold"/>, kept with its event so a replay can answer the first time.
    /// </summary>
    public static StandingAcceptance? Standing(IReadOnlyList<AppendedEvent> rootStreamInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(rootStreamInSeqOrder);

        StandingAcceptance? standing = null;

        foreach (var appended in rootStreamInSeqOrder)
        {
            if (appended.Event.Type.Value != AnswerAcceptedType) continue;
            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            string? answer = null;
            foreach (var member in payload.Members)
                if (member.Key == AnswerIdField && member.Value is JsonValue.String s) answer = s.Value;

            if (answer is not null) standing = new StandingAcceptance(answer, appended.ServerTimestamp.Value);
        }

        return standing;
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
    /// serving path already means by "no accepted answer".</para>
    /// </summary>
    public static ImmutableDictionary<string, string> Fold(IReadOnlyList<AppendedEvent> eventsInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);

        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);
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

            // Last writer wins: a later acceptance on the same thread replaces the earlier one.
            if (root is not null && answer is not null) accepted[root] = answer;
        }

        return accepted.ToImmutableDictionary(StringComparer.Ordinal);
    }
}

/// <summary>The acceptance that stands in a thread: the answer and the instant the Forum recorded naming it.</summary>
public sealed record StandingAcceptance(string AnswerId, DateTimeOffset AcceptedAt);
