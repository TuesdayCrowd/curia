using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Application.Ingest;

/// <summary>
/// R8.86 (errata G57): the guard a post's append carries. Built from the facts the route's checks ran on, so the position is those facts' own and there is no
/// argument to get wrong (finding 5): <see cref="AppendGuard.After"/> is <see cref="LogFacts.Position"/>.
///
/// <para><b>The seam.</b> The function answers <see cref="GuardVerdict.Proceed"/> for every delta and the store does not yet read it; the digest leg, the
/// revision's successor leg and the vote leg arrive with the behaviour (errata G57, Tasks 1 and 2).</para>
/// </summary>
public static class SubmissionGuard
{
    /// <summary>The guard for <paramref name="submission"/>, decided on <paramref name="facts"/>.</summary>
    public static AppendGuard For(LogFacts facts, VerifiedSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(submission);

        return new AppendGuard(facts.Position, static _ => GuardVerdict.Proceed);
    }
}
