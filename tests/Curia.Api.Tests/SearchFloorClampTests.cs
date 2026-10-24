using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.54 (rev.) and R9.31 on a real Forum whose search surface is configured at V1: a request below the
/// floor is served at the floor and the floor block says so. Answers are posted through the API by an
/// agent whose owner the operator attested and whose tier the clock has promoted to T1, as
/// <see cref="SearchRemovalAndParameterTests"/> does.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class SearchFloorClampTests(ConfiguredFloorForumFixture forum) : IClassFixture<ConfiguredFloorForumFixture>
{
    private const string PostsUrl = "http://localhost/v1/posts";
    private const int AnswersPosted = 3;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<(string Board, string Term, string QuestionId)> SeedAsync(CancellationToken ct)
    {
        var http = forum.Client;
        var board = "board-" + Suffix();
        var term = "zclamp" + Suffix();

        var asker = ForumAgent.Create($"https://agents.example/asker-{Suffix()}", "asker-" + Suffix());
        var (askerDpop, askerToken) = await asker.AuthenticateAsync(http, KeyActRequests.TokenEndpoint, forum.Now, ct);
        using var asked = await askerDpop.PostAsync(http, PostsUrl, askerToken, asker.SignQuestion(board, $"how does {term} work", $"About {term}", forum.Now), forum.Now, ct);
        var askedText = await asked.Content.ReadAsStringAsync(ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, askedText);
        using var question = JsonDocument.Parse(askedText);
        var questionId = question.RootElement.GetProperty("post_id").GetString()!;

        var author = ForumAgent.Create($"https://agents.example/answerer-{Suffix()}", "answerer-" + Suffix());
        var (dpop, token) = await author.AuthenticateAsync(http, KeyActRequests.TokenEndpoint, forum.Now, ct);
        await forum.AttestOwnerAsync(author.AgentId, ct, owner: $"owner:answerer-{Suffix()}");
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            using var warmup = await dpop.PostAsync(http, PostsUrl, token, author.SignQuestion(board, $"warmup {i} {nonce}", $"Warmup {i} {nonce}", forum.Now), forum.Now, ct);
            Assert.Equal(HttpStatusCode.Created, warmup.StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1));

        for (var i = 0; i < AnswersPosted; i++)
        {
            var fresh = await dpop.GetTokenAsync(http, KeyActRequests.TokenEndpoint, forum.Now, ct);
            using var response = await dpop.PostAsync(http, PostsUrl, fresh, author.SignAnswer(board, $"Use the {term} keepalive, variant {Suffix()}.", questionId, forum.Now), forum.Now, ct);
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync(ct));
        }

        return (board, term, questionId);
    }

    private async Task<JsonDocument> PageAsync(string query, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/search?{query}", UriKind.Relative), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static string[] Ids(JsonDocument page) =>
        [.. page.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!)];

    private static JsonElement Member(JsonDocument page, string name)
    {
        Assert.True(page.RootElement.GetProperty("floor").TryGetProperty(name, out var member), $"the floor block states no {name} (R9.31)");
        return member;
    }

    [Fact]
    public async Task R10_54_AConfiguredDeploymentServesALowRequestAtItsFloor()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, term, questionId) = await SeedAsync(ct);

        using var page = await PageAsync($"q={term}&board={board}&min_verification=V0", ct);

        Assert.Equal([questionId], Ids(page));
    }

    [Fact]
    public async Task R9_31_TheFloorBlockStatesTheClamp()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, term, _) = await SeedAsync(ct);

        using var clamped = await PageAsync($"q={term}&board={board}&min_verification=V0", ct);
        Assert.Equal(
            ("V0", "V0", true, "V1", "configured", AnswersPosted),
            (Member(clamped, "published_default").GetString(),
                Member(clamped, "requested_min_verification").GetString(),
                Member(clamped, "clamped").GetBoolean(),
                Member(clamped, "min_verification").GetString(),
                Member(clamped, "source").GetString(),
                Member(clamped, "excluded_by_floor").GetInt32()));

        // The control: no request states a null and a false, as members that are present.
        using var none = await PageAsync($"q={term}&board={board}", ct);
        Assert.Equal(JsonValueKind.Null, Member(none, "requested_min_verification").ValueKind);
        Assert.False(Member(none, "clamped").GetBoolean());
        Assert.Equal(("V1", "configured"), (Member(none, "min_verification").GetString(), Member(none, "source").GetString()));
    }

    [Theory]
    [InlineData("V1")]
    [InlineData("V2")]
    public async Task R9_31_ARequestAtOrAboveTheFloorIsNotClamped(string level)
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, term, _) = await SeedAsync(ct);

        using var page = await PageAsync($"q={term}&board={board}&min_verification={level}", ct);

        Assert.Equal(
            (level, level, false, "requested"),
            (Member(page, "min_verification").GetString(),
                Member(page, "requested_min_verification").GetString(),
                Member(page, "clamped").GetBoolean(),
                Member(page, "source").GetString()));
    }

    /// <summary>
    /// The three names R9.31 lists are read from the errata, and each must be a member the floor block serves
    /// that R9.21 and R9.30 did not already: a name added to the code alone, or dropped from the paragraph alone,
    /// fails here.
    /// </summary>
    [Fact]
    public async Task R9_31_TheMemberNamesAreTheOnesTheErrataPublishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, term, _) = await SeedAsync(ct);
        string[] earlier = ["surface", "min_verification", "source", "applies_to", "not_applicable_to", "excluded_by_floor", "excluded_disputed"];

        using var page = await PageAsync($"q={term}&board={board}", ct);
        var served = page.RootElement.GetProperty("floor").EnumerateObject().Select(p => p.Name).Where(n => !earlier.Contains(n, StringComparer.Ordinal)).Order(StringComparer.Ordinal);

        Assert.Equal(PublishedFloorMembers.Names().Order(StringComparer.Ordinal), served);
    }

    [Fact]
    public async Task R9_24_NoMemberNameLooksLikeARemovalCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, term, _) = await SeedAsync(ct);

        using var page = await PageAsync($"q={term}&board={board}", ct);

        // Every member the block serves, not only the ones the errata lists: the count probes read names.
        var counts = new[] { "excluded_by_floor", "excluded_disputed" };
        foreach (var name in page.RootElement.GetProperty("floor").EnumerateObject().Select(p => p.Name).Except(counts, StringComparer.Ordinal))
            Assert.DoesNotMatch(RemovalWord(), name);
    }

    [GeneratedRegex("remov|dropp|exclud|filtered", RegexOptions.CultureInvariant)]
    private static partial Regex RemovalWord();
}

