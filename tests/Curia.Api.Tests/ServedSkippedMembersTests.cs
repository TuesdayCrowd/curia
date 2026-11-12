using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// LRV25 (errata G68, ruling 30, R4.41): the key set the Forum serves (<c>GET /v1/jwks?agent=</c>) carries <c>curia_skipped</c>, the rows of the identity's stream the verifier skips as
/// <c>{log_index, type, predicate}</c> in log order, and <c>curia_skipped_total</c>; both present and empty when nothing is skipped, both omitted when the Forum could not decide the stream; at most 20 listed,
/// the total counting them all. Red on the seam: <c>Expected: curia_skipped_total 2  Actual: member absent</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ServedSkippedMembersTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private async Task<JsonElement> KeySetOfAsync(string agent, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(agent)}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement.Clone();
    }

    private static string Words(JsonElement keySet)
    {
        if (!keySet.TryGetProperty("curia_skipped_total", out var total)) return "curia_skipped_total: member absent";
        var listed = keySet.TryGetProperty("curia_skipped", out var list) ? list.EnumerateArray().ToArray() : [];
        return $"curia_skipped_total {total.GetInt64().ToString(CultureInfo.InvariantCulture)}; listed {listed.Length.ToString(CultureInfo.InvariantCulture)}; "
            + string.Join(" ", listed.Select(e => $"[{string.Join(",", e.EnumerateObject().Select(p => p.Name))}]").Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task LRV25_AnIdentityWithTwoSkippedRowsListsThemAndTotalsTwo()
    {
        var ct = TestContext.Current.CancellationToken;
        var victim = await VerifiedStopSupport.EnrolAsync(forum, "lrv25a", ct);
        var first = await VerifiedStopSupport.CountAsync(forum, ct);

        // The enrolment's binding is the entry just before the first row: prev names it, so that each row fails on its header typ and not on the link (a skipped row moves nothing, so the second names it too).
        await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid, first - 1), ct);
        await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid, first - 1), ct);

        var keySet = await KeySetOfAsync(victim.AgentId, ct);

        var served = keySet.TryGetProperty("curia_skipped", out var list)
            ? string.Join(" ", list.EnumerateArray().Select(e => $"{e.GetProperty("log_index").GetInt64()} {e.GetProperty("type").GetString()} {e.GetProperty("predicate").GetString()}"))
            : "absent";
        WholeText.Same(
            $"curia_skipped_total 2; listed 2; [log_index,type,predicate]; {first} agent.key-retired lifecycle-typ-mismatch {first + 1} agent.key-retired lifecycle-typ-mismatch",
            $"{Words(keySet)}; {served}");
    }

    [Fact]
    public async Task LRV25_AnIdentityWithNoneHasAnEmptyListAndZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var victim = await VerifiedStopSupport.EnrolAsync(forum, "lrv25n", ct);

        var keySet = await KeySetOfAsync(victim.AgentId, ct);

        WholeText.Same("curia_skipped_total 0; listed 0; ", Words(keySet));
    }

    [Fact]
    public async Task LRV25_MoreThanTwentySkippedRowsListTwentyAndTotalTheRest()
    {
        var ct = TestContext.Current.CancellationToken;
        var victim = await VerifiedStopSupport.EnrolAsync(forum, "lrv25m", ct);
        for (var i = 0; i < 23; i++)
            await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid), ct);

        var keySet = await KeySetOfAsync(victim.AgentId, ct);

        WholeText.Same("curia_skipped_total 23; listed 20; [log_index,type,predicate]", Words(keySet));
    }

    /// <summary>A stream the Forum could not decide has neither member (a malformed <c>log.head</c> row stops the Acta's fold, so the key set is served without positions): a control on the seam.</summary>
    [Fact]
    public async Task LRV25_AStreamTheForumCouldNotDecideHasNeitherMember_Control()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var own = await VerifiedStopSupport.NewAsync();
        var victim = await VerifiedStopSupport.EnrolAsync(own, "lrv25u", ct);
        await VerifiedStopSupport.InsertRowAsync(own, "log.head", "log:heads", "{}", ct);

        using var response = await own.Client.GetAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(victim.AgentId)}", UriKind.Relative), ct);
        var keySet = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)).RootElement;

        WholeText.Same(
            "200; curia_lifecycle absent; curia_skipped absent; curia_skipped_total absent",
            $"{(int)response.StatusCode}; curia_lifecycle {(keySet.TryGetProperty("curia_lifecycle", out _) ? "present" : "absent")}; curia_skipped {(keySet.TryGetProperty("curia_skipped", out _) ? "present" : "absent")}; "
            + $"curia_skipped_total {(keySet.TryGetProperty("curia_skipped_total", out _) ? "present" : "absent")}");
    }
}
