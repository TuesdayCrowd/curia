using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// Which Reader Contract a reading names (R10.22's clause 9): the one the Forum served, or, when it
/// served none that is an absolute URI, the well-known one at the configured Forum.
///
/// <para>Not on <see cref="ReaderContract"/>, which is the Domain's and cannot see
/// <see cref="HeadStore"/>; and not a second <c>ReaderContract</c> type in this namespace, which would
/// shadow the Domain's for every file here that reads its well-known path.</para>
/// </summary>
public static class ReaderContractLocation
{
    /// <summary>
    /// <paramref name="served"/> when it is an absolute URI, and otherwise the well-known URI at the
    /// configured Forum's origin, so a password in <c>--forum</c> or <c>CURIA_FORUM</c> never reaches
    /// the line (R10.63). A well-known URI is rooted at the origin (RFC 8615) and
    /// <see cref="ReaderContract.WellKnownPath"/> begins with '/', so for every Forum URL without
    /// userinfo this is the URI the fallback named before the strangers stage's final gate, third round.
    /// </summary>
    public static Uri For(Uri forum, string? served)
    {
        ArgumentNullException.ThrowIfNull(forum);

        return Uri.TryCreate(served, UriKind.Absolute, out var absolute)
            ? absolute
            : new Uri(new Uri(HeadStore.Origin(forum)), ReaderContract.WellKnownPath);
    }
}
