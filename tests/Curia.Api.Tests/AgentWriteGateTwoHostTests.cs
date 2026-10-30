using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G48 across two Forum hosts over one database (probes BRB22, BRB52, BRB61, BRB62): <c>forum.WithWebHostBuilder</c> twice gives two hosts, each with its
/// own <c>PostgresAdapters</c>, its own queue and its own seams, and the database is all they share. Both hosts are warmed first (M11: a cold host answers its
/// half of a burst after the other has committed and passes for the wrong reason), and the warm-up is itself a probe (BRB61). One OS process cannot show a
/// static queue shared by two hosts, so BRB56 fences static state and Task 9 repeats the burst against two real processes.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AgentWriteGateTwoHostTests(AgentWriteGateForumFixture forum) : IClassFixture<AgentWriteGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Said(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.Ordinal)
            ? actual
            : throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");

    private sealed class Hosts : IAsyncDisposable
    {
        internal required WebApplicationFactory<Program> One { get; init; }

        internal required WebApplicationFactory<Program> Two { get; init; }

        internal HttpClient ClientOne { get; init; } = null!;

        internal HttpClient ClientTwo { get; init; } = null!;

        internal ObservingAgentWriteGate GateOne => AgentWriteGateForumFixture.GateOf(One.Services);

        internal ObservingAgentWriteGate GateTwo => AgentWriteGateForumFixture.GateOf(Two.Services);

        public async ValueTask DisposeAsync()
        {
            ClientOne.Dispose();
            ClientTwo.Dispose();
            await One.DisposeAsync();
            await Two.DisposeAsync();
        }
    }

    private Hosts TwoHosts()
    {
        var one = forum.WithWebHostBuilder(_ => { });
        var two = forum.WithWebHostBuilder(_ => { });
        return new Hosts { One = one, Two = two, ClientOne = one.CreateClient(), ClientTwo = two.CreateClient() };
    }

    /// <summary>One write by a throwaway agent and one read to <paramref name="client"/>, so the host has compiled its write path and opened its pools.</summary>
    private async Task<string> WarmAsync(HttpClient client, string board, ScenePost parent)
    {
        var throwaway = await AcceptedAnswerScene.EnrolAsync(forum, "warm", Ct);
        var sent = await Probe.SendViaAsync(forum, client, throwaway, BudgetBursts.Wire(throwaway.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        using var read = await client.GetAsync(new Uri($"/v1/posts/{parent.PostId}", UriKind.Relative), Ct);
        return $"{BudgetBursts.Kind(sent)} and a read {(int)read.StatusCode}";
    }

    private async Task<(long Stored, string Tally)> SplitBurstAsync(Hosts hosts, string board, ScenePost parent, int width)
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "split", Ct);
        var tokenOne = await actor.Dpop.GetTokenAsync(hosts.ClientOne, BudgetBursts.TokenEndpoint, forum.Now, Ct);
        var tokenTwo = await actor.Dpop.GetTokenAsync(hosts.ClientTwo, BudgetBursts.TokenEndpoint, forum.Now, Ct);
        var sends = Enumerable.Range(0, width).Select(i =>
        {
            var wire = BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now);
            return i % 2 == 0
                ? (Func<Task<Sent>>)(() => Probe.SendViaAsync(forum, hosts.ClientOne, actor, wire, tokenOne, Ct))
                : () => Probe.SendViaAsync(forum, hosts.ClientTwo, actor, wire, tokenTwo, Ct);
        });
        var sent = await Probe.TogetherAsync(sends);
        return (await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId), BudgetBursts.Tally(sent));
    }

    /// <summary>
    /// BRB61. Both hosts are warm: each host's observer has recorded an entry for a throwaway agent and a read has been served, so a probe whose second host is
    /// cold fails here and not as a green burst.
    /// </summary>
    [Fact]
    public async Task R7_27_BRB61_BothHostsAreWarmBeforeTheBurst()
    {
        await using var hosts = TwoHosts();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);

        var one = await WarmAsync(hosts.ClientOne, board, parent);
        var two = await WarmAsync(hosts.ClientTwo, board, parent);

        Said(
            "host one: 201 and a read 200 with 1 entries; host two: 201 and a read 200 with 1 entries",
            $"host one: {one} with {hosts.GateOne.Entered.Count} entries; host two: {two} with {hosts.GateTwo.Entered.Count} entries");
    }

    /// <summary>
    /// BRB22. Two hosts, one database: 16 simultaneous posts of one T0 agent split across them, 3 slots left, store exactly 3 in every repetition; and with A's append
    /// parked on host one, B sent to host two stores nothing until the release.
    /// </summary>
    [Fact]
    public async Task R7_27_BRB22_TwoHostsHoldOneAgentToTheLimit()
    {
        await using var hosts = TwoHosts();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        _ = await WarmAsync(hosts.ClientOne, board, parent);
        _ = await WarmAsync(hosts.ClientTwo, board, parent);
        var limit = BudgetBursts.PostLimit("T0");

        var failures = new List<string>();
        for (var rep = 1; rep <= 10; rep++)
        {
            var (stored, tally) = await SplitBurstAsync(hosts, board, parent, 16);
            if (stored != limit) failures.Add($"rep {rep}: {stored} stored ({tally})");
        }

        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "parked-split", Ct);
        var tokenOne = await actor.Dpop.GetTokenAsync(hosts.ClientOne, BudgetBursts.TokenEndpoint, forum.Now, Ct);
        var tokenTwo = await actor.Dpop.GetTokenAsync(hosts.ClientTwo, BudgetBursts.TokenEndpoint, forum.Now, Ct);
        var storeOne = AgentWriteGateForumFixture.StoreOf(hosts.One.Services);
        storeOne.ParkNext(e => e.Type.Value == "post.accepted");
        long whileParked;
        bool bDone;
        try
        {
            var a = Task.Run(() => Probe.SendViaAsync(forum, hosts.ClientOne, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), tokenOne, Ct), Ct);
            await storeOne.WaitForParkedAsync(1, TimeSpan.FromSeconds(15));
            var b = Task.Run(() => Probe.SendViaAsync(forum, hosts.ClientTwo, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), tokenTwo, Ct), Ct);
            await Task.Delay(800, Ct);
            whileParked = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
            bDone = b.IsCompleted;
            storeOne.Release();
            _ = await a;
            _ = await b;
        }
        finally
        {
            storeOne.Release();
        }

        Said(
            $"exactly {limit} stored in every one of 10 split bursts; B on host two waiting with 0 stored while A is parked on host one",
            $"{(failures.Count == 0 ? $"exactly {limit} stored in every one of 10 split bursts" : $"{failures.Count} of 10 split bursts off the limit: " + string.Join(" | ", failures.Take(3)))}; B on host two {(bDone ? "answered" : "waiting")} with {whileParked} stored while A is parked on host one");
    }

    /// <summary>
    /// BRB62. The two-host control that sees the bug: with the pass-through double on both hosts the same split burst overshoots the limit in at least one of 20
    /// repetitions (M11: 20 of 20, overshoot 13). Without this a green BRB22 could be a burst that cannot overshoot.
    /// </summary>
    [Fact]
    public async Task R7_27_BRB62_WithThePassThroughOnBothHostsTheSplitBurstOvershoots()
    {
        await using var hosts = TwoHosts();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        _ = await WarmAsync(hosts.ClientOne, board, parent);
        _ = await WarmAsync(hosts.ClientTwo, board, parent);
        hosts.GateOne.UsePassThrough = true;
        hosts.GateTwo.UsePassThrough = true;
        var limit = BudgetBursts.PostLimit("T0");

        var overshoots = 0;
        for (var rep = 1; rep <= 20; rep++)
        {
            var (stored, _) = await SplitBurstAsync(hosts, board, parent, 16);
            if (stored > limit) overshoots++;
        }

        Said("the split burst overshoots in at least one of 20 repetitions", $"the split burst overshoots in {(overshoots >= 1 ? "at least one" : "none")} of 20 repetitions");
    }

    /// <summary>BRB52. One instance per host: the production composition registers <see cref="IAgentWriteGate"/> once, as a singleton, and the host resolves one object from the root and from two scopes.</summary>
    [Fact]
    public void R7_27_BRB52_TheGateIsOneInstancePerHost()
    {
        _ = forum.Services;
        var (lifetime, registrations) = forum.ProductionRegistration;
        var root = forum.Services.GetRequiredService<IAgentWriteGate>();
        using var first = forum.Services.CreateScope();
        using var second = forum.Services.CreateScope();
        var sameObject = ReferenceEquals(root, first.ServiceProvider.GetRequiredService<IAgentWriteGate>())
            && ReferenceEquals(root, second.ServiceProvider.GetRequiredService<IAgentWriteGate>());

        Said(
            "registered 1 time as a singleton; one object from the root and from two scopes",
            $"registered {registrations} time{(registrations == 1 ? string.Empty : "s")} as a {(lifetime == ServiceLifetime.Singleton ? "singleton" : lifetime.ToString())}; {(sameObject ? "one object" : "different objects")} from the root and from two scopes");
    }
}
