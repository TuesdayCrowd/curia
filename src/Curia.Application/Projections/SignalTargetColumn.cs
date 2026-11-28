using Curia.Application.Credentials;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>
/// R11.6 (add.), db/0007: the signal target (R8.55) of a vote or verification, read from its signed envelope by the one reader the verification fold uses (<see cref="PostEnvelope.Read"/>), for the
/// append to write in <c>events.signal_target</c>. Total: a row that is not a vote or verification, or whose canonical does not read, has no target, and nothing here throws.
/// </summary>
public static class SignalTargetColumn
{
    /// <summary>The target the append writes for <paramref name="domainEvent"/>, or <see langword="null"/> for a row that has none.</summary>
    public static string? Of(DomainEvent domainEvent)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);

        if (!string.Equals(domainEvent.Type.Value, PostProjector.PostAcceptedType, StringComparison.Ordinal)) return null;
        if (domainEvent.Payload is not JsonValue.Object payload) return null;
        if (Text(payload, "kind") is not { } wire || !PostKinds.TryParse(wire, out var kind) || !PostKinds.RequiresTarget(kind)) return null;
        if (Text(payload, "canonical") is not { } canonical) return null;

        var parsed = JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(canonical));
        if (!parsed.TryGetValue(out var root, out _) || root is not JsonValue.Object envelope) return null;

        return PostEnvelope.Read(envelope).TryGetValue(out var read, out _) ? read!.Target : null;
    }

    private static string? Text(JsonValue.Object o, string name) =>
        o.Members.FirstOrDefault(m => m.Key == name).Value is JsonValue.String s ? s.Value : null;
}

/// <summary>R11.6 (add.): what <see cref="SignalTargetAudit.Check"/> found; both lists are log indexes, in log order.</summary>
/// <param name="Mismatched">Rows whose column is present and is not the target the canonical envelope states (or is present on a row that has none).</param>
/// <param name="Missing">Rows whose canonical states a target and whose column is NULL: appended before db/0007 and not yet backfilled. A vote that does not count toward a finding's verification until it is filled.</param>
public sealed record SignalTargetFindings(IReadOnlyList<long> Mismatched, IReadOnlyList<long> Missing);

/// <summary>
/// R11.6 (add.): the control for the column the signal clause reads. Every vote and verification is read again from its own canonical and compared with the column; a difference is reported
/// and never healed (the Forum holds no UPDATE). A NULL column is a vote the indexed read does not find, a deny nobody decided, so it is a finding and not a note.
/// </summary>
public static class SignalTargetAudit
{
    public static SignalTargetFindings Check(IReadOnlyList<AppendedEvent> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var mismatched = new List<long>();
        var missing = new List<long>();
        for (var i = 0; i < log.Count; i++)
        {
            var expected = SignalTargetColumn.Of(log[i].Event);
            var stored = log[i].StoredSignalTarget;
            if (string.Equals(expected, stored, StringComparison.Ordinal)) continue;

            if (stored is null) missing.Add(i);
            else mismatched.Add(i);
        }

        return new SignalTargetFindings(mismatched, missing);
    }
}
