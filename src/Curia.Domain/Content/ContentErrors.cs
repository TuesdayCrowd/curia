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
}
