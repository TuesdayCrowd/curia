using System.Text.Json;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Jwt;

/// <summary>RFC 7519 §2's "NumericDate": seconds since the Unix epoch, as a JSON number. Shared by
/// every claim parser (access token, client assertion, DPoP proof) for <c>iat</c>/<c>exp</c>/<c>nbf</c>.
/// A value <see cref="DateTimeOffset.FromUnixTimeSeconds"/> cannot represent is malformed, never a
/// throw, because two of the three JWTs that reach this, the client assertion and the DPoP proof, are
/// signed by keys the caller holds and are parsed after their signatures verify, so any enrolled agent
/// chooses the number; the access token's are the issuer's, and are read by the same rule
/// (R11.33).</summary>
internal static class NumericDate
{
    /// <summary>The first second <see cref="DateTimeOffset"/> holds; an earlier one is malformed (R11.33).</summary>
    private static readonly long MinSeconds = DateTimeOffset.MinValue.ToUnixTimeSeconds();

    /// <summary>The last second <see cref="DateTimeOffset"/> holds; a later one is malformed (R11.33).</summary>
    private static readonly long MaxSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>Reads a mandatory NumericDate claim; fails (rather than defaulting) when it is
    /// absent or not a JSON number -- unlike <c>CompactJws.ReadString</c>'s tolerant-empty style,
    /// silently defaulting a missing <c>exp</c> to the Unix epoch would make every token look
    /// permanently expired, which hides the real "claim missing" failure behind a misleading one.</summary>
    public static Result<DateTimeOffset> ReadRequired(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var seconds))
            return Result<DateTimeOffset>.Fail(AuthNErrors.Malformed($"'{name}' must be an integer NumericDate"));

        if (seconds < MinSeconds || seconds > MaxSeconds)
            return Result<DateTimeOffset>.Fail(AuthNErrors.Malformed($"'{name}' is outside the range of a NumericDate"));

        return Result<DateTimeOffset>.Ok(DateTimeOffset.FromUnixTimeSeconds(seconds));
    }

    /// <summary>Reads an optional NumericDate claim (e.g. <c>nbf</c>, SHOULD per Table 8): absent
    /// is a real, valid state (<see langword="null"/>), distinct from present-but-malformed
    /// (a failure) -- Table 8's SHOULD only ever governs what happens when the claim is there.</summary>
    public static Result<DateTimeOffset?> ReadOptional(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v))
            return Result<DateTimeOffset?>.Ok(null);

        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var seconds))
            return Result<DateTimeOffset?>.Fail(AuthNErrors.Malformed($"'{name}' must be an integer NumericDate"));

        if (seconds < MinSeconds || seconds > MaxSeconds)
            return Result<DateTimeOffset?>.Fail(AuthNErrors.Malformed($"'{name}' is outside the range of a NumericDate"));

        return Result<DateTimeOffset?>.Ok(DateTimeOffset.FromUnixTimeSeconds(seconds));
    }
}
