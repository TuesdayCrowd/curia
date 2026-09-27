using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;

namespace Curia.Application.Credentials;

/// <summary>
/// What an identifier's enrollment bound, as the log records it (R4.31, errata G14): the <c>kid</c>
/// named by its <c>agent.enrolled</c> event, and when. There is at most one such event per
/// identifier -- <see cref="EnrollAgent"/> appends it at <c>AggregateVersion.New</c> -- so the first
/// found is the binding.
///
/// <para><b>Why the log and not the key store.</b> The store can lose rows (db/0002 counts losing
/// them as an availability cost: "agents re-enroll"), and a store written before errata G14 can hold
/// keys no enrollment bound. The log is append-only under R11.6's grant and signed into heads, so it
/// is the one record of which key an identity began with that neither failure can change. A
/// re-enrollment is honoured only for the key the log says the identity was enrolled with.</para>
/// </summary>
/// <param name="Kid">
/// The bound <c>kid</c>, or <see langword="null"/> when the enrollment event names none -- which no
/// writer in this solution has ever produced, and which <see cref="Binds"/> therefore treats as
/// binding nothing: an identity whose binding cannot be read is refused re-enrollment, not granted it.
/// </param>
/// <param name="EnrolledAt">
/// The instant the log recorded the enrollment. R4.31 dates a key it re-registers after a lost row
/// from here, so every post signed before the loss is still inside the key's window (R6.31).
/// </param>
public sealed record EnrollmentBinding(string? Kid, DateTimeOffset EnrolledAt)
{
    /// <summary>The binding the log holds for <paramref name="agentId"/>, or <see langword="null"/> when it holds no enrollment.</summary>
    public static EnrollmentBinding? Find(IReadOnlyList<AppendedEvent> history, string agentId)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        foreach (var appended in history)
        {
            if (!string.Equals(appended.Event.Type.Value, AgentStandingProjector.EnrolledType, StringComparison.Ordinal)) continue;
            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            string? agent = null, kid = null;
            foreach (var member in payload.Members)
            {
                if (member.Value is not JsonValue.String text) continue;
                if (string.Equals(member.Key, AgentStandingProjector.AgentIdField, StringComparison.Ordinal)) agent = text.Value;
                else if (string.Equals(member.Key, AgentStandingProjector.KeyIdField, StringComparison.Ordinal)) kid = text.Value;
            }

            if (string.Equals(agent, agentId, StringComparison.Ordinal)) return new EnrollmentBinding(kid, appended.ServerTimestamp.Value);
        }

        return null;
    }

    /// <summary>Whether this binding names <paramref name="kid"/>. A binding that names no kid names none.</summary>
    public bool Binds(string kid) => Kid is not null && string.Equals(Kid, kid, StringComparison.Ordinal);
}
