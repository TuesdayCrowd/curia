using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Curia.Application.Ingest;
using Curia.Application.Projections;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R8.78 and R9.47 (errata G45), probes RVO40, RVO41, RVO44 to RVO49 and RVO52: a stored duplicate digest does not disable a post kind on its board (D87,
/// the unmodified tree's permanent 500), an identical resubmission is refused by the stored digest for every kind, and a log that already holds a
/// duplicate is read as it stands.
///
/// <para><b>Boards.</b> Every fact takes a board of its own, and every fact that stores two posts of one digest asserts first, from the <c>events</c>
/// table, that exactly two <c>post.accepted</c> events carry it, so a seeding that failed is a red and not a vacuous green. Until R9.47 is built the next
/// submission of that kind on that board is a 500 (an in-process client sees the exception, <see cref="Sent.Says"/>), so the assertions that must not
/// meet that 500 run first.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class StoredDuplicateEndpointTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<SceneActor> NewAsync(string stem) => AcceptedAnswerScene.EnrolAsync(forum, stem, Ct);

    private Task<SceneActor> T1Async(string board) => AcceptedAnswerScene.T1Async(forum, board, Ct);

    private Task<ScenePost> AskAsync(SceneActor actor, string board) => RevisionProbe.AskAsync(forum, actor, board, Ct);

    private Task<Sent> SendAsync(SceneActor actor, byte[] wire) => RevisionProbe.SendAsync(forum, actor, wire, cancellationToken: Ct);

    private byte[] Comment(SceneActor actor, string board, ScenePost on, string? text = null, string? title = null, string[]? tags = null) =>
        actor.Agent.Sign(PostKind.Comment, board, text ?? "A comment " + Unrelated(), title, on.PostId, forum.Now, tags: tags);

    /// <summary>The wire of a submission of <paramref name="kind"/> on <paramref name="board"/> whose post is the one a duplicate is seeded of, with its setup done.</summary>
    private async Task<(SceneActor Author, byte[] Wire, long SetupPosts)> SceneForAsync(string kind, string board)
    {
        switch (kind)
        {
            case "comment":
            {
                var author = await T1Async(board);
                var question = await AskAsync(author, board);
                return (author, Comment(author, board, question), 4);
            }

            case "answer":
            {
                var asker = await NewAsync("dup-asker");
                var question = await AskAsync(asker, board);
                var answerer = await T1Async(board);
                return (answerer, answerer.Agent.SignAnswer(board, "An answer " + Unrelated(), question.PostId, forum.Now), 3);
            }

            case "revision":
            {
                var author = await T1Async(board);
                var question = await AskAsync(author, board);
                return (author, author.Agent.SignRevision(board, "A correction " + Unrelated(), question.PostId, question.Digest, forum.Now), 4);
            }

            default:
            {
                var author = await NewAsync("dup-question");
                return (author, author.Agent.SignQuestion(board, Unrelated(), "About " + Unrelated(), forum.Now), 0);
            }
        }
    }

    // ---------------------------------------------------------------- RVO40

    /// <summary>
    /// RVO40. A stored duplicate does not disable a kind: two posts with one digest, seeded with ingest bypassed on a board of its own, then a different
    /// submission of that kind on that board by another author is accepted and is not a 500 (D87, measured for all four kinds).
    /// </summary>
    [Theory]
    [InlineData("revision")]
    [InlineData("answer")]
    [InlineData("comment")]
    [InlineData("question")]
    public async Task R9_47_RVO40_AStoredDuplicateDoesNotDisableTheKindOnItsBoard(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var board = Board();
        var (author, wire, _) = await SceneForAsync(kind, board);
        var (_, digest) = await forum.AppendStoredPostAsync(wire, Ct);
        await forum.AppendStoredPostAsync(wire, Ct);
        Assert.Equal(2, await EventsWithDigestAsync(forum, digest));

        // A different submission of the kind, on another post where the kind needs one.
        Sent next;
        if (kind == "question")
        {
            var other = await NewAsync("dup-next");
            next = await SendAsync(other, other.Agent.SignQuestion(board, Unrelated(), "About " + Unrelated(), forum.Now));
        }
        else
        {
            var owner = await NewAsync("dup-next-asker");
            var question = await AskAsync(owner, board);
            next = kind switch
            {
                "comment" => await CommentFromNewAgentAsync(board, question),
                "answer" => await SendAsync(author, author.Agent.SignAnswer(board, "Another answer " + Unrelated(), question.PostId, forum.Now)),
                _ => await SendAsync(owner, owner.Agent.SignRevision(board, "Another correction " + Unrelated(), question.PostId, question.Digest, forum.Now)),
            };
        }

        Said("201", next.Says);
    }

    private async Task<Sent> CommentFromNewAgentAsync(string board, ScenePost on)
    {
        var commenter = await NewAsync("dup-next");
        return await SendAsync(commenter, Comment(commenter, board, on));
    }

    // ---------------------------------------------------------------- RVO41

    /// <summary>RVO41. Search over a stored duplicate answers: 200, the digest named once, at the first post in log order. (Read from the source before it was run; the line quoted when it reads green is the control.)</summary>
    [Fact]
    public async Task R9_47_RVO41_SearchOverAStoredDuplicateAnswersAndNamesTheFirstPost()
    {
        var board = Board();
        var term = "zdup" + Suffix();
        var author = await NewAsync("dup-search");
        var wire = author.Agent.SignQuestion(board, $"The body mentions {term}", $"About {term}", forum.Now);
        var (first, digest) = await forum.AppendStoredPostAsync(wire, Ct);
        await forum.AppendStoredPostAsync(wire, Ct);
        Assert.Equal(2, await EventsWithDigestAsync(forum, digest));

        string said;
        try
        {
            using var response = await forum.Client.GetAsync(new Uri($"/v1/search?q={term}&board={board}", UriKind.Relative), Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            if (!response.IsSuccessStatusCode)
            {
                said = $"{(int)response.StatusCode}";
            }
            else
            {
                using var json = JsonDocument.Parse(body);
                var named = json.RootElement.GetProperty("results").EnumerateArray()
                    .Where(r => r.GetProperty("post").GetProperty("digest").GetString() == digest)
                    .Select(r => r.GetProperty("post").GetProperty("post_id").GetString())
                    .ToArray();
                said = $"200; the digest named {named.Length} time at {string.Join("+", named.Select(n => n == first ? "the first post" : n))}";
            }
        }
#pragma warning disable CA1031 // An in-process host rethrows what the deployed host answers as 500.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            said = $"500 (threw {ex.GetType().Name}: {ex.Message})";
        }

        Said("200; the digest named 1 time at the first post", said);
    }

    // ---------------------------------------------------------------- RVO44

    /// <summary>
    /// RVO44. A sequential identical resubmission is refused, for every kind, each on a board of its own: the second send of the same signed bytes is
    /// <c>already-recorded</c> naming the first receipt's post id; the table holds one event of the digest; the author's posting budget moved by one (the author
    /// has exactly its setup posts and the one).
    /// </summary>
    [Theory]
    [InlineData("comment")]
    [InlineData("answer")]
    [InlineData("revision")]
    [InlineData("question")]
    public async Task R8_78_RVO44_AnIdenticalResubmissionIsRefusedForEveryKind(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var board = Board();
        var (author, wire, setup) = await SceneForAsync(kind, board);

        var first = await SendAsync(author, wire);
        var second = await SendAsync(author, wire);

        Said(
            $"{first.Status}; 409 {IngestErrors.AlreadyRecordedType} post={first.PostId}; 1 event with the digest; the author holds {setup + 1} posts",
            $"{first.Status}; {second.Says} {(second.Detail is null ? string.Empty : second.Detail)}; {await EventsWithDigestAsync(forum, first.Digest ?? "none")} event with the digest; the author holds {await PostsByAsync(forum, author.Agent.AgentId)} posts");
    }

    // ---------------------------------------------------------------- RVO45

    /// <summary>
    /// RVO45. Concurrent identical wires of a non-revision kind (a recorded non-property, held as a control of tolerance; since errata G48 this class runs on the host as it was before the agent write gate, ruling 22, because the gate closes the race the non-property describes: BRB64 is the same workload through the real gate and requires one event): four identical comment wires released
    /// together on a board of its own; the table holds one or more (R8.78's check is not atomic with the append, and the assertion that would make this a
    /// property is deliberately absent); whatever it holds, the next comment on that board is accepted. Four rather than two, so a run that stores a duplicate
    /// is the usual one and the fact is not green by jitter.
    /// </summary>
    [Fact]
    public async Task R9_47_RVO45_WhateverConcurrentIdenticalCommentsStoreTheNextCommentIsAccepted()
    {
        var board = Board();
        var author = await T1Async(board);
        var question = await AskAsync(author, board);
        var wire = Comment(author, board, question);
        var token = await author.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct);

        var sent = await TogetherAsync(Enumerable.Range(0, 4).Select(_ => (Func<Task<Sent>>)(() => RevisionProbe.SendAsync(forum, author, wire, token, Ct))));
        var digest = sent.Select(s => s.Digest).First(d => d is not null)!;
        var events = await EventsWithDigestAsync(forum, digest);
        var next = await SendAsync(author, Comment(author, board, question));

        Said($"{events} events; the next comment 201", $"{events} events; the next comment {next.Says}");
    }

    // ---------------------------------------------------------------- RVO46

    /// <summary>RVO46. Stored, not served: the author's comment is withheld by a moderator's record; the same wire sent again is <c>already-recorded</c> naming the withheld post's id (the author already holds that id).</summary>
    [Fact]
    public async Task R8_78_RVO46_BytesAModeratorWithheldAreStillRecorded()
    {
        var board = Board();
        var author = await NewAsync("dup-withheld");
        var question = await AskAsync(author, board);
        var wire = Comment(author, board, question);
        var first = await SendAsync(author, wire);
        await forum.WithholdAsync(first.PostId!, Ct);

        var again = await SendAsync(author, wire);

        Said(
            $"409 {IngestErrors.AlreadyRecordedType} post={first.PostId}",
            $"{again.Says} {again.Detail}".TrimEnd());
    }

    // ---------------------------------------------------------------- RVO47

    /// <summary>RVO47, control. The digest is the identity, not the text: the same text with a fresh nonce is a different digest and is accepted. Green now; red under FRVO36.</summary>
    [Fact]
    public async Task R8_78_RVO47_TheSameWordsWithAFreshNonceAreANewPost()
    {
        var board = Board();
        var author = await NewAsync("dup-nonce");
        var question = await AskAsync(author, board);
        var text = "The same words " + Unrelated();
        Assert.Equal(201, (await SendAsync(author, Comment(author, board, question, text))).Status);

        var again = await SendAsync(author, Comment(author, board, question, text));

        Said("201", again.Says);
    }

    // ---------------------------------------------------------------- RVO48

    /// <summary>
    /// RVO48, control. AUTHORIZE first: a T0 author's answer (answer is T1) whose digest is already stored (seeded with ingest bypassed) is refused by Table 10 with
    /// the tier named, never <c>already-recorded</c>, and the refusal names no post. Green now; red under FRVO37.
    /// </summary>
    [Fact]
    public async Task R8_78_RVO48_ACallerDeniedByTierIsToldTheTierAndNeverThatAPostExists()
    {
        var board = Board();
        var asker = await NewAsync("dup-asker");
        var question = await AskAsync(asker, board);
        var author = await NewAsync("dup-t0");
        var wire = author.Agent.SignAnswer(board, "An answer " + Unrelated(), question.PostId, forum.Now);
        var (storedId, _) = await forum.AppendStoredPostAsync(wire, Ct);

        var denied = await SendAsync(author, wire);

        Said(
            "403 curia/authz/denied; the detail names table-10/denied tier=T0; no already-recorded and no post id",
            $"{denied.Says}; the detail {(denied.Detail?.Contains("table-10/denied tier=T0", StringComparison.Ordinal) == true ? "names table-10/denied tier=T0" : "is " + denied.Detail)}; " +
            $"{(denied.Body.Contains("already-recorded", StringComparison.Ordinal) || denied.Body.Contains(storedId, StringComparison.Ordinal) ? "the body names a recorded post" : "no already-recorded and no post id")}");
    }

    // ---------------------------------------------------------------- RVO49

    /// <summary>RVO49. The refusal's shape: <c>application/problem+json</c> with type, title, status 409 and exactly <c>post=&lt;id&gt;</c>; a sentinel in the body, title and tags appears nowhere in it.</summary>
    [Fact]
    public async Task R8_78_RVO49_TheAlreadyRecordedRefusalEchoesNothingAnAuthorWrote()
    {
        var board = Board();
        var author = await NewAsync("dup-shape");
        var question = await AskAsync(author, board);
        var sentinel = "ZQSENTINEL" + Suffix();
        var tag = "zq" + Suffix();
        var wire = Comment(author, board, question, $"A comment {sentinel} " + Unrelated(), sentinel + " title", [tag]);
        var first = await SendAsync(author, wire);

        var second = await SendAsync(author, wire);

        var problems = RevisionProbe.ShapeProblems(second, IngestErrors.AlreadyRecorded(first.PostId ?? "none"), 409, sentinel, tag);
        Said("none", problems.Length == 0 ? "none" : problems);
    }

    // ---------------------------------------------------------------- RVO52

    /// <summary>
    /// RVO52. A log that holds a duplicate replays (R11.9): after the seeding of RVO40 on a board of its own, a second host built over the same database (a
    /// restart) serves each post by id with a canonical form that hashes to the digest, the batch names the first post, the read models fold from the table without
    /// throwing, and a different question on the board is accepted.
    /// </summary>
    [Fact]
    public async Task R9_47_RVO52_ALogThatHoldsADuplicateReplaysAndServesTheFirst()
    {
        var board = Board();
        var author = await NewAsync("dup-replay");
        var wire = author.Agent.SignQuestion(board, Unrelated(), "About " + Unrelated(), forum.Now);
        var (first, digest) = await forum.AppendStoredPostAsync(wire, Ct);
        var (second, _) = await forum.AppendStoredPostAsync(wire, Ct);
        Said([first, second], await PostIdsWithDigestAsync(forum, digest));

        await using var restarted = forum.WithWebHostBuilder(_ => { });
        using var client = restarted.CreateClient();
        var lines = new List<string>();

        async Task<string> ServedAsync(string id)
        {
            using var response = await client.GetAsync(new Uri($"/v1/posts/{id}", UriKind.Relative), Ct);
            if (!response.IsSuccessStatusCode) return $"{(int)response.StatusCode}";
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
            var canonical = json.RootElement.GetProperty("canonical").GetString()!;
            var hashed = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            return hashed == json.RootElement.GetProperty("digest").GetString() && hashed == digest ? "200 and hashes to the digest" : "200 but the canonical form does not hash to the digest";
        }

        lines.Add($"first: {await ServedAsync(first)}");
        lines.Add($"second: {await ServedAsync(second)}");

        using var batch = await client.PostAsJsonAsync("/v1/posts/batch", new { digests = new[] { digest } }, Ct);
        using var items = JsonDocument.Parse(await batch.Content.ReadAsStringAsync(Ct));
        var item = items.RootElement.GetProperty("items")[0];
        lines.Add($"batch: {item.GetProperty("state").GetString()} at {(item.GetProperty("post").GetProperty("post_id").GetString() == first ? "the first post" : "another post")}");

        var log = await ReadLogAsync(forum, Ct);
        _ = PostProjector.Fold(log);
        _ = SearchProjector.Fold(log);
        lines.Add("fold: read");

        var asker = await NewAsync("dup-replay-next");
        var next = await SendAsync(asker, asker.Agent.SignQuestion(board, Unrelated(), "About " + Unrelated(), forum.Now));
        lines.Add($"next question: {next.Says}");

        Said(
            ["first: 200 and hashes to the digest", "second: 200 and hashes to the digest", "batch: current at the first post", "fold: read", "next question: 201"],
            lines);
    }
}
