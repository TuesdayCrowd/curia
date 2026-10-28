using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Moderation;
using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// PR 4 (errata G44), Task 0's probes that live below the HTTP route: the chain walk (<see cref="CorrectionIndex.ChainRootOf"/>, R8.75) over crafted
/// posts, and the rule that says which post a thread's acceptance names (<see cref="AcceptedAnswerIndex"/>, R9.46) over a crafted log.
///
/// <para><b>Oracles.</b> The posts are built here with the ids, digests, authors and <c>prev</c> each fact writes, so every expectation is the ledger of
/// the fact and R9.28's sentence, never a value read from the index under test. Each fact on a chain is <b>paired</b> (plan, vacuity notes): the seam
/// returns a post as its own root, which is the right answer for a stranger's revision, so a fact that only asked that would pass for the wrong reason.
/// Each therefore also asks the honest case, the author's own revision, whose root is the answer; the pair is red until the walk exists and keeps
/// the refusing half honest after it does.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptTheTextProbeTests
{
    private const string AuthorX = "https://agents.example/x";
    private const string Stranger = "https://agents.example/stranger";
    private const string Asker = "https://agents.example/asker";
    private const string OtherAsker = "https://agents.example/other-asker";

    private static readonly DateTimeOffset Start = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static string Digest(char c) => "sha256:" + new string(c, 64);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static PostView Post(string id, string digest, string kind, string author, string? parent, string? prev = null) => new(
        id, "{}", "sig", digest, author, "board", kind, parent, ServerTimestamp.At(Start), [], prev);

    private static bool AllServable(string _) => true;

    private static CorrectionIndex IndexOf(params PostView[] posts) => CorrectionIndex.Fold([.. posts], AllServable);

    private static PostView Question(string id, char digest, string author = Asker) => Post(id, Digest(digest), "question", author, null);

    private static PostView Answer(string id, char digest, string parent, string author = AuthorX) => Post(id, Digest(digest), "answer", author, parent);

    private static PostView Revision(string id, char digest, string parent, string prevDigest, string author = AuthorX) =>
        Post(id, Digest(digest), "revision", author, parent, prevDigest);

    /// <summary>
    /// PK4's crafted form (plan ruling 13: ingest refuses a stranger's revision, so only a log this code did not write holds one). A revision of the
    /// answer by a stranger is not a correction (R9.28: another author), so the walk from it ends at itself; the author's own revision of the same answer
    /// walks to the answer. Paired: the honest half is red against the seam, the stranger half is held by FPK5 and FPK40.
    /// </summary>
    [Fact]
    public void R8_75_PK4_AStrangersRevisionOfAnAnswerIsItsOwnChainRootAndTheAuthorsIsNot()
    {
        var q = Question("Q", '0');
        var a = Answer("A", 'a', "Q");
        var honest = Revision("R", '1', "Q", a.Digest);
        var stranger = Revision("S", '2', "Q", a.Digest, Stranger);
        var index = IndexOf(q, a, honest, stranger);

        // The honest half first: the walk reaches the answer. (Red against the seam, which returns the revision itself.)
        Assert.Equal("A", index.ChainRootOf(honest).PostId);

        // The stranger's revision corrects nothing, so its chain ends at the revision, whose kind is what the refusal would name.
        var strangerRoot = index.ChainRootOf(stranger);
        Assert.Equal("S", strangerRoot.PostId);
        Assert.Equal("revision", strangerRoot.Kind);
    }

    /// <summary>
    /// PK7's crafted form (plan ruling 13: a <c>prev</c> that names no digest is refused 403 at ingest, so only a replayed log holds the post). The walk
    /// from a revision whose <c>prev</c> no post bears ends at that revision; paired with the honest revision, as PK4 is.
    /// </summary>
    [Fact]
    public void R8_75_PK7_ARevisionWhosePrevNamesNoPostIsItsOwnChainRoot()
    {
        var a = Answer("A", 'a', "Q");
        var honest = Revision("R", '1', "Q", a.Digest);
        var dangling = Revision("D", '3', "Q", Digest('f'));
        var index = IndexOf(Question("Q", '0'), a, honest, dangling);

        Assert.Equal("A", index.ChainRootOf(honest).PostId);
        Assert.Equal("D", index.ChainRootOf(dangling).PostId);
    }

    /// <summary>PK5 and PK6 below the route: a revision of a comment, or of a question, walks to that comment or that question, and the root's kind is the one the refusal names.</summary>
    [Theory]
    [InlineData("comment")]
    [InlineData("question")]
    public void R8_75_PK5_PK6_ARevisionOfACommentOrAQuestionWalksToThatPostAndItsKind(string rootKind)
    {
        var q = Question("Q", '0');
        var root = rootKind == "comment" ? Post("C", Digest('c'), "comment", AuthorX, "Q") : Question("QX", 'd', AuthorX);
        var revision = Revision("R", '1', root.PostId, root.Digest);
        var index = IndexOf(q, root, revision);

        var walked = index.ChainRootOf(revision);

        Assert.Equal(root.PostId, walked.PostId);
        Assert.Equal(rootKind, walked.Kind);
    }

    /// <summary>PK14 below the route: A, R1, R2 by one author: the walk from R2 passes R1 and ends at A (not at the first hop).</summary>
    [Fact]
    public void R8_75_PK14_ARevisionOfARevisionWalksToTheAnswer()
    {
        var a = Answer("A", 'a', "Q");
        var r1 = Revision("R1", '1', "Q", a.Digest);
        var r2 = Revision("R2", '2', "Q", r1.Digest);
        var index = IndexOf(Question("Q", '0'), a, r1, r2);

        Assert.Equal("A", index.ChainRootOf(r2).PostId);
        Assert.Equal("A", index.ChainRootOf(r1).PostId);
        Assert.Equal("A", index.ChainRootOf(a).PostId);
    }

    /// <summary>
    /// FPK34's own fact (plan, falsification table): a cycle in <c>prev</c> in a crafted log (R1 names R2's digest and R2 names R1's, both by one author, so
    /// each is a correction of the other) does not loop the walk. It terminates and ends at a post of the cycle. <b>Green first against the seam</b>
    /// (the seam walks nothing); held by FPK34, a walk without a visited set. Run with a deadline so a loop is a failure and not a hang.
    /// </summary>
    [Fact]
    public async Task R8_75_FPK34_ACycleInPrevTerminatesTheWalk()
    {
        var r1 = Revision("R1", '1', "Q", Digest('2'));
        var r2 = Revision("R2", '2', "Q", Digest('1'));
        var index = IndexOf(Question("Q", '0'), r1, r2);

        var walk = Task.Run(() => index.ChainRootOf(r1).PostId, TestContext.Current.CancellationToken);
        var finished = await Task.WhenAny(walk, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        Assert.True(ReferenceEquals(finished, walk), "the walk did not end on a cycle in prev");
        var ended = await walk.ConfigureAwait(true);
        Assert.True(ended is "R1" or "R2", $"the walk ended at {ended}, which is no post of the cycle");
    }

    // ----- PK41: which post a thread's acceptance names, over a crafted log -----

    private const string Q = "01JP0ST0000000000000000001";
    private const string QS = "01JP0ST0000000000000000002";
    private const string A = "01JP0ST000000000000000000A";
    private const string RL = "01JP0ST00000000000000000RX";

    private static string Canonical(PostKind kind, string author, string? parent, string? prev)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(kind))));
        members.Add(new("author", new JsonValue.String(author)));
        members.Add(new("board", new JsonValue.String("postgres")));
        if (kind is PostKind.Question) members.Add(new("title", new JsonValue.String("a question")));
        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
        if (prev is not null) members.Add(new("prev", new JsonValue.String(prev)));
        members.Add(new("body", new JsonValue.String("a body")));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));

        var canonical = Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable())));
        return System.Text.Encoding.UTF8.GetString(canonical.Span);
    }

    private static async Task AppendPostAsync(
        InMemoryEventStore store, string id, string digest, PostKind kind, string author, string? parent, string? prev, CancellationToken ct)
    {
        var payload = new List<KeyValuePair<string, JsonValue>>
        {
            new("post_id", new JsonValue.String(id)),
            new("canonical", new JsonValue.String(Canonical(kind, author, parent, prev))),
            new("signature", new JsonValue.String("sig")),
            new("digest", new JsonValue.String(digest)),
            new("author", new JsonValue.String(author)),
            new("board", new JsonValue.String("postgres")),
            new("kind", new JsonValue.String(PostKinds.Wire(kind))),
        };
        if (parent is not null) payload.Add(new("parent", new JsonValue.String(parent)));

        Require(await store.AppendAsync(
            Require(AggregateId.Create(id)),
            AggregateVersion.New,
            [new DomainEvent(Require(EventId.Create(id)), Require(EventType.Create(PostProjector.PostAcceptedType)), Require(ActorId.Create(author)), new JsonValue.Object([.. payload]))],
            ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The log of the seam (measured, Task 0 item 4): question Q (asker), question QS (another asker), answer A under Q, and RL, the answer's author's
    /// revision of A whose <c>parent</c> names QS. It is stored (R8.70 does not read a revision's parent), it lists in QS's thread, and its chain root is A.
    /// </summary>
    private static async Task<(InMemoryEventStore Store, ManualTimeProvider Clock)> SeamLogAsync(CancellationToken ct)
    {
        var clock = new ManualTimeProvider(Start);
        var store = new InMemoryEventStore(clock, new PermissiveLifecycleAppendGate());
        await AppendPostAsync(store, Q, Digest('0'), PostKind.Question, Asker, null, null, ct).ConfigureAwait(false);
        await AppendPostAsync(store, QS, Digest('5'), PostKind.Question, OtherAsker, null, null, ct).ConfigureAwait(false);
        await AppendPostAsync(store, A, Digest('a'), PostKind.Answer, AuthorX, Q, null, ct).ConfigureAwait(false);
        await AppendPostAsync(store, RL, Digest('1'), PostKind.Revision, AuthorX, QS, Digest('a'), ct).ConfigureAwait(false);
        return (store, clock);
    }

    private static async Task AcceptUnderAsync(InMemoryEventStore store, ManualTimeProvider clock, string thread, string post, string by, CancellationToken ct)
    {
        clock.Set(clock.GetUtcNow() + TimeSpan.FromMinutes(1));
        Require(await new AcceptAnswer(store, clock).RecordAsync(Typed.Post(thread), Typed.Post(post), Typed.Agent(by), ct).ConfigureAwait(false));
    }

    private static async Task<AcceptedAnswerIndex> FoldedAsync(InMemoryEventStore store, CancellationToken ct)
    {
        var log = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false));
        return AcceptedAnswerIndex.Fold(log, PostProjector.Fold(log), AllServable);
    }

    private static async Task<ImmutableArray<PostView>> PostsAsync(InMemoryEventStore store, CancellationToken ct) =>
        PostProjector.Fold(Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false)));

    /// <summary>
    /// PK41 (the rule fact, plan S5): a crafted acceptance whose <c>thread_root</c> is not the thread of the chain root. The log holds the acceptance of
    /// A under Q and an acceptance of RL under QS (the revision's own <c>parent</c>, the thread the route would never record it under). Which post the
    /// standing acceptance of Q names is A, and a post is accepted only through the thread of its chain root, so RL, whose chain root A belongs to Q, does not
    /// read accepted because a crafted event put it under QS.
    /// </summary>
    [Fact]
    public async Task R9_46_PK41_AnAcceptanceRecordedUnderTheWrongThreadMarksNeitherPostThere()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, clock) = await SeamLogAsync(ct);
        await AcceptUnderAsync(store, clock, Q, A, Asker, ct);
        await AcceptUnderAsync(store, clock, QS, RL, OtherAsker, ct);
        var index = await FoldedAsync(store, ct);
        var posts = await PostsAsync(store, ct);
        var a = posts.Single(p => p.PostId == A);
        var rl = posts.Single(p => p.PostId == RL);

        Assert.True(index.IsAccepted(a), "A is the post Q's standing acceptance names");
        Assert.False(index.IsAccepted(rl), "RL read accepted by its own parent (QS), a thread its chain root does not belong to");
    }

    /// <summary>
    /// PK3b below the route: the true asker's acceptance of RL under Q, the chain root's thread (what the route would record). RL reads accepted although
    /// its own <c>parent</c> names QS, and the original A stops reading accepted; the question's member names RL. One rule, so the three agree.
    /// </summary>
    [Fact]
    public async Task R9_46_PK3b_ARevisionWithALyingParentReadsAcceptedWhenItsTrueQuestionNamesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, clock) = await SeamLogAsync(ct);
        await AcceptUnderAsync(store, clock, Q, A, Asker, ct);
        await AcceptUnderAsync(store, clock, Q, RL, Asker, ct);
        var index = await FoldedAsync(store, ct);
        var posts = await PostsAsync(store, ct);

        Assert.Equal(Digest('1'), index.StandingOf(posts.Single(p => p.PostId == Q)).Digest);
        Assert.True(index.IsAccepted(posts.Single(p => p.PostId == RL)), "RL is the post Q names");
        Assert.False(index.IsAccepted(posts.Single(p => p.PostId == A)), "A was replaced by its own revision");
    }

    /// <summary>
    /// PK34 / FPK35's rule: the same state read through the listing rule. Listed beneath QS (G38's recorded non-property) RL reads accepted only if QS
    /// is the thread of its chain root, which it is not; listed beneath Q's own thread it does.
    /// </summary>
    [Fact]
    public async Task R9_46_PK34_AThreadListingMarksAPostAcceptedOnlyInTheThreadItsChainRootBelongsTo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, clock) = await SeamLogAsync(ct);
        await AcceptUnderAsync(store, clock, Q, RL, Asker, ct);
        var index = await FoldedAsync(store, ct);
        var rl = (await PostsAsync(store, ct)).Single(p => p.PostId == RL);

        Assert.True(index.IsAcceptedIn(rl, Typed.Post(Q)), "RL is the post Q's acceptance names");
        Assert.False(index.IsAcceptedIn(rl, Typed.Post(QS)), "RL listed beneath QS must not read accepted there");
    }
}
