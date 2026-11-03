using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Moderation;

/// <summary>
/// R10.94 (errata G57): the guard a flag's append carries. The flag's raiser and post are private (R10.62), so the delta cannot decide the repeat; it can say
/// that a flag of this kind landed since the read, and then the use case decides again. <see cref="AppendGuard.After"/> is the last event of the list the use
/// case read.
///
/// <para><b>The seam.</b> The function answers <see cref="GuardVerdict.Proceed"/> for every delta and <c>RaiseFlag</c> does not yet pass it.</para>
/// </summary>
public static class FlagGuard
{
    /// <summary>The guard for a flag of <paramref name="kind"/>, decided on <paramref name="read"/>.</summary>
    public static AppendGuard For(IReadOnlyList<AppendedEvent> read, string kind)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(kind);

        return new AppendGuard(read.Count == 0 ? EventSequence.Zero : read[^1].Seq, static _ => GuardVerdict.Proceed);
    }
}
