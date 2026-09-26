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
    /// SCREEN over a canonical envelope — what ingest and the client's pre-send check hold.
    ///
    /// <para><b>Screens what the author wrote, not how JCS encoded it</b> (register D19). In
    /// canonical text a line break is the two characters <c>\n</c> and a quote is <c>\"</c>, so a
    /// rule anchored on a word boundary read the escape's letter instead of the separator the author
    /// typed: an AWS key, a JWT or an assigned secret on any line after the first was admitted.
    /// Every string token — member names included — is decoded by <see cref="CanonicalStrings"/>
    /// and screened on its own, which also stops a pattern running from one member into the next.</para>
    ///
    /// <para><b>Offsets keep their unit:</b> UTF-16 offsets into the canonical text. They are
    /// persisted in each post event's <c>risk_flags</c> and reported in rejections, and an event
    /// written before this change must mean the same thing by an offset as one written after.</para>
    /// </summary>
    /// <param name="canonicalEnvelope">
    /// The canonical bytes VERIFY consumed. A span, so this phase cannot keep them.
    /// </param>
    public static Result<ScreeningResult> ScreenEnvelope(ReadOnlySpan<byte> canonicalEnvelope)
    {
        var canonical = Decode(canonicalEnvelope);
        var found = new List<RiskFlag>();

        foreach (var token in CanonicalStrings.Of(canonical))
        {
            foreach (var flag in Detect(token.Text))
            {
                var (offset, length) = token.ToCanonical(flag.Offset, flag.Length);
                found.Add(flag with { Offset = offset, Length = length });
            }
        }

        return Result<ScreeningResult>.Ok(Conclude(found));
    }

    /// <summary>
    /// SCREEN over bare text — a flag's rationale, which is never wrapped in an envelope. An
    /// envelope passed here would be screened as JSON escapes, which is register D19.
    /// </summary>
    public static Result<ScreeningResult> ScreenText(ReadOnlySpan<byte> utf8Text) =>
        Result<ScreeningResult>.Ok(Conclude(Detect(Decode(utf8Text))));

    private static string Decode(ReadOnlySpan<byte> bytes)
    {
        // R6.13's derived copy, and the only one. The bytes are already known-valid UTF-8 -- ADMIT
        // rejected invalid UTF-8, unpaired surrogates and NUL bytes before canonicalization was
        // attempted (R6.15) -- so a throwing decoder is the right one here: a failure would mean
        // an earlier phase let something through, which is a bug rather than a submission outcome.
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException(
                "SCREEN received bytes that are not valid UTF-8. ADMIT rejects invalid UTF-8 (R6.15), " +
                "so reaching this point means a phase upstream admitted something it should not have.",
                ex);
        }
    }

    /// <summary>Every finding over one piece of text, with offsets into that text.</summary>
    private static List<RiskFlag> Detect(string text)
    {
        // R6.13's derived copy, read several ways. The detectors were trivially evadable against the
        // literal text alone -- character spacing, Markdown emphasis, homoglyphs and base64 wrapping
        // all defeat a pattern that matches words. Each view maps its offsets back to the text so a
        // rejection still reports a location the author can act on (R10.27).
        var found = new List<RiskFlag>();

        foreach (var view in DerivedViews.Of(text))
        {
            // The unseparated view strips punctuation and whitespace entirely, which recovers a
            // credential split across words. It is deliberately *not* fed to the injection detector:
            // running prose with every space removed is one long token, and phrase patterns over it
            // would match across sentence boundaries that were never adjacent.
            var scoped = view.Name is "unseparated"
                ? SecretScanner.Scan(view.Text, relaxWordBoundaries: true)
                : SecretScanner.Scan(view.Text).Concat(InjectionDetector.Scan(view.Text));

            foreach (var flag in scoped)
            {
                var (offset, length) = view.ToOriginal(flag.Offset, flag.Length);
                found.Add(flag with { Offset = offset, Length = length });
            }
        }

        return found;
    }

    private static ScreeningResult Conclude(IEnumerable<RiskFlag> found)
    {
        // One finding per (category, position). The same attack surfaces in several views by design
        // -- a homoglyph override also appears in the unconfused view and possibly the despaced one --
        // and reporting it three times would inflate every count R10.24 publishes.
        var flags = found
            .GroupBy(f => (f.Category, f.Offset))
            .Select(g => g.First())
            .OrderBy(f => f.Offset)
            .ThenBy(f => f.Category)
            .ToArray();

        var annotations = RiskAnnotations.Create(flags, DetectorVersions);

        var outcome = annotations.Rejecting.Any()
            ? ScreeningOutcome.Rejected
            : annotations.IsEmpty
                ? ScreeningOutcome.Accepted
                : ScreeningOutcome.Annotated;

        return new ScreeningResult(outcome, annotations);
    }
}
