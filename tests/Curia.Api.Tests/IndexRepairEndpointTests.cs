using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G51 (R11.69) at the host: probes PIL18 (the middle gap, after a restart), PIL19 (a post with nothing to embed is a skip), PIL20 (a reconcile that
/// cannot complete stops the host) and PIL22 (the service runs, on its clock). A restart is a second host over the same database (<c>WithWebHostBuilder</c>), its
/// reconcile run by its hosted service at start; the oracle is <c>post_embeddings</c> counted by digest from the fixture's own connection string, and for PIL18
/// the search route's own <c>why_ranked</c>. PIL22 runs on a host whose clock has timers (ruling 15) and advances it in one-second steps, after a timer is armed.
/// The deep gap (PIL18's second fact) is <see cref="IndexRepairDeepGapEndpointTests"/>, which needs a database of its own.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IndexRepairEndpointTests(AgentWriteGateForumFixture forum) : IClassFixture<AgentWriteGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>
    /// PIL18. The middle gap, after a restart: posts A, B and C, A's index write failing and B's and C's succeeding; a second host over the same database starts. A is
    /// in <c>post_embeddings</c> and a search for A's own words ranks it with a vector term. The reconcile that read the index's highest sequence started after C and
    /// never saw A.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL18_APostBelowTheHighestIndexedSequenceIsIndexedByARestart()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-gap", Ct);
        var bodyA = "Marzipan harbour " + Probe.Unrelated();

        forum.Index.FailUpserts = true;
        await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, bodyA, "About " + Probe.Unrelated(), forum.Now), null, Ct);
        forum.Index.FailUpserts = false;
        await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), null, Ct);
        await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), null, Ct);
        var digests = await IndexLag.DigestsOfAsync(forum, actor.Agent.AgentId);
        var postA = (await IndexLag.PostIdsOfAsync(forum, actor.Agent.AgentId))[0];

        await using var restarted = forum.WithWebHostBuilder(_ => { });
        using var client = restarted.CreateClient();
        using var search = await IndexLag.SearchAsync(client, $"q={Uri.EscapeDataString(bodyA)}&board={board}&why=true", Ct);

        Said(
            "3 posts stored; A is in post_embeddings (1 row); a search for A's words ranks A with a vector term",
            $"{digests.Length} posts stored; A is in post_embeddings ({await IndexLag.RowsAsync(forum, digests[0])} row); a search for A's words ranks A {(IndexLag.HasVectorTerm(search, postA) ? "with" : "without")} a vector term");
    }

    /// <summary>
    /// PIL19 (a control). A post with nothing to embed is a skip and never a failure, as the newest post and as a middle one: the host that starts over a log holding
    /// two of them (around a post that embeds) stores no vector for either and still starts.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL19_APostWithNothingToEmbedIsSkippedAtStart()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-skip", Ct);
        await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, "!!!", "???", forum.Now, tags: []), null, Ct);
        await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), null, Ct);
        await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, "???", "!!!", forum.Now, tags: []), null, Ct);
        var digests = await IndexLag.DigestsOfAsync(forum, actor.Agent.AgentId);

        await using var restarted = forum.WithWebHostBuilder(_ => { });
        using var client = restarted.CreateClient();

        Said(
            "3 posts stored; rows for the middle, the first and the last: 1, 0, 0",
            $"{digests.Length} posts stored; rows for the middle, the first and the last: {await IndexLag.RowsAsync(forum, digests[1])}, {await IndexLag.RowsAsync(forum, digests[0])}, {await IndexLag.RowsAsync(forum, digests[2])}");
    }

    /// <summary>The host's own index except that its reads of what it holds fail, as a database that cannot answer fails them.</summary>
    private sealed class ReadFailingIndex(IVectorIndex inner) : IVectorIndex
    {
        private static Error Fault() => RetrievalErrors.IndexUnavailable("the digests read failed");

        public Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default) =>
            inner.UpsertAsync(digest, postId, sequence, embedding, cancellationToken);

        public Task<Result<System.Collections.Immutable.ImmutableArray<VectorMatch>>> NearestAsync(Embedding query, int limit, CancellationToken cancellationToken = default) =>
            inner.NearestAsync(query, limit, cancellationToken);

        public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => Task.FromResult(Result<long>.Fail(Fault()));

        public Task<Result<long>> MaxSequenceAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => Task.FromResult(Result<long>.Fail(Fault()));

        public Task<Result<IReadOnlySet<string>>> IndexedDigestsAsync(EmbeddingModel model, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlySet<string>>.Fail(Fault()));
    }

    /// <summary>
    /// PIL20 (a control). A reconcile that cannot complete stops the host, with the slug, as it did: a host whose index cannot say what it holds does not start, and
    /// does not start serving a channel it cannot keep in step with the log.
    /// </summary>
    [Fact]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The fact records whatever a host that cannot start threw: its words are the observation.")]
    public void R11_69_PIL20_AReconcileThatCannotCompleteStopsTheHost()
    {
        string what;
        try
        {
            using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IVectorIndex>(sp => new ReadFailingIndex(sp.GetRequiredService<Curia.Infrastructure.PostgresAdapters>().VectorIndex))));
            using var client = host.CreateClient();
            what = "started";
        }
        catch (Exception ex)
        {
            var words = string.Join(" | ", Flatten(ex).Select(e => e.Message));
            what = words.Contains(RetrievalErrors.IndexUnavailable("x").Type, StringComparison.Ordinal) ? "did not start, naming the slug" : "did not start, without naming the slug";
        }

        Said("did not start, naming the slug", what);
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
        {
            yield return e;
            if (e.InnerException is null) yield break;
        }
    }

    /// <summary>
    /// PIL22. The service runs, on its clock: a host whose clock has timers resolves one catch-up service among its hosted services; with the index failing and then
    /// healed, after a timer is armed (an advance before the loop has armed one is lost) and advanced in one-second steps with no restart, the post is not stored at the
    /// failure plus four seconds, is stored by the failure plus six (the tick, R11.69), and the host logged <c>VectorIndexCaughtUp</c>.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL22_TheCatchUpServiceRunsOnItsClockAndStoresThePostWithoutARestart()
    {
        using var logged = new LogCapture();
        var clock = TimerClockHost.At(forum);
        await using var host = TimerClockHost.Of(forum, clock, logged);
        using var client = host.CreateClient();
        var index = AgentWriteGateForumFixture.IndexOf(host.Services);
        var services = host.Services.GetServices<IHostedService>().Count(s => s.GetType().Name == "EmbeddingCatchUpService");
        var fresh = await IndexLag.FreshAsync(forum, "question", Ct);

        index.FailUpserts = true;
        await Probe.SendViaAsync(forum, client, fresh.Actor, fresh.Wire, null, Ct);
        index.FailUpserts = false;
        var digest = (await IndexLag.DigestsOfAsync(forum, fresh.Actor.Agent.AgentId)).FirstOrDefault();
        var armed = await IndexLag.EventuallyAsync(() => clock.Armed >= 1, TimeSpan.FromSeconds(10), Ct);

        var rowsAtFour = -1L;
        for (var second = 1; second <= 6; second++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(150, Ct);
            if (second == 4 && digest is not null) rowsAtFour = await IndexLag.RowsAsync(forum, digest);
        }

        var rowsAtSix = 0L;
        for (var i = 0; i < 40 && digest is not null && rowsAtSix == 0; i++)
        {
            rowsAtSix = await IndexLag.RowsAsync(forum, digest);
            if (rowsAtSix == 0) await Task.Delay(50, Ct);
        }

        var caughtUp = logged.All.Count(e => e.Name == PublishedIndexLag.CaughtUpEvent());
        Said(
            $"1 catch-up service; a timer armed; at the failure plus 4 s 0 rows; at the failure plus 6 s 1 row; 1 {PublishedIndexLag.CaughtUpEvent()} event",
            $"{services} catch-up service{(services == 1 ? string.Empty : "s")}; {(armed ? "a timer armed" : "no timer armed")}; at the failure plus 4 s {rowsAtFour} rows; at the failure plus 6 s {rowsAtSix} row; {caughtUp} {PublishedIndexLag.CaughtUpEvent()} events");
    }
}
