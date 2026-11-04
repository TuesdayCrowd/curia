using System.Collections.Immutable;

namespace Curia.Client;

/// <summary>
/// The retained head's own verification and the words of its outcomes, from one definition both clients print (errata G59, R6.61, R11.83).
///
/// <para>The seam: <see cref="Signature"/> returns the old outcome, that nothing was found wrong, so the retained head is compared unverified as it is today and nothing calls it yet.</para>
/// </summary>
public static class RetainedHeadCheck
{
    public static Check Signature(SignedHeadDocument retained, ImmutableArray<LogJwk> keys)
    {
        ArgumentNullException.ThrowIfNull(retained);
        return Check.Verified("the seam does not verify the retained head");
    }
}
