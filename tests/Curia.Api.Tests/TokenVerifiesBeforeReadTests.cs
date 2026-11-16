using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// VBR2 and VBR8 (register item 282, errata G72, R5.25): what the token endpoint reads of the log, and what it serves, once it verifies an assertion under the key the store holds before it reads the log.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class TokenVerifiesBeforeReadTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Base64Url(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// VBR2. One token request per row, the reads of the log the request made counted by the counter <c>RequestReadCounting.CountRequestReads</c> installs: a request whose assertion does not
    /// verify, whose claims are not accepted or whose <c>jti</c> is spent answers <c>401</c> <c>invalid_client</c> and reads <b>0</b> (R5.25). The five forged rows (another key under the victim's registered
    /// <c>kid</c>, the wrong <c>aud</c>, expired, a header that names the other algorithm, the second use of an assertion whose first use is served) read one before the fix. Two controls refuse at the key
    /// store and read 0 on both trees (a <c>kid</c> registered to another agent, an unknown <c>kid</c>); the valid row is served <c>200</c> and reads at least one (the counter's own liveness, which
    /// FVBR10 patches; the number is recorded to the test output and not pinned, because merging the binding and the standings reads, item 314, would lower it and be an improvement).
    /// </summary>
    [Theory]
    [InlineData("forged-signature")]
    [InlineData("wrong-aud")]
    [InlineData("expired")]
    [InlineData("header-alg")]
    [InlineData("second-use")]
    [InlineData("kid-of-another-agent")]
    [InlineData("unknown-kid")]
    [InlineData("valid")]
    public async Task R5_25_VBR2_ARequestThatDoesNotVerifyReadsNothingOfTheLog(string row)
    {
        var counter = new Counter();
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.CountRequestReads(counter)));
        using var client = host.CreateClient();

        var victim = await AcceptedAnswerScene.EnrolAsync(forum, "vbr2-" + row, Ct);
        var other = await AcceptedAnswerScene.EnrolAsync(forum, "vbr2-other-" + row, Ct);
        var endpoint = Probe.TokenEndpoint;
        var now = forum.Now;

        async Task<Reply> SendAsync(string assertion)
        {
            counter.Count = 0;
            return await DpopClient.RequestTokenReplyAsync(client, victim.Agent.AgentId, assertion, victim.Dpop.Proof("POST", endpoint, forum.Now), Ct);
        }

        Reply reply;
        switch (row)
        {
            case "forged-signature":
            {
                var forger = ForumAgent.Create(victim.Agent.AgentId, victim.Agent.Kid);
                reply = await SendAsync(DpopClient.For(forger, forger.AssertionKey).ClientAssertion(endpoint, now));
                break;
            }

            case "wrong-aud":
                reply = await SendAsync(victim.Dpop.ClientAssertion("http://localhost/not-the-token-endpoint", now));
                break;
            case "expired":
                reply = await SendAsync(victim.Dpop.ClientAssertion(endpoint, now.ToUnixTimeSeconds() - 120, now.ToUnixTimeSeconds() - 60));
                break;
            case "header-alg":
            {
                var parts = victim.Dpop.ClientAssertion(endpoint, now).Split('.');
                var header = Base64Url($"{{\"alg\":\"EdDSA\",\"kid\":\"{victim.Agent.Kid}\",\"typ\":\"JWT\"}}");
                reply = await SendAsync($"{header}.{parts[1]}.{parts[2]}");
                break;
            }

            case "second-use":
            {
                var assertion = victim.Dpop.ClientAssertion(endpoint, now);
                var firstUse = await SendAsync(assertion);
                Assert.True(firstUse.Status == 200, $"the first use of the assertion, a valid request: expected 200, actual {firstUse.Says}");
                reply = await SendAsync(assertion);
                break;
            }

            case "kid-of-another-agent":
            {
                var wrong = ForumAgent.Create(victim.Agent.AgentId, other.Agent.Kid);
                reply = await SendAsync(DpopClient.For(wrong, wrong.AssertionKey).ClientAssertion(endpoint, now));
                break;
            }

            case "unknown-kid":
            {
                var unknown = ForumAgent.Create(victim.Agent.AgentId, "unknown-kid-" + Guid.NewGuid().ToString("N")[..6]);
                reply = await SendAsync(DpopClient.For(unknown, unknown.AssertionKey).ClientAssertion(endpoint, now));
                break;
            }

            case "valid":
                reply = await SendAsync(victim.Dpop.ClientAssertion(endpoint, now));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(row), row, "no such row");
        }

        var reads = counter.Count;
        if (row == "valid")
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"VBR2 valid row reads={reads.ToString(CultureInfo.InvariantCulture)}");
            Assert.True(reply.Status == 200, $"a valid assertion: expected 200, actual {reply.Says}");
            Assert.True(reads >= 1, $"a valid request's reads of the log: expected at least 1 (the counter is live), actual {reads}");
            return;
        }

        Assert.True(reply.Status == 401 && reply.Error == "invalid_client", $"{row}: expected 401 invalid_client, actual {reply.Says}");
        Assert.True(reads == 0, $"{row}: reads of the log by a request that does not verify: expected 0, actual {reads}");
    }

    /// <summary>
    /// VBR8, a control green before and after the fix: a normal agent's token request is served as it was: <c>200</c> with <c>access_token</c>, <c>token_type</c>, <c>expires_in</c> and <c>scope</c>, and
    /// a token whose <c>tier</c>, <c>scope</c> and <c>curia_akid</c> (the assertion's <c>kid</c>, R5.22) claims are those of the agent's standing and key. And an agent with a key in the store and no
    /// <c>agent.enrolled</c> row in the log, asserting its own key under its own identifier, is refused <c>401</c> <c>invalid_client</c> (R5.20, R5.24).
    /// </summary>
    [Fact]
    public async Task R5_25_VBR8_ANormalAgentIsServedAsBeforeAndAnAgentTheLogNeverEnrolledIsRefused()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "vbr8", Ct);
        var served = await DpopClient.RequestTokenReplyAsync(
            forum.Client, actor.Agent.AgentId, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now), Ct);
        Assert.True(served.Status == 200, $"a normal agent's token request: expected 200, actual {served.Says}");

        using var body = JsonDocument.Parse(served.Body);
        var root = body.RootElement;
        var problems = new List<string>();
        foreach (var member in new[] { "access_token", "token_type", "expires_in", "scope" })
        {
            if (!root.TryGetProperty(member, out _)) problems.Add($"the reply has no {member}");
        }

        var token = root.GetProperty("access_token").GetString()!.Split('.');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(token[1].Replace('-', '+').Replace('_', '/').PadRight((token[1].Length + 3) / 4 * 4, '=')));
        var payload = claims.RootElement;
        if (payload.GetProperty("tier").GetString() != "T0") problems.Add($"the token's tier: expected T0, actual {payload.GetProperty("tier").GetString()}");
        if (payload.GetProperty("scope").GetString() != root.GetProperty("scope").GetString())
            problems.Add($"the token's scope {payload.GetProperty("scope").GetString()} is not the reply's {root.GetProperty("scope").GetString()}");
        if (payload.GetProperty("curia_akid").GetString() != actor.Agent.Kid) problems.Add($"the token's curia_akid: expected {actor.Agent.Kid}, actual {payload.GetProperty("curia_akid").GetString()}");
        if (payload.GetProperty("sub").GetString() != actor.Agent.AgentId) problems.Add($"the token's sub: expected {actor.Agent.AgentId}, actual {payload.GetProperty("sub").GetString()}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));

        // A key in the store and no enrolment in the log, asserting its own key under its own identifier.
        var orphan = ForumAgent.Create("https://agents.example/vbr8-orphan-" + Guid.NewGuid().ToString("N")[..8], "orphan-" + Guid.NewGuid().ToString("N")[..8]);
        await using (var admin = new NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var row = new NpgsqlCommand(
                "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) VALUES (@kid, @agent, 'ES256', @key, @from, NULL);", admin);
            row.Parameters.AddWithValue("kid", orphan.Kid);
            row.Parameters.AddWithValue("agent", orphan.AgentId);
            row.Parameters.AddWithValue("key", orphan.AssertionKey.ExportSubjectPublicKeyInfo());
            row.Parameters.AddWithValue("from", forum.Now);
            Assert.Equal(1, await row.ExecuteNonQueryAsync(Ct));
        }

        var refused = await DpopClient.RequestTokenReplyAsync(
            forum.Client, orphan.AgentId, DpopClient.For(orphan, orphan.AssertionKey).ClientAssertion(Probe.TokenEndpoint, forum.Now),
            DpopClient.For(orphan, orphan.AssertionKey).Proof("POST", Probe.TokenEndpoint, forum.Now), Ct);
        Assert.True(refused.Status == 401 && refused.Error == "invalid_client", $"an agent the log never enrolled, asserting its own key: expected 401 invalid_client, actual {refused.Says}");
        Assert.DoesNotContain("access_token", refused.Body, StringComparison.Ordinal);
    }
}
