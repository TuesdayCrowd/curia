using System.Text.Json;
using Curia.AuthN.Jwt;
using Curia.Domain.Primitives;

namespace Curia.AuthN;

/// <summary>Appendix C.1's client assertion claim set (RFC 7523 private_key_jwt).
/// <c>aud</c> is a single string here (the token endpoint URL), not the array
/// <see cref="AccessTokenClaims.Aud"/> allows -- Appendix C.1's example and R5.1's "the assertion
/// SHALL specify the token endpoint as aud" both describe one URL, not a set.</summary>
public sealed record ClientAssertionClaims(
    string Iss,
    string Sub,
    string Aud,
    DateTimeOffset Iat,
    DateTimeOffset Exp,
    string Jti)
{
    internal static Result<ClientAssertionClaims> Parse(JsonElement root)
    {
        if (!NumericDate.ReadRequired(root, "iat").TryGetValue(out var iat, out var iatError))
            return Result<ClientAssertionClaims>.Fail(iatError!);

        if (!NumericDate.ReadRequired(root, "exp").TryGetValue(out var exp, out var expError))
            return Result<ClientAssertionClaims>.Fail(expError!);

        // R11.33 (errata G17): the replay cache is asked about this jti, and an absent, blank,
        // NUL-holding or overlong one threw there, at the token endpoint.
        var jti = CompactJws.ReadString(root, "jti");
        if (CompactJws.IdentifierRefusal(jti, "jti", CompactJws.MaxJtiUtf8Bytes) is { } jtiError)
            return Result<ClientAssertionClaims>.Fail(jtiError);

        return Result<ClientAssertionClaims>.Ok(new ClientAssertionClaims(
            Iss: CompactJws.ReadString(root, "iss"),
            Sub: CompactJws.ReadString(root, "sub"),
            Aud: CompactJws.ReadString(root, "aud"),
            Iat: iat,
            Exp: exp,
            Jti: jti));
    }
}
