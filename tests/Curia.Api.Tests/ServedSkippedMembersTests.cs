using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        // Amendment 7, ruling 88: a total beside no list is a defect (R4.41 serves both members, an empty list included), so it is said and never defaults to an empty list.
        if (!keySet.TryGetProperty("curia_skipped", out var list)) return $"curia_skipped_total {total.GetInt64().ToString(CultureInfo.InvariantCulture)}; curia_skipped member absent";
        var listed = list.EnumerateArray().ToArray();
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

    /// <summary>
    /// R4.41 (errata G68; PR N's Amendment 7, ruling 90 (4)): the two members the key set serves, and the members of a listed row, are the ones R4.41's paragraph names. The names are read from the
    /// errata at test time (the backticked identifiers beginning <c>curia_skipped</c>, and the member list in braces) and never from <c>Jwks.cs</c>, so a rename on either side without the other fails
    /// here naming both spellings.
    /// </summary>
    [Fact]
    public async Task R4_41_TheServedMembersAreTheOnesTheErrataNames()
    {
        var ct = TestContext.Current.CancellationToken;
        var (members, row) = PublishedSkippedMembers();
        var victim = await VerifiedStopSupport.EnrolAsync(forum, "r441w", ct);
        await VerifiedStopSupport.InsertRowAsync(forum, "agent.key-retired", victim.AgentId, VerifiedStopSupport.ForgedRetirement(victim.AgentId, victim.Kid), ct);

        var keySet = await KeySetOfAsync(victim.AgentId, ct);

        var served = keySet.EnumerateObject().Where(p => p.Name.StartsWith("curia_skipped", StringComparison.Ordinal)).ToArray();
        var lists = served.Where(p => p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0).ToArray();
        var servedRow = lists.Length == 1 ? string.Join(", ", lists[0].Value[0].EnumerateObject().Select(p => p.Name)) : $"{lists.Length} lists with a row";
        WholeText.Same(
            $"members {string.Join(", ", members.Order(StringComparer.Ordinal))}; a row is {{{string.Join(", ", row)}}}",
            $"members {string.Join(", ", served.Select(p => p.Name).Order(StringComparer.Ordinal))}; a row is {{{servedRow}}}");
    }

    /// <summary>R4.41's paragraph as the errata publishes it: the backticked identifiers that begin <c>curia_skipped</c>, and the one backticked member list in braces. Each count is asserted.</summary>
    private static (string[] Members, string[] Row) PublishedSkippedMembers()
    {
        const string errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
        const string anchor = "**R4.41** ";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, errata)))
            dir = dir.Parent;
        if (dir is null) throw new InvalidOperationException($"{errata} not found above {AppContext.BaseDirectory}");

        var paragraphs = File.ReadAllLines(Path.Combine(dir.FullName, errata)).Where(line => line.StartsWith(anchor, StringComparison.Ordinal)).ToArray();
        if (paragraphs.Length != 1) throw new InvalidOperationException($"{errata} holds {paragraphs.Length} paragraphs that begin \"{anchor}\", expected one");

        var members = Regex.Matches(paragraphs[0], "`(curia_skipped[a-z_]*)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        var rows = Regex.Matches(paragraphs[0], @"`\{([^}`]*)\}`", RegexOptions.CultureInvariant);
        if (members.Length != 2) throw new InvalidOperationException($"{errata}'s R4.41 names {members.Length} members that begin curia_skipped, expected two: {string.Join(", ", members)}");
        if (rows.Count != 1) throw new InvalidOperationException($"{errata}'s R4.41 holds {rows.Count} member lists in braces, expected one");
        return (members, [.. rows[0].Groups[1].Value.Split(',').Select(name => name.Trim())]);
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
