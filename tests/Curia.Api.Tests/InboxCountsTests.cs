using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What a fact that talks to <c>GET /v1/inbox</c> needs, and a ledger of what it signed. The ledger is the oracle: the
/// tests count the questions they asked as whom and the answers they signed, and never ask the selector or the route what
/// they think (R9.39, errata G37; the probe's whole point is that two readings of one sentence once agreed).
/// </summary>
internal sealed class InboxProbe(ForumFixture forum)
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>An agent a test enrolled.</summary>
    internal sealed record Member(ForumAgent Agent, DpopClient Dpop)
    {
        internal string Id => Agent.AgentId;
    }

    /// <summary>A question a test signed: who asked it, where, and with which tags.</summary>
    internal sealed record Asked(string Id, string AskerId, string Board, string[] Tags);

    /// <summary>One page of an inbox walk.</summary>
    internal sealed record Page(HttpStatusCode Status, string[] Ids, string? NextCursor, int Open, int Own, int Answered, string Body);

    internal List<Asked> Questions { get; } = [];

    /// <summary>Each answer a test signed: the answerer and the question.</summary>
    internal List<(string AnswererId, string QuestionId)> Answers { get; } = [];

    internal static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    internal ForumFixture Forum => forum;

    internal async Task<Member> EnrolAsync(string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{Suffix()}", stem + "-" + Suffix());
        var (dpop, _) = await agent.AuthenticateAsync(forum.Client, TokenEndpoint, forum.Now, ct);
        return new Member(agent, dpop);
    }

    internal async Task<(HttpStatusCode Status, string Body)> PostAsync(Member member, byte[] wire, CancellationToken ct)
    {
        var client = forum.Client;
        using var response = await member.Dpop.PostAsync(
            client, PostsUrl, await member.Dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct), wire, forum.Now, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static string IdOf(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("post_id").GetString()!;
    }

    internal async Task<Asked> AskAsync(Member asker, string board, string title, CancellationToken ct, params string[] tags)
    {
        var (status, body) = await PostAsync(
            asker, asker.Agent.SignQuestion(board, "a body " + Guid.NewGuid().ToString("N"), title, forum.Now, tags.Length == 0 ? null : tags), ct);
        Assert.True(status == HttpStatusCode.Created, $"{status}: {body}");
        var asked = new Asked(IdOf(body), asker.Id, board, tags);
        Questions.Add(asked);
        return asked;
    }

    /// <summary>Signs an answer as <paramref name="answerer"/>, who must be T1, and records it.</summary>
    internal async Task<string> AnswerAsync(Member answerer, Asked question, CancellationToken ct)
    {
        var (status, body) = await PostAsync(
            answerer, answerer.Agent.SignAnswer(question.Board, "an answer " + Guid.NewGuid().ToString("N"), question.Id, forum.Now), ct);
        Assert.True(status == HttpStatusCode.Created, $"{status}: {body}");
        Answers.Add((answerer.Id, question.Id));
        return IdOf(body);
    }

    internal async Task<string> CommentAsync(Member commenter, string board, string parent, CancellationToken ct)
    {
        var (status, body) = await PostAsync(
            commenter, commenter.Agent.Sign(PostKind.Comment, board, "a comment " + Guid.NewGuid().ToString("N"), null, parent, forum.Now), ct);
        Assert.True(status == HttpStatusCode.Created, $"{status}: {body}");
        return IdOf(body);
    }

    /// <summary>
    /// Table 11: answering needs T1 (48 hours, three clean questions, an owner verified by the operator, R4.30). The three
    /// warm-up questions are on a board of their own and are in the ledger.
    /// </summary>
    internal async Task<Member> EnrolT1Async(string stem, CancellationToken ct)
    {
        var member = await EnrolAsync(stem, ct);
        await forum.AttestOwnerAsync(member.Id, ct);
        var warmup = "warmup-" + Suffix();
        for (var i = 0; i < 3; i++)
            await AskAsync(member, warmup, $"Warmup {i} {Suffix()}", ct);
        forum.Clock.Advance(TimeSpan.FromHours(49));
        return member;
    }

    /// <summary>Lets the three-per-day budget of a T0 asker refill.</summary>
    internal void NextDay() => forum.Clock.Advance(TimeSpan.FromHours(25));

    /// <summary><c>GET /v1/inbox</c> as <paramref name="member"/>; <paramref name="query"/> is the query string with its <c>?</c>, or empty.</summary>
    internal async Task<(HttpStatusCode Status, string Body)> InboxRawAsync(Member member, string query, CancellationToken ct)
    {
        var client = forum.Client;
        var token = await member.Dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"http://localhost/v1/inbox{query}").PathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", member.Dpop.Proof("GET", "http://localhost/v1/inbox", forum.Now, token, nonce: null));
        using var response = await client.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    internal async Task<Page> InboxPageAsync(Member member, string query, CancellationToken ct)
    {
        var (status, body) = await InboxRawAsync(member, query, ct);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}");
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        return new Page(
            status,
            [.. root.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post_id").GetString()!)],
            root.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null,
            root.GetProperty("open_before_exclusions").GetInt32(),
            root.GetProperty("excluded_as_own").GetInt32(),
            root.GetProperty("excluded_as_already_answered").GetInt32(),
            body);
    }

    /// <summary>Every page of the inbox for <paramref name="query"/> (with its <c>?</c>) at <paramref name="limit"/>, following <c>next_cursor</c> unaltered; bounded.</summary>
    internal async Task<List<Page>> WalkAsync(Member member, string filters, int limit, CancellationToken ct)
    {
        var pages = new List<Page>();
        string? cursor = null;
        for (var i = 0; i < 40; i++)
        {
            var query = $"?{filters}&limit={limit.ToString(CultureInfo.InvariantCulture)}"
                + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            var page = await InboxPageAsync(member, query, ct);
            pages.Add(page);
            if (page.NextCursor is null) return pages;
            cursor = page.NextCursor;
        }

        Assert.Fail("a walk of the inbox did not end within forty pages");
        return pages;
    }

    /// <summary>
    /// The three counts R9.39 asks for, read from the ledger and from nothing the Forum said: among the questions asked that
    /// match <paramref name="board"/> and every one of <paramref name="tags"/> (no question in these facts is resolved), those
    /// <paramref name="readerId"/> asked, and those it answered without asking.
    /// </summary>
    internal (int Open, int Own, int Answered, string[] Served) Truth(string readerId, string? board, params string[] tags)
    {
        var matching = Questions.Where(q =>
            (board is null || q.Board == board)
            && tags.All(t => q.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))).ToArray();
        var own = matching.Where(q => q.AskerId == readerId).ToArray();
        var answered = matching.Where(q => q.AskerId != readerId && Answers.Contains((readerId, q.Id))).ToArray();
        var served = matching.Except(own).Except(answered).Select(q => q.Id).ToArray();
        return (matching.Length, own.Length, answered.Length, served);
    }
}

