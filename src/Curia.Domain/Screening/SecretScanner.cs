using System.Text.RegularExpressions;

namespace Curia.Domain.Screening;

/// <summary>
/// R10.25's credential scan. Every hit here is a <see cref="RiskDisposition.Reject"/>, because
/// R10.26 makes this a gate rather than a cleanup pass:
///
/// <para><i>"Redaction is not merely the wrong response, it is an unavailable one: editing the
/// content would invalidate the author's signature (§6.4), so there is no redaction primitive in
/// this system. A secret admitted here can be withheld from serving but never removed from what
/// was signed and logged."</i></para>
///
/// <para><b>Deliberately conservative, and biased toward the direction that is recoverable.</b> A
/// false positive costs an author a resubmission and an appeal; a false negative writes a live
/// credential into an append-only log forever. So the patterns below match shapes that are
/// credential-specific rather than merely high-entropy, and the one entropy-based rule R10.25
/// mentions ("high-entropy strings in assignment position") is scoped to assignment position
/// exactly as written -- an unscoped entropy rule would reject base64 test vectors, hashes, and
/// half the content this Forum exists to carry.</para>
///
/// <para><b>Never captures what it matched.</b> These methods return offsets and lengths, never
/// substrings -- see <see cref="RiskFlag"/>. That is R10.27 and R10.28 made structural.</para>
/// </summary>
public static partial class SecretScanner
{
    /// <summary>
    /// R10.10/R10.30: bump this whenever a pattern changes, so a re-run over the archive is
    /// attributable to a rule set. The date is the rule set's, not the build's.
    /// </summary>
    public const string Version = "secrets/2026-08-16";

    private static readonly (Regex Pattern, RiskCategory Category)[] Rules =
    [
        // "private key PEM blocks". The most unambiguous shape in the list: nothing legitimate
        // carries a PEM private-key header except a document *about* one, and that document
        // should still not be signed into an append-only log.
        (PrivateKeyPem(), RiskCategory.PrivateKeyBlock),

        // "API keys with recognizable prefixes". Recognizable is the operative word -- these are
        // vendor-assigned prefixes, not a guess at what a key looks like.
        (ApiKeyPrefix(), RiskCategory.ApiKey),

        // "cloud provider credentials".
        (CloudCredential(), RiskCategory.CloudCredential),

        // "JWTs". Three base64url segments separated by dots, with a header that decodes to
        // something starting `{"` -- the `eyJ` prefix is that, and it is what makes this specific
        // enough to reject on rather than merely flag.
        (JwtShape(), RiskCategory.JsonWebToken),

        // "connection strings with embedded passwords".
        (ConnectionStringPassword(), RiskCategory.ConnectionStringPassword),

    ];

    /// <summary>
    /// Scans a derived copy of the content. The caller owns that copy and discards it (R6.13);
    /// nothing returned from here references it.
    /// </summary>
    public static IEnumerable<RiskFlag> Scan(string derivedCopy)
    {
        ArgumentNullException.ThrowIfNull(derivedCopy);

        foreach (var (pattern, category) in Rules)
            foreach (var match in pattern.Matches(derivedCopy).Cast<Match>())
                yield return new RiskFlag(category, match.Index, match.Length, Version);

        // "high-entropy strings in assignment position" is the one rule with a second condition,
        // so it runs outside the table rather than being forced into it. The regex finds the
        // assignment; the entropy floor decides whether the assigned run is plausibly a secret at
        // all, which is what keeps `token = "example"` and `secret: TODO-fill-this-in` out.
        foreach (var match in HighEntropyAssignment().Matches(derivedCopy).Cast<Match>())
            if (LooksHighEntropy(derivedCopy.AsSpan(match.Index, match.Length)))
                yield return new RiskFlag(RiskCategory.ApiKey, match.Index, match.Length, Version);
    }

    /// <summary>
    /// Shannon-ish entropy floor over the matched run, without materializing it as a string. A
    /// deliberately crude test: its job is only to stop the assignment rule from firing on
    /// <c>password = "changeme"</c>-style placeholders and on ordinary prose, not to be a
    /// classifier.
    /// </summary>
    private static bool LooksHighEntropy(ReadOnlySpan<char> run)
    {
        if (run.Length < 24) return false;

        Span<int> counts = stackalloc int[128];
        var considered = 0;
        foreach (var c in run)
        {
            if (c >= 128) return true; // non-ASCII in an assigned secret is unusual enough to flag
            counts[c]++;
            considered++;
        }

        var distinct = 0;
        foreach (var count in counts)
            if (count > 0) distinct++;

        // At least half the run's length in distinct characters, capped -- a 40-character token
        // drawn from base64's alphabet clears this comfortably; a repeated placeholder does not.
        return distinct >= Math.Min(16, considered / 2);
    }

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |OPENSSH |PGP |DSA )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKeyPem();

    // Vendor prefixes that are documented as key prefixes: GitHub, Stripe, Slack, OpenAI,
    // Anthropic, Google, SendGrid, npm. Extending this list is a version bump (R10.10).
    [GeneratedRegex(
        @"\b(?:gh[pousr]_[A-Za-z0-9]{16,}|sk-[A-Za-z0-9_-]{16,}|xox[baprs]-[A-Za-z0-9-]{10,}|SG\.[A-Za-z0-9_-]{16,}|npm_[A-Za-z0-9]{16,}|AIza[A-Za-z0-9_-]{20,})",
        RegexOptions.CultureInvariant)]
    private static partial Regex ApiKeyPrefix();

    [GeneratedRegex(@"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CloudCredential();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex JwtShape();

    // A URI with credentials in the authority, or a keyword connection string carrying a password.
    [GeneratedRegex(
        @"(?:[a-z][a-z0-9+.-]*://[^\s:@/]+:[^\s:@/]+@)|(?:\b(?:password|pwd)\s*=\s*[^\s;""']{4,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPassword();

    // "high-entropy strings in assignment position": a secret-ish name, an assignment, a long run.
    [GeneratedRegex(
        @"(?<=\b(?:secret|token|api[_-]?key|apikey|access[_-]?key|private[_-]?key|credential)\b\s*[:=]\s*[""']?)[A-Za-z0-9+/=_-]{24,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HighEntropyAssignment();
}
