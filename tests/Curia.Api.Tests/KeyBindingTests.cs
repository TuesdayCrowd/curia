using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G16 at the surface: every key the Forum honours is a key the event log binds (R4.35), and
/// the key set names, for each key it publishes, the leaf that binds it (R6.54). Before G16 the
/// ingest path, the token endpoint and the key set read the key store alone, so a key the store held
/// and no enrollment had bound -- a row added through errata G14's hole, which R4.19 forbids deleting
/// -- signed posts and minted tokens as the identity it was filed under, and was served to every
/// reader as that identity's key.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class KeyBindingTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>What the token endpoint answers a stored key the log does not bind to the agent named.</summary>
    private const string NotBoundByTheLog =
        "{\"error\":\"invalid_client\",\"error_description\":\"The event log binds no such key to that agent\",\"detail\":\"curia/keys/not-bound-by-the-log\"}";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static async Task<JsonElement> KeySetAsync(HttpClient client, string agentId, CancellationToken ct) =>
        await client.GetFromJsonAsync<JsonElement>($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", ct);

    /// <summary>
    /// R4.35, errata G16's finding. The provisioning role writes a second key row for an enrolled
    /// identity, as errata G14's hole wrote them and as any store written before G14 may still hold
    /// them. Its holder asks for the identity's token with it, and the identity's own token submits a
    /// question signed under it. Before G16 the first was issued and the second accepted; each is now
    /// refused by name, and the key set does not publish the row. The damage is asserted first, so a
    /// regression's first red line says what was done in the identity's name. The identity's own key,
    /// which its enrollment bound, is the positive control, on both paths.
    /// </summary>
    [Fact]
    public async Task R4_35_AKeyTheStoreHoldsAndTheLogDoesNotBindSignsNothingAndMintsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();
        var owner = ForumAgent.Create($"https://agents.example/bound-{suffix}", $"bound-{suffix}");
        var (dpop, token) = await owner.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var hole = ForumAgent.Create(owner.AgentId, $"hole-{suffix}");
        await using (var admin = new NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) " +
                "VALUES (@kid, @agent, 'ES256', @key, @from, NULL);",
                admin);
            insert.Parameters.AddWithValue("kid", hole.Kid);
            insert.Parameters.AddWithValue("agent", owner.AgentId);
            insert.Parameters.AddWithValue("key", hole.AssertionKey.ExportSubjectPublicKeyInfo());
            insert.Parameters.AddWithValue("from", forum.Now);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
        }

        var (tokenStatus, tokenBody) = await DpopClient.For(hole, hole.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, owner.AgentId, ct);
        using var forged = await dpop.PostAsync(
            client, PostsUrl, token,
            hole.SignQuestion("board-" + suffix, "Signed under a key the log never bound.", "Hole " + suffix, forum.Now),
            forum.Now, ct);
        var forgedBody = await forged.Content.ReadAsStringAsync(ct);
        var keySet = (await KeySetAsync(client, owner.AgentId, ct)).GetRawText();

        Assert.True(
            tokenStatus != HttpStatusCode.OK && forged.StatusCode != HttpStatusCode.Created && !keySet.Contains(hole.Kid, StringComparison.Ordinal),
            $"a key the log never bound acted as {owner.AgentId}: token request {(int)tokenStatus}, question {(int)forged.StatusCode} {forgedBody}, key set {keySet}");
        Assert.Equal($"401 {NotBoundByTheLog}", $"{(int)tokenStatus} {tokenBody}");
        Assert.Equal(
            "401 curia/keys/not-bound-by-the-log",
            $"{(int)forged.StatusCode} {JsonNode.Parse(forgedBody)!["type"]!.GetValue<string>()}");

        using var own = await dpop.PostAsync(
            client, PostsUrl, token,
            owner.SignQuestion("board-" + suffix, "Signed under the key the log bound.", "Bound " + suffix, forum.Now),
            forum.Now, ct);
        Assert.True(own.StatusCode == HttpStatusCode.Created, "the owner's own key was refused too, so the refusals above prove nothing: " + await own.Content.ReadAsStringAsync(ct));
        Assert.NotNull(await DpopClient.For(owner, owner.AssertionKey).GetTokenAsync(client, TokenEndpoint, forum.Now, ct));
    }

    /// <summary>
    /// R6.54's Forum half: each key the key set publishes names the leaf that binds it, and that leaf,
    /// fetched from the log, is the identity's <c>agent.key-bound</c> entry carrying exactly the key
    /// published beside it. The comparison drops only the key set's own <c>curia_</c> members, which
    /// the log does not carry.
    /// </summary>
    [Fact]
    public async Task R6_54_EachPublishedKeyNamesTheLeafThatBindsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();
        var agent = ForumAgent.Create($"https://agents.example/positioned-{suffix}", $"positioned-{suffix}");
        using (var enrolled = await agent.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var served = Assert.Single((await KeySetAsync(client, agent.AgentId, ct)).GetProperty("keys").EnumerateArray());
        Assert.True(
            served.TryGetProperty("curia_log_index", out var index) && index.ValueKind == JsonValueKind.Number,
            "the key set names no leaf for the key: " + served.GetRawText());

        var entry = (await client.GetFromJsonAsync<JsonElement>($"/v1/log/entries/{index.GetInt64()}", ct)).GetProperty("entry");
        var payload = entry.GetProperty("payload");

        var published = JsonNode.Parse(served.GetRawText())!.AsObject();
        foreach (var name in published.Select(m => m.Key).Where(k => k.StartsWith("curia_", StringComparison.Ordinal)).ToList())
            published.Remove(name);

        var carried = payload.TryGetProperty("jwk", out var jwk) ? JsonNode.Parse(jwk.GetRawText()) : null;

        Assert.Equal(
            $"agent.key-bound {agent.AgentId} {agent.AgentId} {agent.Kid} jwk-equal=True",
            $"{entry.GetProperty("event_type").GetString()} {entry.GetProperty("aggregate_id").GetString()} "
            + $"{payload.GetProperty("agent_id").GetString()} {payload.GetProperty("kid").GetString()} "
            + $"jwk-equal={(carried is null ? "absent" : JsonNode.DeepEquals(published, carried).ToString())}");
    }

    /// <summary>
    /// R4.35's other binding: an identity enrolled before errata G16 has an <c>agent.enrolled</c> that
    /// names its <c>kid</c> and no key-binding entry, and the log binds that <c>kid</c> alone. Its key
    /// still mints its token and signs its posts, and the key set publishes it naming the enrollment's
    /// leaf, where a reader finds a <c>kid</c> and no key (R6.54's "could not be checked").
    /// </summary>
    [Fact]
    public async Task R4_35_AnIdentityEnrolledBeforeKeyBindingIsHonouredByItsKidAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Suffix();
        var agent = ForumAgent.Create($"https://agents.example/before-g16-{suffix}", $"before-g16-{suffix}");
        await forum.EnrollBeforeKeyBindingAsync(agent.AgentId, agent.Kid, agent.AssertionKey.ExportSubjectPublicKeyInfo(), ct);

        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var token = await dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct);
        using var asked = await dpop.PostAsync(
            client, PostsUrl, token,
            agent.SignQuestion("board-" + suffix, "Enrolled before the log carried keys.", "Before " + suffix, forum.Now),
            forum.Now, ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, "an identity enrolled before G16 lost its key: " + await asked.Content.ReadAsStringAsync(ct));

        var served = Assert.Single((await KeySetAsync(client, agent.AgentId, ct)).GetProperty("keys").EnumerateArray());
        Assert.True(
            served.TryGetProperty("curia_log_index", out var index) && index.ValueKind == JsonValueKind.Number,
            "the key set names no leaf for the key: " + served.GetRawText());
        var entry = (await client.GetFromJsonAsync<JsonElement>($"/v1/log/entries/{index.GetInt64()}", ct)).GetProperty("entry");

        Assert.Equal(
            $"agent.enrolled {agent.Kid} jwk=absent",
            $"{entry.GetProperty("event_type").GetString()} {entry.GetProperty("payload").GetProperty("kid").GetString()} "
            + $"jwk={(entry.GetProperty("payload").TryGetProperty("jwk", out _) ? "present" : "absent")}");
    }

    /// <summary>
    /// The key set is anonymous, and since G16 it reads the event log as well as the key store. Every
    /// agent parameter is answered, and none with a 500: text Postgres <c>text</c> cannot hold, a
    /// noncharacter, an ill-formed UTF-8 sequence the host decodes, and an identifier longer than any
    /// the Forum stores. Each names an agent the store holds nothing for, so each is a 404.
    /// </summary>
    [Theory]
    [InlineData("%00")]
    [InlineData("https%3A%2F%2Fagents.example%2Fnul%00")]
    [InlineData("%EF%BF%BE")]
    [InlineData("%ED%A0%80")]
    [InlineData("long")]
    public async Task R4_35_TheKeySetAnswersEveryAgentWithoutA500(string encoded)
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = encoded == "long"
            ? Uri.EscapeDataString("https://agents.example/" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(1500)))
            : encoded;

        using var response = await forum.Client.GetAsync(new Uri($"/v1/jwks?agent={agent}", UriKind.Relative), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        string type;
        try
        {
            type = JsonNode.Parse(body)?["type"]?.GetValue<string>() ?? "(no type)";
        }
        catch (JsonException)
        {
            type = body.Split('\n')[0][..Math.Min(body.Split('\n')[0].Length, 120)];
        }

        Assert.Equal("404 curia/keys/unknown-agent", $"{(int)response.StatusCode} {type}");
    }
}
