using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What the enrollment probes (errata G79, R4.46 and R4.47) share: the nonce the Forum serves, a proof built by the test project's own builder (<see cref="EnrollProofKit"/>, written from the errata's
/// sentence, never by the client or the validator), a request posted as written, and what a refusal said. <b>The nonce route does not exist at Task 2</b>, so <see cref="NonceAsync"/> answers a
/// placeholder when the route answers 404 and a probe's own assertion is what is red; once the route exists the same call fetches the Forum's value.
/// </summary>
internal static class EnrollProofProbe
{
    internal const string Enrol = "/v1/agents";
    internal const string NonceRoute = "/v1/enroll/nonce";
    internal const string Placeholder = "no-enroll-nonce-route-yet";

    /// <summary>The refusal types of R4.46, by name.</summary>
    internal static string Type(string slug) => "curia/enroll/" + slug;

    /// <summary>The enrollment route's absolute URL as a client dialling <paramref name="client"/> reaches it: R4.46's <c>aud</c>.</summary>
    internal static string Audience(HttpClient client) => new Uri(client.BaseAddress!, Enrol).ToString();

    internal sealed record Answer(HttpStatusCode Status, string Body)
    {
        internal JsonNode? Json => JsonNode.Parse(Body);

        internal string? Type => Json?["type"]?.GetValue<string>();

        internal string? Detail => Json?["detail"]?.GetValue<string>();

        internal string Says => $"{(int)Status} {Body}";
    }

    internal static async Task<Answer> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        using (response) return new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The Forum's nonce, or the placeholder while the route does not exist.</summary>
    internal static async Task<string> NonceAsync(HttpClient client, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri(NonceRoute, UriKind.Relative), ct);
        if (response.StatusCode != HttpStatusCode.OK) return Placeholder;
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!["nonce"]!.GetValue<string>();
    }

    /// <summary>
    /// A proof for <paramref name="agent"/>'s request, signed by <paramref name="signer"/> (the agent's own key unless a probe says another). Every member is the request's unless an argument names another:
    /// <paramref name="agentId"/>, <paramref name="kid"/> (the header's), <paramref name="audience"/>, <paramref name="nonce"/>, <paramref name="jti"/>, <paramref name="iat"/>, <paramref name="publicKey"/> (whose thumbprint the claim carries).
    /// </summary>
    internal static string Proof(
        ForumAgent agent, string audience, string nonce, DateTimeOffset iat, ECDsa? signer = null, string? agentId = null, string? kid = null,
        string? jti = null, byte[]? publicKey = null, Action<JsonObject>? header = null, Action<JsonObject>? payload = null) =>
        EnrollProofKit.Build(
            "ES256", agentId ?? agent.AgentId, kid ?? agent.Kid, publicKey ?? Convert.FromBase64String(agent.PublicKeyBase64), audience, iat, nonce, jti ?? EnrollProofKit.NewJti(),
            EnrollProofKit.SignWith(signer ?? agent.AssertionKey), header, payload);

    /// <summary>The request of <paramref name="agent"/>, as written, with <paramref name="proof"/> as a <c>proof</c> member (none when null). <paramref name="url"/> defaults to the route.</summary>
    internal static async Task<Answer> PostAsync(
        HttpClient client, ForumAgent agent, string? proof, CancellationToken ct, string? agentId = null, string? kid = null, string? alg = null, string? publicKey = null, string? url = null)
    {
        var body = new JsonObject
        {
            ["agent_id"] = agentId ?? agent.AgentId,
            ["kid"] = kid ?? agent.Kid,
            ["alg"] = alg ?? "ES256",
            ["public_key"] = publicKey ?? agent.PublicKeyBase64,
        };
        if (proof is not null) body["proof"] = proof;
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await ReadAsync(await client.PostAsync(new Uri(url ?? Enrol, UriKind.RelativeOrAbsolute), content, ct), ct);
    }

    /// <summary>The request of <paramref name="agent"/> with a proof its own key signed for the Forum's current nonce: the control every refusal is paired with.</summary>
    internal static async Task<Answer> PostValidAsync(HttpClient client, ForumAgent agent, DateTimeOffset now, CancellationToken ct)
    {
        var proof = Proof(agent, Audience(client), await NonceAsync(client, ct), now);
        return await PostAsync(client, agent, proof, ct);
    }

    internal static ForumAgent NewAgent(string stem)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        return ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
    }

    /// <summary>The rows of <c>events</c> (all of them), read as the provisioning role.</summary>
    internal static async Task<long> EventsAsync(ForumFixture forum, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The key rows the store holds for <paramref name="agentId"/>, read as the provisioning role.</summary>
    internal static async Task<long> KeyRowsAsync(ForumFixture forum, string agentId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM agent_keys WHERE agent_id = @v;", connection);
        count.Parameters.AddWithValue("v", agentId);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }

    internal static void AssertRefused(Answer answer, string slug, string? detail = null)
    {
        Assert.True(answer.Status == HttpStatusCode.BadRequest && answer.Type == Type(slug), $"expected 400 {Type(slug)}; the Forum answered {answer.Says}");
        if (detail is not null) Assert.Equal(detail, answer.Detail);
    }

    /// <summary>A key set read as served, or null when the Forum has none for the identifier.</summary>
    internal static async Task<JsonElement?> JwksAsync(HttpClient client, string agentId, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", UriKind.Relative), ct);
        return response.StatusCode == HttpStatusCode.OK ? JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone() : null;
    }
}