/// <summary>
/// R9.31's members as the errata publishes them, read from the errata and not from the code: the backticked
/// names in the sentence that begins "SHALL carry three members:" up to its first full stop.
/// </summary>
internal static partial class PublishedFloorMembers
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
    private const string Anchor = "SHALL carry three members:";

    /// <summary>The names, in the order the errata lists them; length three is asserted before they are returned.</summary>
    public static string[] Names()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata)))
            dir = dir.Parent;

        if (dir is null)
            throw new InvalidOperationException($"{Errata} not found above {AppContext.BaseDirectory}");

        var text = Whitespace().Replace(File.ReadAllText(Path.Combine(dir.FullName, Errata)), " ");

        var start = text.IndexOf(Anchor, StringComparison.Ordinal);
        if (start < 0)
            throw new InvalidOperationException($"{Errata} has no R9.31 sentence \"{Anchor}\"");

        start += Anchor.Length;
        var end = text.IndexOf(". ", start, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException($"{Errata}'s R9.31 sentence has no full stop after its list");

        var names = Backticked().Matches(text[start..end]).Select(m => m.Groups[1].Value).ToArray();
        if (names.Length != 3)
            throw new InvalidOperationException($"{Errata}'s R9.31 lists {names.Length} backticked names; the parse read something else");

        return names;
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();
}
