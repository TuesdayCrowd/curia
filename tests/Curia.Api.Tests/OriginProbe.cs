using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What D41's probes (errata G82; the plan's ORG series) share. <b>Every credential here is built from a literal the test writes</b>, never from <c>ForumFixture.PublicOrigin</c> and never from a client
/// library's own URL builder: <c>ForumClient</c> and every <c>Uri</c>-based client lower-case a host and drop a default port before they sign, so no real client can produce <c>http://LOCALHOST/...</c> or
/// <c>http://localhost:80/...</c>, and a probe that took its URLs from the origin would restate the code it checks (amendment 1, #4). The <c>Host</c> is forced with
/// <c>HttpClient.DefaultRequestHeaders.Host</c>, which survives to the Forum unchanged (T0-7).
/// </summary>
internal static class OriginProbe
{
    internal const string HostRefusalType = "curia/request/host-not-served";
    internal const string HostRefusalTitle = "The Forum is not served under this name";

    /// <summary>A refusal or an answer as the Forum sent it: the status, the body, and what the body says of itself.</summary>
    internal sealed record OriginReply(int Status, string Body, string? ContentType, IReadOnlyList<string> HeaderNames)
    {
        private JsonNode? Json => Body.Length > 0 && Body[0] == '{' ? JsonNode.Parse(Body) : null;

        /// <summary>A problem document's <c>type</c>.</summary>
        internal string? Type => Json?["type"]?.GetValue<string>();

        /// <summary>An RFC 6749 object's <c>error</c>.</summary>
        internal string? Error => Json?["error"]?.GetValue<string>();

        internal string? Title => Json?["title"]?.GetValue<string>();

        internal string? Detail => Json?["detail"]?.GetValue<string>();

        internal string? ErrorDescription => Json?["error_description"]?.GetValue<string>();

        /// <summary>The name a refusal gives itself, whichever of the two shapes it is in.</summary>
        internal string? Kind => Type ?? Error;

        internal bool IsHostRefusal =>
            Status == 400 && (Type == HostRefusalType || (Error == "invalid_request" && ErrorDescription == HostRefusalTitle));

        internal string Says => $"{Status} {(Body.Length > 240 ? Body[..240] + "..." : Body)}";
    }

