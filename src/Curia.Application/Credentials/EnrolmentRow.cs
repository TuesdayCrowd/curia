using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;

namespace Curia.Application.Credentials;

/// <summary>
/// The one decision of an <c>agent.enrolled</c> row (errata G80, R4.48, R6.64 (rev.)): the lifecycle fold and the standing's feed
/// (<see cref="VerifiedLog"/>) both ask it, so no second fold decides a root a first has skipped.
/// </summary>
internal static class EnrolmentRow
{
    /// <summary>
    /// The slug of the predicate that skips the row, or null when it is its stream's honoured root. <paramref name="agent"/> is the stream's agent (the row's aggregate) and <paramref name="isFirst"/>
    /// whether the row is the stream's first <c>agent.enrolled</c>, whether or not that first was honoured. A payload that is not an object, or whose <c>agent_id</c> is not the stream's agent, is
    /// <c>statement-mismatch</c> and is decided first; otherwise a row that is not the stream's first is <c>broken-link</c>.
    /// </summary>
    internal static string? Predicate(AppendedEvent row, string agent, bool isFirst)
    {
        if (row.Event.Payload is not JsonValue.Object o
            || o.Members.FirstOrDefault(m => string.Equals(m.Key, AgentStandingProjector.AgentIdField, StringComparison.Ordinal)).Value is not JsonValue.String id
            || !string.Equals(id.Value, agent, StringComparison.Ordinal))
            return LifecyclePredicates.StatementMismatch;

        return isFirst ? null : LifecyclePredicates.BrokenLink;
    }
}
