using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Curia.Infrastructure.Lifecycle;
using Curia.OperatorTool;
using Npgsql;
using Xunit;
using Xunit.Sdk;

namespace Curia.Api.Tests;

/// <summary>
/// A row an administrator planted at <c>seq</c> 0 or below, and what it does to the writers (errata G68, PR N's ruling 72). Plants as the administrator (the application role
/// cannot once <c>db/0006</c> is applied, errata G69); the planted row is inert, not detected.
/// </summary>
internal static class ShadowRows
{
    private const string RetirementShapedPayload = "{\"agent_id\":\"x\",\"authorization\":\"this-is-not-a-signature-qq7\",\"kid\":\"x\",\"prev\":1,\"statement\":\"{}\"}";

    /// <summary>Plants one row on <paramref name="aggregate"/> at <paramref name="seq"/>, as the administrator. An agent's stream gets a forged retirement; any other aggregate gets a neutral event.</summary>
    internal static async Task PlantAsync(ForumFixture forum, string aggregate, long seq, bool onAgent, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO events (seq, event_id, event_type, aggregate_id, actor_id, payload, server_ts) OVERRIDING SYSTEM VALUE VALUES (@seq, @id, @type, @aggregate, 'operator:ops', @payload::jsonb, @ts);",
            admin);
        insert.Parameters.AddWithValue("seq", seq);
        insert.Parameters.AddWithValue("id", "shadow-" + Guid.NewGuid().ToString("N")[..12]);
        insert.Parameters.AddWithValue("type", onAgent ? "agent.key-retired" : "test.event");
        insert.Parameters.AddWithValue("aggregate", aggregate);
        insert.Parameters.AddWithValue("payload", onAgent ? RetirementShapedPayload : "{}");
        insert.Parameters.AddWithValue("ts", forum.Now);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
    }

    /// <summary>Removes the row at seq 0, as the administrator (the application role has no DELETE).</summary>
    internal static async Task RemoveSeqZeroAsync(ForumFixture forum, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var delete = new NpgsqlCommand("DELETE FROM events WHERE seq = 0;", admin);
        await delete.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The aggregate's rows in the log (the whole log's, for an empty aggregate), counted by raw SQL with <c>seq &gt; 0</c> written out here.</summary>
    internal static async Task<long> LogRowsAsync(ForumFixture forum, string aggregate, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events WHERE (@agg = '' OR aggregate_id = @agg) AND seq > 0;", admin);
        count.Parameters.AddWithValue("agg", aggregate);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>
/// LRV37: every writer that appends to an aggregate, driven through its real entry point against the Postgres-backed Forum, with a row planted at seq 0 or -1 on the aggregate it appends to.
/// The outcome of each writer, and the number of rows it added to the log (<c>seq &gt; 0</c>, raw SQL), are the same as the control arm's, which plants nothing: a skew the fix
/// does not touch shows as a difference between arms and not as a bare red. The writer is named by its use case: rotate, retire, declare, attest, accept, moderate, post, enrol.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ShadowRowWritersTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    public static TheoryData<string, string> Arms
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var writer in new[] { "rotate", "retire", "declare", "attest", "accept", "moderate", "post", "enrol" })
                foreach (var plant in new[] { "none", "below", "zero" })
                    data.Add(writer, plant);
            return data;
        }
    }

    private static string Expected(string writer) => writer switch
    {
        "rotate" => "201; +1",
        "retire" => "201; +1",
        "declare" => "0; +1",
        "attest" => "ok; +1",
        "accept" => "201; +1",
        "moderate" => "0; +1",
        "post" => "201; +1",  // counted over the whole log: the post is a new aggregate, and the Forum's tests run one at a time in this collection
        "enrol" => "201; +2",
        _ => throw new ArgumentOutOfRangeException(nameof(writer)),
    };

    [Theory]
    [MemberData(nameof(Arms))]
    public async Task LRV37_AWriterAppendsAsIfNoShadowRowExisted(string writer, string plant)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(plant);
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        async Task PlantAsync(string aggregate, bool onAgent)
        {
            // A position is the table's primary key, so each plant in the one database needs a position of its own: a distinct negative one for "below", and seq 0 for "zero", which the
            // test removes again as the administrator (the tests of this collection run one at a time) so that the next "zero" arm can plant it.
            if (plant == "below")
                await ShadowRows.PlantAsync(forum, aggregate, -(long)RandomNumberGenerator.GetInt32(1, int.MaxValue), onAgent, ct);
            else if (plant == "zero")
                await ShadowRows.PlantAsync(forum, aggregate, 0, onAgent, ct);
        }

        string aggregate;
        Func<Task<string>> act;
        switch (writer)
        {
            case "rotate":
            {
                var agent = ForumAgent.Create(KeyActRequests.Unique("lrv37-rotate"), "r-" + Suffix());
                var (dpop, token) = await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
                aggregate = agent.AgentId;
                var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
                await PlantAsync(aggregate, true);
                act = async () =>
                {
                    var next = KeyActRequests.KeyPair.New("next-" + Suffix());
                    using var response = await dpop.PostAsync(client, KeyActRequests.RotateUrl(agent.AgentId), token, KeyActRequests.RotationBody(agent, next, prev), forum.Now, ct, contentType: "application/json");
                    return await StatusAsync(response, ct);
                };
                break;
            }

            case "retire":
            {
                var agent = ForumAgent.Create(KeyActRequests.Unique("lrv37-retire"), "r-" + Suffix());
                var (dpop, token) = await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
                aggregate = agent.AgentId;
                var next = KeyActRequests.KeyPair.New("next-" + Suffix());
                using (var rotated = await dpop.PostAsync(
                    client, KeyActRequests.RotateUrl(agent.AgentId), token,
                    KeyActRequests.RotationBody(agent, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct, contentType: "application/json"))
                    Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);
                var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
                await PlantAsync(aggregate, true);
                act = async () =>
                {
                    using var response = await dpop.PostAsync(
                        client, KeyActRequests.RetireUrl(agent.AgentId, next.Kid), token, KeyActRequests.RetirementBody(agent.AgentId, next, prev), forum.Now, ct, contentType: "application/json");
                    return await StatusAsync(response, ct);
                };
                break;
            }

            case "declare":
            {
                var agent = ForumAgent.Create(KeyActRequests.Unique("lrv37-declare"), "d-" + Suffix());
                await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
                aggregate = agent.AgentId;
                var boundAt = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
                var pem = LogSigningKey.GeneratePem();
                await OperatorAsync(pem, null, ct, "sign-head", "--by", "ops");
                await PlantAsync(aggregate, true);
                act = async () =>
                {
                    var declared = await OperatorAsync(
                        null, await DeclarationKeyHost.PemAsync(forum, ct), ct,
                        "compromise", "--agent", agent.AgentId, "--kid", agent.Kid, "--from-index", boundAt.ToString(CultureInfo.InvariantCulture),
                        "--reason", "owner-reported", "--operator", "ops");
                    return declared.Exit.ToString(CultureInfo.InvariantCulture);
                };
                break;
            }

            case "attest":
            {
                var agent = ForumAgent.Create(KeyActRequests.Unique("lrv37-attest"), "a-" + Suffix());
                await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
                aggregate = agent.AgentId;
                await PlantAsync(aggregate, true);
                act = async () =>
                {
                    try
                    {
                        await forum.AttestOwnerAsync(agent.AgentId, ct, owner: "owner:lrv37-" + Suffix());
                        return "ok";
                    }
                    catch (InvalidOperationException e)
                    {
                        return e.Message;
                    }
                };
                break;
            }

            case "accept":
            {
                var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
                aggregate = scene.Question.PostId;
                await PlantAsync(aggregate, false);
                act = async () =>
                {
                    try
                    {
                        return ((int)await scene.AcceptAsync(forum, scene.Answer1, ct)).ToString(CultureInfo.InvariantCulture);
                    }
                    catch (XunitException e)
                    {
                        return e.Message;
                    }
                };
                break;
            }

            case "moderate":
            {
                var author = await AcceptedAnswerScene.EnrolAsync(forum, "lrv37-moderated", ct);
                var post = await AcceptedAnswerScene.PostAsync(forum, author, author.Agent.SignQuestion("board-" + Suffix(), "A post to moderate. " + Suffix(), "Moderated " + Suffix(), forum.Now), ct);
                aggregate = post.PostId;
                await PlantAsync(aggregate, false);
                act = async () =>
                {
                    var moderated = await OperatorAsync(
                        null, null, ct, "moderate", "--post", post.PostId, "--category", "spam", "--effect", "withhold", "--reason", "Reviewed: advertising.", "--by", "reviewer");
                    return moderated.Exit.ToString(CultureInfo.InvariantCulture);
                };
                break;
            }

            case "post":
            {
                var author = ForumAgent.Create(KeyActRequests.Unique("lrv37-post"), "p-" + Suffix());
                var (dpop, token) = await author.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
                aggregate = string.Empty;
                await PlantAsync(author.AgentId, true);
                act = async () =>
                {
                    using var response = await dpop.PostAsync(
                        client, "http://localhost/v1/posts", token, author.SignQuestion("board-" + Suffix(), "After the shadow row. " + Suffix(), "Shadow " + Suffix(), forum.Now), forum.Now, ct);
                    return await StatusAsync(response, ct);
                };
                break;
            }

            case "enrol":
            {
                var agent = ForumAgent.Create(KeyActRequests.Unique("lrv37-enrol"), "e-" + Suffix());
                aggregate = agent.AgentId;
                await PlantAsync(aggregate, true);
                act = async () =>
                {
                    using var response = await agent.EnrollAsync(client, ct);
                    return await StatusAsync(response, ct);
                };
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(writer));
        }

        var before = await ShadowRows.LogRowsAsync(forum, aggregate, ct);
        string outcome;
        try
        {
            outcome = await act();
        }
        finally
        {
            if (plant == "zero")
                await ShadowRows.RemoveSeqZeroAsync(forum, ct);
        }

        var after = await ShadowRows.LogRowsAsync(forum, aggregate, ct);

        WholeText.Same(Expected(writer), $"{outcome}; +{after - before}");
    }

    /// <summary>The status code, and the problem type after it when the Forum refused.</summary>
    private static async Task<string> StatusAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        if (response.IsSuccessStatusCode)
            return code;

        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("type", out var type) ? $"{code} {type.GetString()}" : code;
        }
        catch (JsonException)
        {
            return code;
        }
    }

    private async Task<(int Exit, string Out, string Err)> OperatorAsync(string? logPem, string? declarationPem, CancellationToken ct, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(
            args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, ct,
            readLogSigningKeyPem: () => logPem,
            readDeclarationSigningKeyPem: () => declarationPem);
        return (exit, stdout.ToString(), stderr.ToString());
    }
}
