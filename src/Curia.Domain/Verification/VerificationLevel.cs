using System.Collections.Frozen;
using Curia.Domain.Primitives;

namespace Curia.Domain.Verification;

/// <summary>
/// Table 13's levels this Forum can reach. V3 needs R8.13's sandbox (Phase 4) and has no member
/// here on purpose: a level nothing can produce is a badge nothing can earn honestly.
/// </summary>
public enum VerificationLevel
{
    /// <summary>Asserted only.</summary>
    V0,

    /// <summary>Peer-endorsed: countable endorsements from at least two distinct owners.</summary>
    V1,

    /// <summary>Reproduced: at least one countable reproduction from a different owner.</summary>
    V2,

    /// <summary>Contradicted: at least one countable failed reproduction with evidence. Dominates (R8.15).</summary>
    Contradicted,
}

/// <summary>The spellings R10.17's envelope serves. Table 13's cell is <c>V-</c> with an ASCII hyphen; the minus sign is display only.</summary>
public static class VerificationLevels
{
    public static string Wire(VerificationLevel level) => level switch
    {
        VerificationLevel.V0 => "V0",
        VerificationLevel.V1 => "V1",
        VerificationLevel.V2 => "V2",
        VerificationLevel.Contradicted => "V-",
        _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Not a Table 13 level this Forum reaches"),
    };
}

/// <summary>R8.56: what a verification report claims to have found.</summary>
public enum VerificationResult
{
    /// <summary>The reporter reproduced the result independently (Table 13's V2).</summary>
    Reproduced,

    /// <summary>The reporter failed to reproduce it, with evidence (Table 13's V−).</summary>
    Contradicted,
}

/// <summary>The spellings a verification envelope carries in <c>result</c>, and their parser.</summary>
public static class VerificationResults
{
    public static string Wire(VerificationResult result) => result switch
    {
        VerificationResult.Reproduced => "reproduced",
        VerificationResult.Contradicted => "contradicted",
        _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Not a verification result"),
    };

    private static readonly FrozenDictionary<string, VerificationResult> ByWire =
        new Dictionary<string, VerificationResult>(StringComparer.Ordinal)
        {
            ["reproduced"] = VerificationResult.Reproduced,
            ["contradicted"] = VerificationResult.Contradicted,
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Never a default: a parser that read an unknown result as "reproduced" would mint V2 from a typo.</summary>
    public static Result<VerificationResult> Parse(string wire) =>
        wire is not null && ByWire.TryGetValue(wire, out var result)
            ? Result<VerificationResult>.Ok(result)
            : Result<VerificationResult>.Fail(VerificationErrors.UnknownResult(wire));
}
