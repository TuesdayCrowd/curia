using System.Globalization;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Application.Ingest;

/// <summary>
/// RFC 9457 problem-type slugs for the pipeline's own rejections -- the ones that belong to the
/// sequence rather than to any single phase's library.
/// </summary>
public static class IngestErrors
{
    /// <summary>
    /// SCREEN found a <see cref="RiskDisposition.Reject"/> category (R10.26). The detail names the
    /// categories and their offsets and nothing else: R10.27 requires the response to identify the
    /// category and location, and R10.28 forbids echoing the value, which <see cref="RiskFlag"/>
    /// makes structurally impossible anyway.
    /// </summary>
    public static Error ScreeningRejected(RiskAnnotations annotations)
    {
        ArgumentNullException.ThrowIfNull(annotations);

        var detail = string.Join(
            ", ",
            annotations.Rejecting.Select(f => string.Create(
                CultureInfo.InvariantCulture, $"{f.Category}@{f.Offset}")));

        return new Error(
            "curia/ingest/screening-rejected",
            "The submission contains credential material and was rejected. Rotate the credential; " +
            "it cannot be redacted after signing, so nothing was stored.",
            detail);
    }

    /// <summary>The slug of <see cref="UnstorableMember"/>, which the submit route maps to 422.</summary>
    public const string UnstorableMemberType = "curia/ingest/unstorable-member";

    /// <summary>
    /// A signed member the log cannot store (R11.33). jsonb refuses U+0000, and <c>board</c> and
    /// <c>parent</c> are written into the <c>post.accepted</c> payload outside the canonical text, so a
    /// post naming one of them with U+0000 cannot be kept, whatever R8.63 says of their value space.
    /// The detail names the member and never its value (R6.40).
    /// </summary>
    public static Error UnstorableMember(string member) =>
        new(UnstorableMemberType, "The log cannot store this member's value", $"member={member}");
}
