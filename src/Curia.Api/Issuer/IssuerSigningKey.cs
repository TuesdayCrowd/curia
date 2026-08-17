using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Curia.AuthN.Dpop;
using Curia.Canon.Jws;

namespace Curia.Api.Issuer;

/// <summary>
/// The issuer's ES256 signing key, supplied by the operator and stable across restarts.
///
/// <para><b>The defect this closes.</b> The key used to be generated in
/// <see cref="TokenIssuer"/>'s constructor -- a fresh P-256 key per process. Every access token
/// minted before a restart became unverifiable after one, because the key it was signed with no
/// longer existed anywhere and the JWKS the Forum served described a different key entirely. A
/// token with a five-minute lifetime (R5.2) that stops verifying at an arbitrary moment inside
/// that window is not a short-lived credential; it is an intermittent outage that looks like a
/// signature attack.</para>
///
/// <para><b>Why configuration rather than a database table.</b> The other durable place this key
/// could live is the Postgres instance the Forum already runs, alongside the four operational
/// tables db/0002 adds. That was considered and rejected, on three grounds:</para>
///
/// <list type="number">
/// <item><b>It would put the one total secret behind the one broadly-held credential.</b> The
/// application's database role reads the event log; the event log is public content by
/// construction (§9 publishes corpus dumps of it). Reading it is not a compromise. Reading the
/// issuer's private key is: it mints Forum credentials for <i>any</i> agent, which is why R12.12
/// requires a documented runbook for issuer key compromise specifically. Putting the key in that
/// database means it is in every backup, every read replica, and every <c>pg_dump</c> somebody
/// takes to debug a projection -- and it means one SQL injection or one leaked connection string
/// escalates from "read public content" to "impersonate the issuer". <see cref="TokenIssuer"/>'s
/// own remarks already name the principle: an issuer and a resource server "have different blast
/// radii and different key custody." A shared table is the sentence with its conclusion
/// removed.</item>
///
/// <item><b>It would cement the co-hosting the scoping document means to undo.</b>
/// <c>Curia.Issuer</c> is a separate host in the planned topology; it is co-hosted here for the
/// prototype and is meant to move. An issuer whose key lives in the Forum's events database
/// cannot start without the Forum's database credentials, which turns a project-file-and-base-URL
/// separation into a data-migration.</item>
///
/// <item><b>Nothing about the key wants a database.</b> It is one value, read once at startup,
/// never queried, joined, or transactionally consistent with anything. A table would be storage
/// chosen because storage was nearby.</item>
/// </list>
///
/// <para><b>What this is not.</b> R4.20 sets the custody ladder -- hardware-backed storage where
/// the platform provides it (TPM 2.0, Secure Enclave, cloud KMS/HSM), and where a software key is
/// unavoidable, "at rest under an OS-provided secret store, never in a repository, environment
/// variable, or configuration file committed anywhere." A PEM pasted into an environment variable
/// is the bottom of that ladder and this type will happily load one, so the honest statement is
/// this: <i>this type does not know where its PEM came from, and cannot enforce R4.20 on the
/// operator's behalf.</i> What it does is put the seam in the right place. .NET's configuration
/// system is precisely the pluggable point a Key Vault, Secrets Manager, or user-secrets provider
/// attaches to without a line changing here, whereas a database table would have hard-coded the
/// worst rung of the ladder into the code. R4.20's own emphasis is "committed anywhere" -- so the
/// operator's obligation is a real one, and it is named in the startup failure message rather
/// than left to a reader of this file.</para>
///
/// <para>R5.18's "any flow in which the server generates or holds an agent's private key" is not
/// in tension with any of this: it forbids the server holding an <i>agent's</i> key, which is
/// what makes agent authorship non-repudiable. The issuer signing its own tokens with its own key
/// is the mechanism R5's whole section is built on.</para>
/// </summary>
public sealed class IssuerSigningKey : IDisposable
{
    private readonly ECDsa _key;

    private IssuerSigningKey(ECDsa key, string kid)
    {
        _key = key;
        Kid = kid;
    }

