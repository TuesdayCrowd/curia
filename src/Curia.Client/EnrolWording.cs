using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>
/// R11.107 (errata G79): the words <c>curia enrol</c> prints for a refusal of the enrollment and for a continuation, written once. Each sentence is a span of the errata's paragraph
/// <c>**R11.107 words.**</c>, which a probe reads from the errata at test time and compares with what a run prints. <c>&lt;type&gt;</c> and <c>&lt;directory&gt;</c> are written as display literals
/// (R10.65) and nothing else of a response is printed. A refusal of the proof says by its cause whether anything was registered (nothing was, for every refusal of R4.46); a refusal of the request
/// itself says the profile cannot be enrolled as it is.
/// </summary>
public static class EnrolWording
{
    private const string ProofWindow =
        "nothing was registered: the Forum's clock and this host's differ by more than 30 seconds. Correct this host's clock, then run the same command again.";

    private const string ProofNotPossessed =
        "nothing was registered: the signature over the enrollment does not verify under the key being enrolled. With --signer, check that the key the signer describes is the key it signs with, then run the same command again.";

    private const string ProofRequired =
        "this Forum requires a proof of possession from the key being enrolled and this client did not send one; update curia. Nothing was registered.";

    private const string NonceAbsent =
        "this Forum does not offer an enrollment nonce, so it predates proof of possession at enrollment. Nothing was sent.";

    private const string ProofOther =
        "the Forum refused the proof (<type>); nothing was registered. This is a defect of this client or of its signer.";

    private const string NotContinuable =
        "nothing was registered, and this profile cannot be enrolled as it is: the Forum refused what the profile names (<type>), and the same command would be refused again. Remove <directory>, then enrol again under a different --agent-id or --kid.";

    /// <summary>Printed when <c>curia enrol</c> resumes a profile that was created and records no enrollment.</summary>
    public const string Continuing = "continuing an enrollment that was not completed, with the keys already in this profile.";

    private static readonly string[] ProofTypes =
    [
        "curia/enroll/proof-required", "curia/enroll/proof-malformed", "curia/enroll/proof-mismatch", "curia/enroll/proof-not-possessed",
        "curia/enroll/proof-window", "curia/enroll/proof-nonce-stale", "curia/enroll/proof-replayed",
    ];

    /// <summary>
    /// The sentence for a refusal of <c>curia enrol</c>, or null when the refusal is none of those the errata words (a fault, a transport failure, a denial), which the client's ordinary summary words.
    /// <paramref name="profileDirectory"/> is the profile's directory, named by the sentence for a refusal of the request itself.
    /// </summary>
    public static string? For(Refusal refusal, string profileDirectory)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        ArgumentNullException.ThrowIfNull(profileDirectory);

        if (refusal.NothingSent) return refusal.Status == 404 ? NonceAbsent : null;

        var type = refusal.Error.Type;
        if (refusal.Kind == RefusalKind.Content && refusal.Status == 400 && Array.IndexOf(ProofTypes, type) >= 0)
        {
            return type switch
            {
                "curia/enroll/proof-window" => ProofWindow,
                "curia/enroll/proof-not-possessed" => ProofNotPossessed,
                "curia/enroll/proof-required" => ProofRequired,
                _ => Fill(ProofOther, type, profileDirectory),
            };
        }

        return (refusal.Kind == RefusalKind.Conflict && refusal.Status == 409) || (refusal.Kind == RefusalKind.Content && refusal.Status == 400)
            ? Fill(NotContinuable, type, profileDirectory)
            : null;
    }

    private static string Fill(string span, string type, string directory) =>
        span.Replace("<type>", DisplayLiteral.Of(type), StringComparison.Ordinal)
            .Replace("<directory>", DisplayLiteral.Of(directory), StringComparison.Ordinal);
}
