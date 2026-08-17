using System.Text.RegularExpressions;

namespace Curia.Domain.Screening;

/// <summary>
/// R10.8's injection patterns. Every hit here is a <see cref="RiskDisposition.Annotate"/>, and
/// R10.9 says why that is not timidity:
///
/// <para><i>"Detection SHALL flag and score, not silently reject, except above a high-confidence
/// threshold. Injection detectors have meaningful false-positive rates, and a legitimate write-up
/// *about* prompt injection -- an obviously valuable Forum topic -- will trip every one of
/// them."</i></para>
///
/// <para>That sentence is a constraint on this file specifically: a security forum's most valuable
/// posts are the ones that quote the attack. So there is no rejection path here at all. R10.9
/// permits a narrow high-confidence one; this rule set does not claim to have found it, and
/// inventing a threshold in order to have one would be inventing exactly the confidence R10.11
/// warns against.</para>
///
/// <para><b>R10.11 governs how a result may be described.</b> "A green 'no injection detected'
/// badge that implies more than 'our current detectors did not fire' is actively harmful, because
/// it invites readers to skip L3." Nothing here returns a verdict -- only which rules fired, at
/// which offsets, under which version.</para>
/// </summary>
public static partial class InjectionDetector
{
    /// <summary>R10.10: versioned, so a November rule set can be re-run over March's archive.</summary>
    public const string Version = "injection/2026-08-16";

    private static readonly (Regex Pattern, RiskCategory Category)[] Rules =
    [
        // "instruction-override phrasing".
        (InstructionOverride(), RiskCategory.InstructionOverride),

        // "role-assumption language".
        (RoleAssumption(), RiskCategory.RoleAssumption),

        // "second-person imperatives directed at an assistant".
        (SecondPersonImperative(), RiskCategory.SecondPersonImperative),

        // "encoded blocks with no declared purpose". Length-gated so an ordinary base64 hash or a
        // short token in prose does not fire; a long undeclared block is the shape that carries a
        // payload.
        (EncodedBlock(), RiskCategory.EncodedBlock),

        // "URLs with credential-shaped query parameters".
        (CredentialShapedUrl(), RiskCategory.CredentialShapedUrl),

        // HTML comments -- the "hidden text" list's one shape that is a pattern rather than a
        // character class. The rest are handled below.
        (HtmlComment(), RiskCategory.HiddenText),
    ];

    public static IEnumerable<RiskFlag> Scan(string derivedCopy)
    {
        ArgumentNullException.ThrowIfNull(derivedCopy);

        foreach (var (pattern, category) in Rules)
            foreach (var match in pattern.Matches(derivedCopy).Cast<Match>())
                yield return new RiskFlag(category, match.Index, match.Length, Version);

        foreach (var flag in HiddenCharacters(derivedCopy))
            yield return flag;
    }

    /// <summary>
    /// R10.8's "zero-width characters ... unusual Unicode direction marks". Character-by-character
    /// rather than by regex, because these are code points rather than a lexical shape, and
    /// because a regex over them reads as noise at review time while a named list does not.
    ///
    /// <para>Note what is <b>not</b> here: homoglyph substitution, which R10.8 also names.
    /// Detecting it needs a confusables table (UTS #39) and a notion of what the text is being
    /// confused *with*; a rule that flagged every Cyrillic character in mixed-script text would
    /// fire on most of the multilingual content the Forum should welcome. Recorded as an
    /// unimplemented clause rather than approximated, so nobody reads a clean scan as evidence
    /// there is no homoglyph.</para>
    /// </summary>
    private static IEnumerable<RiskFlag> HiddenCharacters(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var hidden = c switch
            {
                '​' or '‌' or '‍' or '﻿' => true, // zero-width space/NJ/J, BOM
                '‎' or '‏' => true,                          // LRM, RLM
                >= '‪' and <= '‮' => true,                   // embedding/override
                >= '⁦' and <= '⁩' => true,                   // isolates
                '­' => true,                                      // soft hyphen
                _ => false,
            };

            if (hidden)
                yield return new RiskFlag(RiskCategory.HiddenText, i, 1, Version);
        }
    }

    [GeneratedRegex(
        @"\b(?:ignore|disregard|forget|override)\s+(?:all\s+|any\s+|the\s+|your\s+|previous\s+|prior\s+|above\s+)*(?:instruction|prompt|rule|direction|context)s?\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InstructionOverride();

    [GeneratedRegex(
        @"\b(?:you\s+are\s+now|act\s+as|pretend\s+to\s+be|roleplay\s+as|from\s+now\s+on\s+you|assume\s+the\s+role)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleAssumption();

    // Directed at an assistant specifically -- an imperative aimed at the reader of a post is
    // ordinary prose ("run the tests before you file"), so the assistant-addressing form is what
    // distinguishes this from every technical write-up ever written.
    [GeneratedRegex(
        @"\b(?:assistant|ai|model|agent|system)\s*[,:]?\s*(?:please\s+)?(?:you\s+must|you\s+should|do\s+not|don't|always|never)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecondPersonImperative();

    [GeneratedRegex(@"\b[A-Za-z0-9+/]{120,}={0,2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex EncodedBlock();

    // The fragment is included alongside the query. A fragment is not sent to the server, which is
    // exactly why it is a favoured place to park a token -- it leaks to whatever client-side code
    // reads location.hash, and it never appears in a server log where anyone would notice. Treating
    // `?token=` as credential-shaped while ignoring `#token=` would catch the careless case and miss
    // the deliberate one. Found by adding evasions to the red-team corpus and watching this rule miss.
    [GeneratedRegex(
        @"https?://[^\s""'<>]*[?&#](?:access_token|api[_-]?key|token|secret|password|pwd|auth)=[^\s""'<>&]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialShapedUrl();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlComment();
}
