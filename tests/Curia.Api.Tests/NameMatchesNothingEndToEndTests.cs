using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Api.Tests.Fuzz;
using Curia.Application.Projections;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Api.Tests.NameProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R9.55 (errata G62), NMN4 to NMN14 and NMN44, at the routes: <c>GET /v1/search</c> and <c>GET /v1/inbox</c> state <c>unmatched_filters</c>, the <c>board</c> and
/// <c>author</c> filters the request named that no served post bears, on every <c>200</c>, in the order board then author, judged on each filter alone and ordinally, echoing no
/// value, outside the cursor's binding and never on a refusal.
///
/// <para><b>Oracles.</b> The member's expected value is never taken from <see cref="NamedFilters"/>: it is what the test stored (a board and an author it made, a post it
/// withheld through the moderation writer) or, in NMN8, what the board listing and a search over the whole corpus show for the same name, a different route and a different
/// artifact. Each refusing row has its control: the same request with a name that is borne is the first assertion and is green on the seam, so the red is the rule's and not the
/// request's. The names and the sentences are R9.55 words (<see cref="PublishedNameWords"/>).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class NameMatchesNothingEndToEndTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The words check: the member and its two names are the ones the facts below use (a control, green on the seam).</summary>
    [Fact]
    public void R9_55_NMN4_ThePublishedWordsAreTheOnesTheFactsUse()
    {
        Assert.Equal("unmatched_filters", PublishedNameWords.UnmatchedMember());
        Assert.Equal(["board", "author"], PublishedNameWords.UnmatchedNames());
    }

    /// <summary>
    /// NMN4. A board typed in the wrong case over a stored one: <c>200</c>, <c>unmatched_filters</c> is <c>[board]</c> and the results are empty; the control, the stored spelling, is
    /// <c>[]</c> with its post. Predicted red on the seam: <c>[]</c> for both.
    /// </summary>
    [Fact]
    public async Task R9_55_NMN4_ABoardInTheWrongCaseIsUnmatchedAndTheStoredSpellingIsNot()
    {
        var board = Board();
        await StoreQuestionAsync(forum, board, Ct);

        var matched = await SearchAsync(forum.Client, $"board={board}", Ct);
        var unmatched = await SearchAsync(forum.Client, $"board={Upper(board)}", Ct);

        Said(
            ["the spelling stored: 200, [], 1 result", "the wrong case: 200, [board], 0 results"],
            [$"the spelling stored: {(int)matched.Status}, {matched.Unmatched}, {matched.Results} result{(matched.Results == 1 ? string.Empty : "s")}", $"the wrong case: {(int)unmatched.Status}, {unmatched.Unmatched}, {unmatched.Results} result{(unmatched.Results == 1 ? string.Empty : "s")}"]);
    }

    /// <summary>NMN5. An author typed as a slug instead of the identifier is <c>[author]</c>; the full identifier is <c>[]</c> with its post. Predicted red: <c>[]</c> for the slug.</summary>
    [Fact]
    public async Task R9_55_NMN5_AnAuthorTypedAsASlugIsUnmatchedAndTheFullIdentifierIsNot()
    {
        var (authorId, _) = await StoreQuestionAsync(forum, Board(), Ct);

        var matched = await SearchAsync(forum.Client, "author=" + Uri.EscapeDataString(authorId), Ct);
        var slug = await SearchAsync(forum.Client, "author=" + Uri.EscapeDataString(Slug(authorId)), Ct);

        // Each filter is judged alone and an empty page is never the word: an author who holds a served post that no term finds, and one the page does not carry, is matched.
        var board = Board();
        var (holderId, _) = await StoreQuestionAsync(forum, board, Ct);
        var absentFromPage = await SearchAsync(forum.Client, "q=zzqqxxvvnomatch&author=" + Uri.EscapeDataString(holderId), Ct);
        var absentWithBoard = await SearchAsync(forum.Client, $"q=zzqqxxvvnomatch&board={board}&author=" + Uri.EscapeDataString(holderId), Ct);

        Said(
            ["the full identifier: 200, [], 1 result", "the slug: 200, [author], 0 results", "an author with posts that no term finds: 200, [], 0 results", "the same author and its board: 200, [], 0 results"],
            [$"the full identifier: {(int)matched.Status}, {matched.Unmatched}, {matched.Results} result{(matched.Results == 1 ? string.Empty : "s")}", $"the slug: {(int)slug.Status}, {slug.Unmatched}, {slug.Results} result{(slug.Results == 1 ? string.Empty : "s")}",
                $"an author with posts that no term finds: {(int)absentFromPage.Status}, {absentFromPage.Unmatched}, {absentFromPage.Results} results", $"the same author and its board: {(int)absentWithBoard.Status}, {absentWithBoard.Unmatched}, {absentWithBoard.Results} results"]);
    }

    /// <summary>NMN6. Both together, both unmatched: <c>[board,author]</c> in that order whatever the order the request named them; one matched and one not names only the other.</summary>
    [Fact]
    public async Task R9_55_NMN6_BothFiltersAreListedBoardThenAuthor()
    {
        var board = Board();
        var (authorId, _) = await StoreQuestionAsync(forum, board, Ct);
        var author = Uri.EscapeDataString(authorId);
        var slug = Uri.EscapeDataString(Slug(authorId));

        var neither = await SearchAsync(forum.Client, $"author={slug}&board={Upper(board)}", Ct);
        var boardOnly = await SearchAsync(forum.Client, $"author={author}&board={Upper(board)}", Ct);
        var authorOnly = await SearchAsync(forum.Client, $"author={slug}&board={board}", Ct);
        var both = await SearchAsync(forum.Client, $"author={author}&board={board}", Ct);

        Said(
            ["both matched: []", "board unmatched: [board]", "author unmatched: [author]", "neither matched: [board,author]"],
            [$"both matched: {both.Unmatched}", $"board unmatched: {boardOnly.Unmatched}", $"author unmatched: {authorOnly.Unmatched}", $"neither matched: {neither.Unmatched}"]);
    }

    /// <summary>
    /// NMN7. The word is about the name and not about an empty page (the cheap wrong implementation fires on every empty page): a board with posts and a term that matches none is
    /// <c>[]</c> with no result. A control, green on the seam; it is what FNMN1 turns red.
    /// </summary>
    [Fact]
    public async Task R9_55_NMN7_ABoardThatHasPostsIsMatchedWhateverTheTermFinds()
    {
        var board = Board();
        await StoreQuestionAsync(forum, board, Ct);

        var reply = await SearchAsync(forum.Client, $"board={board}&q=zzqqxxvvnomatch", Ct);

        Said("200, [], 0 results", $"{(int)reply.Status}, {reply.Unmatched}, {reply.Results} results");
    }

    /// <summary>
    /// NMN8. Derived from the listing and from a search over the whole corpus, and not from the function (vacuity question 4): for a board, the expected word is whether
    /// <c>GET /v1/boards/{board}/posts</c> lists nothing (a board the listing route refuses, a NUL, is none either); for an author, whether <c>GET /v1/search?author=</c>, which
    /// names no board or term, returns nothing. The rows: a served board, a board whose only post is withheld, a board whose question is withheld but whose comment is served, the
    /// upper-case spelling of a served board, a NUL board; an author with a post, an author whose only post is withheld, a slug. Predicted red on the seam: the withheld-only
    /// board, the upper-case spelling, the NUL board and the author rows that bear nothing.
    /// </summary>
    [Fact]
    public async Task R9_55_NMN8_TheWordAgreesWithWhatTheListingAndTheSearchShow()
    {
        var served = Board();
        var withheldOnly = Board();
        var withheldWithComment = Board();
        await StoreQuestionAsync(forum, served, Ct);
        var (withheldAuthor, withheldPost) = await StoreQuestionAsync(forum, withheldOnly, Ct);
        await forum.WithholdAsync(withheldPost.PostId, Ct);
        var (_, commentedQuestion) = await StoreQuestionAsync(forum, withheldWithComment, Ct);
        await StoreCommentAsync(forum, withheldWithComment, commentedQuestion.PostId, Ct);
        await forum.WithholdAsync(commentedQuestion.PostId, Ct);
        var (servedAuthor, _) = await StoreQuestionAsync(forum, Board(), Ct);

        var boards = new[] { served, withheldOnly, withheldWithComment, Upper(served), "nmn%00nul" };
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var board in boards)
        {
            // The listing route answers 400 for a name holding U+0000, and a path holding one cannot be built: that row is a board the listing cannot list.
            var listed = board.Contains("%00", StringComparison.Ordinal) ? -1 : await ListedAsync(forum.Client, board, Ct);
            expected.Add($"board {board}: {(listed > 0 ? "[]" : "[board]")}");
            actual.Add($"board {board}: {(await SearchAsync(forum.Client, "board=" + board, Ct)).Unmatched}");
        }

        foreach (var author in new[] { servedAuthor, withheldAuthor, Slug(servedAuthor) })
        {
            var found = await SearchAsync(forum.Client, "author=" + Uri.EscapeDataString(author), Ct);
            expected.Add($"author {author}: {(found.Results > 0 ? "[]" : "[author]")}");
            actual.Add($"author {author}: {found.Unmatched}");
        }

        Said(expected, actual);
    }

    /// <summary>
    /// NMN9. A value no board name or agent identifier can hold (U+0000, an unpaired surrogate as the percent-encoded bytes of one, 70 scalar values, an author over the cap) is
    /// answered <c>200</c>, never <c>5xx</c>, and listed. Predicted red on the seam: not listed.
    /// </summary>
    [Fact]
    public async Task R9_55_NMN9_AValueNoNameCanHoldIsListedAndNeverAServerFault()
    {
        var expected = new List<string>();
        var actual = new List<string>();
        var boards = new[] { "ru%00st", "ru%ED%A0%80st", new string('r', 70), "%EF%BF%BE" };
        foreach (var board in boards)
        {
            var reply = await SearchAsync(forum.Client, "board=" + board, Ct);
            expected.Add($"board {board.Length} chars: 200, [board]");
            actual.Add($"board {board.Length} chars: {(int)reply.Status}, {reply.Unmatched}");
        }

        foreach (var author in new[] { "https%3A%2F%2Fagents.example%2Fa%00", new string('a', 600), "%ED%A0%80" })
        {
            var reply = await SearchAsync(forum.Client, "author=" + author, Ct);
            expected.Add($"author {author.Length} chars: 200, [author]");
            actual.Add($"author {author.Length} chars: {(int)reply.Status}, {reply.Unmatched}");
        }

        Said(expected, actual);
    }

    /// <summary>
    /// NMN10. The member is outside the cursor's binding (R9.35): a cursor minted by page one of a matched query continues it on page two; the member is the same on both pages;
    /// and a cursor presented with an unmatched author is the R9.35 refusal, unchanged. A control, green on the seam.
    /// </summary>
    [Fact]
    public async Task R9_55_NMN10_TheMemberIsOutsideTheCursorsBinding()
    {
        var board = Board();
        await StoreQuestionAsync(forum, board, Ct);
        await StoreQuestionAsync(forum, board, Ct);

        var first = await SearchAsync(forum.Client, $"board={board}&limit=1", Ct);
        var cursor = first.Json.ValueKind == JsonValueKind.Object && first.Json.TryGetProperty("next_cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        Assert.True(cursor is not null, $"NOT REACHED: page one carried no next_cursor: {first.Body}");

        var second = await SearchAsync(forum.Client, $"board={board}&limit=1&cursor={Uri.EscapeDataString(cursor!)}", Ct);
        var mismatched = await SearchAsync(forum.Client, $"board={board}&author=nobody&limit=1&cursor={Uri.EscapeDataString(cursor!)}", Ct);

        Said(
            ["page one: 200, []", "page two: 200, []", "a cursor under an unmatched author: 400 curia/search/cursor-query-mismatch"],
            [$"page one: {(int)first.Status}, {first.Unmatched}", $"page two: {(int)second.Status}, {second.Unmatched}", $"a cursor under an unmatched author: {mismatched.Says}"]);
    }

    /// <summary>
    /// NMN11. The inbox: a board typed in the wrong case is <c>[board]</c>; a board whose questions are all answered is <c>[]</c> with its counts as they were; <c>author</c> is not a
    /// name the inbox reads (R9.37's refusal is unchanged). Predicted red on the seam: <c>[]</c> for the wrong case.
    /// </summary>
    [Fact]
    public async Task R9_55_NMN11_TheInboxNamesAnUnmatchedBoardAndNotAnAnsweredOne()
    {
        // A board whose every question is answered and accepted, with nothing open on it: the tier-seeding answerer's warm-up questions go to a board of their own, so that a
        // check that judges the board on the open questions alone (FNMN8) has none to find here and says [board] for a board that is served.
        var board = Board();
        var warmupBoard = Board();
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "asker", Ct);
        var question = await AcceptedAnswerScene.PostAsync(forum, asker, asker.Agent.SignQuestion(board, "How is znmn11 settled?", "About znmn11", forum.Now), Ct);
        var answerer = await AcceptedAnswerScene.T1Async(forum, warmupBoard, Ct);
        var answer = await AcceptedAnswerScene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "The way to settle znmn11.", question.PostId, forum.Now), Ct);
        using (var accept = await asker.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{answer.PostId}/accept", await asker.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            System.Text.Encoding.UTF8.GetBytes("{}"), forum.Now, Ct, contentType: "application/json"))
        {
            Assert.True(accept.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await accept.Content.ReadAsStringAsync(Ct));
        }

        var answered = await InboxAsync(forum, forum.Client, asker, $"board={board}", Ct);
        var wrongCase = await InboxAsync(forum, forum.Client, asker, $"board={Upper(board)}", Ct);
        var author = await InboxAsync(forum, forum.Client, asker, "author=someone", Ct);

        Said(
            ["answered board: 200, []", "wrong case: 200, [board]", "author: 400 curia/inbox/unknown-parameter"],
            [$"answered board: {(int)answered.Status}, {answered.Unmatched}", $"wrong case: {(int)wrongCase.Status}, {wrongCase.Unmatched}", $"author: {author.Says}"]);
    }

    /// <summary>
    /// NMN12. No filter named: the member is present and <c>[]</c> on every <c>200</c> of the search route and of the inbox, so that absent can mean an older Forum and never none.
    /// A control, green on the seam.
    /// </summary>
    [Fact]
    public async Task R9_55_NMN12_TheMemberIsPresentOnEveryResponseEvenWhenNoFilterIsNamed()
    {
        var board = Board();
        await StoreQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "nmn12", Ct);

        var rows = new (string Name, NameReply NameReply)[]
        {
            ("search, no filter", await SearchAsync(forum.Client, "q=anything", Ct)),
            ("search, a term and a board", await SearchAsync(forum.Client, $"q=zzqq&board={board}", Ct)),
            ("search, a filter alone", await SearchAsync(forum.Client, $"board={board}", Ct)),
            ("inbox, no filter", await InboxAsync(forum, forum.Client, actor, string.Empty, Ct)),
            ("inbox, a board", await InboxAsync(forum, forum.Client, actor, $"board={board}", Ct)),
        };

        Said(rows.Select(r => $"{r.Name}: 200, []"), rows.Select(r => $"{r.Name}: {(int)r.NameReply.Status}, {r.NameReply.Unmatched}"));
    }

    /// <summary>NMN13. No value is echoed: the typed board and author do not occur in the body, as text and as bytes. A control, green on the seam (an empty page holds none).</summary>
    [Fact]
    public async Task R9_55_NMN13_TheWordEchoesNoValue()
    {
        var typedBoard = "TYPED-BOARD-" + Guid.NewGuid().ToString("N")[..8];
        var typedAuthor = "typed-author-" + Guid.NewGuid().ToString("N")[..8];
        var reply = await SearchAsync(forum.Client, $"board={typedBoard}&author={typedAuthor}", Ct);

        var bytes = System.Text.Encoding.UTF8.GetBytes(reply.Body);
        var found = new List<string>();
        if (reply.Body.Contains(typedBoard, StringComparison.OrdinalIgnoreCase)) found.Add("the typed board");
        if (reply.Body.Contains(typedAuthor, StringComparison.OrdinalIgnoreCase)) found.Add("the typed author");
        if (bytes.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(typedBoard)) >= 0) found.Add("the typed board's bytes");

        Said($"200, nothing echoed", $"{(int)reply.Status}, {(found.Count == 0 ? "nothing echoed" : string.Join(" and ", found) + " echoed")}");
    }

    private async Task<int> OperatorAsync(string pem, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        return await Curia.OperatorTool.OperatorCommands.RunAsync(
            args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), Curia.OperatorTool.OperatorCommands.LoadLogSigner, stdout, stderr, Ct,
            readLogSigningKeyPem: () => pem);
    }

    /// <summary>
    /// NMN48 (Amendment 3, ruling 36, M7; G62 non-property (f)). The member is a word about the names and not about what a search shows: a board and an author whose only post is disputed
    /// (its key declared compromised from before it, R6.57) are served by name, so each is matched (<c>[]</c>), while the search for them is empty and says it removed the post
    /// (<c>excluded_disputed</c> is not zero). The control, before the declaration, is that the same search finds the post. Falsification FNMN51 (the predicate patched to the search's own visibility).
    /// </summary>
    [Fact]
    public async Task R9_55_NMN48_ABoardAndAnAuthorWhoseOnlyPostIsDisputedAreMatchedWhileTheSearchIsEmpty()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var board = Board();
        var term = "znmn48" + suffix;
        var thief = ForumAgent.Create("https://agents.example/nmn48-" + suffix, "only-" + suffix);
        var (dpop, token) = await thief.AuthenticateAsync(forum.Client, "http://localhost/oauth/token", forum.Now, Ct);
        using var created = await dpop.PostAsync(forum.Client, "http://localhost/v1/posts", token, thief.SignQuestion(board, "this post mentions " + term, "Signed by the thief " + term, forum.Now), forum.Now, Ct);
        var createdText = await created.Content.ReadAsStringAsync(Ct);
        Assert.True(created.StatusCode == HttpStatusCode.Created, createdText);
        using var accepted = JsonDocument.Parse(createdText);
        var postId = accepted.RootElement.GetProperty("post_id").GetString()!;
        var served = await GetAsync(forum.Client, "/v1/posts/" + postId, ct: Ct);
        var logIndex = served.Json.GetProperty("log_index").GetInt64();
        var author = Uri.EscapeDataString(thief.AgentId);

        static string Excluded(NameReply reply) =>
            reply.Json.TryGetProperty("floor", out var floor) && floor.TryGetProperty("excluded_disputed", out var n) && n.ValueKind == JsonValueKind.Number ? (n.GetInt32() > 0 ? "some" : "none") : "(not stated)";

        var beforeBoard = await SearchAsync(forum.Client, $"q={term}&board={board}", Ct);

        var pem = Curia.OperatorTool.LogSigningKey.GeneratePem();
        Assert.Equal(0, await OperatorAsync(pem, "sign-head", "--by", "ops"));
        Assert.Equal(0, await OperatorAsync(pem, "compromise", "--agent", thief.AgentId, "--kid", thief.Kid, "--from-index", logIndex.ToString(System.Globalization.CultureInfo.InvariantCulture), "--reason", "owner-reported", "--operator", "ops"));

        var byBoard = await SearchAsync(forum.Client, $"q={term}&board={board}", Ct);
        var byAuthor = await SearchAsync(forum.Client, $"q={term}&author={author}", Ct);
        var byBoardAlone = await SearchAsync(forum.Client, $"board={board}", Ct);

        Said(
            [
                "before the declaration: 1 result, [], removed none",
                "by board after it: 0 results, [], removed some",
                "by author after it: 0 results, [], removed some",
                "by board alone after it: 0 results, [], removed some",
            ],
            [
                $"before the declaration: {beforeBoard.Results} result{(beforeBoard.Results == 1 ? string.Empty : "s")}, {beforeBoard.Unmatched}, removed {Excluded(beforeBoard)}",
                $"by board after it: {byBoard.Results} results, {byBoard.Unmatched}, removed {Excluded(byBoard)}",
                $"by author after it: {byAuthor.Results} results, {byAuthor.Unmatched}, removed {Excluded(byAuthor)}",
                $"by board alone after it: {byBoardAlone.Results} results, {byBoardAlone.Unmatched}, removed {Excluded(byBoardAlone)}",
            ]);
    }

    /// <summary>NMN14. A refusal carries no member: a blank parameter, an unknown name, a bad limit and a cursor under another query. A control, green on the seam.</summary>
    [Fact]
    public async Task R9_55_NMN14_ARefusalCarriesNoMember()
    {
        var board = Board();
        await StoreQuestionAsync(forum, board, Ct);
        var rows = new (string Name, NameReply NameReply)[]
        {
            ("blank board", await SearchAsync(forum.Client, "q=x&board=", Ct)),
            ("unknown name", await SearchAsync(forum.Client, "q=x&bogus=1", Ct)),
            ("bad limit", await SearchAsync(forum.Client, "q=x&limit=0", Ct)),
            ("another query's cursor", await SearchAsync(forum.Client, $"board={board}&cursor=c1:1:0123456789abcdef", Ct)),
        };

        Said(
            rows.Select(r => $"{r.Name}: 400, no member"),
            rows.Select(r => $"{r.Name}: {(int)r.NameReply.Status}, {(r.NameReply.Unmatched == "(absent)" ? "no member" : "the member is present: " + r.NameReply.Unmatched)}"));
    }

    /// <summary>
    /// NMN44. <see cref="NamedFilters"/> is total over the closed fuzz's own value alphabet (the strings <see cref="Variations.Closed"/> renders for a query part, decoded) and over
    /// a lone surrogate and U+0000: it never throws, called over empty inputs and over a projection and a corpus that hold one served post each (Amendment 3, ruling 36, M2: a throw inside
    /// the per-post comparison is unreachable over an empty list). A control, green on the seam; its falsification is FNMN15, FNMN59 and the closed fuzz.
    /// </summary>
    [Fact]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The fact is that nothing is thrown; any exception is the finding and is reported by type.")]
    public void R9_55_NMN44_NamedFiltersNeverThrowsOverTheFuzzAlphabet()
    {
        var part = new Part("query:board", PartKind.Query, PartValueKind.String, false, null);
        var values = new List<string?> { "x", string.Empty, " ", char.ConvertFromUtf32(0), ((char)0xD800).ToString(), ((char)0xDFFF).ToString() };
        foreach (var variation in Variations.Closed.Where(v => v.Applies(part)))
        {
            var made = variation.Make(part, "x");
            if (made is VariedValue.Encoded encoded) values.Add(Uri.UnescapeDataString(encoded.Text));
            else if (made is VariedValue.HeaderText text) values.Add(text.Text);
        }

        Assert.True(values.Count > 10, $"NOT REACHED: the closed set rendered only {values.Count} values for a query part");

        var onePost = System.Collections.Immutable.ImmutableArray.Create(new PostView(
            "01JNMN44POST", "{}", "sig", "sha-256:nmn44", "https://agents.example/nmn44", "rust", "question", null,
            Curia.Domain.Primitives.ServerTimestamp.At(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero)), []));
        var oneSearchable = System.Collections.Immutable.ImmutableArray.Create(new Curia.Domain.Search.SearchablePost(
            "01JNMN44POST", "sha-256:nmn44", "rust", Curia.Domain.Content.PostKind.Question, "A title", "A body", [], "https://agents.example/nmn44", 1));

        var failures = new List<string>();
        foreach (var value in values)
        {
            try
            {
                _ = NamedFilters.Unmatched([], _ => true, value, value);
                _ = NamedFilters.BoardHasServedPost([], _ => true, value ?? string.Empty);
                _ = NamedFilters.BoardHasServedPost(System.Collections.Immutable.ImmutableArray<Curia.Domain.Search.SearchablePost>.Empty, value ?? string.Empty);
                _ = NamedFilters.Unmatched(onePost, _ => true, value, value);
                _ = NamedFilters.BoardHasServedPost(onePost, _ => true, value ?? string.Empty);
                _ = NamedFilters.BoardHasServedPost(oneSearchable, value ?? string.Empty);
            }
            catch (Exception ex)
            {
                failures.Add($"{ex.GetType().Name} on a value of {value?.Length} chars");
            }
        }

        Said("never throws over " + values.Count + " values", failures.Count == 0 ? "never throws over " + values.Count + " values" : string.Join("; ", failures.Take(5)));
    }
}
