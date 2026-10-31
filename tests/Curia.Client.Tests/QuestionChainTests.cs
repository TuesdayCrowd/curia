using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 3 (errata G50, R11.67), Task 0's probes RLW23 and RLW45 (the pure halves) at the reference client: the bounded walk down a revised question's chain, and
/// what it says of a fork. <b>CONSISTENCY CHECK (RLW42): this file calls <c>QuestionChain.</c> and <c>Supersession.</c> on purpose</b>, since the walk and its
/// words are what it drives; the bound, the words it must say and the counts are the errata's (<see cref="PublishedReadersLastWords"/>) and the chains
/// are served by <see cref="StubLog"/> in the shapes each row names, so the expectation is the chain each row built and the reads the stub recorded.
///
/// <para>On the seam tree <c>QuestionChain.WalkAsync</c> reads nothing and <c>Supersession.QuestionChainStanding</c> says nothing, so each row is red by assertion
/// (the reads counted, the words missing).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class QuestionChainTests : IDisposable
{
    private sealed record Hop(string Id, PostDraft Draft, SignedSubmission Signed);

    private readonly StubLog _log = new();

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string IdOf(int i) => $"01RLWCHAINPOST0000000{i:D5}";

    private static string Secret(int i) => $"AUTHORED-BODY-OF-HOP-{i}";

    private Hop Question()
    {
        var draft = new PostDraft { Kind = PostKind.Question, Board = "b", Title = "A question about finches", Body = "Which finch is this?" };
        return new Hop(IdOf(0), draft, _log.SignDraft(draft));
    }

    private Hop Revision(int i, Hop previous, PostKind kind = PostKind.Revision, string? prev = null)
    {
        var draft = new PostDraft { Kind = kind, Board = "b", Parent = previous.Id, Prev = kind is PostKind.Revision ? prev ?? previous.Signed.PrefixedDigest : null, Body = Secret(i) };
        return new Hop(IdOf(i), draft, _log.SignDraft(draft));
    }

    private List<Hop> Chain(int depth)
    {
        var hops = new List<Hop> { Question() };
        for (var i = 1; i <= depth; i++) hops.Add(Revision(i, hops[i - 1]));
        return hops;
    }

    private static ImmutableArray<Successor> Listing(params Hop[] next) => [.. next.Select(h => new Successor(h.Signed.PrefixedDigest, h.Id))];

    /// <summary>Serves every hop with <paramref name="lists"/>' word on it (by index; the next hop alone where the index has none, and nothing for the last).</summary>
    private void Serve(List<Hop> hops, Dictionary<int, ImmutableArray<Successor>>? lists = null)
    {
        for (var i = 0; i < hops.Count; i++)
        {
            var list = lists is not null && lists.TryGetValue(i, out var given) ? given : i + 1 < hops.Count ? Listing(hops[i + 1]) : [];
            _log.ServePost(hops[i].Id, hops[i].Draft, hops[i].Signed, list);
        }
    }

    private int PostReads => _log.Requests.Count(r => r.StartsWith("GET /v1/posts/", StringComparison.Ordinal));

    private async Task<(int Reads, string Words)> WalkAsync(Hop question)
    {
        var client = _log.Client();
        var read = await client.GetPostAsync(question.Id, MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(read.TryGetValue(out var post, out var refusal), refusal?.Summary);
        _log.Requests.Clear();

        var walked = await QuestionChain.WalkAsync(client, post!, TestContext.Current.CancellationToken);
        return (PostReads, Supersession.QuestionChainStanding(walked).Text);
    }

    private static string Id(string id) => Curia.Canon.Json.DisplayLiteral.Of(id);

    /// <summary>
    /// RLW23. The walk, with a counting reader. Depth 0 reads nothing and states nothing; depths 1, 2 and 8 read exactly as many posts as there are revisions and name
    /// each by post id and the last as the latest; depth 9 reads the bound and says <c>NOT WALKED</c> naming the eighth post; a fork stops the walk without a read of
    /// either branch; a successor with no post id ends it in R10.91's words; a served post that is not the one named (its digest, its signed kind or its signed
    /// <c>prev</c> does not hold), a cycle and a failed read each end it in fixed words of their own naming nothing an author wrote; and the walk never throws.
    /// </summary>
    [Fact]
    public async Task R11_67_RLW23_TheWalkReadsExactlyTheChainAndStopsInFixedWords()
    {
        var bound = PublishedReadersLastWords.WalkBound();
        var (notWalked, _, forked) = PublishedReadersLastWords.WalkWords();
        var definition = PublishedReadersLastWords.DefinitionSentence();
        var (chainEnd, latestPhrase) = PublishedReadersLastWords.ChainEnd();
        var publishedStops = PublishedReadersLastWords.Stops();
        var problems = new List<string>();
        var stopLines = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var depth in new[] { 0, 1, 2, bound, bound + 1 })
        {
            var hops = Chain(depth);
            Serve(hops);
            var (reads, words) = await WalkAsync(hops[0]);
            var expected = Math.Min(depth, bound);
            if (reads != expected) problems.Add($"depth {depth}: Expected: {expected} reads Actual: {reads}");
            if (depth == 0 && words.Length != 0) problems.Add($"depth 0: Expected: nothing said of a question that is not revised Actual: {words}");
            if (depth is >= 1 and <= 8)
            {
                foreach (var hop in hops.Skip(1)) if (!words.Contains(Id(hop.Id), StringComparison.Ordinal)) problems.Add($"depth {depth}: {hop.Id} is not named: {words}");
                if (!PublishedReadersLastWords.Says(words, chainEnd)) problems.Add($"depth {depth}: Expected: {chainEnd} Actual: {words}");
                if (words.Contains(notWalked, StringComparison.Ordinal)) problems.Add($"depth {depth}: says {notWalked} of a chain it walked whole");
            }

            if (depth == bound + 1)
            {
                if (!words.Contains(notWalked, StringComparison.Ordinal)) problems.Add($"depth {depth}: Expected: {notWalked} Actual: {words}");
                if (!words.Contains(Id(hops[bound].Id), StringComparison.Ordinal)) problems.Add($"depth {depth}: {notWalked} does not name the last post reached ({hops[bound].Id}): {words}");
                if (words.Contains(Id(hops[bound + 1].Id), StringComparison.Ordinal) && !words.Contains(notWalked, StringComparison.Ordinal)) problems.Add($"depth {depth}: names the post past the bound as reached");
            }
        }

        // A fork at the second hop: Q -> r1 -> {r2a, r2b}. The first branch point stops the walk; neither branch is read.
        var fork = Chain(1);
        var branchA = Revision(2, fork[1]);
        var branchB = Revision(3, fork[1]);
        fork.AddRange([branchA, branchB]);
        Serve(fork, new() { [1] = Listing(branchA, branchB), [2] = [], [3] = [] });
        var (forkReads, forkWords) = await WalkAsync(fork[0]);
        if (forkReads != 1) problems.Add($"a fork at the second hop: Expected: 1 read (the first hop) Actual: {forkReads}");
        if (forkWords.Contains(latestPhrase, StringComparison.Ordinal)) problems.Add($"a fork at the second hop names a latest revision with the published phrase \"{latestPhrase}\": {forkWords}");
        if (!forkWords.Contains(forked, StringComparison.Ordinal) || !forkWords.Contains(Id(branchA.Id), StringComparison.Ordinal) || !forkWords.Contains(Id(branchB.Id), StringComparison.Ordinal))
            problems.Add($"a fork at the second hop: Expected: FORKED naming both branches Actual: {forkWords}");

        // A successor with no post id: the walk ends in R10.91's words and claims no head.
        var unserved = Chain(1);
        Serve(unserved, new() { [0] = [new Successor(unserved[1].Signed.PrefixedDigest, null)] });
        var (unservedReads, unservedWords) = await WalkAsync(unserved[0]);
        if (unservedReads != 0 || !unservedWords.Contains(definition, StringComparison.Ordinal) || unservedWords.Contains(latestPhrase, StringComparison.Ordinal) || !publishedStops.Any(stop => PublishedReadersLastWords.Says(unservedWords, stop)))
            problems.Add($"a successor with no post id: Expected: 0 reads, R10.91's words, no latest Actual: {unservedReads} reads, {unservedWords}");

        // Each stop that is a post that is not the one named, a cycle and a failed read. Every scenario is served and walked in its turn: the stub keeps one
        // document per post id, and these scenarios reuse the same ids with different content, so building them all first would walk the last one five times.
        var scenarios = new (string Name, Func<List<Hop>> Serve)[]
        {
            ("a digest that is not the one named", () =>
            {
                var hops = Chain(1);
                Serve(hops, new() { [0] = [new Successor("sha256:" + new string('f', 64), hops[1].Id)] });
                return hops;
            }),
            ("a signed kind that is not revision", () =>
            {
                var hops = new List<Hop> { Question() };
                hops.Add(Revision(1, hops[0], PostKind.Answer));
                Serve(hops);
                return hops;
            }),
            ("a signed prev that is not the previous digest", () =>
            {
                var hops = new List<Hop> { Question() };
                hops.Add(Revision(1, hops[0], prev: "sha256:" + new string('9', 64)));
                Serve(hops);
                return hops;
            }),
            ("a post already read", () =>
            {
                var hops = Chain(1);
                Serve(hops, new() { [1] = Listing(hops[1]) });
                return hops;
            }),
            ("a read that fails", () =>
            {
                var hops = Chain(1);
                Serve(hops);
                _log.RefusedPosts[hops[1].Id] = (System.Net.HttpStatusCode.ServiceUnavailable, "curia/stub/unavailable");
                return hops;
            }),
        };

        foreach (var (name, serve) in scenarios)
        {
            _log.RefusedPosts.Clear();
            var hops = serve();
            var (reads, words) = await WalkAsync(hops[0]);
            if (reads is < 1 or > 2) problems.Add($"{name}: Expected: the one read of the hop Actual: {reads}");
            if (words.Length == 0) problems.Add($"{name}: Expected: a stop in fixed words Actual: nothing");
            if (words.Contains("AUTHORED-BODY", StringComparison.Ordinal) || words.Contains(new string('f', 64), StringComparison.Ordinal) || words.Contains(new string('9', 64), StringComparison.Ordinal))
                problems.Add($"{name}: the stop names a value the Forum or an author wrote: {words}");
            if (words.Contains(latestPhrase, StringComparison.Ordinal)) problems.Add($"{name}: states a latest revision past a stop with the published phrase \"{latestPhrase}\": {words}");
            if (!publishedStops.Any(stop => PublishedReadersLastWords.Says(words, stop))) problems.Add($"{name}: says none of the published stops: {words}");
            stopLines[name] = words;
        }

        if (stopLines.Values.Distinct(StringComparer.Ordinal).Count() != stopLines.Count) problems.Add($"two stops share their words: {string.Join(" | ", stopLines.Select(s => s.Key + " => " + s.Value))}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// RLW45 (the pure half; added 2026-10-30, ruling 17). For a question whose list holds two or more successors the line says FORKED, names each by post id,
    /// says that no branch was followed (R11.67's sentence) and names no latest revision, and the walk makes no read; a single-successor chain in the same fact
    /// states its latest as before.
    /// </summary>
    [Fact]
    public async Task R11_67_RLW45_AForkIsStatedAsNotFollowed()
    {
        var (_, _, forked) = PublishedReadersLastWords.WalkWords();
        var none = PublishedReadersLastWords.NoneFollowed();
        var (chainEnd, latestPhrase) = PublishedReadersLastWords.ChainEnd();
        var forkLine = PublishedReadersLastWords.ForkLine();

        var hops = new List<Hop> { Question() };
        var a = Revision(1, hops[0]);
        var b = Revision(2, hops[0]);
        hops.AddRange([a, b]);
        Serve(hops, new() { [0] = Listing(a, b), [1] = [], [2] = [] });
        var (reads, words) = await WalkAsync(hops[0]);

        var single = Chain(1);
        Serve(single);
        var (_, singleWords) = await WalkAsync(single[0]);

        var problems = new List<string>();
        if (reads != 0) problems.Add($"a fork: Expected: no branch read Actual: {reads} reads");
        if (!words.Contains(forked, StringComparison.Ordinal)) problems.Add($"Expected: FORKED Actual: {words}");
        if (!words.Contains(Id(a.Id), StringComparison.Ordinal) || !words.Contains(Id(b.Id), StringComparison.Ordinal)) problems.Add($"Expected: both branches named Actual: {words}");
        if (!words.Contains(none, StringComparison.Ordinal)) problems.Add($"Expected: the none-followed sentence \"{none}\" Actual: {words}");
        if (words.Contains(latestPhrase, StringComparison.Ordinal)) problems.Add($"a fork names a latest revision with the published phrase \"{latestPhrase}\": {words}");
        if (!PublishedReadersLastWords.Says(words, forkLine)) problems.Add($"Expected: the published fork line {forkLine} Actual: {words}");
        if (!PublishedReadersLastWords.Says(singleWords, chainEnd) || singleWords.Contains(none, StringComparison.Ordinal)) problems.Add($"the control: a single-successor chain states its latest and follows it: {singleWords}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// R11.67 (review I2). The receipt's closing parenthesis says what the walk read: where it stopped before any post after the question was read and confirmed
    /// (a fork at the question, a successor the Forum does not serve, a read that failed at the first hop, a first successor that does not hash to its digest)
    /// it is the published "none was" form, and never the form that says each digest on the chain was recomputed; where one hop was confirmed it is the other.
    /// </summary>
    [Fact]
    public async Task R11_67_I2_TheClosingParenthesisSaysWhatTheWalkRead()
    {
        var (read, none) = PublishedReadersLastWords.Closings();
        var problems = new List<string>();

        var scenarios = new (string Name, Func<List<Hop>> Serve, bool Confirmed)[]
        {
            ("a fork at the question", () =>
            {
                var hops = new List<Hop> { Question() };
                var a = Revision(1, hops[0]);
                var b = Revision(2, hops[0]);
                hops.AddRange([a, b]);
                Serve(hops, new() { [0] = Listing(a, b), [1] = [], [2] = [] });
                return hops;
            }, false),
            ("a first successor the Forum does not serve", () =>
            {
                var hops = Chain(1);
                Serve(hops, new() { [0] = [new Successor(hops[1].Signed.PrefixedDigest, null)] });
                return hops;
            }, false),
            ("a read that failed at the first hop", () =>
            {
                var hops = Chain(1);
                Serve(hops);
                _log.RefusedPosts[hops[1].Id] = (System.Net.HttpStatusCode.ServiceUnavailable, "curia/stub/unavailable");
                return hops;
            }, false),
            ("a first successor that does not hash to its digest", () =>
            {
                var hops = Chain(1);
                Serve(hops, new() { [0] = [new Successor("sha256:" + new string('f', 64), hops[1].Id)] });
                return hops;
            }, false),
            ("one hop confirmed, then the latest", () =>
            {
                var hops = Chain(1);
                Serve(hops);
                return hops;
            }, true),
        };

        foreach (var (name, serve, confirmed) in scenarios)
        {
            _log.RefusedPosts.Clear();
            var hops = serve();
            var (_, words) = await WalkAsync(hops[0]);
            var (expected, other) = confirmed ? (read, none) : (none, read);
            if (!PublishedReadersLastWords.Says(words, expected)) problems.Add($"{name}: Expected: {expected} Actual: {words}");
            if (PublishedReadersLastWords.Says(words, other)) problems.Add($"{name}: says the other closing: {words}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
