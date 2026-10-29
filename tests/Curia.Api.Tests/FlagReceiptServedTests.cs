using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Tests.Shared;
using Xunit;

using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// PR 3 (errata G47, R9.49), probes CHN33, CHN34 and CHN56 against a real Forum: the flag receipt carries <c>post_served</c>, always present as a JSON
/// boolean, <c>true</c> exactly where an anonymous <c>GET /v1/posts/{id}</c> of the id answers <c>200</c> (so <c>false</c> for a withheld or quarantined post
/// and for a vote's id, <c>true</c> for a verification's), while an invented id stays refused <c>curia/flag/no-such-post</c>.
///
/// <para><b>Oracles.</b> The expected value of each case is the construction's (an answer nobody touched is served; one the moderator's path withheld is not;
/// a vote's id is not a post a reader is served), and, for CHN56, the status of the live anonymous read of the same id, never the receipt's own function. The
/// member's name is R9.49's code span (<see cref="PublishedChainWords.ServedMember"/>). The vote and the verification are appended through
/// <c>AppendStoredPostAsync</c> (the log does not need the tier or the budget to hold them), and each flag is raised by a reporter enrolled for it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagReceiptServedTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private sealed record Flagged(HttpStatusCode Status, string Raw, JsonElement Body);

    private async Task<Flagged> FlagAsync(string postId)
    {
        var reporter = await Scene.EnrolAsync(forum, "reporter", Ct);
        using var response = await reporter.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{postId}/flags", await reporter.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            Encoding.UTF8.GetBytes("{\"kind\":\"incorrect\",\"rationale\":\"It is wrong.\"}"), forum.Now, Ct, contentType: "application/json");
        var raw = await response.Content.ReadAsStringAsync(Ct);
        return new Flagged(response.StatusCode, raw, raw.StartsWith('{') ? JsonDocument.Parse(raw).RootElement.Clone() : default);
    }

    private async Task<(HttpStatusCode Status, string Type)> AnonymousReadAsync(string postId)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        var type = response.StatusCode == HttpStatusCode.OK ? string.Empty : JsonDocument.Parse(raw).RootElement.GetProperty("type").GetString()!;
        return (response.StatusCode, type);
    }

    /// <summary>One post per case the plan names, with the value the construction implies for <c>post_served</c>.</summary>
    private async Task<(string Name, string Id, bool Served)[]> ScenarioAsync()
    {
        var scene = await Scene.BuildAsync(forum, Ct);

        // A revision of the answer that is then withheld stays a served post: the withholding takes the answer and not the revision.
        var revisionOfWithheld = await Scene.PostAsync(
            forum, scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, $"A corrected second answer about {scene.Term}.", scene.Answer2.PostId, scene.Answer2.Digest, forum.Now), Ct);
        await forum.WithholdAsync(scene.Answer2.PostId, Ct);
        await forum.QuarantineAsync(scene.Comment.PostId, Ct);

        // A vote and a verification on the first answer, in the log without the tier or the budget they would otherwise need.
        var (voteId, _) = await forum.AppendStoredPostAsync(scene.Asker.Agent.SignVote(scene.Board, scene.Answer1.Digest, forum.Now), Ct);
        var (verificationId, _) = await forum.AppendStoredPostAsync(scene.Asker.Agent.SignVerification(scene.Board, scene.Answer1.Digest, "reproduced", forum.Now), Ct);

        return
        [
            ("a served answer", scene.Answer1.PostId, true),
            ("a withheld answer", scene.Answer2.PostId, false),
            ("a quarantined post", scene.Comment.PostId, false),
            ("a question", scene.Question.PostId, true),
            ("a served revision", scene.Revision.PostId, true),
            ("a revision of a withheld answer", revisionOfWithheld.PostId, true),
            ("a verification", verificationId, true),
            ("a vote", voteId, false),
        ];
    }

    /// <summary>CHN33. The receipt of a flag carries <c>post_served</c> as a JSON boolean, <c>true</c> or <c>false</c> as the construction implies, for every case.</summary>
    [Fact]
    public async Task R9_49_CHN33_TheReceiptStatesWhetherThePostIsServed()
    {
        var member = PublishedChainWords.ServedMember();
        var problems = new List<string>();
        foreach (var (name, id, served) in await ScenarioAsync())
        {
            var flag = await FlagAsync(id);
            if (flag.Status != HttpStatusCode.Created) { problems.Add($"{name}: {(int)flag.Status} {flag.Raw}"); continue; }
            if (!flag.Body.TryGetProperty(member, out var value)) problems.Add($"{name}: Expected: member {member} Actual: absent from {flag.Raw}");
            else if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) problems.Add($"{name}: {member} is not a boolean: {value.GetRawText()}");
            else if (value.GetBoolean() != served) problems.Add($"{name}: Expected: {served} Actual: {value.GetBoolean()}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// CHN34. No new oracle: an invented id is still <c>404</c> <c>curia/flag/no-such-post</c> (a control); a withheld post's <c>false</c> equals what an anonymous
    /// read says (<c>404</c> <c>curia/posts/withheld</c>); a vote's id is <c>201</c> with <c>false</c> while the read answers <c>404</c> <c>curia/posts/not-found</c>,
    /// and a verification's is <c>201</c>, <c>true</c> and <c>200</c> (ruling 21).
    /// </summary>
    [Fact]
    public async Task R9_49_CHN34_TheMemberIsNoNewOracle()
    {
        var member = PublishedChainWords.ServedMember();
        var cases = await ScenarioAsync();
        string Id(string name) => cases.Single(c => c.Name == name).Id;

        var invented = await FlagAsync("01M6FFFF000000000000000000");
        Assert.True(invented.Status == HttpStatusCode.NotFound && invented.Raw.Contains("curia/flag/no-such-post", StringComparison.Ordinal), $"an invented id: {(int)invented.Status} {invented.Raw}");

        var problems = new List<string>();
        foreach (var (name, status, type, served) in new[]
        {
            ("a withheld answer", HttpStatusCode.NotFound, "curia/posts/withheld", false),
            ("a vote", HttpStatusCode.NotFound, "curia/posts/not-found", false),
            ("a verification", HttpStatusCode.OK, string.Empty, true),
        })
        {
            var read = await AnonymousReadAsync(Id(name));
            if (read != (status, type)) problems.Add($"{name}: the anonymous read is {(int)read.Status} {read.Type}, not {(int)status} {type}");
            var flag = await FlagAsync(Id(name));
            if (flag.Status != HttpStatusCode.Created) problems.Add($"{name}: the flag is {(int)flag.Status}");
            else if (!flag.Body.TryGetProperty(member, out var value)) problems.Add($"{name}: Expected: {member} {served} Actual: absent");
            else if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.GetBoolean() != served) problems.Add($"{name}: Expected: {served} Actual: {value.GetRawText()}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>CHN56 (a property over the scene). For every kind, <c>post_served</c> is <c>true</c> exactly where an anonymous read of the same id answers <c>200</c>; the expected value is read from the live route.</summary>
    [Fact]
    public async Task R9_49_CHN56_PostServedEqualsTheAnonymousRead()
    {
        var member = PublishedChainWords.ServedMember();
        var problems = new List<string>();
        var checkedCases = 0;
        foreach (var (name, id, _) in await ScenarioAsync())
        {
            var read = await AnonymousReadAsync(id);
            var flag = await FlagAsync(id);
            checkedCases++;
            if (flag.Status != HttpStatusCode.Created) { problems.Add($"{name}: {(int)flag.Status} {flag.Raw}"); continue; }
            if (!flag.Body.TryGetProperty(member, out var value)) problems.Add($"{name}: Expected: member {member} (the read answers {(int)read.Status}) Actual: absent");
            else if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || value.GetBoolean() != (read.Status == HttpStatusCode.OK))
                problems.Add($"{name}: the read answers {(int)read.Status}, post_served is {value.GetRawText()}");
        }

        Assert.Equal(8, checkedCases);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
