using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Curia.Application.Projections;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using CsCheck;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// PR 3 (errata G47, R9.48), probes CHN1 to CHN9 and CHN54: the thread a post is a member of is decided by the root of its correction chain (R8.75) and
/// never by a revision's own <c>parent</c>. Every expected membership is written from the way the scene was built (which thread each post was made
/// for), never from <see cref="CorrectionIndex.ChainRootOf"/>, which is the rule under test. The posts are hand-built <see cref="PostView"/>s: placement
/// reads only <c>PostId</c>, <c>Digest</c>, <c>Author</c>, <c>Kind</c>, <c>Parent</c> and <c>Prev</c>, so nothing here is hashed or signed.
///
/// <para>Every probe names the final signature, <c>Thread(posts, root, corrections)</c>, which PR 3's Task 0 declared as an inert seam that places by
/// <c>parent</c> as before; each is therefore red by assertion on the unmodified tree, not by compile error. CHN8 is a control, green until FCHN9.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "A seeded generator builds a reproducible scene; nothing here is secret.")]
[SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "A test runs each once.")]
public sealed class ThreadPlacementTests
{
    private const string AuthorA = "https://agents.example/a";
    private const string AuthorB = "https://agents.example/b";

    private static string Id(int n) => "01CHN" + n.ToString("D21", CultureInfo.InvariantCulture);

    private static string Digest(int n) => "sha256:" + n.ToString("x64", CultureInfo.InvariantCulture);

    /// <summary>A hand-built post. Ids and digests derive from one counter, so the n-th post is Id(n) and Digest(n).</summary>
    private sealed class Scene
    {
        private readonly List<PostView> _posts = [];

        public ImmutableArray<PostView> Posts => [.. _posts];

        public PostView Add(string kind, string? parent, PostView? revises = null, string author = AuthorA, string? prevDigest = null)
        {
            var n = _posts.Count + 1;
            var post = new PostView(
                Id(n), "{}", "sig", Digest(n), author, "board", kind, parent,
                ServerTimestamp.At(new DateTimeOffset(2026, 10, 29, 0, 0, 0, TimeSpan.Zero).AddSeconds(n)), [], revises?.Digest ?? prevDigest);
            _posts.Add(post);
            return post;
        }

        public PostView Question() => Add("question", null);

        public PostView Answer(PostView question) => Add("answer", question.PostId);

        public PostView Comment(PostView on, string author = AuthorA) => Add("comment", on.PostId, author: author);

        /// <summary>An honest revision: a revision of <paramref name="of"/>, <c>parent</c> the post revised.</summary>
        public PostView Revise(PostView of, string? parent = null, string? author = null) =>
            Add("revision", parent ?? of.PostId, of, author ?? of.Author);
    }

    private static ImmutableArray<PostView> ThreadOf(ImmutableArray<PostView> posts, PostView root) =>
        PostProjector.Thread(posts, Typed.Post(root.PostId), CorrectionIndex.Fold(posts, static _ => true));

    private static string Ids(IEnumerable<PostView> posts, ImmutableArray<PostView> all) =>
        "[" + string.Join(", ", posts.Select(p => Name(p, all))) + "]";

    private static string Name(PostView p, ImmutableArray<PostView> all) =>
        $"#{all.ToList().FindIndex(x => x.PostId == p.PostId) + 1}:{p.Kind}";

    private static void Same(string what, IEnumerable<PostView> expected, IEnumerable<PostView> actual, ImmutableArray<PostView> all) =>
        Assert.True(
            expected.Select(p => p.PostId).SequenceEqual(actual.Select(p => p.PostId)),
            $"{what}: Expected: {Ids(expected, all)} Actual: {Ids(actual, all)}");

    /// <summary>CHN1. The lying revision moves: a revision of A5 whose <c>parent</c> names another question is in A5's thread and not the other's.</summary>
    [Fact]
    public void R9_48_CHN1_ALyingRevisionIsInTheThreadOfItsChainNotOfItsParent()
    {
        var s = new Scene();
        var q5 = s.Question();
        var a5 = s.Answer(q5);
        var q6 = s.Question();
        var lp1 = s.Revise(a5, parent: q6.PostId);
        var posts = s.Posts;

        Same("Thread(Q5)", [q5, a5, lp1], ThreadOf(posts, q5), posts);
        Same("Thread(Q6)", [q6], ThreadOf(posts, q6), posts);
    }