    internal static async Task<OriginReply> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using (response)
        {
            return new OriginReply(
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(ct),
                response.Content.Headers.ContentType?.MediaType,
                [.. response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key))]);
        }
    }

    /// <summary>A client of <paramref name="forum"/> that sends <paramref name="host"/> as its <c>Host</c> whatever it dials.</summary>
    internal static HttpClient ClientWith(ForumFixture forum, string host)
    {
        var client = forum.CreateClient();
        client.DefaultRequestHeaders.Host = host;
        return client;
    }

    /// <summary>
    /// One token request: the assertion's <c>aud</c> and the proof's <c>htu</c> are two strings the test writes, because the endpoint makes two comparisons and a probe that spells both alike cannot
    /// tell which one refused (amendment 1, #4's three rows).
    /// </summary>
    internal static async Task<OriginReply> TokenAsync(
        HttpClient client, string path, DpopClient dpop, string assertionAudience, string proofUrl, DateTimeOffset now, CancellationToken ct)
    {
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = dpop.AgentId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = dpop.ClientAssertion(assertionAudience, now),
            ["scope"] = "question:create answer:create",
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
        request.Headers.TryAddWithoutValidation("DPoP", dpop.Proof("POST", proofUrl, now));
        return await ReadAsync(await client.SendAsync(request, ct), ct);
    }

    /// <summary>The access token a token request's answer carries, or null.</summary>
    internal static string? AccessToken(OriginReply reply) =>
        reply.Status == 200 && reply.Body.Length > 0 ? JsonNode.Parse(reply.Body)?["access_token"]?.GetValue<string>() : null;

    /// <summary>One resource request with a DPoP-bound token, retried once on the nonce challenge the way a client must.</summary>
    internal static async Task<OriginReply> GetAsync(
        HttpClient client, string path, DpopClient dpop, string htu, string token, DateTimeOffset now, CancellationToken ct)
    {
        string? nonce = null;
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
            request.Headers.Add("DPoP", dpop.Proof("GET", htu, now, token, nonce));
            using var response = await client.SendAsync(request, ct);
            if (attempt == 0 && response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.TryGetValues("DPoP-Nonce", out var values))
            {
                nonce = values.First();
                continue;
            }

            return new OriginReply(
                (int)response.StatusCode,
                await response.Content.ReadAsStringAsync(ct),
                response.Content.Headers.ContentType?.MediaType,
                [.. response.Headers.Select(h => h.Key).Concat(response.Content.Headers.Select(h => h.Key))]);
        }
    }

    /// <summary>The enrollment nonce, fetched at <paramref name="prefix"/> (a path base a test host puts the Forum under, or empty).</summary>
    internal static async Task<string> NonceAsync(HttpClient client, string prefix, CancellationToken ct)
    {
        using var response = await client.GetAsync(prefix + EnrollProofProbe.NonceRoute, ct);
        return response.StatusCode == HttpStatusCode.OK
            ? JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!["nonce"]!.GetValue<string>()
            : EnrollProofProbe.Placeholder;
    }

    /// <summary>One enrollment whose proof is made for <paramref name="audience"/>, a literal, and posted at <paramref name="prefix"/> + the route.</summary>
    internal static async Task<OriginReply> EnrolAsync(
        HttpClient client, ForumAgent agent, string audience, DateTimeOffset now, CancellationToken ct, string prefix = "")
    {
        var proof = EnrollProofProbe.Proof(agent, audience, await NonceAsync(client, prefix, ct), now);
        var body = new JsonObject
        {
            ["agent_id"] = agent.AgentId,
            ["kid"] = agent.Kid,
            ["alg"] = "ES256",
            ["public_key"] = agent.PublicKeyBase64,
            ["proof"] = proof,
        };
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await ReadAsync(await client.PostAsync(prefix + EnrollProofProbe.Enrol, content, ct), ct);
    }

    /// <summary>
    /// An agent enrolled through the use case and not the route, so a probe of the token endpoint or a resource route does not depend on the enrollment route's own reading of the name (a control that
    /// failed there would hide the verdict of the row under test).
    /// </summary>
    internal static async Task<(ForumAgent Agent, DpopClient Dpop)> EnrolledAsync(ForumFixture forum, string stem, CancellationToken ct)
    {
        var agent = EnrollProofProbe.NewAgent(stem);
        await forum.EnrollPastTheRouteAsync(agent.AgentId, agent.Kid, Convert.FromBase64String(agent.PublicKeyBase64), ct);
        return (agent, DpopClient.For(agent, agent.AssertionKey));
    }

    /// <summary>
    /// One request written raw to a Kestrel socket (so a <c>Host</c> a client library would not send reaches the Forum, or Kestrel's own parser, as written), answered as a <see cref="OriginReply"/>. The request ends
    /// the connection (<c>Connection: close</c>), so the answer is read to its end.
    /// </summary>
    internal static async Task<OriginReply> RawAsync(Uri address, byte[] request, CancellationToken ct)
    {
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(address.Host, address.Port, ct);
        await using var stream = client.GetStream();
        await stream.WriteAsync(request, ct);
        using var answer = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await stream.CopyToAsync(answer, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
        }

        var text = Encoding.Latin1.GetString(answer.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var lines = head.Split("\r\n");
        var status = lines.Length > 0 && lines[0].Split(' ') is { Length: >= 2 } parts && int.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var code) ? code : 0;
        var names = lines.Skip(1).Select(l => l.Split(':')[0]).ToList();
        var contentType = lines.Skip(1).FirstOrDefault(l => l.StartsWith("Content-Type:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim().Split(';')[0];
        var body = split < 0 ? string.Empty : text[(split + 4)..];
        if (body.IndexOf('{', StringComparison.Ordinal) is var open and >= 0 && body.LastIndexOf('}') is var close && close > open) body = body[open..(close + 1)]; // chunk framing, if the answer is chunked
        if (status == 0) body = "(no status line) " + text;
        return new OriginReply(status, body, contentType, names);
    }

    /// <summary>The claims of a JWT, read without verifying it.</summary>
    internal static JsonObject Claims(string jwt) => (JsonObject)JsonNode.Parse(Base64Url.DecodeFromChars(jwt.Split('.')[1]))!;

    /// <summary>A JWT's header, read without verifying it.</summary>
    internal static JsonObject Header(string jwt) => (JsonObject)JsonNode.Parse(Base64Url.DecodeFromChars(jwt.Split('.')[0]))!;

    /// <summary>A token as the issuer signs one: the same key and header as a real token, with <paramref name="edit"/> applied to its claims.</summary>
    internal static string Resign(string jwt, string issuerKeyPem, Action<JsonObject> edit)
    {
        var header = Header(jwt);
        var claims = Claims(jwt);
        edit(claims);
        using var key = ECDsa.Create();
        key.ImportFromPem(issuerKeyPem);
        var input = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(header.ToJsonString())) + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return input + "." + Base64Url.EncodeToString(signature);
    }

    /// <summary>The value of <c>provenance.reader_contract</c> a served post carries.</summary>
    internal static string? ReaderContractOf(OriginReply post) => JsonNode.Parse(post.Body)?["provenance"]?["reader_contract"]?.GetValue<string>();

    internal static void Expect(List<string> failures, bool condition, string message)
    {
        if (!condition) failures.Add(message);
    }

    internal static void NoFailures(List<string> failures, string what) =>
        Assert.True(failures.Count == 0, what + Environment.NewLine + string.Join(Environment.NewLine, failures));
}
