using System.Buffers.Text;
using System.Security.Cryptography;
using Curia.AuthN.Dpop;
using Curia.Canon.Json;

namespace Curia.OperatorTool;

/// <summary>
/// What the operator tool's three ES256 keys share and nothing more (R12.18): loading a P-256 PEM, deriving the RFC 7638 thumbprint, generating a PEM and stating the public JWK. The three keys stay
/// three types (<see cref="LogSigningKey"/>, <see cref="DeclarationSigningKey"/>, <see cref="OperatorActsSigningKey"/>) so that no type holds two and a wrong call is a compile error; this is only
/// the code they would otherwise repeat.
/// </summary>
internal static class EcKeyMaterial
{
    /// <summary>
    /// The key in <paramref name="pem"/> and its <c>kid</c>. <paramref name="what"/> names the key in the refusal ("The log signing key"); a key that is not P-256 is an
    /// <see cref="InvalidOperationException"/>, and the key is disposed when the import fails.
    /// </summary>
    internal static (ECDsa Key, string Kid) Import(string pem, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            var parameters = key.ExportParameters(includePrivateParameters: false);
            if (parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
            {
                throw new InvalidOperationException(
                    $"{what} must be an ECDSA P-256 key: ES256 is P-256 by definition (RFC 7518 §3.4).");
            }

            return (key, JwkThumbprint.Compute(new Jwk.EcP256(x, y)));
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>A fresh P-256 key as PEM: the persistable form, never a live key.</summary>
    internal static string GeneratePem()
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return generated.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>The public JWK a key entry publishes (RFC 7518 §6.2.1's EC form), as a canonicalizable value.</summary>
    internal static JsonValue.Object PublicJwk(ECDsa key, string kid)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        return new JsonValue.Object(
        [
            new("alg", new JsonValue.String("ES256")),
            new("crv", new JsonValue.String("P-256")),
            new("kid", new JsonValue.String(kid)),
            new("kty", new JsonValue.String("EC")),
            new("use", new JsonValue.String("sig")),
            new("x", new JsonValue.String(Base64Url.EncodeToString(parameters.Q.X!))),
            new("y", new JsonValue.String(Base64Url.EncodeToString(parameters.Q.Y!))),
        ]);
    }
}
