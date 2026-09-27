using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Domain.Acta;
using Curia.OperatorTool;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R4.33 (errata G15), the namespace clause, at the damage the review printed. On a Forum whose log
/// had never published a key, one anonymous enrollment under <c>log:keys</c> appended an agent's
/// first event where the operator's tool appends the Acta's first key. From then on
/// <c>sign-head</c> failed on a concurrency conflict, and since the log cannot drop the event, it
/// failed forever. <c>log:heads</c> did the same before the first head.
///
/// <para>Its own class, so its own <see cref="ForumFixture"/> and a database no other fact has
/// written to: the precondition that neither stream holds an event is what makes the namespace
/// clause the only thing that can refuse. With an event already there, the stream clause would
/// refuse as well, and forgetting <c>log:</c> would change nothing this fact can see.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ActaNamespaceTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static async Task<long> CountAsync(NpgsqlConnection admin, string column, string value, CancellationToken ct)
    {
        await using var command = column == "kid"
            ? new NpgsqlCommand("SELECT count(*) FROM agent_keys WHERE kid = @value;", admin)
            : new NpgsqlCommand("SELECT count(*) FROM events WHERE aggregate_id = @value;", admin);
        command.Parameters.AddWithValue("value", value);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task<string> AnswerAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return $"{(int)response.StatusCode} enrolled";
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return $"{(int)response.StatusCode} {problem.GetProperty("type").GetString()}";
    }

    /// <summary>
    /// Enrollments under the Acta's two streams are refused before anything is written, and the
    /// operator can still sign a head. The head is asserted before the refusals, so if either
    /// enrollment gets through, the first red line is the damage: <c>sign-head</c> refused.
    /// </summary>
    [Fact]
    public async Task R4_33_AnEnrollmentNamingTheActasStreamsIsRefusedAndAHeadCanStillBeSigned()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var keysKid = "acta-keys-" + suffix;
        var headsKid = "acta-heads-" + suffix;

        await using (var admin = new NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(ct);
            Assert.Equal(
                "events under log:keys 0, under log:heads 0",
                $"events under log:keys {await CountAsync(admin, "aggregate", LogEntries.KeysAggregate, ct)}, under log:heads {await CountAsync(admin, "aggregate", LogEntries.HeadsAggregate, ct)}");
        }

        using var keys = await ForumAgent.Create(LogEntries.KeysAggregate, keysKid).EnrollAsync(client, ct);
        using var heads = await ForumAgent.Create(LogEntries.HeadsAggregate, headsKid).EnrollAsync(client, ct);
        var keysAnswer = await AnswerAsync(keys, ct);
        var headsAnswer = await AnswerAsync(heads, ct);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(
            ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, stdout, stderr, ct,
            logSigningKeyPem: LogSigningKey.GeneratePem());
        Assert.True(exit == ExitCode.Ok, $"sign-head exited {exit}: {stderr.ToString().Trim()}");
        Assert.Contains("signed head", stdout.ToString(), StringComparison.Ordinal);

        Assert.Equal("409 curia/enroll/identifier-reserved", keysAnswer);
        Assert.Equal("409 curia/enroll/identifier-reserved", headsAnswer);

        await using (var admin = new NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(ct);
            Assert.Equal(
                "key rows 0 and 0",
                $"key rows {await CountAsync(admin, "kid", keysKid, ct)} and {await CountAsync(admin, "kid", headsKid, ct)}");
        }
    }
}
