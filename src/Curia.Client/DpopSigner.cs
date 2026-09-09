using System.Buffers.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Json;
using Curia.Canon.Jws;

namespace Curia.Client;

/// <summary>
/// The client half of §5: RFC 7523 <c>private_key_jwt</c> client assertions and RFC 9449 DPoP
/// proofs, both built from the published RFCs on this agent's own keys.
///
/// <para>Nothing here is Cūria-specific. That is the point -- the Forum's token endpoint is
/// ordinary OAuth 2.0 with sender-constrained tokens, and a client that needed bespoke knowledge
/// to talk to it would be a client nobody could reimplement.</para>
/// </summary>
internal sealed class DpopSigner
{
    private readonly EnrolledAgent _agent;

    internal DpopSigner(EnrolledAgent agent) => _agent = agent;

    /// <summary>
    /// RFC 7523 §2.2: a JWT the agent signs with its <i>registered</i> key, audience the token
    /// endpoint. This is what makes the token request authenticate <i>this</i> agent rather than
    /// merely some holder of some key.
    /// </summary>
    internal string ClientAssertion(Uri tokenEndpoint, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(tokenEndpoint);

        var header = new List<KeyValuePair<string, JsonValue>>
        {
            new("alg", new JsonValue.String(_agent.Profile.Alg)),
            new("kid", new JsonValue.String(_agent.Profile.Kid)),
            new("typ", new JsonValue.String("JWT")),
        };

        var payload = new List<KeyValuePair<string, JsonValue>>
        {
            new("iss", new JsonValue.String(_agent.Profile.AgentId)),
            new("sub", new JsonValue.String(_agent.Profile.AgentId)),
            new("aud", new JsonValue.String(tokenEndpoint.ToString())),
            new("iat", new JsonValue.Number(now.ToUnixTimeSeconds())),
            new("exp", new JsonValue.Number(now.AddSeconds(60).ToUnixTimeSeconds())),
            new("jti", new JsonValue.String(Jti())),
        };

        // Through the port, not with an ECDsa. This is the OTHER thing the registered key signs,
        // and a seam that covered only SubmissionBuilder would leave this process able to
        // authenticate as the agent -- which is most of what R11.20 protects.
        return Sign(_agent.Signer, header, payload);
    }

    /// <summary>
    /// RFC 9449 §4.2: a proof bound to one method and one URL, carrying the DPoP public key.
    /// </summary>
    /// <param name="accessToken">
    /// When present its SHA-256 goes in <c>ath</c>, binding the proof to that specific token. A
    /// proof without <c>ath</c> is valid on the token request and useless on a resource request;
    /// A17 additionally requires <c>typ: "dpop+jwt"</c>, which is set unconditionally below.
    /// </param>
    /// <param name="nonce">
    /// The server-issued nonce. Absent on the first write attempt, present on the retry after the
    /// <c>401</c> + <c>DPoP-Nonce</c> challenge -- RFC 9449 §8 makes that exchange the normal
    /// flow, not an error path.
    /// </param>
    internal string Proof(
        string method, Uri url, DateTimeOffset now, string? accessToken = null, string? nonce = null)
    {
        ArgumentNullException.ThrowIfNull(url);

        var header = new List<KeyValuePair<string, JsonValue>>
        {
            new("alg", "ES256".AsJson()),
            new("typ", "dpop+jwt".AsJson()),
            new("jwk", PublicJwk(_agent.DpopKey)),
        };

        var payload = new List<KeyValuePair<string, JsonValue>>
        {
            new("htm", new JsonValue.String(method)),
            new("htu", new JsonValue.String(url.ToString())),
            new("iat", new JsonValue.Number(now.ToUnixTimeSeconds())),
            new("jti", new JsonValue.String(Jti())),
        };

        if (accessToken is not null)
        {
            var ath = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
            payload.Add(new("ath", new JsonValue.String(ath)));
        }

        if (nonce is not null) payload.Add(new("nonce", new JsonValue.String(nonce)));

        return Sign(_agent.DpopKey, header, payload);
    }

    /// <summary>The DPoP public key as an RFC 7517 JWK, embedded in every proof header.</summary>
    private static JsonValue.Object PublicJwk(ECDsa key)
    {
        var p = key.ExportParameters(includePrivateParameters: false);
        return new JsonValue.Object(
        [
            new("crv", "P-256".AsJson()),
            new("kty", "EC".AsJson()),
            new("x", Base64Url.EncodeToString(p.Q.X!).AsJson()),
            new("y", Base64Url.EncodeToString(p.Q.Y!).AsJson()),
        ]);
    }

    private static string Jti() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// A compact JWS over a header and payload, signed by whatever holds the key.
    ///
    /// <para>Two overloads because the two keys are held differently on purpose: the registered key
    /// is behind <see cref="IAgentSigner"/> so this process need not hold it, and the DPoP key is an
    /// <see cref="ECDsa"/> here because delegating it would put a signer round-trip on every request
    /// including the nonce retry, for a key whose theft is bounded by a 300-second token.</para>
    /// </summary>
    private static string Sign(
        IAgentSigner signer,
        IEnumerable<KeyValuePair<string, JsonValue>> header,
        IEnumerable<KeyValuePair<string, JsonValue>> payload)
    {
        var input = SigningInput(header, payload);
        return Compact(input, signer.Sign(Encoding.ASCII.GetBytes(input)));
    }

    private static string Sign(
        ECDsa key,
        IEnumerable<KeyValuePair<string, JsonValue>> header,
        IEnumerable<KeyValuePair<string, JsonValue>> payload)
    {
        var input = SigningInput(header, payload);

        return Compact(input, key.SignData(
            Encoding.ASCII.GetBytes(input),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    private static string SigningInput(
        IEnumerable<KeyValuePair<string, JsonValue>> header,
        IEnumerable<KeyValuePair<string, JsonValue>> payload) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(ClientJson.Render(header))) + "." +
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes(ClientJson.Render(payload)));

    private static string Compact(string input, byte[] signature) => string.Create(
        CultureInfo.InvariantCulture,
        $"{input}.{Base64Url.EncodeToString(signature)}");
}

internal static class JsonValueExtensions
{
    internal static JsonValue AsJson(this string value) => new JsonValue.String(value);

    internal static ImmutableArray<JsonValue> AsJsonArray(this IEnumerable<string> values) =>
        [.. values.Select(v => (JsonValue)new JsonValue.String(v))];
}
