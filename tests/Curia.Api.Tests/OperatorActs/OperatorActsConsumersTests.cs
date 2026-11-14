#pragma warning disable CA1305, CA1308, CA1707, CA1822, CA1031, CA2007, CA1861, CA1859, CA1062, CA1307, CA1310, CA1002, CA1819
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// OAS31 (PR N2, errata G70, Amendment 1's consumers table, rows C1 to C20): every consumer Task 0 found reads honoured acts. Each consumer is observed before and after a forged act row is inserted as the
/// application role, by the same observer, and the test fails by the consumer's name when its output moved. The rows covered here are C1, C2, C3, C4, C5, C7, C11, C13 and C19; C6, C9, C10, C12, C14 to C18 are
/// the rows of OAS36, OAS6, OAS15, OAS37, OAS27, OAS22 and OAS21 (each named in its own probe), C8 (the inbox) and C20 (a forged flag) are not run by Task 1: see the report.
/// </summary>
[Collection("forum")]
public sealed class OperatorActsConsumersTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> BatchStatusAsync(OperatorActsApi api, string digest)
    {
        using var r = await api.Http().PostAsJsonAsync("/v1/posts/batch", new { digests = new[] { digest } }, Ct);
        var body = await r.Content.ReadAsStringAsync(Ct);
        var item = JsonDocument.Parse(body).RootElement.GetProperty("items")[0];
        return $"{(int)r.StatusCode} {item.GetProperty("state").GetString()}";
    }

    private static async Task<string> BoardListsAsync(OperatorActsApi api, string board, string postId)
    {
        var body = await api.GetBodyAsync($"/v1/boards/{board}/posts", Ct);
        return body.Contains(postId, StringComparison.Ordinal) ? "lists the post" : "omits the post";
    }

    [Fact]
    public async Task OAS31_EveryConsumerReadsHonouredActs()
    {
        var api = new OperatorActsApi(forum);
        var rows = new List<(string Consumer, string Before, string After)>();

        // A forged withhold of a served post: C2 (GetPost), C3 (thread), C4 (board), C5 (batch), C7 (search), C11 (the serving filter).
        var victim = await api.QuestionAsync("victim", Ct);
        async Task<string[]> PostConsumersAsync() =>
        [
            $"{await api.GetStatusAsync($"/v1/posts/{victim.Id}", Ct)}",
            $"{await api.GetStatusAsync($"/v1/threads/{victim.Id}", Ct)}",
            await BoardListsAsync(api, victim.Board, victim.Id),
            await BatchStatusAsync(api, victim.Digest),
            $"{await api.SearchCountAsync(victim.Words, victim.Board, Ct)} result(s)",
        ];
        string[] names = ["C2 GetPost", "C3 GetThread", "C4 ListBoard", "C5 Batch", "C7/C11 Search and the serving filter"];
        var postBefore = await PostConsumersAsync();
        await api.ForgeModerationAsync(victim.Id, victim.Digest, "withhold", Ct);
        var postAfter = await PostConsumersAsync();
        for (var i = 0; i < names.Length; i++) rows.Add((names[i], postBefore[i], postAfter[i]));

        // A forged attestation for an aged genuine agent: C1 (the token's tier claim), C2 (provenance), C13 (re-announcement), C19 (the write gate).
        var board = OperatorActsApi.Board();
        var asker = await api.GenuineAsync("asker", Ct);
        var target = await api.QuestionByAsync(asker, board, Ct);
        var x = await api.GenuineAsync("aged", Ct);
        var first = await api.AskAsync(x, board, Ct);
        for (var i = 0; i < 2; i++) await api.AskAsync(x, board, Ct);
        forum.Clock.Advance(TimeSpan.FromHours(49));
        async Task<string[]> AgentConsumersAsync() =>
        [
            (await api.TokenAsync(x, Ct)).Tier ?? "no token",
            (await api.ProvenanceAsync(first.PostId, Ct)).OwnerVerified?.ToString().ToLowerInvariant() ?? "no provenance",
            await api.EnrollBodyMemberAsync(x.Agent, "owner_verified", Ct),
            OperatorActsApi.Said(await api.AnswerAsync(x, board, target, Ct)),
        ];
        string[] agentNames = ["C1 token tier", "C2 provenance owner_verified", "C13 re-announcement owner_verified", "C19 write gate (answer)"];
        var agentBefore = await AgentConsumersAsync();
        await api.ForgeAttestationNowAsync(x.Agent.AgentId, "owner:forged-" + OperatorActsApi.Suffix(), Ct);
        var agentAfter = await AgentConsumersAsync();
        for (var i = 0; i < agentNames.Length; i++) rows.Add((agentNames[i], agentBefore[i], agentAfter[i]));

        // Search diversification: six authors on one board, a forged same-owner attestation for each.
        var term = "zdiv" + OperatorActsApi.Suffix();
        var divBoard = OperatorActsApi.Board();
        var posters = new List<OperatorActsApi.Actor>();
        for (var i = 0; i < 6; i++)
        {
            var a = await api.GenuineAsync("div" + i, Ct);
            posters.Add(a);
            Assert.Equal(201, (await api.PostRawAsync(a, a.Agent.SignQuestion(divBoard, $"{term} behaviour number {i} {RevisionProbe.Unrelated()}", $"{term} {i}", forum.Now), Ct)).Status);
        }

        async Task<string> DeferredAsync() =>
            $"deferred_by_diversification x{Regex.Count(await api.GetBodyAsync($"/v1/search?q={term}&board={divBoard}&limit=3&why=true", Ct), "\"deferred_by_diversification\":true")}";
        var divBefore = await DeferredAsync();
        foreach (var p in posters) await api.ForgeAttestationNowAsync(p.Agent.AgentId, "owner:one-farm", Ct);
        rows.Add(("C7 search diversification", divBefore, await DeferredAsync()));

        WholeText.Same(
            string.Join("; ", rows.Select(r => $"{r.Consumer}: {r.Before}")),
            string.Join("; ", rows.Select(r => $"{r.Consumer}: {r.After}")));
        _ = HttpStatusCode.OK;
    }
}
