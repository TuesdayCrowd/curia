using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.OperatorTool;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// LRV31 (errata G68, ruling 36): a row at <c>seq</c> 0, which no forward read returns, is honoured by nobody. Since PR N's ruling 72 the store counts and returns only the rows at <c>seq &gt; 0</c>,
/// so no read sees it: it is inert, not detected. LRV36 are its siblings: the same at <c>seq</c> -1 (which answered a read fault before ruling 72) and two posts by the victim (a skew
/// the store kept permanent showed on the second post too). The row is planted as the administrator, because the
/// application role cannot plant it once <c>db/0006</c> is applied (PR S, errata G69). The victim's token request, an issued token's next request, a post, the key set and the audit
/// all behave as if the row were absent; the audit's silence is asserted as the control that the detector of a shadow row is PR S's start-up check and not the audit (the row is invisible to the whole log).
/// On the control build (<c>b348f6a9</c>, before the seam) the token request answers <c>401 invalid_client</c>, and no detector sees the row: the first form is the harm.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ShadowRowTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task LRV31_ARowAtSeqZeroIsHonouredByNobody() => await ShadowAsync(0, 1);

    /// <summary>LRV36: a row at seq -1, which the by-aggregate read answered as a fault before ruling 72.</summary>
    [Fact]
    public async Task LRV36_ARowAtSeqMinusOneIsHonouredByNobody() => await ShadowAsync(-1, 1);

    /// <summary>LRV36: a permanent skew shows on the victim's second post too.</summary>
    [Fact]
    public async Task LRV36_TheVictimsSecondPostLandsToo() => await ShadowAsync(-2, 2);

    private async Task ShadowAsync(long seq, int posts)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = ForumAgent.Create(KeyActRequests.Unique("lrv31"), "lrv31-" + Suffix());
        var (dpop, token) = await victim.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var board = "board-" + Suffix();

        var statement = $"{{\"agent_id\":\"{victim.AgentId}\",\"kid\":\"{victim.Kid}\",\"prev\":1,\"typ\":\"curia.key-retirement/1\"}}";
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["agent_id"] = victim.AgentId,
            ["authorization"] = "this-is-not-a-signature-qq7",
            ["kid"] = victim.Kid,
            ["prev"] = 1,
            ["statement"] = statement,
        });
        await using (var admin = new NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO events (seq, event_id, event_type, aggregate_id, actor_id, payload, server_ts) OVERRIDING SYSTEM VALUE VALUES (@seq, @id, 'agent.key-retired', @aggregate, 'operator:ops', @payload::jsonb, @ts);",
                admin);
            insert.Parameters.AddWithValue("seq", seq);
            insert.Parameters.AddWithValue("id", "shadow-" + Suffix());
            insert.Parameters.AddWithValue("aggregate", victim.AgentId);
            insert.Parameters.AddWithValue("payload", payload);
            insert.Parameters.AddWithValue("ts", forum.Now);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
        }

        var (tokenStatus, tokenBody) = await dpop.RequestTokenAsync(client, KeyActRequests.TokenEndpoint, forum.Now, victim.AgentId, ct);
        var askedStatuses = new List<int>();
        for (var i = 0; i < posts; i++)
        {
            using var asked = await dpop.PostAsync(client, "http://localhost/v1/posts", token, victim.SignQuestion(board, $"After the shadow row {i}. " + Suffix(), $"Shadow {i} " + Suffix(), forum.Now), forum.Now, ct);
            askedStatuses.Add((int)asked.StatusCode);
        }

        using var keySetResponse = await client.GetAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(victim.AgentId)}", UriKind.Relative), ct);
        var keySetText = await keySetResponse.Content.ReadAsStringAsync(ct);
        var keySet = keySetResponse.StatusCode == HttpStatusCode.OK ? JsonDocument.Parse(keySetText).RootElement : default;
        var status = keySetResponse.StatusCode == HttpStatusCode.OK ? keySet.GetProperty("keys")[0].GetProperty("curia_status").GetString() : "n/a";

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var audit = await OperatorCommands.RunAsync(
            ["lifecycle-audit"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, ct,
            readLogSigningKeyPem: null, readDeclarationSigningKeyPem: null);

        var tokenWords = tokenStatus == HttpStatusCode.OK ? "200" : $"{(int)tokenStatus} {ErrorOf(tokenBody)}";
        WholeText.Same(
            $"token 200; issued-token ask {string.Join(", ", Enumerable.Repeat(201, posts))}; key set 200 key active; audit exit 0",
            $"token {tokenWords}; issued-token ask {string.Join(", ", askedStatuses)}; key set {(int)keySetResponse.StatusCode} key {status}; audit exit {audit.ToString(CultureInfo.InvariantCulture)}");
    }

    private static string ErrorOf(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e) ? e.GetString()! : "?"; }
        catch (JsonException) { return "?"; }
    }
}
