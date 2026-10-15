using System.Text.Json;
using Curia.AuthN.Jwt;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

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
    Jti Jti)
{
    /// <summary>The assertion header's <c>kid</c>, set by the validator once the key resolved (R5.22 puts it in the token).</summary>
    public string Kid { get; init; } = "";

    internal static Result<ClientAssertionClaims> Parse(JsonElement root)
    {
        if (!NumericDate.ReadRequired(root, "iat").TryGetValue(out var iat, out var iatError))
            return Result<ClientAssertionClaims>.Fail(iatError!);

        if (!NumericDate.ReadRequired(root, "exp").TryGetValue(out var exp, out var expError))
            return Result<ClientAssertionClaims>.Fail(expError!);

        // R11.33 (errata G17): the replay cache is asked about this jti, and an absent, blank,
        // NUL-holding or overlong one threw there, at the token endpoint. R11.34 (errata G18): it
        // reaches the cache only as a Jti.
        if (!Jti.Parse(CompactJws.ReadString(root, "jti")).TryGetValue(out var jti, out var jtiError))
            return Result<ClientAssertionClaims>.Fail(CompactJws.AsMalformed(jtiError!, "jti", Jti.MaxUtf8Bytes));

        return Result<ClientAssertionClaims>.Ok(new ClientAssertionClaims(
            Iss: CompactJws.ReadString(root, "iss"),
            Sub: CompactJws.ReadString(root, "sub"),
            Aud: CompactJws.ReadString(root, "aud"),
            Iat: iat,
            Exp: exp,
            Jti: jti!));
    }
}
