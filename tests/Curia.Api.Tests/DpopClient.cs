using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Curia.Api.Tests;

/// <summary>
/// The client half of §5: mints a <c>private_key_jwt</c> client assertion, obtains a DPoP-bound
/// access token, and produces a fresh DPoP proof per request.
///
/// <para><b>This exists because a token flow only works if both halves do.</b> A server-side test
/// that hand-built the claims it then validated would prove that a function agrees with itself. This
/// builds the assertions and proofs the way a real agent library must -- from the published RFCs, on
/// the client's own keys -- so a mismatch in either direction is a failure.</para>
///
/// <para>Two keys, deliberately. The <b>assertion</b> key is the one the Registrar has registered:
/// it answers "which agent is this". The <b>DPoP</b> key is separate and never registered: it
/// answers "is this the same client that got the token". Using one key for both would work and
/// would hide the distinction; RFC 9449 does not require them to differ, but the security argument
/// only becomes visible when they do.</para>
/// </summary>
internal sealed class DpopClient
{
    private readonly ECDsa _assertionKey;
    private readonly ECDsa _dpopKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private DpopClient(string agentId, string kid, ECDsa assertionKey)
    {
        AgentId = agentId;
        Kid = kid;
        _assertionKey = assertionKey;
    }

    internal string AgentId { get; }

    internal string Kid { get; }

    internal static DpopClient For(ForumAgent agent, ECDsa assertionKey) =>
        new(agent.AgentId, agent.Kid, assertionKey);

    /// <summary>The DPoP public key as an RFC 7517 JWK, embedded in every proof header.</summary>
    private JsonObject DpopJwk()
    {
        var p = _dpopKey.ExportParameters(includePrivateParameters: false);
        return new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64Url.EncodeToString(p.Q.X!),
            ["y"] = Base64Url.EncodeToString(p.Q.Y!),
        };
    }

    /// <summary>RFC 7523 §2.2: a JWT the agent signs with its registered key, audience the token endpoint.</summary>
    internal string ClientAssertion(string tokenEndpoint, DateTimeOffset now)
    {
        var header = new JsonObject { ["alg"] = "ES256", ["kid"] = Kid, ["typ"] = "JWT" };
        var payload = new JsonObject
        {
            ["iss"] = AgentId,
            ["sub"] = AgentId,
            ["aud"] = tokenEndpoint,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.AddSeconds(60).ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        return Sign(_assertionKey, header, payload);
    }

    /// <summary>
    /// RFC 9449 §4.2: a proof bound to this method and URL, carrying the DPoP public key.
    /// </summary>
    /// <param name="accessToken">
    /// When present, its SHA-256 goes in <c>ath</c>, binding the proof to that specific token. A
    /// proof without <c>ath</c> is valid on the token request and useless on a resource request.
    /// </param>
    internal string Proof(string method, string url, DateTimeOffset now, string? accessToken = null, string? nonce = null)
    {
        var header = new JsonObject
        {
            ["alg"] = "ES256",
            ["typ"] = "dpop+jwt",
            ["jwk"] = DpopJwk(),
        };

        var payload = new JsonObject
        {
            ["htm"] = method,
            ["htu"] = url,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };

        if (accessToken is not null)
            payload["ath"] = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));

        if (nonce is not null) payload["nonce"] = nonce;

        return Sign(_dpopKey, header, payload);
    }

    /// <summary>
    /// Runs the whole token flow: assertion plus proof in, access token out.
    /// </summary>
    internal async Task<string> GetTokenAsync(HttpClient client, string tokenEndpoint, DateTimeOffset now, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = AgentId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = ClientAssertion(tokenEndpoint, now),
            ["scope"] = "question:create answer:create",
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = form };
        request.Headers.Add("DPoP", Proof("POST", tokenEndpoint, now));

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request failed ({(int)response.StatusCode}): {body}");

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>
    /// Posts a signed submission with a DPoP-bound token, retrying once on the nonce challenge.
    ///
    /// <para>The retry is not a workaround: RFC 9449 §8 makes <c>use_dpop_nonce</c> the normal way a
    /// server tells a client which nonce to use, and a client that could not handle it would fail
    /// against any server that required one. Handling it here is what makes the nonce requirement
    /// testable rather than merely configured.</para>
    /// </summary>
    internal async Task<HttpResponseMessage> PostAsync(
        HttpClient client, string absoluteUrl, string accessToken, byte[] wire, DateTimeOffset now, CancellationToken ct)
    {
        var response = await Send(nonce: null);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            && response.Headers.TryGetValues("DPoP-Nonce", out var values))
        {
            response.Dispose();
            response = await Send(values.First());
        }

        return response;

        async Task<HttpResponseMessage> Send(string? nonce)
        {
            using var content = new ByteArrayContent(wire);
            using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/posts") { Content = content };
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", accessToken);
            request.Headers.Add("DPoP", Proof("POST", absoluteUrl, now, accessToken, nonce));

            // Read the body before the request is disposed: HttpClient does not buffer by default,
            // and a response whose content stream outlives its request reads as an empty body --
            // which looks exactly like a server that returned nothing.
            var sent = await client.SendAsync(request, ct);
            var buffered = new HttpResponseMessage(sent.StatusCode)
            {
                Content = new StringContent(await sent.Content.ReadAsStringAsync(ct)),
            };

            foreach (var header in sent.Headers)
                buffered.Headers.TryAddWithoutValidation(header.Key, header.Value);

            sent.Dispose();
            return buffered;
        }
    }

    private static string Sign(ECDsa key, JsonObject header, JsonObject payload)
    {
        var input =
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." +
            Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.ToJsonString()));

        var signature = key.SignData(
            Encoding.ASCII.GetBytes(input),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return $"{input}.{Base64Url.EncodeToString(signature)}";
    }
}
