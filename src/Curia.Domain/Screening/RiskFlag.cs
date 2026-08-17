using System.Collections.Frozen;
using System.Globalization;

namespace Curia.Domain.Screening;

/// <summary>
/// What a detector found, in a shape that cannot carry the thing it found.
///
/// <para><b>There is no field here for the matched text, and that is the whole design.</b> Three
/// requirements land on the same shape:</para>
/// <list type="bullet">
/// <item>R6.13 -- analysis runs on a derived copy "discarded after the analysis completes". A
/// finding that could hold a fragment of that copy would be exactly the escape hatch, so it
/// cannot.</item>
/// <item>R10.27 -- a rejection "SHALL identify the *category* detected and its location, and SHALL
/// NOT echo the detected value". Category and location are what this carries; the value is not
/// available to echo.</item>
/// <item>R10.28 -- "Detected credentials SHALL NOT be written to logs, error trackers, or metrics.
/// A scanner that logs what it finds is a credential aggregator." A logger that serializes an
/// entire <see cref="RiskAnnotations"/> still logs no secret, because there is none in it.</item>
/// </list>
///
/// <para>Making it structural rather than a rule matters here more than usual: the failure is
/// silent, it is discovered by someone else reading the logs, and by then the credential has been
/// aggregated into whatever ships logs off the box.</para>
/// </summary>
/// <param name="Category">Which pattern fired.</param>
/// <param name="Offset">
/// Where, as a UTF-16 code-unit offset into the derived text copy. R10.27's "location". An offset
/// is a coordinate, not content: it tells an author where to look in something they already have.
/// </param>
/// <param name="Length">How long the match was. Again a measurement, not the measured text.</param>
/// <param name="DetectorVersion">
/// R10.10 and R10.30: rules are "versioned and re-runnable over the archive, so that a pattern
/// discovered in November can be applied to content posted in March". A hit is only attributable
/// to a rule set if the finding says which one produced it.
/// </param>
public sealed record RiskFlag(RiskCategory Category, int Offset, int Length, string DetectorVersion)
{
    /// <summary>
    /// Deliberately overridden so that a naive interpolation of a flag into a log line stays safe.
    /// The base record <c>ToString</c> would already be safe today -- there is no content field --
    /// but this makes the guarantee independent of the record's member list, which is the sort of
    /// thing that acquires a "just for debugging" field later.
    /// </summary>
    public override string ToString() => string.Create(
        CultureInfo.InvariantCulture,
        $"{Category} at {Offset}+{Length} ({DetectorVersion})");
}

/// <summary>
/// The patterns §10 names. Split by <see cref="RiskDisposition"/> rather than by detector, because
/// what matters at the call site is what the Forum must *do* about a hit.
/// </summary>
public enum RiskCategory
{
    // ---- R10.25's credential material: hard rejection (R10.26) ----------------------------

    /// <summary>"API keys with recognizable prefixes".</summary>
    ApiKey,

    /// <summary>"private key PEM blocks".</summary>
    PrivateKeyBlock,

    /// <summary>"JWTs".</summary>
    JsonWebToken,

    /// <summary>"connection strings with embedded passwords".</summary>
    ConnectionStringPassword,

    /// <summary>"cloud provider credentials".</summary>
    CloudCredential,

    // ---- R10.8's injection patterns: flag and score (R10.9) --------------------------------

    /// <summary>"second-person imperatives directed at an assistant".</summary>
    SecondPersonImperative,

    /// <summary>"role-assumption language".</summary>
    RoleAssumption,

    /// <summary>"instruction-override phrasing".</summary>
    InstructionOverride,

    /// <summary>"hidden text (zero-width characters, homoglyph substitution, HTML comments, unusual Unicode direction marks)".</summary>
    HiddenText,

    /// <summary>"encoded blocks with no declared purpose".</summary>
    EncodedBlock,

    /// <summary>"URLs with credential-shaped query parameters".</summary>
    CredentialShapedUrl,

    // ---- R10.29's PII: flag for review, never hard rejection -------------------------------

    /// <summary>
    /// R10.29: PII "SHOULD" be scanned for and "SHALL flag for review rather than hard-reject,
    /// since false positives are common and the consequence is lower".
    /// </summary>
    PersonalData,
}

/// <summary>What a category obliges the Forum to do. The single place §10's two regimes are distinguished.</summary>
public enum RiskDisposition
{
    /// <summary>
    /// R10.9: "flag and score, not silently reject". The submission is persisted with the flag
    /// beside it.
    /// </summary>
    Annotate,

    /// <summary>
    /// R10.26: "Detected credentials SHALL cause **hard rejection** of the submission." Not a
    /// score, not a threshold -- there is no redaction primitive in this system, so a secret
    /// admitted here can be withheld from serving but never removed from what was signed.
    /// </summary>
    Reject,
}

/// <summary>The category-to-disposition table. §10's two regimes, side by side and reviewable.</summary>
public static class RiskCategories
{
    private static readonly FrozenDictionary<RiskCategory, RiskDisposition> Dispositions =
        new Dictionary<RiskCategory, RiskDisposition>
        {
            // R10.25 / R10.26 -- credential material, hard rejection.
            [RiskCategory.ApiKey] = RiskDisposition.Reject,
            [RiskCategory.PrivateKeyBlock] = RiskDisposition.Reject,
            [RiskCategory.JsonWebToken] = RiskDisposition.Reject,
            [RiskCategory.ConnectionStringPassword] = RiskDisposition.Reject,
            [RiskCategory.CloudCredential] = RiskDisposition.Reject,

            // R10.8 / R10.9 -- injection patterns, flag and score. A legitimate write-up *about*
            // prompt injection is an obviously valuable Forum topic and will trip every one of
            // these, which is exactly why none of them rejects.
            [RiskCategory.SecondPersonImperative] = RiskDisposition.Annotate,
            [RiskCategory.RoleAssumption] = RiskDisposition.Annotate,
            [RiskCategory.InstructionOverride] = RiskDisposition.Annotate,
            [RiskCategory.HiddenText] = RiskDisposition.Annotate,
            [RiskCategory.EncodedBlock] = RiskDisposition.Annotate,
            [RiskCategory.CredentialShapedUrl] = RiskDisposition.Annotate,

            // R10.29 -- PII, flag for review rather than hard-reject.
            [RiskCategory.PersonalData] = RiskDisposition.Annotate,
        }.ToFrozenDictionary();

    public static RiskDisposition Disposition(RiskCategory category) =>
        Dispositions.TryGetValue(category, out var disposition)
            ? disposition
            : throw new ArgumentOutOfRangeException(nameof(category), category, "No disposition for this category");

    /// <summary>Every category, for tests that must cover all of them rather than the ones someone listed.</summary>
    public static IReadOnlyCollection<RiskCategory> All => Dispositions.Keys;
}
