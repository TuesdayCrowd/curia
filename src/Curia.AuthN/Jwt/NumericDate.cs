using System.Text.Json;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Jwt;

/// <summary>RFC 7519 §2's "NumericDate": seconds since the Unix epoch, as a JSON number. Shared by
/// every claim parser (access token, client assertion, DPoP proof) for <c>iat</c>/<c>exp</c>/<c>nbf</c>.</summary>
internal static class NumericDate
{
    /// <summary>Reads a mandatory NumericDate claim; fails (rather than defaulting) when it is
    /// absent or not a JSON number -- unlike <c>CompactJws.ReadString</c>'s tolerant-empty style,
    /// silently defaulting a missing <c>exp</c> to the Unix epoch would make every token look
    /// permanently expired, which hides the real "claim missing" failure behind a misleading one.</summary>
    public static Result<DateTimeOffset> ReadRequired(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt64(out var seconds))
            return Result<DateTimeOffset>.Fail(AuthNErrors.Malformed($"'{name}' must be an integer NumericDate"));

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

        return Result<DateTimeOffset?>.Ok(DateTimeOffset.FromUnixTimeSeconds(seconds));
    }
}
