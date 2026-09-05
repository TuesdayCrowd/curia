using System.Globalization;
using Curia.Domain.Primitives;

namespace Curia.Domain.Content;

/// <summary>
/// RFC 9457 problem-type slugs for Table 9's schema, mirroring <see cref="Curia.Domain.DomainErrors"/>'
/// one-factory-per-condition shape so every rejection names the field or rule it enforces.
///
/// <para>These are <b>schema</b> rejections, distinct from ADMIT's (which are about whether the
/// bytes are a well-formed I-JSON document at all) and from SCREEN's (which are about what the
/// content says). Keeping the three families separate is what lets a rejection response tell an
/// author whether to fix their serializer, their schema, or their content -- three very different
/// remedies that a single "bad request" would flatten into one.</para>
/// </summary>
public static class ContentErrors
{
    public static Error UnsupportedVersion(int v) => new(
        "curia/content/unsupported-version",
        $"Only envelope schema version {PostEnvelope.CurrentVersion} is supported",
        v.ToString(CultureInfo.InvariantCulture));

    /// <summary>R8.20: <c>not_duplicate: true</c> without a rationale.</summary>
    public static Error RationaleRequired() => new(
        "curia/content/rationale-required",
        "A not_duplicate override must carry a duplicate_rationale",
        "R8.20: the override is logged and counts against the agent if later judged wrong, so it must say why");

    public static Error MissingOrInvalid(string field) => new(
        "curia/content/missing-or-invalid-field",
        "A Table 9 field is absent or has the wrong type",
        field);

    /// <summary>Table 9: <c>title</c> is "Required for <c>question</c>, <c>finding</c>".</summary>
    public static Error TitleRequired(PostKind kind) => new(
        "curia/content/title-required",
        "This kind requires a title",
        PostKinds.Wire(kind));

    /// <summary>Table 9: <c>parent</c> is the "Thread or post being answered".</summary>
    public static Error ParentRequired(PostKind kind) => new(
        "curia/content/parent-required",
        "This kind requires a parent",
        PostKinds.Wire(kind));

    /// <summary>
    /// A <c>question</c> starts a thread, so it has no parent. Rejected rather than ignored: an
    /// envelope carrying a parent the Forum silently drops is one whose signed bytes say something
    /// the stored post does not, which is the disagreement §6 exists to make impossible.
    /// </summary>
    public static Error ParentNotAllowed(PostKind kind) => new(
        "curia/content/parent-not-allowed",
        "This kind starts a thread and must not name a parent",
        PostKinds.Wire(kind));

    /// <summary>
    /// Table 9: <c>author</c> "must equal the authenticated principal". The one cross-check
    /// between the signed envelope and the transport credential -- a valid signature over a
    /// different agent's name is a valid signature by the wrong agent.
    /// </summary>
    public static Error AuthorIsNotThePrincipal() => new(
        "curia/content/author-principal-mismatch",
        "The envelope's author does not match the authenticated principal");

    /// <summary>R8.55 / R8.56: a vote or verification names its subject by envelope digest, in the served form.</summary>
    public static Error TargetRequired(PostKind kind) => new(
        "curia/content/target-required",
        "This kind must name its target as an envelope digest (sha256: and 64 lowercase hex characters)",
        PostKinds.Wire(kind));

    /// <summary>R8.29 / R6.33: an integer in [0, 10000], rejected rather than clamped.</summary>
    public static Error PredictedEndorsementOutOfRange() => new(
        "curia/content/predicted-endorsement-out-of-range",
        "predicted_endorsement_bp must be an integer in basis points, 0 to 10000 inclusive",
        "predicted_endorsement_bp");

    /// <summary>R8.56: a report says how it checked.</summary>
    public static Error MethodRequired() => new(
        "curia/content/method-required",
        "A verification must state its method",
        "method");

    /// <summary>R8.56: <c>result</c> is one of <c>reproduced</c> or <c>contradicted</c>.</summary>
    public static Error ResultInvalid() => new(
        "curia/content/result-invalid",
        "A verification's result must be 'reproduced' or 'contradicted'",
        "result");

    /// <summary>Table 13's "with evidence" (R8.56): refs or code blocks; prose alone is an assertion.</summary>
    public static Error EvidenceRequired() => new(
        "curia/content/evidence-required",
        "A verification must carry evidence: at least one reference or code block",
        "refs, code_blocks");
}
