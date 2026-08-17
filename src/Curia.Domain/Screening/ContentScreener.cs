using System.Text;
using Curia.Domain.Primitives;

namespace Curia.Domain.Screening;

/// <summary>The three things SCREEN is permitted to conclude (R6.13).</summary>
public enum ScreeningOutcome
{
    /// <summary>Nothing fired. The submission proceeds to PERSIST unchanged.</summary>
    Accepted,

    /// <summary>
    /// Something fired, all of it <see cref="RiskDisposition.Annotate"/>. The submission proceeds
    /// to PERSIST <b>unchanged</b>, with the findings beside it (R6.14).
    /// </summary>
    Annotated,

    /// <summary>
    /// A <see cref="RiskDisposition.Reject"/> category fired. Nothing is persisted. R10.26: there
    /// is no redaction primitive, so this is a gate rather than a cleanup pass.
    /// </summary>
    Rejected,
}

/// <summary>
/// The result of SCREEN. Carries annotations and an outcome -- never content, and never a modified
/// copy of anything.
/// </summary>
public sealed record ScreeningResult(ScreeningOutcome Outcome, RiskAnnotations Annotations)
{
    /// <summary>Whether PERSIST may proceed. R6.13's "accept, reject, and annotate" reduced to the gate.</summary>
    public bool MayPersist => Outcome is not ScreeningOutcome.Rejected;
}

/// <summary>
/// SCREEN, the third of §6.4's four phases.
///
/// <para><b>The invariant this phase exists to not break (R6.12).</b> "No component SHALL modify
/// the canonical envelope between signature verification and persistence. The bytes written SHALL
/// be byte-identical to the bytes over which the signature was verified." Everything below is
/// arranged so that breaking it requires adding a member, not forgetting a rule:</para>
///
/// <list type="bullet">
/// <item><see cref="Screen"/> takes <see cref="ReadOnlySpan{T}"/> of the verified bytes. A span
/// cannot be stored in a field, so the phase structurally cannot retain the content it
/// screened.</item>
/// <item>It returns a <see cref="ScreeningResult"/>, which holds only
/// <see cref="RiskAnnotations"/>, which holds only <see cref="RiskFlag"/>s, which hold no text.
/// There is no return path a byte of content could travel along.</item>
/// <item>The derived copy R6.13 permits -- the decoded string the detectors read -- is a local.
/// It is created here, read by the detectors, and unreachable when this method returns.</item>
/// </list>
///
/// <para>P23/P25 then test what the types already claim, which is the right redundancy: the
/// property suite is checking the compiler's homework, not doing it.</para>
/// </summary>
public static class ContentScreener
{
    /// <summary>
    /// Every detector version this screener runs, whether or not it fires. R10.10's re-runnability
    /// needs to know what was <i>asked</i>: "no flags" from a rule set that never included a rule
    /// is a different statement from "no flags" from one that did.
    /// </summary>
    public static IReadOnlyList<string> DetectorVersions { get; } =
        [SecretScanner.Version, InjectionDetector.Version];

    /// <summary>
    /// Screens the exact bytes VERIFY consumed, and returns findings that ride beside them.
    /// </summary>
    /// <param name="verifiedContent">
    /// The canonical bytes the signature was verified over. Passed as a span rather than an array
    /// or a string so this phase cannot keep them, and so no caller can be handed a copy it might
    /// mistake for the persistable form.
    /// </param>
    public static Result<ScreeningResult> Screen(ReadOnlySpan<byte> verifiedContent)
    {
        // R6.13's derived copy, and the only one. The bytes are already known-valid UTF-8 -- ADMIT
        // rejected invalid UTF-8, unpaired surrogates and NUL bytes before canonicalization was
        // attempted (R6.15) -- so a throwing decoder is the right one here: a failure would mean
        // an earlier phase let something through, which is a bug rather than a submission outcome.
        string derived;
        try
        {
            derived = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(verifiedContent);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                "SCREEN received bytes that are not valid UTF-8. ADMIT rejects invalid UTF-8 (R6.15), " +
                "so reaching this point means a phase upstream admitted something it should not have.",
                ex);
        }

        var flags = SecretScanner.Scan(derived)
            .Concat(InjectionDetector.Scan(derived))
            .OrderBy(f => f.Offset)
            .ThenBy(f => f.Category)
            .ToArray();

        var annotations = RiskAnnotations.Create(flags, DetectorVersions);

        var outcome = annotations.Rejecting.Any()
            ? ScreeningOutcome.Rejected
            : annotations.IsEmpty
                ? ScreeningOutcome.Accepted
                : ScreeningOutcome.Annotated;

        return Result<ScreeningResult>.Ok(new ScreeningResult(outcome, annotations));

        // `derived` goes out of scope here. There is deliberately no path by which it, or any
        // substring of it, is part of the value returned -- see the type remarks.
    }
}
