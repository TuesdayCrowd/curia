using System.Text.Json;
using Curia.AuthN.Jwt;
using Curia.Domain.Primitives;

namespace Curia.AuthN;

/// <summary>Table 8's access token claim set (RFC 9068 profile). Every field the algorithm's
/// Phase 3/4 checks read; nothing beyond that (e.g. no free-form extension bag) -- an unknown
/// claim is simply not modeled here, matching Phase 3's own "all mandatory, no skipping" framing
/// by not giving a caller anything to accidentally trust that was never validated.</summary>
public sealed record AccessTokenClaims(
    string Iss,
    string Sub,
    IReadOnlyList<string> Aud,
    string ClientId,
    DateTimeOffset Iat,
    DateTimeOffset Exp,
    DateTimeOffset? Nbf,
    string Jti,
    string Scope,
    string CnfJkt,
    string Owner,
    string Tier)
{
    internal static Result<AccessTokenClaims> Parse(JsonElement root)
    {
        if (!NumericDate.ReadRequired(root, "iat").TryGetValue(out var iat, out var iatError))
            return Result<AccessTokenClaims>.Fail(iatError!);

        if (!NumericDate.ReadRequired(root, "exp").TryGetValue(out var exp, out var expError))
            return Result<AccessTokenClaims>.Fail(expError!);

        if (!NumericDate.ReadOptional(root, "nbf").TryGetValue(out var nbf, out var nbfError))
            return Result<AccessTokenClaims>.Fail(nbfError!);

        return Result<AccessTokenClaims>.Ok(new AccessTokenClaims(
            Iss: CompactJws.ReadString(root, "iss"),
            Sub: CompactJws.ReadString(root, "sub"),
            Aud: ReadAudience(root),
            ClientId: CompactJws.ReadString(root, "client_id"),
            Iat: iat,
            Exp: exp,
            Nbf: nbf,
            Jti: CompactJws.ReadString(root, "jti"),
            Scope: CompactJws.ReadString(root, "scope"),
            CnfJkt: ReadCnfJkt(root),
            Owner: CompactJws.ReadString(root, "owner"),
            Tier: CompactJws.ReadString(root, "tier")));
    }

    /// <summary><c>aud</c> is a single string or an array of strings per RFC 7519 §4.1.3; both
    /// forms normalize to a list here so Phase 3's "aud contains THIS_RESOURCE_SERVER" check has
    /// one shape to read regardless of which an issuer emitted.</summary>
    private static List<string> ReadAudience(JsonElement root)
    {
        if (!root.TryGetProperty("aud", out var aud))
            return [];

        if (aud.ValueKind == JsonValueKind.String)
            return [aud.GetString()!];

        if (aud.ValueKind != JsonValueKind.Array)
            return [];

        List<string> values = [];
        foreach (var entry in aud.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
                values.Add(entry.GetString()!);
        }

        return values;
    }

    private static string ReadCnfJkt(JsonElement root) =>
        root.TryGetProperty("cnf", out var cnf) && cnf.ValueKind == JsonValueKind.Object
            ? CompactJws.ReadString(cnf, "jkt")
            : "";
}
