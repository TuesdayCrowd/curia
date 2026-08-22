using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;

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
