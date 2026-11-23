using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CsCheck;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Curia.Domain.Verification;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>
/// R9.68 (errata G64), PR 8's probes DVW1 to DVW3 and DVW7: a post the share cap defers moves to the next window of <c>pageSize</c> posts in which its author and owner are
/// under the cap, never to the end of the list; a window the cap leaves short is topped up with the earliest-ranked deferred posts; and a post is deferred if and only if its
/// window in the diversified list is later than its window in the admitted list.
///
/// <para>The oracles are hand tables quoted beside R9.68's sentence, and, for the property, a simulation written here from the sentence (the cap, the windows, the top-up,
/// the position rule) over the two lists the test holds. None reads the implementation's <c>Deferred</c> flag as a premise: the flag is the thing compared against the position
/// computed from the lists. A probe reads only <c>HybridRanking.Diversify</c>'s signature, which does not change.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class WindowedDiversifyTests
{
    private static SearchablePost Post(string id, string author, long seq, string? owner = null, string? duplicateOf = null) =>
        new(id, Digest(id), "board", PostKind.Answer, id, "body of " + id, [], author, seq, duplicateOf, owner);

    private static string Digest(string id) => "sha256:" + id.PadRight(64, '0');

    private static ImmutableArray<RankedPost> Ranked(params SearchablePost[] posts) =>
        HybridRanking.Rank(
            [.. posts.Select((p, i) => new SearchHit(p, 1000 - i, new RankExplanation(0, 1000 - i, 0, 1000 - i)))],
            [],
            _ => VerificationLevel.V0);

    private static string[] Ids(IEnumerable<RankedPost> posts) => [.. posts.Select(r => r.Post.PostId)];

    private static string[] DeferredIds(IEnumerable<RankedPost> posts) => [.. posts.Where(r => r.Deferred).Select(r => r.Post.PostId)];

    private static string Show(IEnumerable<string> ids) => "[" + string.Join(",", ids) + "]";

    /// <summary>
    /// DVW1. R9.68: "a post the cap defers moves to the next window of `limit` results in which its author and owner are under the cap, never to the end of the list".
    /// The scene is ops's: twelve candidates, <c>A1 A2 B C D E F G H I J K</c>, A authoring the first two and each other post its own author, <c>limit</c> 3 so the cap is 1.
    /// Hand table, window by window over the remaining posts in rank order:
    /// <code>
    /// window 0: A1 pass, A2 fails (A holds 1 of 1), B pass, C pass            -> A1 B C
    /// window 1: remaining A2 D E F ...; A2 passes (a new window), D, E        -> A2 D E
    /// window 2: F G H     window 3: I J K
    /// list: A1 B C | A2 D E | F G H | I J K
    /// </code>
    /// A2 was at position 1 of the admitted list (window 0, positions 0 to 2) and is at position 3 of the diversified list (window 1): deferred. C was at position 3 (window 1) and is
    /// at position 2 (window 0): moved earlier, not deferred. The old rule put A2 last, in window 3.
    /// </summary>
    [Fact]
    public void DVW1_ADeferredPostMovesToTheNextWindowAndNotToTheEnd()
    {
        var a1 = Post("A1", "agent://a", 1); var a2 = Post("A2", "agent://a", 2);
        var others = "BCDEFGHIJK".Select((c, i) => Post(c.ToString(), "agent://" + c, 3 + i)).ToArray();
        var ranked = Ranked([a1, a2, .. others]);

        var diversified = HybridRanking.Diversify(ranked, pageSize: 3);

        var expected = new[] { "A1", "B", "C", "A2", "D", "E", "F", "G", "H", "I", "J", "K" };
        Assert.True(
            Ids(diversified).SequenceEqual(expected),
            $"Expected: {Show(expected)}  Actual: {Show(Ids(diversified))}");
        Assert.True(DeferredIds(diversified).SequenceEqual(["A2"]), $"Expected: deferred [A2]  Actual: deferred {Show(DeferredIds(diversified))}");
    }

    /// <summary>The per-post oracle DVW2 uses: the window in the admitted list and in the diversified list, from the positions the test holds.</summary>
    private static bool MovedToALaterWindow(string id, IReadOnlyList<RankedPost> before, IReadOnlyList<RankedPost> after, int pageSize)
    {
        var b = Index(before, id) / pageSize;
        var a = Index(after, id) / pageSize;
        return a > b;
    }

    private static int Index(IReadOnlyList<RankedPost> list, string id)
    {
        for (var i = 0; i < list.Count; i++) if (list[i].Post.PostId == id) return i;
        return -1;
    }

    /// <summary>One generated scene: the page size and the posts, each with an author, an owner (or none) and a possible-duplicate target (an earlier post, or none).</summary>
    private static readonly Gen<(int PageSize, ImmutableArray<RankedPost> Ranked)> Scenes =
        Gen.Select(Gen.Int[1, 6], Gen.Int[0, 16], Gen.Int[0, 3].Array[16], Gen.Int[0, 3].Array[16], Gen.Int[-1, 15].Array[16])
            .Select(t =>
            {
                var (pageSize, length, authors, owners, duplicates) = t;
                var posts = new List<SearchablePost>();
                for (var i = 0; i < length; i++)
                {
                    var id = "p" + i.ToString(CultureInfo.InvariantCulture);
                    var owner = owners[i] == 0 ? null : "owner://" + owners[i].ToString(CultureInfo.InvariantCulture);
                    var of = duplicates[i] is >= 0 and var target && target < i ? Digest("p" + target.ToString(CultureInfo.InvariantCulture)) : null;
                    posts.Add(Post(id, "agent://" + authors[i].ToString(CultureInfo.InvariantCulture), i + 1, owner, of));
                }

                return (pageSize, Ranked([.. posts]));
            });

    private static string Owner(SearchablePost p) => p.Owner ?? p.Author;

    /// <summary>The cap the sentence gives: half a window, at least one.</summary>
    private static int Cap(int pageSize) => Math.Max(1, pageSize / 2);

    private sealed class State
    {
        public Dictionary<string, int> Authors { get; } = [];
        public Dictionary<string, int> Owners { get; } = [];
        public HashSet<string> Digests { get; } = [];
        public HashSet<string> DuplicatesOf { get; } = [];

        public State Copy()
        {
            var c = new State();
            foreach (var kv in Authors) c.Authors[kv.Key] = kv.Value;
            foreach (var kv in Owners) c.Owners[kv.Key] = kv.Value;
            c.Digests.UnionWith(Digests);
            c.DuplicatesOf.UnionWith(DuplicatesOf);
            return c;
        }

        public bool Fails(SearchablePost p, int cap) =>
            (Authors.TryGetValue(p.Author, out var a) && a >= cap)
            || (Owners.TryGetValue(Owner(p), out var o) && o >= cap)
            || (p.PossibleDuplicateOf is not null && Digests.Contains(p.PossibleDuplicateOf))
            || DuplicatesOf.Contains(p.Digest);

        public void Add(SearchablePost p)
        {
            Authors[p.Author] = Authors.GetValueOrDefault(p.Author) + 1;
            Owners[Owner(p)] = Owners.GetValueOrDefault(Owner(p)) + 1;
            Digests.Add(p.Digest);
            if (p.PossibleDuplicateOf is not null) DuplicatesOf.Add(p.PossibleDuplicateOf);
        }
    }

    /// <summary>DVW2 (a): the output is a permutation of the input; nothing is dropped and nothing is repeated.</summary>
    [Fact]
    public void DVW2a_TheOutputIsAPermutationOfTheInput()
    {
        Scenes.Sample(
            scene => Ids(HybridRanking.Diversify(scene.Ranked, scene.PageSize)).Order(StringComparer.Ordinal).SequenceEqual(Ids(scene.Ranked).Order(StringComparer.Ordinal)),
            iter: 400,
            threads: 1);
    }

    /// <summary>
    /// DVW2 (b): R9.68's fill rule. Walking the output window by window, the first post that fails the cap against the passing posts before it opens the top-up, and every post
    /// from there on (the rest of the window and every later window) fails the same state: a window exceeds the cap only when every remaining post failed it. (That every window
    /// but the last is <c>pageSize</c> long is the list being cut by position; the property is that the top-up is the only way a window goes over the cap.)
    /// </summary>
    [Fact]
    public void DVW2b_AWindowExceedsTheCapOnlyWhenEveryRemainingPostFailedIt()
    {
        Scenes.Sample(
            scene =>
            {
                var output = HybridRanking.Diversify(scene.Ranked, scene.PageSize);
                var cap = Cap(scene.PageSize);
                for (var start = 0; start < output.Length; start += scene.PageSize)
                {
                    var state = new State();
                    var end = Math.Min(start + scene.PageSize, output.Length);
                    for (var p = start; p < end; p++)
                    {
                        if (state.Fails(output[p].Post, cap))
                        {
                            // The passing candidates are exhausted at p: everything after p, in this window or a later one, failed this window's passing state.
                            var passing = state.Copy();
                            for (var rest = p; rest < output.Length; rest++)
                                if (!passing.Fails(output[rest].Post, cap)) return false;
                            break;
                        }

                        state.Add(output[p].Post);
                    }
                }

                return true;
            },
            iter: 400,
            threads: 1);
    }

    /// <summary>DVW2 (c): R9.68's rule, "deferred if and only if its window in the diversified list is later than its window in the admitted list", computed from the two lists.</summary>
    [Fact]
    public void DVW2c_DeferredIsExactlyALaterWindow()
    {
        Scenes.Sample(
            scene =>
            {
                var output = HybridRanking.Diversify(scene.Ranked, scene.PageSize);
                return output.All(r => r.Deferred == MovedToALaterWindow(r.Post.PostId, scene.Ranked, output, scene.PageSize));
            },
            iter: 400,
            threads: 1);
    }

    /// <summary>DVW2 (d): a list in which no author, owner or duplicate repeats is returned as it came and nothing is deferred.</summary>
    [Fact]
    public void DVW2d_AListWithNoRepeatIsReturnedUnchangedAndNothingIsDeferred()
    {
        Gen.Select(Gen.Int[1, 6], Gen.Int[0, 16]).Sample(
            t =>
            {
                var (pageSize, length) = t;
                var posts = Enumerable.Range(0, length).Select(i => Post("p" + i.ToString(CultureInfo.InvariantCulture), "agent://" + i.ToString(CultureInfo.InvariantCulture), i + 1)).ToArray();
                var ranked = Ranked(posts);
                var output = HybridRanking.Diversify(ranked, pageSize);
                return Ids(output).SequenceEqual(Ids(ranked)) && output.All(r => !r.Deferred);
            },
            iter: 200,
            threads: 1);
    }

    /// <summary>
    /// DVW3. R9.68's top-up: "SHALL fill a window the cap leaves short with the earliest-ranked deferred posts". One author, six posts, <c>limit</c> 4 (cap 2).
    /// Hand table: window 0 takes a1 and a2 (A holds 2 of 2) and a3 to a6 all fail, so the window is topped up with the earliest-ranked, a3 and a4; window 1 is a5 a6.
    /// <code>a1 a2 a3 a4 | a5 a6</code> A post never leaves its window (a3 and a4 were in window 0 of the admitted list, a5 and a6 in window 1), so nothing is deferred and the count is 0.
    /// </summary>
    [Fact]
    public void DVW3_AWindowTheCapLeavesShortIsToppedUpAndNothingIsDeferred()
    {
        var posts = Enumerable.Range(1, 6).Select(i => Post("a" + i.ToString(CultureInfo.InvariantCulture), "agent://a", i)).ToArray();
        var diversified = HybridRanking.Diversify(Ranked(posts), pageSize: 4);

        Assert.True(Ids(diversified).SequenceEqual(["a1", "a2", "a3", "a4", "a5", "a6"]), $"Expected: [a1,a2,a3,a4,a5,a6]  Actual: {Show(Ids(diversified))}");
        Assert.True(DeferredIds(diversified).Length == 0, $"Expected: deferred []  Actual: deferred {Show(DeferredIds(diversified))}");
    }

    /// <summary>
    /// DVW7. R10.6 inside the window pass. <c>limit</c> 2 (cap 1), ranked <c>o</c>, <c>dupe</c> (a possible duplicate of o), <c>other</c>.
    /// Hand table at limit 2: window 0 takes o, dupe fails (its original is placed), other passes -> <c>o other</c>; window 1 is <c>dupe</c>: <c>o other | dupe</c>.
    /// dupe was at position 1 (window 0) and sits at position 2 (window 1): deferred, and the page counts it.
    /// At limit 3 one window holds all three: o and other pass, dupe fails and tops the window up -> <c>o other dupe</c>, all in window 0, so nothing is deferred (the duplicate is
    /// behind its original in order only; this is R10.6's row, <c>["o","x","d"]</c>, which the same window rule keeps).
    /// </summary>
    [Fact]
    public void DVW7_ADuplicateIsDeferredByPositionOnlyWhenItsWindowMoves()
    {
        var original = Post("o", "agent://a", 1);
        var duplicate = Post("dupe", "agent://b", 2, duplicateOf: original.Digest);
        var other = Post("other", "agent://c", 3);
        var ranked = Ranked(original, duplicate, other);

        var atTwo = HybridRanking.Diversify(ranked, pageSize: 2);
        Assert.True(Ids(atTwo).SequenceEqual(["o", "other", "dupe"]), $"limit 2 Expected: [o,other,dupe]  Actual: {Show(Ids(atTwo))}");
        Assert.True(DeferredIds(atTwo).SequenceEqual(["dupe"]), $"limit 2 Expected: deferred [dupe]  Actual: deferred {Show(DeferredIds(atTwo))}");

        var atThree = HybridRanking.Diversify(ranked, pageSize: 3);
        Assert.True(Ids(atThree).SequenceEqual(["o", "other", "dupe"]), $"limit 3 Expected: [o,other,dupe]  Actual: {Show(Ids(atThree))}");
        Assert.True(DeferredIds(atThree).Length == 0, $"limit 3 Expected: deferred []  Actual: deferred {Show(DeferredIds(atThree))}");
    }
}
