using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests.ReadCost;

/// <summary>
/// RC6 (errata G87, R5.28, R11.113): the cost of a class A request is what its read set says, held against Postgres's own counters, a different artifact from the code. The same request is made over a log of
/// a few hundred events and again after twelve thousand enrolment-shaped rows have been added to it (the flood a closed beta is bounded against): the heap tuples the request fetched (index fetches plus
/// sequential-scan reads on <c>events</c>, from <c>pg_stat_user_tables</c>) do not grow with the log, and the index entries it read grow by at most one window pass over the primary key for each read set it made,
/// which is the rank (R6.47) that a log index is. The number of read sets a token request makes is one (register item 314: two whole reads before), and no class A request reads the log whole.
/// </summary>
[Collection("forum")]
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Constant statements; the filler's count is an integer this test chose.")]
public sealed class ReadCostCounterTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Flood = 12_000;

    private async Task<(long Heap, long Index)> StatsAsync()
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using (var clear = new NpgsqlCommand("SELECT pg_stat_clear_snapshot();", connection))
            await clear.ExecuteNonQueryAsync(Ct);

        await using var command = new NpgsqlCommand(
            "SELECT COALESCE(t.idx_tup_fetch, 0) + COALESCE(t.seq_tup_read, 0), COALESCE((SELECT sum(idx_tup_read) FROM pg_stat_user_indexes WHERE relname = 'events'), 0) FROM pg_stat_user_tables t WHERE t.relname = 'events';",
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        return (reader.GetInt64(0), (long)reader.GetDecimal(1));
    }

    /// <summary>
    /// The counters the requests moved, together: a backend publishes what it counted when it has been idle for ten seconds (PGSTAT_IDLE_INTERVAL), so each phase makes all its requests and waits once.
    /// </summary>
    private async Task<(long Heap, long Index)> MeasureAsync(IEnumerable<(string Name, Func<Task<string>> Request)> requests)
    {
        var before = await StatsAsync();
        foreach (var (name, request) in requests)
        {
            var said = await request();
            Assert.True(said.StartsWith("200", StringComparison.Ordinal), $"{name}: expected 200, actual {said}");
        }

        await Task.Delay(TimeSpan.FromSeconds(11), Ct);
        var after = await StatsAsync();
        return (after.Heap - before.Heap, after.Index - before.Index);
    }

    [Fact]
    public async Task RC6_AClassARequestsHeapReadsDoNotGrowWithTheLogAndItsIndexReadsGrowByAWindowPass()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "rc6", Ct);
        var token = await actor.Dpop.GetTokenAsync(forum.Client, Probe.TokenEndpoint, forum.Now, Ct);

        (string Name, Func<Task<string>> Request)[] routes =
        [
            ("POST /oauth/token", async () =>
            {
                var reply = await DpopClient.RequestTokenReplyAsync(
                    forum.Client, actor.Agent.AgentId, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now), Ct);
                return reply.Says;
            }),
            ("GET /v1/budget", async () => (await FaultActs.GetAsync(forum, actor, "/v1/budget", Ct, token)).Says),
            ("GET /v1/flags", async () => (await FaultActs.GetAsync(forum, actor, "/v1/flags", Ct, token)).Says),
            ("GET /v1/jwks", async () => (await FaultActs.GetAsync(forum, actor, $"/v1/jwks?agent={Uri.EscapeDataString(actor.Agent.AgentId)}", Ct, token)).Says),
        ];

        // Warm each route once (a first request builds what a second does not), then count over the small log.
        foreach (var (_, request) in routes) _ = await request();
        var small = await MeasureAsync(routes);

        // Vacuity (a counter that never moved proves nothing): the four requests moved both counters over the small log.
        Assert.True(small.Heap > 0 && small.Index > 0, $"NOT REACHED: the four requests moved the counters by {small}; the statistics view did not see them");

        // A flood of enrolment-shaped rows by other identities, then a vacuum so that index-only scans need no heap (as autovacuum leaves a log).
        await using (var connection = new NpgsqlConnection(forum.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using (var fill = new NpgsqlCommand(
                $"""
                INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload)
                SELECT 'rc6-flood-' || g, 'agent.enrolled', 'https://agents.example/rc6-flood-' || g, 'https://agents.example/rc6-flood-' || g,
                       jsonb_build_object('agent_id', 'https://agents.example/rc6-flood-' || g, 'kid', 'rc6k' || g, 'reason', 'flood')
                FROM generate_series(1, {Flood.ToString(CultureInfo.InvariantCulture)}) g;
                """, connection))
                await fill.ExecuteNonQueryAsync(Ct);
            await using var vacuum = new NpgsqlCommand("VACUUM (ANALYZE) events;", connection);
            await vacuum.ExecuteNonQueryAsync(Ct);
        }

        foreach (var (_, request) in routes) _ = await request();
        var big = await MeasureAsync(routes);
        TestContext.Current.SendDiagnosticMessage($"RC6: with {Flood} more rows the four requests fetched {small.Heap} -> {big.Heap} heap tuples and read {small.Index} -> {big.Index} index entries");
        var problems = new List<string>();

        // Heap tuples: the caller's own rows and the key lists; a small allowance for a statement's own bookkeeping, and none for the flood.
        if (big.Heap > small.Heap + 8 * routes.Length) problems.Add($"heap tuples fetched grew from {small.Heap} to {big.Heap} with {Flood} more rows");

        // Index entries: at most one window pass over the primary key for each read set the requests made (the token's one, and for each of the three authenticated routes the key status in authentication, its own
        // read and, for the flag join, its two more), so no more than twelve passes over the rows added.
        if (big.Index > small.Index + (12L * Flood))
            problems.Add($"index entries read grew from {small.Index} to {big.Index}, more than twelve window passes over {Flood} rows");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>R5.28, register item 314: a token request for a valid assertion makes one read set, and reads no part of the log whole.</summary>
    [Fact]
    public async Task R5_28_ATokenRequestMakesOneReadSetAndNoWholeRead()
    {
        var counter = new Counter();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "rc6-token", Ct);
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.CountRequestReads(counter)));
        using var client = host.CreateClient();

        counter.Count = 0;
        counter.Plans = 0;
        var reply = await DpopClient.RequestTokenReplyAsync(
            client, actor.Agent.AgentId, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now), Ct);

        Assert.True(reply.Status == 200, reply.Says);
        Assert.Equal("1 read(s), 1 of them a read plan", $"{counter.Count} read(s), {counter.Plans} of them a read plan");
    }
}
