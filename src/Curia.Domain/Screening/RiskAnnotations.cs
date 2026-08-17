using System.Collections.Immutable;

namespace Curia.Domain.Screening;

/// <summary>
/// What SCREEN produces: findings that ride <b>beside</b> the signed content, never inside it.
///
/// <para>R6.14: "Derived analysis artifacts -- normalized forms, folded forms, decoded blocks,
/// extracted entities -- SHALL be stored in fields distinct from the signed content, following the
/// <c>slug</c>/<c>slug_folded</c> pattern, and SHALL NEVER overwrite the signed form." This type
/// is that separate field. It holds no bytes of the submission and offers no path to any --
/// see <see cref="RiskFlag"/> for why that is structural rather than a convention.</para>
///
/// <para>R10.11 is worth reading before anyone renders this: "a green 'no injection detected'
/// badge that implies more than 'our current detectors did not fire' is actively harmful, because
/// it invites readers to skip L3." <see cref="IsEmpty"/> therefore means precisely "the detectors
/// at <see cref="DetectorVersions"/> found nothing", and the property is named for the
/// annotations rather than for the content -- there is deliberately no <c>IsClean</c> or
/// <c>IsSafe</c> on this type for a UI to bind a badge to.</para>
/// </summary>
public sealed record RiskAnnotations
{
    public static readonly RiskAnnotations None = new([], []);

    private RiskAnnotations(ImmutableArray<RiskFlag> flags, ImmutableArray<string> detectorVersions)
    {
        Flags = flags;
        DetectorVersions = detectorVersions;
    }

    /// <summary>Every finding, in the order the detectors ran and then by position.</summary>
    public ImmutableArray<RiskFlag> Flags { get; }

    /// <summary>
    /// Every detector that ran, by version -- not merely the ones that fired. R10.10's
    /// re-runnability needs to know what was *asked*, since "no flags" from a detector set that
    /// never included a rule is a different statement from "no flags" from one that did.
    /// </summary>
    public ImmutableArray<string> DetectorVersions { get; }

    /// <summary>
    /// True when the detectors at <see cref="DetectorVersions"/> found nothing. Not a claim that
    /// the content is safe (R10.11).
    /// </summary>
    public bool IsEmpty => Flags.IsEmpty;

    /// <summary>
    /// The findings whose category obliges rejection (R10.26). Empty is the only acceptable value
    /// for anything that goes on to PERSIST.
    /// </summary>
    public IEnumerable<RiskFlag> Rejecting =>
        Flags.Where(f => RiskCategories.Disposition(f.Category) is RiskDisposition.Reject);

    public static RiskAnnotations Create(IEnumerable<RiskFlag> flags, IEnumerable<string> detectorVersions)
    {
        ArgumentNullException.ThrowIfNull(flags);
        ArgumentNullException.ThrowIfNull(detectorVersions);

        return new RiskAnnotations([.. flags], [.. detectorVersions]);
    }
}
