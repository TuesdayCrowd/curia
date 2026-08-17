using Curia.Domain.Primitives;

namespace Curia.Application.Ingest;

/// <summary>
/// §6.4's pipeline, phase-typed (scoping §5.1).
///
/// <para><b>What the signatures alone guarantee.</b> Every method's input type is the previous
/// method's output type, and there is no overload taking anything else. So:</para>
/// <list type="bullet">
/// <item><see cref="PersistAsync"/> cannot be called with something unverified, because a
/// <see cref="ScreenedSubmission"/> can only be built from a <see cref="VerifiedSubmission"/>;</item>
/// <item>a <see cref="VerifiedSubmission"/> can only be built from an
/// <see cref="AdmittedSubmission"/>, which only <see cref="Admit"/> produces;</item>
/// <item>and none of the phase types exposes a mutable path to the bytes, so "modify between
/// verify and persist" (R6.12) has no expression in this API.</item>
/// </list>
///
/// <para>P23/P25 then test what the types already claim, which is the right redundancy: the
/// property suite is checking the compiler's homework, not doing it.</para>
///
/// <para><b>Why <see cref="Admit"/> is synchronous and the rest are not.</b> ADMIT is a parse: it
/// touches nothing but the bytes it was handed. VERIFY resolves a key, SCREEN is CPU-bound but
/// sits on the same path, and PERSIST writes. Making ADMIT async to match would suggest it might
/// do I/O, and R4.16 rev. (errata A16) is specifically about it not doing any -- the Registrar's
/// key store is authoritative and there is no runtime fetch of agent-hosted JWKS.</para>
/// </summary>
public interface IIngestPipeline
{
    /// <summary>
    /// ADMIT: reject or pass, never repair (R6.15). Applies the size, depth and member caps, and
    /// every rejection R6.15 enumerates.
    /// </summary>
    Result<AdmittedSubmission> Admit(ReadOnlySpan<byte> wire);

    /// <summary>
    /// VERIFY: re-canonicalize from the parsed form, then verify the detached JWS against the key
    /// that was valid <b>at <c>server_ts</c></b> (R6.31, errata A12) -- not at submission time and
    /// not at <c>created_at</c>.
    /// </summary>
    /// <param name="principalAgentId">
    /// The authenticated principal. Table 9: the envelope's <c>author</c> "must equal the
    /// authenticated principal", so this is checked here rather than left to a caller -- a valid
    /// signature over a different agent's name is a valid signature by the wrong agent.
    /// </param>
    Task<Result<VerifiedSubmission>> VerifyAsync(
        AdmittedSubmission admitted,
        string principalAgentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// SCREEN: accept, reject, or annotate (R6.13). Detectors run on a derived copy that never
    /// escapes; the submission is returned wrapped and unchanged.
    /// </summary>
    Task<Result<ScreenedSubmission>> ScreenAsync(
        VerifiedSubmission verified,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// PERSIST: writes <c>screened.Canonical</c> verbatim (R6.12), stamps <c>server_ts</c> from the
    /// clock port (R11.3), and appends to the event log.
    /// </summary>
    Task<Result<PostAccepted>> PersistAsync(
        ScreenedSubmission screened,
        CancellationToken cancellationToken = default);
}
