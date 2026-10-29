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

    /// <summary>The slug of <see cref="Contended"/>, which the submit route maps to 503.</summary>
    public const string ContendedType = "curia/ingest/contended";

    /// <summary>
    /// R6.56: the author's stream moved under every attempt to decide and append (see
    /// <see cref="IngestAttempts"/>). Nothing was stored; the submission may be sent again.
    /// </summary>
    public static Error Contended() =>
        new(ContendedType, "The author's stream changed under every attempt; nothing was stored");

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

    /// <summary>The slug of <see cref="AlreadyRevised"/>, which the submit route maps to 409 (R8.77).</summary>
    public const string AlreadyRevisedType = "curia/ingest/already-revised";

    /// <summary>The slug of <see cref="RevisionInFlight"/>, which the submit route maps to 409 (R8.77).</summary>
    public const string RevisionInFlightType = "curia/ingest/revision-in-flight";

    /// <summary>The slug of <see cref="RevisionGateUnavailable"/>, which the submit route maps to 503 (R8.77).</summary>
    public const string RevisionGateUnavailableType = "curia/ingest/revision-gate-unavailable";

    /// <summary>The slug of <see cref="AlreadyRecorded"/>, which the submit route maps to 409 (R8.78).</summary>
    public const string AlreadyRecordedType = "curia/ingest/already-recorded";

    /// <summary>R8.77: the Forum already serves a revision of this post. The detail is <c>successor=&lt;post id&gt;</c> and nothing an author wrote.</summary>
    public static Error AlreadyRevised(string successorPostId) =>
        new(AlreadyRevisedType, "This post already has a revision", $"successor={successorPostId}");

    /// <summary>R8.77: another revision of this post is being recorded. Retryable; names no post and no digest.</summary>
    public static Error RevisionInFlight() =>
        new(RevisionInFlightType, "Another revision of this post is being recorded", "retry once the other revision is recorded (R8.77)");

    /// <summary>R8.77: the revision gate's store could not be reached. Nothing was stored; the detail is not served.</summary>
    public static Error RevisionGateUnavailable() =>
        new(RevisionGateUnavailableType, "The revision gate could not be reached; nothing was stored");

    /// <summary>R8.78: the Forum already stores a post of this digest. The detail is <c>post=&lt;post id&gt;</c> and nothing an author wrote.</summary>
    public static Error AlreadyRecorded(string postId) =>
        new(AlreadyRecordedType, "This post is already recorded", $"post={postId}");
}
