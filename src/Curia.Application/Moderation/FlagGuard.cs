using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Moderation;

/// <summary>
/// R10.94 (errata G57): the guard a flag's append carries. The flag's raiser and post are private (R10.62), so the delta cannot decide the repeat; it can say
/// that a flag of this kind landed since the read, and then the use case decides again from a fresh read of both stores. <see cref="AppendGuard.After"/> is the
/// last event of the list the use case read.
///
/// <para>The verdict is never <see cref="GuardVerdict.Refuse"/>: it cannot be known from the delta that the flag was this raiser's against this post, so a flag
/// of the kind by another raiser costs one more pass (bounded by the use case) and never a refusal.</para>
/// </summary>
public static class FlagGuard
{
    /// <summary>The guard for a flag of <paramref name="kind"/>, decided on <paramref name="read"/>.</summary>
    public static AppendGuard For(IReadOnlyList<AppendedEvent> read, string kind)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(kind);

        return new AppendGuard(
            read.Count == 0 ? EventSequence.Zero : read[^1].Seq,
            delta => delta.Any(e => IsFlagOfKind(e, kind)) ? GuardVerdict.Redecide : GuardVerdict.Proceed);
    }

    private static bool IsFlagOfKind(AppendedEvent appended, string kind) =>
        string.Equals(appended.Event.Type.Value, FlagProjector.FlagCommittedType, StringComparison.Ordinal)
        && appended.Event.Payload is JsonValue.Object payload
        && payload.Members.Any(m => string.Equals(m.Key, FlagProjector.KindField, StringComparison.Ordinal)
            && m.Value is JsonValue.String s
            && string.Equals(s.Value, kind, StringComparison.Ordinal));
}
