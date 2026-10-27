using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.55 (errata G42): <c>curia_answer</c>, when the Forum refuses with <c>curia/ingest/parent-not-a-question</c> and the post it read under
/// <c>parentPostId</c> is a revision, adds to the Forum's refusal the question that post revises, where and only where the Forum's own
/// <c>superseded_by</c> confirms it.
///
/// <para><b>Oracles (plan ruling 10).</b> The posts the test signed and served, their digests as the test's own builder computed them, the
/// stub's list of the requests it received (a <c>POST</c> happened; how many reads a walk made), and the errata's bound
/// (<see cref="PublishedForkWords.WalkBound"/>) and problem types (<see cref="PublishedWithheldWords"/>), never the tool's report of itself.
/// The tool's refusal is a thrown tool error (<c>isError</c>), not a line the tool prefixes: the live record on the unmodified tree.</para>
///
/// <para><b>Pairing.</b> The facts that expect no pointer (PI26 to PI34) are green on a tool that names nothing, so each is built on
/// <see cref="Revised"/>, the fixture <see cref="R11_55_AnAnswerToARevisionNamesTheQuestionItRevises"/> expects a pointer from, and differs from it
/// by one field. A tool that names nothing passes them for the right reason only because that fixture, unchanged, makes it name the question.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RevisedQuestionPointerTests : IDisposable
{
    private const string QuestionId = "01PIQUESTION0000000000001";
    private const string RevisionId = "01PIREVISION0000000000001";
    private const string Marker = "ZQ9PIAUTHORMARKER";
    private const string RefusalType = "curia/ingest/parent-not-a-question";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-pi-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private async Task<McpProtocolSession> StartAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
        return await McpProtocolSession.StartAsync(tools, writer.Agent.Profile.AgentId, agent);
    }

    private static PostDraft Question(string? marker = null) => new()
    {
        Kind = PostKind.Question,
        Board = "b",
        Title = marker is null ? "A question about finches" : $"A question about {marker}",
        Body = marker is null ? "Which finch is this?" : $"Which finch is this? {marker}",
        Tags = marker is null ? [] : [marker],
    };

    private static PostDraft RevisionOf(string parentId, string prevDigest, string body = "Corrected: which finch, exactly?") => new()
    {
        Kind = PostKind.Revision,
        Board = "b",
        Parent = parentId,
        Prev = prevDigest,
        Body = body,
    };

    /// <summary>A question and one revision of it, served, the question's <c>superseded_by</c> holding the revision's digest and id.</summary>
    private sealed record Pair(string QuestionDigest, string RevisionDigest, SignedSubmission Revision, PostDraft RevisionDraft);

    /// <summary>
    /// The fixture the pointer is expected from: question <see cref="QuestionId"/> served with <c>superseded_by</c> [the revision], the revision
    /// <see cref="RevisionId"/> naming it as parent, and the Forum refusing an answer under the revision.
    /// </summary>
    private Pair Revised(
        ImmutableArray<Successor>? questionList = null, string? marker = null, string? revisionParent = null, bool serveQuestion = true, string? questionCanonical = null)
    {
        var question = Question(marker);
        var signedQuestion = _log.SignDraft(question);
        var draft = RevisionOf(revisionParent ?? QuestionId, signedQuestion.PrefixedDigest);
        var signedRevision = _log.SignDraft(draft);
        var confirming = ImmutableArray.Create(new Successor(signedRevision.PrefixedDigest, RevisionId));
        if (serveQuestion)
            _log.ServePost(QuestionId, question, signedQuestion, questionList ?? confirming, servedCanonical: questionCanonical);
        _log.ServePost(RevisionId, draft, signedRevision, supersededBy: []);
        _log.RefusesAsParentNotAQuestion = (RevisionId, "revision");
        return new Pair(signedQuestion.PrefixedDigest, signedRevision.PrefixedDigest, signedRevision, draft);
    }

    private async Task<McpAnswer> AnswerAsync(string parentId)
    {
        await using var session = await StartAsync();
        return await session.CallAsync("curia_answer", new() { ["parentPostId"] = parentId, ["body"] = "It is a goldfinch." });
    }

    private int ReadsOfPosts() => _log.Requests.Count(r => r.StartsWith("GET /v1/posts/", StringComparison.Ordinal));

    [Fact]
    public async Task R11_55_AnAnswerToARevisionNamesTheQuestionItRevises()
    {
        Revised();

        // Control: the fixture is sound. Both posts are served and verify when read, so a pointer that is not given is not given for a fixture fault.
        await using (var reading = await StartAsync())
        {
            foreach (var id in new[] { QuestionId, RevisionId })
            {
                var read = await reading.CallAsync("curia_read", new() { ["postId"] = id });
                Assert.False(read.IsError, read.Text);
                Assert.DoesNotContain("FAILED", read.Text, StringComparison.Ordinal);
            }
        }

        var answer = await AnswerAsync(RevisionId);

        Assert.True(answer.IsError, answer.Text);

        // The Forum's refusal is kept, whole.
        Assert.Contains(RefusalType, answer.Text, StringComparison.Ordinal);
        Assert.Contains($"parent={RevisionId} kind=revision", answer.Text, StringComparison.Ordinal);

        // The pointer: the question by its post id, and to answer that one.
        Assert.Contains(QuestionId, answer.Text, StringComparison.Ordinal);
        Assert.Contains("answer", answer.Text.Replace("An answer's parent", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("other-digest")]
    [InlineData("absent")]
    [InlineData("unrelated-parent")]
    public async Task R11_55_APointerTheForumDoesNotConfirmIsNotGiven(string variation)
    {
        const string UnrelatedId = "01PIUNRELATED0000000001";
        var otherDigest = "sha256:" + new string('9', 64);
        ImmutableArray<Successor>? list = variation switch
        {
            "empty" => [],
            "other-digest" => ImmutableArray.Create(new Successor(otherDigest, RevisionId)),
            "absent" => default(ImmutableArray<Successor>),
            _ => null,
        };

        if (variation == "unrelated-parent")
        {
            // The revision's author wrote a parent that names an unrelated question; the question it names does not list the revision.
            var unrelated = Question("unrelated");
            _log.ServePost(UnrelatedId, unrelated, _log.SignDraft(unrelated), supersededBy: []);
            Revised(revisionParent: UnrelatedId);
        }
        else
        {
            Revised(list);
        }

        var answer = await AnswerAsync(RevisionId);

        Assert.True(answer.IsError, answer.Text);
        Assert.Contains(RefusalType, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(QuestionId, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(UnrelatedId, answer.Text, StringComparison.Ordinal);
        Assert.Contains("revision", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_55_AnOriginalThatDoesNotVerifyIsNotNamed()
    {
        // The served canonical form is not the one the signature covers: the read does not verify.
        var tampered = Encoding.UTF8.GetString(_log.SignDraft(Question("tampered")).Canonical.Span);
        Revised(questionCanonical: tampered);

        var answer = await AnswerAsync(RevisionId);

        Assert.True(answer.IsError, answer.Text);
        Assert.Contains(RefusalType, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(QuestionId, answer.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("withheld")]
    [InlineData("not-found")]
    [InlineData("unavailable")]
    public async Task R11_55_AWithheldOrMissingOriginalIsNotNamedAndTheRefusalStands(string how)
    {
        Revised(serveQuestion: false);
        _log.RefusedPosts[QuestionId] = how switch
        {
            "withheld" => ((System.Net.HttpStatusCode)PublishedWithheldWords.PostWithheldStatus(), PublishedWithheldWords.PostWithheldType()),
            "not-found" => (System.Net.HttpStatusCode.NotFound, PublishedWithheldWords.PostNotFoundType()),
            _ => (System.Net.HttpStatusCode.ServiceUnavailable, "curia/stub/unavailable"),
        };

        var answer = await AnswerAsync(RevisionId);

        // The thrown refusal is the Forum's; the walk's own failure, a 503 included, never replaces it.
        Assert.True(answer.IsError, answer.Text);
        Assert.Contains(RefusalType, answer.Text, StringComparison.Ordinal);
        Assert.Contains($"parent={RevisionId} kind=revision", answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(QuestionId, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("curia/stub/unavailable", answer.Text, StringComparison.Ordinal);
    }

    /// <summary>A chain Q, r1, r2, ... each the next's parent and prev, every one listing its successor; returns the ids from Q to the last.</summary>
    private string[] Chain(int revisions, string? rootMarker = null)
    {
        var drafts = new List<PostDraft> { Question(rootMarker) };
        var signed = new List<SignedSubmission> { _log.SignDraft(drafts[0]) };
        var ids = new List<string> { QuestionId };
        for (var i = 1; i <= revisions; i++)
        {
            ids.Add($"01PICHAIN{i:D2}00000000000{i:D2}");
            drafts.Add(RevisionOf(ids[i - 1], signed[i - 1].PrefixedDigest, $"Correction {i}."));
            signed.Add(_log.SignDraft(drafts[i]));
        }

        for (var i = 0; i <= revisions; i++)
        {
            ImmutableArray<Successor> list = i < revisions ? [new Successor(signed[i + 1].PrefixedDigest, ids[i + 1])] : [];
            _log.ServePost(ids[i], drafts[i], signed[i], list);
        }

        _log.RefusesAsParentNotAQuestion = (ids[revisions], "revision");
        return [.. ids];
    }

    [Fact]
    public async Task R11_55_ARevisionOfARevisionIsFollowedToTheQuestion()
    {
        var ids = Chain(2);

        var answer = await AnswerAsync(ids[2]);

        Assert.True(answer.IsError, answer.Text);
        Assert.Contains(RefusalType, answer.Text, StringComparison.Ordinal);
        Assert.Contains(QuestionId, answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void R11_55_TheWalkBoundInTheCodeIsTheBoundTheErrataPublishes() =>
        Assert.Equal(ForumTools.WalkBound, PublishedForkWords.WalkBound());

    [Fact]
    public async Task R11_55_NineHopsNameNothingAndTheWalkStopsAtTheBound()
    {
        var bound = PublishedForkWords.WalkBound();
        var ids = Chain(bound + 1);

        var answer = await AnswerAsync(ids[^1]);

        Assert.True(answer.IsError, answer.Text);
        Assert.Contains(RefusalType, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(QuestionId, answer.Text, StringComparison.Ordinal);

        // The read of the parent, then at most the bound's reads of the walk.
        Assert.True(ReadsOfPosts() <= 1 + bound, $"the walk made {ReadsOfPosts()} reads of posts for a bound of {bound}");
    }

    [Fact]
    public async Task R11_55_ACycleTerminates()
    {
        const string IdA = "01PICYCLEA00000000000001";
        const string IdB = "01PICYCLEB00000000000001";
        var draftA = RevisionOf(IdB, "sha256:" + new string('1', 64), "A.");
        var draftB = RevisionOf(IdA, "sha256:" + new string('2', 64), "B.");
        var signedA = _log.SignDraft(draftA);
        var signedB = _log.SignDraft(draftB);
        _log.ServePost(IdA, draftA, signedA, [new Successor(signedB.PrefixedDigest, IdB)]);
        _log.ServePost(IdB, draftB, signedB, [new Successor(signedA.PrefixedDigest, IdA)]);
        _log.RefusesAsParentNotAQuestion = (IdA, "revision");

        var answer = await AnswerAsync(IdA);

        Assert.True(answer.IsError, answer.Text);
        Assert.Contains(RefusalType, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(QuestionId, answer.Text, StringComparison.Ordinal);
        Assert.True(ReadsOfPosts() <= 1 + PublishedForkWords.WalkBound(), $"the cycle was walked {ReadsOfPosts()} times");
    }

    [Theory]
    [InlineData(PostKind.Answer)]
    [InlineData(PostKind.Comment)]
    public async Task R11_55_ARevisionOfAnAnswerNamesNoQuestion(PostKind rootKind)
    {
        const string RootId = "01PIROOT0000000000000001";
        var root = new PostDraft { Kind = rootKind, Board = "b", Parent = QuestionId, Body = "A root that is no question." };
        var signedRoot = _log.SignDraft(root);
        var draft = RevisionOf(RootId, signedRoot.PrefixedDigest);
        var signedRevision = _log.SignDraft(draft);
        _log.ServePost(RootId, root, signedRoot, [new Successor(signedRevision.PrefixedDigest, RevisionId)]);
        _log.ServePost(RevisionId, draft, signedRevision, supersededBy: []);
        _log.RefusesAsParentNotAQuestion = (RevisionId, "revision");

        var answer = await AnswerAsync(RevisionId);

        Assert.True(answer.IsError, answer.Text);
        Assert.Contains(RefusalType, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(RootId, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(QuestionId, answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_55_TheWalkIsNotARead()
    {
        Revised();
        await using var session = await StartAsync();

        _ = await session.CallAsync("curia_answer", new() { ["parentPostId"] = RevisionId, ["body"] = "It is a goldfinch." });
        var verified = await session.CallAsync("curia_verify", new() { ["postId"] = QuestionId });

        // The question was reached by the walk, not read by the model: R11.29's subject is a post a read served.
        Assert.Contains("no read in this session served this post", verified.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_55_NothingAnAuthorWroteIsInTheResult()
    {
        Revised(marker: Marker);

        var answer = await AnswerAsync(RevisionId);

        Assert.True(answer.IsError, answer.Text);
        Assert.DoesNotContain(Marker, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("goldfinch", answer.Text.Replace("It is a goldfinch.", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_55_TheToolSendsAndTheForumDecides()
    {
        Revised();

        var answer = await AnswerAsync(RevisionId);

        // The tool did not pre-refuse: the POST reached the Forum, and the refusal in the result is the Forum's own.
        Assert.Contains("POST /v1/posts", _log.Requests);
        Assert.True(answer.IsError, answer.Text);
        Assert.Contains($"parent={RevisionId} kind=revision", answer.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_55_ACommentOnARevisionIsAcceptedAndANonRevisionParentIsUnchanged()
    {
        Revised();
        _log.RefusesAsParentNotAQuestion = null;
        await using var session = await StartAsync();

        var commented = await session.CallAsync("curia_comment", new() { ["parentPostId"] = RevisionId, ["body"] = "A remark on the revision." });

        // A comment's parent may be any kind (R8.70), so it is accepted and nothing is walked: one read, the parent's.
        Assert.False(commented.IsError, commented.Text);
        Assert.StartsWith("POSTED", commented.Text, StringComparison.Ordinal);
        Assert.Equal(1, ReadsOfPosts());

        // An answer under a comment is refused as before, and no walk follows it.
        const string CommentId = "01PICOMMENT00000000000001";
        var comment = new PostDraft { Kind = PostKind.Comment, Board = "b", Parent = QuestionId, Body = "A comment." };
        _log.ServePost(CommentId, comment, _log.SignDraft(comment), supersededBy: []);
        _log.RefusesAsParentNotAQuestion = (CommentId, "comment");
        var before = ReadsOfPosts();

        var answered = await session.CallAsync("curia_answer", new() { ["parentPostId"] = CommentId, ["body"] = "An answer under a comment." });

        Assert.True(answered.IsError, answered.Text);
        Assert.Contains($"parent={CommentId} kind=comment", answered.Text, StringComparison.Ordinal);
        Assert.Equal(before + 1, ReadsOfPosts());
    }

    /// <summary>
    /// R11.55: where a step fails the refusal says the post is a revision and why no question is named, and names none. The causes are fixed
    /// words, one per failing step, and the Forum's refusal stays in the result.
    /// </summary>
    [Theory]
    [InlineData("does-not-list", "does not list")]
    [InlineData("does-not-verify", "did not verify")]
    [InlineData("unreadable", "could not be read")]
    [InlineData("bound", "bound of")]
    [InlineData("cycle", "already read")]
    [InlineData("not-a-question", "not a question")]
    public async Task R11_55_AFailedStepSaysWhyNoQuestionIsNamed(string step, string cause)
    {
        string unnamed;
        switch (step)
        {
            case "does-not-list":
                Revised([]);
                unnamed = QuestionId;
                break;
            case "does-not-verify":
                Revised(questionCanonical: Encoding.UTF8.GetString(_log.SignDraft(Question("tampered")).Canonical.Span));
                unnamed = QuestionId;
                break;
            case "unreadable":
                Revised(serveQuestion: false);
                _log.RefusedPosts[QuestionId] = (System.Net.HttpStatusCode.NotFound, PublishedWithheldWords.PostNotFoundType());
                unnamed = QuestionId;
                break;
            case "bound":
                unnamed = Chain(PublishedForkWords.WalkBound() + 1)[0];
                break;
            case "cycle":
            {
                const string IdA = "01PICYCLEA00000000000002";
                const string IdB = "01PICYCLEB00000000000002";
                var draftA = RevisionOf(IdB, "sha256:" + new string('1', 64), "A.");
                var draftB = RevisionOf(IdA, "sha256:" + new string('2', 64), "B.");
                var signedA = _log.SignDraft(draftA);
                var signedB = _log.SignDraft(draftB);
                _log.ServePost(IdA, draftA, signedA, [new Successor(signedB.PrefixedDigest, IdB)]);
                _log.ServePost(IdB, draftB, signedB, [new Successor(signedA.PrefixedDigest, IdA)]);
                _log.RefusesAsParentNotAQuestion = (IdA, "revision");
                unnamed = IdB;
                break;
            }
            default:
            {
                var root = new PostDraft { Kind = PostKind.Answer, Board = "b", Parent = QuestionId, Body = "A root that is no question." };
                var signedRoot = _log.SignDraft(root);
                var draft = RevisionOf("01PIROOT0000000000000002", signedRoot.PrefixedDigest);
                var signedRevision = _log.SignDraft(draft);
                _log.ServePost("01PIROOT0000000000000002", root, signedRoot, [new Successor(signedRevision.PrefixedDigest, RevisionId)]);
                _log.ServePost(RevisionId, draft, signedRevision, supersededBy: []);
                _log.RefusesAsParentNotAQuestion = (RevisionId, "revision");
                unnamed = "01PIROOT0000000000000002";
                break;
            }
        }

        var parent = step switch { "cycle" => "01PICYCLEA00000000000002", "bound" => $"01PICHAIN{PublishedForkWords.WalkBound() + 1:D2}00000000000{PublishedForkWords.WalkBound() + 1:D2}", _ => RevisionId };
        var answer = await AnswerAsync(parent);

        Assert.True(answer.IsError, answer.Text);
        Assert.Contains(RefusalType, answer.Text, StringComparison.Ordinal);
        Assert.Contains("is a revision; no question is named because", answer.Text, StringComparison.Ordinal);
        Assert.Contains(cause, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(unnamed, answer.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(QuestionId, answer.Text, StringComparison.Ordinal);
    }
}
