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
}