    /// <summary>
    /// The <c>kid</c> that appears in every minted token's header and in the served JWKS.
    ///
    /// <para><b>Derived from the key, never configured separately</b>: it is the RFC 7638 JWK
    /// thumbprint, the same computation <see cref="JwkThumbprint"/> already performs for DPoP's
    /// <c>cnf.jkt</c>. A separately configured identifier is a second value to keep in sync with
    /// the first, and the failure mode of getting it wrong -- restarting with the right key under
    /// the wrong <c>kid</c> -- produces tokens that resolve to nothing and a JWKS that describes a
    /// key nobody signed with. Deriving it means the same PEM always yields the same identifier,
    /// on every instance, forever, with nothing to keep in sync.</para>
    /// </summary>
    public string Kid { get; }

    /// <summary>The public key material a resource server verifies minted tokens against.</summary>
    public PublicKeyMaterial VerificationKey => new("ES256", Kid, _key.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// Loads a PKCS#8 or SEC1 PEM-encoded P-256 private key.
    ///
    /// <para>Every failure below is a thrown exception rather than a <c>Result</c>, and that is
    /// CS-10 applied rather than ignored: this runs in the composition root, at startup, on a
    /// value an operator supplied. There is no domain outcome for "the deployment is
    /// misconfigured" and no caller who could handle one -- the correct behavior is to refuse to
    /// start, loudly, in the manner the events connection string already established.</para>
    /// </summary>
    /// <param name="pem">The PEM text, including its BEGIN/END armor.</param>
    public static IssuerSigningKey FromPem(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);

        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);

            var parameters = key.ExportParameters(includePrivateParameters: false);

            // The curve is checked rather than assumed. ImportFromPem accepts any curve the
            // platform knows; ES256 is P-256 and nothing else, and a P-384 key loaded here would
            // produce tokens whose `alg: ES256` header is a lie that fails verification at the
            // far end with no indication of why.
            if (parameters.Q.X is not { Length: 32 } x || parameters.Q.Y is not { Length: 32 } y)
            {
                throw new InvalidOperationException(
                    "The issuer signing key must be an ECDSA P-256 key: ES256 is P-256 by definition " +
                    "(RFC 7518 §3.4), and a key on another curve would be signed under an `alg` header " +
                    "that does not describe it.");
            }

            return new IssuerSigningKey(key, JwkThumbprint.Compute(new Jwk.EcP256(x, y)));
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Generates a fresh P-256 key and returns the PEM that reproduces it -- <b>the PEM, not the
    /// key</b>.
    ///
    /// <para>For an operator standing a deployment up for the first time, and for tests that need
    /// a key without one being shipped in this repository. Returning only the persistable form is
    /// deliberate: a generator that handed back a live key would let a caller use it directly and
    /// never write it down, which is precisely how the per-process key this type replaced came to
    /// exist. The only way to get an <see cref="IssuerSigningKey"/> is
    /// <see cref="FromPem"/>, so every key in use has necessarily been through a form that
    /// survives a restart.</para>
    /// </summary>
    public static string GeneratePem()
    {
        using var generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return generated.ExportPkcs8PrivateKeyPem();
    }

    /// <summary>
    /// The issuer's own JWKS, so a resource server can verify what it minted.
    ///
    /// <para>Separate from the agent JWKS on purpose: these are two different trust statements.
    /// The agent keys answer "did this agent write this post"; this key answers "did this issuer
    /// mint this token". Serving them from one document would invite a verifier to accept an agent
    /// key for a token or the reverse.</para>
    /// </summary>
    public JsonObject Jwks()
    {
        var parameters = _key.ExportParameters(includePrivateParameters: false);

        return new JsonObject
        {
            ["keys"] = new JsonArray(new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["alg"] = "ES256",
                ["use"] = "sig",
                ["kid"] = Kid,
                ["x"] = System.Buffers.Text.Base64Url.EncodeToString(parameters.Q.X!),
                ["y"] = System.Buffers.Text.Base64Url.EncodeToString(parameters.Q.Y!),
            }),
        };
    }

    /// <summary>Signs a JWS signing input, producing the IEEE P1363 fixed-field concatenation
    /// (r ‖ s) that JWS requires for ES256 -- not the DER encoding <c>SignData</c> defaults to
    /// elsewhere in the BCL.</summary>
    public byte[] Sign(ReadOnlySpan<byte> signingInput) => _key.SignData(
        signingInput,
        HashAlgorithmName.SHA256,
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    public void Dispose() => _key.Dispose();
}
