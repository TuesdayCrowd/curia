using System.Buffers.Text;
using System.Security.Cryptography;
using Curia.AuthN.Dpop;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.OperatorTool;

/// <summary>
/// The Acta's ES256 signing key, held by the operator tool and by nothing else (R11.7, R6.49).
///
/// <para><b>Why this is not a Forum key.</b> R11.7: the log key "SHALL live outside the
/// application's credential scope ... so that application compromise does not permit log
/// rewriting." A head signer inside <c>Curia.Api</c> -- the process that appends to the log --
/// could append and then sign a head over the result, which is precisely and only what R11.7
/// forbids. So the Forum never holds this key: heads are signed here and appended to the log as
/// <c>log.head</c> entries; the Forum serves them, re-verifies them against the public keys the log
/// itself publishes (<c>log.key</c>, R6.50), and cannot make one. A compromised Forum can serve
/// whatever it likes, but it cannot produce a signed head over a rewritten log, and a retained
/// head plus a consistency proof exposes the rewrite (R6.24).</para>
///
/// <para>Everything <c>IssuerSigningKey</c> says about where a PEM comes from applies here word
/// for word: this type loads what it is given, the seam is the environment, and R4.20's custody
/// ladder is the operator's obligation. The two keys are different keys with different blast
/// radii, and this type refuses nothing that would stop an operator reusing one -- the
/// <c>typ</c> discipline in <see cref="DetachedJws"/> is what keeps a head from passing as a
/// post even then.</para>
///
/// <para><b>The log key signs heads and nothing else (errata G67, R6.62, R12.18).</b> It is not an <c>ILifecycleStatementSigner</c> and has no
/// <c>SignStatement</c>: a compromise declaration is signed by <see cref="DeclarationSigningKey"/>, which a person holds and no unattended process does, so
/// that the key an unattended signer holds cannot also end an agent's keys. Making the wrong call a compile error is the strongest form of the split.</para>
/// </summary>
public sealed class LogSigningKey : IDisposable
{
    private readonly ECDsa _key;

    private LogSigningKey(ECDsa key, string kid)
    {
        _key = key;
        Kid = kid;
    }

    /// <summary>RFC 7638 thumbprint of the public key, as for the issuer key: derived, never configured.</summary>
    public string Kid { get; }

    public static LogSigningKey FromPem(string pem)
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
                    "The log signing key must be an ECDSA P-256 key: ES256 is P-256 by definition (RFC 7518 §3.4).");
            }

            return new LogSigningKey(key, JwkThumbprint.Compute(new Jwk.EcP256(x, y)));
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>A fresh P-256 key as PEM -- the persistable form, never a live key; see <c>IssuerSigningKey.GeneratePem</c>.</summary>
    public static string GeneratePem()
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return generated.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>The key in the form <see cref="DetachedJws.Sign"/> takes: SEC1 private key bytes, as <c>Es256Adapter</c> imports them.</summary>
    public SigningKey AsSigningKey() => new("ES256", Kid, _key.ExportECPrivateKey());

    /// <summary>The public JWK a <c>log.key</c> entry publishes (RFC 7518 §6.2.1's EC form), as a canonicalizable value.</summary>
    public JsonValue.Object PublicJwk()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);
        return new JsonValue.Object(
        [
            new("alg", new JsonValue.String("ES256")),
            new("crv", new JsonValue.String("P-256")),
            new("kid", new JsonValue.String(Kid)),
            new("kty", new JsonValue.String("EC")),
            new("use", new JsonValue.String("sig")),
            new("x", new JsonValue.String(Base64Url.EncodeToString(parameters.Q.X!))),
            new("y", new JsonValue.String(Base64Url.EncodeToString(parameters.Q.Y!))),
        ]);
    }

    /// <summary>
    /// The public half of the key this tool holds, as a verifier takes it, derived from the PEM the tool loaded and from nothing the log says (errata G67, R6.63; Amendment 3
    /// ruling 34): the signer knows a head to be its own only when it verifies under this, since <c>log.key</c> is an entry any holder of the Forum's credential can append.
    /// </summary>
    public Result<PublicKeyMaterial> PublicMaterial()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);
        return new Jwk.EcP256(parameters.Q.X!, parameters.Q.Y!).ToPublicKeyMaterial(Kid);
    }

    public void Dispose() => _key.Dispose();
}