    private sealed record Built(ImmutableArray<PostView> Posts, Dictionary<string, HashSet<string>> ThreadByQuestion, List<PostView> Revisions);

    /// <summary>
    /// A random scene: 1 to 3 questions; per question 0 to 3 answers and 0 to 2 comments; revisions of random posts of depth 1 to 4, a fork at
    /// random. <c>Home</c> records the question each post was built into, which is the expected membership of that question's thread.
    /// </summary>
    private static Built Build(int seed)
    {
        var rng = new Random(seed);
        var s = new Scene();
        var home = new Dictionary<string, string>(StringComparer.Ordinal);
        var revisions = new List<PostView>();
        var questions = new List<PostView>();
        var targets = new List<PostView>();
        for (var i = rng.Next(1, 4); i > 0; i--)
        {
            var q = s.Question();
            questions.Add(q);
            home[q.PostId] = q.PostId;
            targets.Add(q);
            var own = new List<PostView> { q }; // a comment is built on a post of its own question, so its recorded home is true
            for (var a = rng.Next(0, 4); a > 0; a--)
            {
                var answer = s.Answer(q);
                home[answer.PostId] = q.PostId;
                targets.Add(answer);
                own.Add(answer);
            }

            for (var c = rng.Next(0, 3); c > 0; c--)
            {
                var comment = s.Comment(own[rng.Next(own.Count)]);
                home[comment.PostId] = q.PostId;
                targets.Add(comment);
                own.Add(comment);
            }
        }

        // Revisions: pick a post (not a revision's own successor chain end only: the chain may fork), revise it 1 to 4 deep.
        for (var r = rng.Next(1, 5); r > 0; r--)
        {
            var target = targets[rng.Next(targets.Count)];
            var at = target;
            for (var depth = rng.Next(1, 5); depth > 0; depth--)
            {
                var rev = s.Revise(at, parent: at.PostId);
                home[rev.PostId] = home[target.PostId];
                revisions.Add(rev);
                at = rng.Next(4) == 0 ? target : rev; // a fork: sometimes revise the original again
            }
        }

        var byQuestion = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var q in questions)
            byQuestion[q.PostId] = [.. home.Where(kv => kv.Value == q.PostId).Select(kv => kv.Key)];
        return new Built(s.Posts, byQuestion, revisions);
    }

    /// <summary>The ids of a revision's other-thread, other-post and non-id <c>parent</c> variants (ruling 19(d)); the seeded half adds its own id.</summary>
    private static IEnumerable<string> ParentVariants(Built built, PostView revision, bool seeded)
    {
        var all = built.Posts;
        var chainPrev = all.First(p => p.Digest == revision.Prev);
        yield return chainPrev.PostId;
        foreach (var q in built.ThreadByQuestion.Keys)
            yield return q;
        yield return all.First(p => p.Kind == "comment" || p.Kind == "answer" || p.Kind == "question").PostId;
        yield return "sha256:" + new string('9', 64);
        yield return "not-an-id";
        yield return Id(9_999);
        if (seeded) yield return revision.PostId;
    }

    private static string Describe(Built built, string question, HashSet<string> expected, IEnumerable<string> actual)
    {
        string N(string id) => "#" + (built.Posts.ToList().FindIndex(p => p.PostId == id) + 1);
        return $"thread of {N(question)}: Expected: [{string.Join(", ", built.Posts.Where(p => expected.Contains(p.PostId)).Select(p => N(p.PostId)))}] Actual: [{string.Join(", ", actual.Select(N))}]";
    }

    /// <summary>
    /// CHN2 (a property). No placement reads a revision's <c>parent</c>: for every revision, any <c>parent</c> from the variant list leaves every thread
    /// as the construction recorded. Expected membership is the construction's, never <see cref="CorrectionIndex.ChainRootOf"/>.
    /// </summary>
    [Fact]
    public void R9_48_CHN2_NoPlacementReadsARevisionsParent()
    {
        Gen.Int[0, 1_000_000].Sample(
            seed =>
            {
                foreach (var seeded in new[] { false, true })
                {
                    var built = Build(seed);
                    foreach (var revision in built.Revisions)
                    {
                        foreach (var variant in ParentVariants(built, revision, seeded).Distinct(StringComparer.Ordinal))
                        {
                            var lied = built.Posts.Select(p => p.PostId == revision.PostId ? p with { Parent = variant } : p).ToImmutableArray();
                            var index = CorrectionIndex.Fold(lied, static _ => true);
                            foreach (var (question, expected) in built.ThreadByQuestion)
                            {
                                var actual = PostProjector.Thread(lied, Typed.Post(question), index).Select(p => p.PostId).ToArray();
                                Assert.True(
                                    expected.SetEquals(actual) && actual.Length == expected.Count,
                                    $"parent=\"{(variant.Length > 12 ? variant[^12..] : variant)}\" on revision #{built.Posts.ToList().FindIndex(p => p.PostId == revision.PostId) + 1}: "
                                    + Describe(built, question, expected, actual));
                            }
                        }
                    }
                }
            },
            iter: 60);
    }

    /// <summary>CHN3. A question's revision lists with the question, a chain of any depth, whatever each revision's <c>parent</c> says.</summary>
    [Fact]
    public void R9_48_CHN3_AQuestionsRevisionsListWithTheQuestion()
    {
        var variants = new[] { "predecessor", "question", "other-question", "no-post", "digest", "non-id", "other-own-post", "itself" };
        foreach (var depth in new[] { 1, 2, 3, 4 })
        {
            foreach (var variant in variants)
            {
                var s = new Scene();
                var q = s.Question();
                var other = s.Question();
                var ownA = s.Answer(other);
                var chain = new List<PostView>();
                var at = q;
                for (var i = 0; i < depth; i++)
                {
                    var rev = s.Revise(at);
                    chain.Add(rev);
                    at = rev;
                }

                var posts = s.Posts;
                var lied = posts.Select(p => chain.Any(c => c.PostId == p.PostId)
                    ? p with
                    {
                        Parent = variant switch
                        {
                            "predecessor" => p.Parent,
                            "question" => q.PostId,
                            "other-question" => other.PostId,
                            "no-post" => Id(9_999),
                            "digest" => Digest(7),
                            "non-id" => "not-an-id",
                            "other-own-post" => ownA.PostId,
                            _ => p.PostId,
                        },
                    }
                    : p).ToImmutableArray();

                var actual = ThreadOf(lied, q);
                Same($"depth {depth}, parent {variant}: Thread(question)", [q, .. chain], actual, lied);
            }
        }
    }

    /// <summary>CHN4. A comment's revision lists with the comment; a comment on a revision lists where the revision does, even when the revision's parent lies.</summary>
    [Fact]
    public void R9_48_CHN4_ACommentsRevisionAndAReplyToARevisionFollowTheChain()
    {
        var s = new Scene();
        var q5 = s.Question();
        var a5 = s.Answer(q5);
        var q6 = s.Question();
        var c = s.Comment(a5);
        var revisionOfC = s.Revise(c, parent: q6.PostId);
        var onRevision = s.Comment(revisionOfC);
        var posts = s.Posts;

        Same("Thread(Q5)", [q5, a5, c, revisionOfC, onRevision], ThreadOf(posts, q5), posts);
        Same("Thread(Q6)", [q6], ThreadOf(posts, q6), posts);
    }

    /// <summary>CHN5 (i) and (ii), seeded logs only (ruling 19). A revision that corrects nothing is in no thread but the one it roots.</summary>
    [Theory]
    [InlineData("no-stored-prev")]
    [InlineData("another-authors-post")]
    public void R9_48_CHN5_ARevisionThatCorrectsNothingIsInNoThreadButItsOwn(string form)
    {
        var s = new Scene();
        var q = s.Question();
        var a = s.Answer(q);
        var stranger = s.Add("answer", q.PostId, author: AuthorB);
        var orphan = form == "no-stored-prev"
            ? s.Add("revision", q.PostId, prevDigest: "sha256:" + new string('e', 64))
            : s.Add("revision", q.PostId, revises: stranger);
        var posts = s.Posts;

        Same($"{form}: Thread(question)", [q, a, stranger], ThreadOf(posts, q), posts);
        Same($"{form}: Thread(itself)", [orphan], ThreadOf(posts, orphan), posts);
    }

    /// <summary>CHN6 (LP2). A revision of a comment, right <c>prev</c>, same author, <c>parent</c> naming no post, is in its comment's thread.</summary>
    [Fact]
    public void R9_48_CHN6_ARevisionWhoseParentNamesNoPostIsInItsCommentsThread()
    {
        var s = new Scene();
        var q = s.Question();
        var c = s.Comment(q);
        var lp2 = s.Revise(c, parent: Id(9_999));
        var posts = s.Posts;

        Same("Thread(question)", [q, c, lp2], ThreadOf(posts, q), posts);
    }

    /// <summary>CHN7 (ruling 7). A listing asked for at a revision is the subtree under that id; one asked for at the answer holds the whole chain.</summary>
    [Fact]
    public void R9_48_CHN7_AListingAtARevisionIsTheSubtreeUnderIt()
    {
        var s = new Scene();
        var q = s.Question();
        var a5 = s.Answer(q);
        var lp1 = s.Revise(a5);
        var onLp1 = s.Comment(lp1);
        var lp1b = s.Revise(lp1);
        var posts = s.Posts;

        Same("Thread(LP1)", [lp1, onLp1], ThreadOf(posts, lp1), posts);
        Same("Thread(A5)", [a5, lp1, onLp1, lp1b], ThreadOf(posts, a5), posts);
    }

    /// <summary>CHN8 (a control). A <c>prev</c> cycle, or two posts of one digest, end the walk: no hang, no throw, deterministic membership.</summary>
    [Fact]
    public async Task R9_48_CHN8_APrevCycleOrARepeatedDigestEndsTheWalk()
    {
        var s = new Scene();
        var q = s.Question();
        // r1 and r2 name each other's digest; r3 and r3b share one digest.
        var r1 = s.Add("revision", q.PostId, prevDigest: Digest(3));
        var r2 = s.Add("revision", q.PostId, prevDigest: Digest(2));
        var twin = s.Add("revision", q.PostId, prevDigest: Digest(2));
        var posts = s.Posts.Select(p => p.PostId == twin.PostId ? p with { Digest = Digest(3) } : p).ToImmutableArray();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var run = Task.Run(() => (ThreadOf(posts, q), ThreadOf(posts, q)), cts.Token);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.True(ReferenceEquals(finished, run), "Thread did not return within ten seconds (an unbounded walk)");

        var (first, second) = await run.ConfigureAwait(true);
        Assert.True(first.Select(p => p.PostId).SequenceEqual(second.Select(p => p.PostId)), "membership is not deterministic");
        Assert.Contains(first, p => p.PostId == q.PostId);
        Assert.NotNull(r1);
        Assert.NotNull(r2);
    }

    /// <summary>CHN9. One <c>Thread</c>: exactly one public method of that name on <see cref="PostProjector"/>, taking a <see cref="CorrectionIndex"/>.</summary>
    [Fact]
    public void R9_48_CHN9_ThereIsOneThreadAndItTakesTheCorrectionIndex()
    {
        var threads = typeof(PostProjector).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .Where(m => m.Name == "Thread").ToArray();
        var signatures = string.Join("; ", threads.Select(m => "Thread(" + string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name)) + ")"));

        Assert.True(
            threads.Length == 1 && threads[0].GetParameters().Any(p => p.ParameterType == typeof(CorrectionIndex)),
            $"Expected: exactly one Thread, taking a CorrectionIndex. Actual: {signatures}");
    }

    /// <summary>
    /// CHN54 (replaces CHN5 (iii), ruling 19(c)). A revision chained to one own post and parented to another is placed with its chain: one author with A5
    /// and A8 in different threads, a revision of A5 whose <c>parent</c> is A8.
    /// </summary>
    [Fact]
    public void R9_48_CHN54_ARevisionChainedToOnePostAndParentedToAnotherIsPlacedWithItsChain()
    {
        var s = new Scene();
        var q5 = s.Question();
        var a5 = s.Answer(q5);
        var q8 = s.Question();
        var a8 = s.Answer(q8);
        var via = s.Revise(a5, parent: a8.PostId);
        var posts = s.Posts;

        Same("Thread(Q5)", [q5, a5, via], ThreadOf(posts, q5), posts);
        Same("Thread(Q8)", [q8, a8], ThreadOf(posts, q8), posts);
    }
}
