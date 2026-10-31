using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G51 (R11.69), PIL18's second fact: the gap is more than one page of the log before its end. <c>EventReaderExtensions.PageSize</c> is 10,000, so the log
/// holds more than that many events after the post whose vector failed; they are seeded in bulk by SQL from the fixture's own connection (a clone of a stored
/// question per row, with its own post id and digest), not by ten thousand requests. This class has a fixture, and so a database, of its own: the seeded posts
/// would otherwise sit in every other fact's corpus.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IndexRepairDeepGapEndpointTests(AgentWriteGateForumFixture forum) : IClassFixture<AgentWriteGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One more page than the reader's page size: the events seeded after the post whose vector failed.</summary>
    private const int Seeded = 10_050;

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "A constant statement; its values are bound as parameters.")]
    private async Task SeedAfterAsync(string templateDigest)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts)
            SELECT '01' || lpad(upper(to_hex(g)), 24, '0'), e.event_type, '01' || lpad(upper(to_hex(g)), 24, '0'), e.actor_id,
                   jsonb_set(jsonb_set(e.payload, '{post_id}', to_jsonb('01' || lpad(upper(to_hex(g)), 24, '0'))),
                             '{digest}', to_jsonb('sha256:' || md5(g::text) || md5(g::text || 'x'))),
                   e.server_ts
            FROM events e CROSS JOIN generate_series(1, @n) AS g
            WHERE e.event_type = 'post.accepted' AND e.payload->>'digest' = @digest;
            """, admin);
        command.Parameters.AddWithValue("n", Seeded);
        command.Parameters.AddWithValue("digest", templateDigest);
        await command.ExecuteNonQueryAsync(Ct);
    }

    /// <summary>
    /// PIL18, the deep fact. Post A's vector fails, B's succeeds, more than one page of events follows, and a second host starts: A is in <c>post_embeddings</c>. A
    /// reconcile "by absence over the last page only" (FPIL17's second form) passes the shallow fact and is red here.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL18_APostMoreThanOnePageBeforeTheEndOfTheLogIsIndexedByARestart()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-deep", Ct);

        forum.Index.FailUpserts = true;
        await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, "Marzipan harbour " + Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), null, Ct);
        forum.Index.FailUpserts = false;
        await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), null, Ct);
        var digests = await IndexLag.DigestsOfAsync(forum, actor.Agent.AgentId);
        await SeedAfterAsync(digests[1]);

        await using var restarted = forum.WithWebHostBuilder(_ => { });
        using var client = restarted.CreateClient();

        Probe.Said(
            $"2 posts stored then {Seeded} seeded; A is in post_embeddings (1 row)",
            $"{digests.Length} posts stored then {Seeded} seeded; A is in post_embeddings ({await IndexLag.RowsAsync(forum, digests[0])} row)");
    }
}