/// <summary>
/// R9.39 (errata G37): <c>excluded_as_own</c> and <c>excluded_as_already_answered</c> are the questions the caller asked and
/// the questions it answered without asking, among those that match <c>board</c> and <c>tags</c>, whatever the <c>cursor</c>
/// and the <c>limit</c>; <c>open_before_exclusions</c> is those plus the questions it could be served. Before this, the second
/// was computed as what remained after the page, which is right only when the page holds every open question, which is every
/// fixture the tree had.
///
/// <para><b>Oracle.</b> The ledger <see cref="InboxProbe"/> keeps of what the test asked as whom and answered as whom. Never
/// the selector and never the route's own earlier number. <b>Non-vacuity</b> (vacuity question 3): every fact that is meant to
/// separate the formulas first asserts that the corpus it reads holds more questions than its <c>limit</c> serves, because a
/// corpus that fits one page cannot tell the remainder from the count.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class InboxCountsTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    /// <summary>
    /// A reader (T1) and a corpus on two boards. Board B: six questions by X (the first three, a day, the next three), one by
    /// Z, and the reader's own; the reader answered two of X's. Board C: three questions by Y and the reader's own; the reader
    /// answered one of Y's. Tag <c>Red</c> is on the reader's own B question, on X's first three and fifth, and on Y's first.
    /// </summary>
    private sealed record Scene(InboxProbe Probe, InboxProbe.Member Reader, string BoardB, string BoardC, string Red)
    {
        internal string ReaderId => Reader.Id;
    }

    private async Task<Scene> BuildAsync(CancellationToken ct)
    {
        var probe = new InboxProbe(forum);
        var reader = await probe.EnrolT1Async("counts-reader", ct);
        var x = await probe.EnrolAsync("counts-x", ct);
        var y = await probe.EnrolAsync("counts-y", ct);
        var z = await probe.EnrolAsync("counts-z", ct);
        var s = InboxProbe.Suffix();
        string b = "board-b-" + s, c = "board-c-" + s, red = "red-" + s, blue = "blue-" + s;

        var q1 = await probe.AskAsync(x, b, "Q1 " + s, ct, red);
        var q2 = await probe.AskAsync(x, b, "Q2 " + s, ct, red);
        await probe.AskAsync(x, b, "Q3 " + s, ct, red);
        probe.NextDay();
        await probe.AskAsync(x, b, "Q4 " + s, ct, blue);
        await probe.AskAsync(x, b, "Q5 " + s, ct, red);
        await probe.AskAsync(x, b, "Q6 " + s, ct, blue);
        await probe.AskAsync(z, b, "Z1 " + s, ct, blue);
        var c1 = await probe.AskAsync(y, c, "C1 " + s, ct, red);
        await probe.AskAsync(y, c, "C2 " + s, ct, blue);
        await probe.AskAsync(y, c, "C3 " + s, ct, blue);
        await probe.AskAsync(reader, b, "R-B " + s, ct, red);
        await probe.AskAsync(reader, c, "R-C " + s, ct, blue);

        await probe.AnswerAsync(reader, q1, ct);
        await probe.AnswerAsync(reader, q2, ct);
        await probe.AnswerAsync(reader, c1, ct);
        return new Scene(probe, reader, b, c, red);
    }

    private static string Numbers(int open, int own, int answered) =>
        string.Create(CultureInfo.InvariantCulture, $"open {open}, own {own}, answered {answered}");

    private static string Numbers(InboxProbe.Page page) => Numbers(page.Open, page.Own, page.Answered);

    private static string Numbers((int Open, int Own, int Answered, string[] Served) truth) => Numbers(truth.Open, truth.Own, truth.Answered);

    /// <summary>
    /// PD21: a reader who asked one question and answered two, with others open, at <c>limit=2</c>: the second number is the
    /// answers, not the page's remainder.
    /// </summary>
    [Fact]
    public async Task R9_39_AlreadyAnsweredIsTheQuestionsTheCallerAnsweredNotThePagesRemainder()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await BuildAsync(ct);
        var truth = scene.Probe.Truth(scene.ReaderId, scene.BoardB);
        Assert.True(truth.Served.Length > 2, "the corpus must hold more served questions than the limit, or the formulas cannot be told apart");
        Assert.Equal(Numbers(8, 1, 2), Numbers(truth));

        var page = await scene.Probe.InboxPageAsync(scene.Reader, $"?board={scene.BoardB}&limit=2", ct);

        Assert.Equal(Numbers(truth), Numbers(page));
    }

    /// <summary>PD21, second row: a reader who answered nothing, with ten open and a page of two, is told it answered none.</summary>
    [Fact]
    public async Task R9_39_AReaderWhoAnsweredNothingIsToldItAnsweredNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new InboxProbe(forum);
        var asker = await probe.EnrolAsync("counts-ten", ct);
        var reader = await probe.EnrolAsync("counts-fresh", ct);
        var board = "board-ten-" + InboxProbe.Suffix();
        for (var i = 0; i < 10; i++)
        {
            if (i > 0 && i % 3 == 0) probe.NextDay();
            await probe.AskAsync(asker, board, $"Ten {i} {InboxProbe.Suffix()}", ct);
        }

        var truth = probe.Truth(reader.Id, board);
        Assert.True(truth.Served.Length > 2, "the corpus must hold more served questions than the limit, or the formulas cannot be told apart");
        Assert.Equal(Numbers(10, 0, 0), Numbers(truth));

        var fresh = await probe.InboxPageAsync(reader, $"?board={board}&limit=2", ct);

        Assert.Equal(Numbers(truth), Numbers(fresh));
    }

    /// <summary>PD22: the three numbers are the same on every page of a walk.</summary>
    [Fact]
    public async Task R9_39_TheCountsDoNotChangeFromPageToPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await BuildAsync(ct);
        var truth = scene.Probe.Truth(scene.ReaderId, scene.BoardB);

        var pages = await scene.Probe.WalkAsync(scene.Reader, $"board={scene.BoardB}", 2, ct);

        Assert.True(pages.Count >= 3, $"a walk of {pages.Count} page(s) cannot show a count change from page to page");
        for (var i = 1; i < pages.Count; i++)
            Assert.Equal(Numbers(pages[0]), Numbers(pages[i]));
        foreach (var page in pages)
            Assert.Equal(Numbers(truth), Numbers(page));
    }

    /// <summary>PD23: the numbers do not depend on the limit, and they add up to what a complete walk listed.</summary>
    [Fact]
    public async Task R9_39_TheCountsDoNotDependOnTheLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await BuildAsync(ct);
        var truth = scene.Probe.Truth(scene.ReaderId, scene.BoardB);
        Assert.True(truth.Served.Length > 3, "the limits must include some below the number of served questions");

        foreach (var limit in new[] { 1, 2, 3, 100 })
        {
            var pages = await scene.Probe.WalkAsync(scene.Reader, $"board={scene.BoardB}", limit, ct);
            var listed = pages.SelectMany(p => p.Ids).ToArray();

            Assert.Equal(truth.Served.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
            foreach (var page in pages)
                Assert.Equal(Numbers(truth), Numbers(page));

            var first = pages[0];
            Assert.Equal(first.Open, first.Own + first.Answered + listed.Length);
        }
    }

    /// <summary>PD24: the counts are scoped to the request's <c>board</c> and <c>tags</c>.</summary>
    [Fact]
    public async Task R9_39_TheCountsAreScopedToTheRequestsFilters()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await BuildAsync(ct);

        var rows = new (string Query, string? Board, string[] Tags)[]
        {
            ($"board={scene.BoardC}", scene.BoardC, []),
            ($"board={scene.BoardB}&tags={scene.Red}", scene.BoardB, [scene.Red]),
            ($"tags={scene.Red}", null, [scene.Red]),
        };

        foreach (var (query, board, tags) in rows)
        {
            var truth = scene.Probe.Truth(scene.ReaderId, board, tags);
            Assert.True(truth.Served.Length > 1, $"'{query}': the corpus must hold more served questions than the limit");

            var page = await scene.Probe.InboxPageAsync(scene.Reader, $"?{query}&limit=1", ct);

            Assert.Equal(Numbers(truth), Numbers(page));
        }

        // A board that holds none of the answered questions says 0 answered.
        var other = new InboxProbe(forum);
        var asker = await other.EnrolAsync("counts-other", ct);
        var elsewhere = "board-none-" + InboxProbe.Suffix();
        await other.AskAsync(asker, elsewhere, "Elsewhere " + InboxProbe.Suffix(), ct);
        var none = await scene.Probe.InboxPageAsync(scene.Reader, $"?board={elsewhere}&limit=1", ct);
        Assert.Equal(Numbers(1, 0, 0), Numbers(none));
    }

    /// <summary>PD25: the shape of every fixture the tree had, a limit above the open set. The case that hid the defect, kept so a fix is shown not to break it.</summary>
    [Fact]
    public async Task R9_39_ACorpusThatFitsOnePageStillCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await BuildAsync(ct);
        var truth = scene.Probe.Truth(scene.ReaderId, scene.BoardB);
        Assert.True(truth.Served.Length < 100, "the limit must exceed the served questions");

        var page = await scene.Probe.InboxPageAsync(scene.Reader, $"?board={scene.BoardB}&limit=100", ct);

        Assert.Equal(Numbers(truth), Numbers(page));
        Assert.Equal(truth.Served.Order(StringComparer.Ordinal), page.Ids.Order(StringComparer.Ordinal));
        Assert.Null(page.NextCursor);
    }
}
