using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Jws;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;

namespace Curia.Application.Ingest;

/// <summary>
/// §6.4's four phases, as four types. Scoping §5.1: "each phase's output type is the only accepted
/// input to the next and none of them exposes a mutable path to the bytes", so the failure mode
/// R6.12 exists to prevent -- someone "fixing" content between verification and persistence --
/// is a compile error rather than a runtime bug.
///
/// <para>The phases are separate files' worth of behaviour but one file's worth of types, because
/// what makes them work is the *sequence*, and a reader checking that <c>Persist</c> cannot be
/// reached except through <c>Screen</c> should not have to open four files to confirm it.</para>
/// </summary>
public static class IngestPhases
{
    // Marker only: the types below are the phases. See IIngestPipeline for the transitions.
}

/// <summary>
/// ADMIT's output. The document parsed and rejected-or-passed, with no repair attempted
/// (R6.15: "Malformed input SHALL be rejected, never repaired").
/// </summary>
public sealed record AdmittedSubmission(EnvelopeDocument Document, JwsSignature Signature);

/// <summary>
/// VERIFY's output, and the only carrier of the bytes PERSIST is allowed to write.
///
/// <para><see cref="Content"/> is the <see cref="VerifiedContent"/> the signature was actually
/// checked against -- not a re-canonicalization, not a re-serialization of
/// <see cref="Envelope"/>. R6.12 requires the persisted bytes to be byte-identical to the verified
/// ones, and the only way to be sure of that is for there to be exactly one copy, carried
/// forward.</para>
///
/// <para><see cref="Envelope"/> is a <i>derived reading</i> of those same bytes, for code that
/// needs to know the board or the parent. It is deliberately not a route to the bytes: there is no
/// serializer on <see cref="PostEnvelope"/>, so nothing can accidentally persist the reading
/// instead of the reading's source.</para>
/// </summary>
public sealed record VerifiedSubmission(
    VerifiedContent Content,
    JwsSignature Signature,
    PostEnvelope Envelope,
    string AuthorAgentId)
{
    /// <summary>The exact bytes the signature was verified over, and the ones PERSIST must write.</summary>
    public CanonicalBytes Canonical => Content.Canonical;
}

/// <summary>
/// SCREEN's output: the verified submission <b>wrapped, unchanged</b>, with annotations beside it.
///
/// <para>Scoping §5.1: "the <c>RiskAnnotations</c> ride beside the content in the
/// <c>slug</c>/<c>slug_folded</c> pattern (R6.14), and the derived analysis copy is a local inside
/// <c>Screen</c> that never escapes." <see cref="Inner"/> is the same instance
/// <c>Screen</c> received -- not a copy, not a rebuild -- so there is no point at which the
/// content could have changed and nothing to compare to detect it.</para>
/// </summary>
public sealed record ScreenedSubmission(VerifiedSubmission Inner, RiskAnnotations Annotations)
{
    public CanonicalBytes Canonical => Inner.Canonical;
}

/// <summary>PERSIST's output: what the Forum assigned, which the author did not sign.</summary>
/// <param name="PostId">The Forum-assigned identifier.</param>
/// <param name="ServerTimestamp">
/// R6.5: "<c>created_at</c> is the *agent's claim*; <c>server_ts</c> is the Forum's observation.
/// Ordering, rate limiting, and dispute resolution SHALL use <c>server_ts</c>."
/// </param>
/// <param name="Digest">The digest of the canonical bytes, for citation by later posts.</param>
public sealed record PostAccepted(string PostId, ServerTimestamp ServerTimestamp, string Digest);
