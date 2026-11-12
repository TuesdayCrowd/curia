using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G69, R11.6 (add.), through the Forum and as the application role (the fixture's host connects as it). SEQ9 is the attack: a holder of the Forum's own credential
/// plants a forged lifecycle row under a victim's aggregate at <c>seq</c> -3 and at 0, and each insert is refused <c>42501</c> and the victim is as with no attempt (a fresh
/// token request, an issued token's next request, the served key set, a bystander). SEQ7 is the start-up log: a Forum whose role can name <c>seq</c> says so once at Error
/// and starts; one whose role cannot says nothing.
///
/// <para>Every fact puts the role's <c>INSERT</c> grant back as it found it, and deletes what it planted as the owner.</para>
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every statement is a constant or built from the fixture's own generated names.")]
[Collection("role-forum")]
public sealed class EventsPositionApiTests(RoleForumFixture forum) : IClassFixture<RoleForumFixture>
{
    private const int EventsSeqWritable = 7008;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// SEQ9: the two forms PR N's Task 0 and this PR's Task 0 measured. A forged <c>agent.key-retired</c> at <c>seq</c> -3 answers every read of the victim's aggregate with a
    /// <c>500</c>; at <c>seq</c> 0 it is invisible to every forward read and makes the victim's key set unreadable (a fresh token request <c>401 invalid_client</c>, an issued
    /// token's next request <c>503 curia/log/unreadable</c>). With the grant, neither insert is accepted and the victim is unaffected.
    /// </summary>
    [Fact]
    public async Task R11_6_SEQ9_AForgedRowCannotBePlantedThroughTheForumsOwnRoleAndTheVictimIsUnaffected()
    {
        var victim = await AcceptedAnswerScene.EnrolAsync(forum, "victim", Ct);
        var bystander = await AcceptedAnswerScene.EnrolAsync(forum, "bystander", Ct);
        var problems = new List<string>();
        try
        {
            foreach (var (label, seq, eventId) in new[] { ("-3", -3L, "seq9-neg"), ("0", 0L, "seq9-zero") })
            {
                var issued = await victim.Dpop.GetTokenAsync(forum.Client, Probe.TokenEndpoint, forum.Now, Ct);

                var refusal = await PlantAsync(victim.Agent, seq, eventId);
                if (refusal is not null) problems.Add($"seq {label}: {refusal}");

                var fresh = await victim.Dpop.RequestTokenAsync(forum.Client, Probe.TokenEndpoint, forum.Now, victim.Agent.AgentId, Ct);
                if ((int)fresh.Status != 200) problems.Add($"seq {label}: the victim's fresh token request: expected 200, actual {(int)fresh.Status} {Short(fresh.Body)}");

                var budget = await FaultActs.GetAsync(forum, victim, "/v1/budget", Ct, issued);
                if (budget.Status != 200) problems.Add($"seq {label}: the victim's issued token's next request: expected 200, actual {budget.Says}");

                using var keys = new HttpRequestMessage(HttpMethod.Get, "/v1/jwks?agent=" + Uri.EscapeDataString(victim.Agent.AgentId));
                var served = await Reply.SendAsync(forum.Client, keys, Ct);
                if (served.Status != 200) problems.Add($"seq {label}: the victim's served key set: expected 200, actual {served.Says}");

                var other = await FaultActs.GetAsync(forum, bystander, "/v1/budget", Ct);
                if (other.Status != 200) problems.Add($"seq {label}: a bystander's budget: expected 200, actual {other.Says}");

                await forum.RemovePlantedRowsAsync("seq9-", Ct);
            }
        }
        finally
        {
            await forum.RemovePlantedRowsAsync("seq9-", CancellationToken.None);
        }

        Assert.True(problems.Count == 0, $"Expected: each insert refused 42501, the victim and a bystander as with no attempt{Environment.NewLine}Actual:   {string.Join(" | ", problems)}");
    }

    /// <summary>SEQ7, the loud arm: the role holds <c>INSERT</c> on the whole table, so the Forum says so once, at Error, without a connection string or the role's name, and starts and serves.</summary>
    [Fact]
    public async Task R11_6_SEQ7_AForumWhoseRoleCanNameSeqSaysSoOnceAndStarts()
    {
        var found = await forum.HoldsTableLevelInsertOnEventsAsync(Ct);
        try
        {
            await forum.RestoreInsertOnEventsAsync(tableLevel: true, Ct);
            await using var captured = new CapturedForum(forum);

            var said = captured.Log.Single(0, EventsSeqWritable);

            var problems = new List<string>();
            if (said.Level != LogLevel.Error) problems.Add($"level: expected Error, actual {said.Level}");
            if (!string.Equals(said.Name, "EventsSeqWritable", StringComparison.Ordinal)) problems.Add($"name: expected EventsSeqWritable, actual {said.Name}");
            if (said.Message.Contains(forum.RoleName, StringComparison.Ordinal)) problems.Add("the message names the role");
            if (said.Message.Contains("Host=", StringComparison.OrdinalIgnoreCase) || said.Message.Contains("Password", StringComparison.OrdinalIgnoreCase) || said.Message.Contains(forum.RolePassword, StringComparison.Ordinal))
                problems.Add("the message carries a connection string");
            if (!said.Message.Contains("db/0006", StringComparison.Ordinal)) problems.Add("the message names no remedy (db/0006)");
            using var health = await captured.Client.GetAsync(new Uri("/health", UriKind.Relative), Ct);
            if ((int)health.StatusCode != 200) problems.Add($"the Forum did not serve: {(int)health.StatusCode}");
            Assert.True(problems.Count == 0, $"Expected: one Error 7008 naming the fact and db/0006, no secret; the Forum serving{Environment.NewLine}Actual:   {string.Join(" | ", problems)}");
        }
        finally
        {
            await forum.RestoreInsertOnEventsAsync(found, CancellationToken.None);
        }
    }

    /// <summary>SEQ7, the quiet arm: the role holds the column grant of <c>db/0006</c> and not <c>seq</c>, so the Forum starts and says nothing of it.</summary>
    [Fact]
    public async Task R11_6_SEQ7_AForumWhoseRoleCannotNameSeqSaysNothing()
    {
        var found = await forum.HoldsTableLevelInsertOnEventsAsync(Ct);
        try
        {
            await forum.RestoreInsertOnEventsAsync(tableLevel: false, Ct);
            await using var captured = new CapturedForum(forum);

            using var health = await captured.Client.GetAsync(new Uri("/health", UriKind.Relative), Ct);
            Assert.Equal(200, (int)health.StatusCode);
            Assert.DoesNotContain(captured.Log.All, e => e.EventId == EventsSeqWritable);
        }
        finally
        {
            await forum.RestoreInsertOnEventsAsync(found, CancellationToken.None);
        }
    }

    /// <summary>Plants a forged <c>agent.key-retired</c> under the agent's aggregate at <paramref name="seq"/>, as the application role. Null when the insert was refused <c>42501</c>.</summary>
    private async Task<string?> PlantAsync(ForumAgent agent, long seq, string eventId)
    {
        var statement = JsonSerializer.Serialize(new SortedDictionary<string, object> { ["agent_id"] = agent.AgentId, ["kid"] = agent.Kid, ["prev"] = 1, ["typ"] = "curia.key-retirement/1" });
        var payload = JsonSerializer.Serialize(new SortedDictionary<string, object>
        {
            ["agent_id"] = agent.AgentId, ["authorization"] = "FORGED-NO-VALID-SIGNATURE", ["kid"] = agent.Kid, ["prev"] = 1, ["statement"] = statement,
        });
        await using var connection = new NpgsqlConnection(forum.RoleConnectionString);
        await connection.OpenAsync(Ct);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO events (seq, event_id, event_type, aggregate_id, actor_id, payload, server_ts) OVERRIDING SYSTEM VALUE " +
            $"VALUES ({seq.ToString(CultureInfo.InvariantCulture)}, @id, 'agent.key-retired', @agg, @agg, @payload::jsonb, now());",
            connection);
        insert.Parameters.AddWithValue("id", eventId);
        insert.Parameters.AddWithValue("agg", agent.AgentId);
        insert.Parameters.AddWithValue("payload", payload);
        try
        {
            await insert.ExecuteNonQueryAsync(Ct);
            return "the insert was accepted (expected 42501 permission denied for table events)";
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege && ex.MessageText.Contains("permission denied for table events", StringComparison.Ordinal))
        {
            return null;
        }
        catch (PostgresException ex)
        {
            return $"expected 42501 'permission denied for table events', actual {ex.SqlState} '{ex.MessageText}'";
        }
    }

    private static string Short(string body) => body.Length > 160 ? body[..160] + "..." : body;
}
