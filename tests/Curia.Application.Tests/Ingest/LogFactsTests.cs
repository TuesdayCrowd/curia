#pragma warning disable CA2007 // test code: there is no SynchronizationContext to resume on (as the other suites of this project do, per await, with ConfigureAwait(true))
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using CsCheck;
using Xunit;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// Errata G57, ruling 20 to 21: the incremental form and the digest-only reading, probes LCB27, LCB28 and LCB30. Logs are built through the in-memory store
/// (<see cref="LateCommitLog"/>) so every event carries a store-assigned sequence. A projection is compared by value (a <see cref="PostView"/> holds an
/// <see cref="ImmutableArray{T}"/>, whose record equality is by reference), never by the projectors' own equality.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LogFactsTests
{
    private const string Author = "https://agents.example/author";
    private const string Voter = "https://agents.example/voter";
    private const string Voter2 = "https://agents.example/voter-two";

    internal static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    internal static string Show(PostView p) =>
        string.Join("|", p.PostId, p.Digest, p.Author, p.Board, p.Kind, p.Parent, p.Prev, p.Canonical, p.Signature, p.ServerTimestamp.Value.ToString("o", System.Globalization.CultureInfo.InvariantCulture), string.Join(",", p.RiskFlagCategories), p.PossibleDuplicateOf);

    private static string Show(KeyValuePair<string, PostModeration> m) =>
        m.Key + "=" + string.Join(";", m.Value.History.Select(a => string.Join("|", a.PostId, a.Moderator, a.ActorId, a.Effect, a.Category, a.Rationale, string.Join(",", a.Adjudicates))));

    /// <summary>One generated operation: 0 a question, 1 a revision of an earlier post, 2 a vote, 3 a withhold, 4 a restore, 5 an unrelated event, 6 a flag entry.</summary>
    private static async Task<IReadOnlyList<AppendedEvent>> BuildAsync(List<(int Op, int Pick)> script)
    {
        var log = new LateCommitLog();
        var posts = new List<string>();
        for (var i = 0; i < script.Count; i++)
        {
            var (op, pick) = script[i];
            var id = $"P{i:D3}";
            var earlier = posts.Count == 0 ? null : posts[pick % posts.Count];
            switch (op)
            {
                case 0 or 7 when earlier is null || op == 0:
                    await log.AppendPostAsync(id, "question", Author, "{\"kind\":\"question\"}");
                    posts.Add(id);
                    break;
                case 1 when earlier is not null:
                    await log.AppendPostAsync(id, "revision", Author, LateCommitLog.RevisionCanonical(Author, "board", LateCommitLog.Digest(earlier)));
                    posts.Add(id);
                    break;
                case 2 when earlier is not null:
                    var who = pick % 2 == 0 ? Voter : Voter2;
                    await log.AppendPostAsync(id, "vote", who, LateCommitLog.VoteCanonical(who, "board", LateCommitLog.Digest(earlier), "n" + i));
                    posts.Add(id);
                    break;
                case 3 when earlier is not null:
                    await log.AppendModerationAsync(earlier, ModerationEffects.Wire(ModerationEffect.Withhold));
                    break;
                case 4 when earlier is not null:
                    await log.AppendModerationAsync(earlier, ModerationEffects.Wire(ModerationEffect.Restore));
                    break;
                case 6:
                    await log.AppendOtherAsync(FlagProjector.FlagCommittedType, new JsonValue.Object([new("commitment", new JsonValue.String("c")), new("kind", new JsonValue.String("spam"))]));
                    break;
                default:
                    await log.AppendOtherAsync("test.unrelated");
                    break;
            }
        }

        return await log.ReadAsync();
    }

    private static string Answers(LogFacts facts, IReadOnlyList<PostView> basis)
    {
        var answers = new List<string>();
        foreach (var p in basis)
        {
            answers.Add("rev:" + p.PostId + "=" + (SubmissionChecks.RevisionRefusal(facts, p.Digest, "sha256:new") is { } e ? e.Type + e.Detail : "none"));
            var scan = CorrectionIndex.SuccessorsOf(facts.Posts, p, facts.Servable);
            var whole = CorrectionIndex.Fold(facts.Posts, facts.Servable);
            answers.Add("for:" + p.PostId + "=" + string.Join(",", scan.Select(s => s.Digest + "/" + s.PostId)) + (scan.SequenceEqual(whole.For(p)) ? string.Empty : " (differs from For)"));
            answers.Add("fork:" + p.PostId + "=" + string.Join(",", whole.ForkOf(p).Select(s => s.Digest)));
            answers.Add("vote:" + p.PostId + "=" + (SubmissionChecks.AlreadyVoted(facts, Voter, p.Digest) is { } v ? v.Type : "none"));
        }

        return string.Join(" ", answers);
    }

    /// <summary>
    /// LCB27. Extending equals folding the whole, at every split. Over generated logs (questions, revisions and forks, votes, withhold and restore records, flag entries and unrelated events),
    /// for every split of a log into a read and a delta (a delta of one event and a split between a revision and the record that withholds it included): the posts and the moderation of
    /// <c>Of(read).Extend(delta)</c> equal those of <c>Of(read ++ delta)</c> in order and by value, the position is the last sequence, and <c>RevisionRefusal</c>, <c>AlreadyVoted</c> and the
    /// correction rule (the scan against <c>CorrectionIndex.For</c>/<c>ForkOf</c>) answer the same on both. A delta that begins at or below the position is refused.
    /// </summary>
    [Fact]
    public void R6_60_LCB27_ExtendingEqualsFoldingTheWholeAtEverySplit()
    {
        var problems = new List<string>();
        Gen.Select(Gen.Int[0, 6], Gen.Int[0, 50]).List[0, 12].Sample(script =>
        {
            var log = BuildAsync(script).GetAwaiter().GetResult();
            var whole = LogFacts.Of(log);
            var basis = whole.Posts;
            for (var k = 0; k <= log.Count; k++)
            {
                var extended = LogFacts.Of([.. log.Take(k)]).Extend([.. log.Skip(k)]);
                var same = extended.Position == whole.Position
                    && extended.Posts.Select(Show).SequenceEqual(whole.Posts.Select(Show))
                    && extended.Moderation.OrderBy(m => m.Key, StringComparer.Ordinal).Select(Show).SequenceEqual(whole.Moderation.OrderBy(m => m.Key, StringComparer.Ordinal).Select(Show))
                    && Answers(extended, basis) == Answers(whole, basis)
                    && LeanMismatch([.. log.Take(k)], [.. log.Skip(k)], whole, basis, [Voter]) is null;
                if (!same) problems.Add($"split {k} of {log.Count}");
                if (k < log.Count && k > 0)
                {
                    var overlap = Assert.Throws<ArgumentException>(() => LogFacts.Of([.. log.Take(k + 1)]).Extend([.. log.Skip(k)]));
                    Assert.NotNull(overlap);
                }
            }
        }, iter: 60, threads: 1);

        Said("every split equals the whole", problems.Count == 0 ? "every split equals the whole" : $"{problems.Count} splits differ, the first: {problems[0]}");
    }

    /// <summary>
    /// What the vote leg answers for a read and a delta, against the fold of the whole: for each author and each target (every post's digest and one that names no post), the guard's verdict
    /// (<c>Proceed</c> or the refusal's type) must equal <c>AlreadyVoted(Of(read ++ delta))</c>. The first difference is returned, or <see langword="null"/>. An empty delta is not asked (the store
    /// never evaluates one).
    /// </summary>
    private static string? LeanMismatch(IReadOnlyList<AppendedEvent> read, IReadOnlyList<AppendedEvent> delta, LogFacts whole, IReadOnlyList<PostView> basis, string[] authors)
    {
        if (delta.Count == 0) return null;
        foreach (var author in authors)
        {
            foreach (var target in basis.Select(p => p.Digest).Append("sha256:none"))
            {
                var expected = SubmissionChecks.AlreadyVoted(whole, author, target)?.Type ?? "proceed";
                var got = VerdictOf(SubmissionGuard.ForVote(LogFacts.Of(read), "sha256:new", author, target).Evaluate(delta));
                if (!string.Equals(expected, got, StringComparison.Ordinal))
                    return $"{author} for {target}: the whole says {expected}, the guard {got}";
            }
        }

        return null;
    }

    private static string VerdictOf(GuardVerdict verdict) => verdict.Match(() => "proceed", e => e.Type, () => "redecide");

    /// <summary>
    /// LCB31. The lean vote answer equals the fold of the whole, at every split (ruling 31). Over generated logs with votes (two voters and the author, several targets, withhold and restore records,
    /// records naming other posts, a non-vote post by the author), for every split into a read and a delta, the vote guard's verdict for every author and target equals
    /// <c>AlreadyVoted(Of(read ++ delta))</c>. Green on the built tree (the built leg is correct): this is the regression guard for the lean form, and its named controls (the next test) are what FLCB38 to
    /// FLCB40 break.
    /// </summary>
    [Fact]
    public void R8_86_LCB31_TheLeanVoteAnswerEqualsTheFoldOfTheWholeAtEverySplit()
    {
        var problems = new List<string>();
        Gen.Select(Gen.Int[0, 6], Gen.Int[0, 50]).List[0, 14].Sample(script =>
        {
            var log = BuildAsync(script).GetAwaiter().GetResult();
            var whole = LogFacts.Of(log);
            for (var k = 0; k < log.Count; k++)
                if (LeanMismatch([.. log.Take(k)], [.. log.Skip(k)], whole, whole.Posts, [Voter, Voter2, Author]) is { } difference)
                    problems.Add($"split {k} of {log.Count}: {difference}");
        }, iter: 80, threads: 1);

        Said("every split equals the whole", problems.Count == 0 ? "every split equals the whole" : $"{problems.Count} splits differ, the first: {problems[0]}");
    }

    /// <summary>
    /// LCB31's controls, named and not left to the generator: (a) a vote the read held withheld and the delta restores stands again (refuse); (b) a vote served at the read and withheld in the delta does not
    /// stand (proceed); (c) a vote in the delta withheld by a later record of the same delta does not stand (proceed); (d) a vote in the delta whose id the read's records already withheld does not stand (proceed);
    /// (e) a vote in the delta by the author for the target stands (refuse); (f) a record in the delta naming another post changes nothing (a restore of the read's served vote's neighbour: refuse still).
    /// </summary>
    [Fact]
    public async Task R8_86_LCB31_TheLeanVoteAnswerHoldsAtItsNamedControls()
    {
        var target = LateCommitLog.Digest("PQ");
        string Vote(string id) => LateCommitLog.VoteCanonical(Voter, "board", target, "n-" + id);
        var results = new List<string>();

        async Task<string> RunAsync(Func<LateCommitLog, Task> before, Func<LateCommitLog, Task> after)
        {
            var log = new LateCommitLog();
            await log.AppendPostAsync("PQ", "question", Author, "{\"kind\":\"question\"}");
            await before(log);
            var read = await log.ReadAsync();
            await after(log);
            var all = await log.ReadAsync();
            var delta = all.Skip(read.Count).ToArray();
            var guard = SubmissionGuard.ForVote(LogFacts.Of(read), "sha256:new", Voter, target);
            var got = VerdictOf(guard.Evaluate(delta));
            var whole = SubmissionChecks.AlreadyVoted(LogFacts.Of(all), Voter, target)?.Type ?? "proceed";
            return got == whole ? got : $"{got} (the whole says {whole})";
        }

        var refuse = VerificationErrorsType();
        results.Add("a: " + await RunAsync(
            async l => { await l.AppendPostAsync("PV", "vote", Voter, Vote("PV")); await l.AppendModerationAsync("PV", ModerationEffects.Wire(ModerationEffect.Withhold)); },
            l => l.AppendModerationAsync("PV", ModerationEffects.Wire(ModerationEffect.Restore))));
        results.Add("b: " + await RunAsync(
            l => l.AppendPostAsync("PV", "vote", Voter, Vote("PV")),
            l => l.AppendModerationAsync("PV", ModerationEffects.Wire(ModerationEffect.Withhold))));
        results.Add("c: " + await RunAsync(
            _ => Task.CompletedTask,
            async l => { await l.AppendPostAsync("PV", "vote", Voter, Vote("PV")); await l.AppendModerationAsync("PV", ModerationEffects.Wire(ModerationEffect.Withhold)); }));
        results.Add("d: " + await RunAsync(
            l => l.AppendModerationAsync("PV", ModerationEffects.Wire(ModerationEffect.Withhold)),
            l => l.AppendPostAsync("PV", "vote", Voter, Vote("PV"))));
        results.Add("e: " + await RunAsync(
            _ => Task.CompletedTask,
            l => l.AppendPostAsync("PV", "vote", Voter, Vote("PV"))));
        results.Add("f: " + await RunAsync(
            async l => { await l.AppendPostAsync("PV", "vote", Voter, Vote("PV")); await l.AppendPostAsync("PO", "question", Author, "{\"kind\":\"question\"}"); },
            l => l.AppendModerationAsync("PO", ModerationEffects.Wire(ModerationEffect.Restore))));

        Said(
            $"a: {refuse} | b: proceed | c: proceed | d: proceed | e: {refuse} | f: {refuse}",
            string.Join(" | ", results));
    }

    private static string VerificationErrorsType() => Curia.Domain.Verification.VerificationErrors.AlreadyVoted("sha256:x").Type;

    /// <summary>Bytes allocated by the calling thread while <paramref name="guard"/> evaluates <paramref name="delta"/> (the function is synchronous; a first call warms it).</summary>
    private static long AllocatedBy(AppendGuard guard, IReadOnlyList<AppendedEvent> delta)
    {
        _ = guard.Evaluate(delta);
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = guard.Evaluate(delta);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private const long AllocationSlack = 2048;

    /// <summary>
    /// LCB32. A heavy voter costs the vote leg nothing (ruling 31). The vote leg's work under the lock is the delta's, whatever the number of votes the submitting author has cast in the read (0, 5, 500):
    /// the bytes the thread allocates while the function evaluates a delta of two unrelated events do not grow with that number. (The plan asked for a count of <c>TargetOf</c> calls; there is no seam that
    /// counts them without production code that exists for a test, and every call parses a canonical text into a tree, so the allocation is the same count in bytes.) Red on the built tree, which
    /// parses each vote of the author's. <b>What it holds alone:</b> the votes it makes target other digests, so the read's candidates (<c>VotesFor</c>) are none and a function that scanned
    /// <c>facts.Posts</c> for a target with no candidates would stay green here; LCB28's armed read (a throw on access) holds that mutant, and the pair together hold the claim.
    /// </summary>
    [Fact]
    public async Task R8_86_LCB32_AHeavyVoterCostsTheVoteLegNothing()
    {
        var said = new List<string>();
        var bytes = new List<long>();
        foreach (var votes in new[] { 0, 5, 500 })
        {
            var log = new LateCommitLog();
            await log.AppendPostAsync("PQ", "question", Author, "{\"kind\":\"question\"}");
            for (var i = 0; i < votes; i++)
                await log.AppendPostAsync($"V{i:D4}", "vote", Voter, LateCommitLog.VoteCanonical(Voter, "board", LateCommitLog.Digest($"other-{i}"), "n" + i));
            var read = await log.ReadAsync();
            await log.AppendOtherAsync("test.unrelated");
            await log.AppendOtherAsync("test.unrelated");
            var delta = (await log.ReadAsync()).Skip(read.Count).ToArray();
            var guard = SubmissionGuard.ForVote(LogFacts.Of(read), "sha256:new", Voter, LateCommitLog.Digest("PQ"));
            bytes.Add(AllocatedBy(guard, delta));
            said.Add($"{votes} votes: {bytes[^1]} bytes");
        }

        var within = bytes[1] <= bytes[0] + AllocationSlack && bytes[2] <= bytes[0] + AllocationSlack;
        Said("the allocation at 5 and at 500 votes is within 2048 bytes of the allocation at none", within ? "the allocation at 5 and at 500 votes is within 2048 bytes of the allocation at none" : string.Join("; ", said));
    }

    /// <summary>
    /// LCB28. The guard's function touches only the delta, and the fold is made outside the lock. <c>SubmissionGuard.For</c> forces a revision's and a vote's fold when it is built (so the function folds
    /// none) and builds no fold for a digest-only kind. <b>On the seam</b> the function answers <c>Proceed</c> and inspects nothing, so the part about the function is vacuous; the forced fold is not made yet
    /// and the probe is red by it (changed from the plan, which predicted green: the plan's text says <c>For</c> forces the fold).
    /// </summary>
    [Fact]
    public async Task R6_60_LCB28_TheGuardForcesTheFoldWhenItIsBuiltAndTheFunctionFoldsNothing()
    {
        var log = new LateCommitLog();
        await log.AppendPostAsync("P000", "question", Author, "{\"kind\":\"question\"}");
        var read = await log.ReadAsync();
        var small = await LateDeltaAsync(log, read.Count, 2);
        var large = await LateDeltaAsync(log, read.Count + 2, 18);
        var said = new List<string>();
        var expected = new List<string>();

        foreach (var (kind, folds) in new[] { (PostKind.Question, false), (PostKind.Revision, true), (PostKind.Vote, true) })
        {
            // The read is armed after the guard is built: the function may not touch it (a throw on access is the probe's evidence), and the delta's events are counted at 2 and at 20.
            var armed = new ArmedList(read);
            var facts = LogFacts.Of(armed);
            var submission = await SubmissionOf.KindAsync(kind, Author);
            var guard = SubmissionGuard.For(facts, submission);
            expected.Add($"{kind}: fold {(folds ? "made when the guard is built" : "never made")}");
            said.Add($"{kind}: fold {(facts.IsFolded ? "made when the guard is built" : "never made")}");

            armed.Arm();
            var countingSmall = new CountingList(small);
            var countingLarge = new CountingList([.. small, .. large]);
            string touchedTheRead = "never";
            try
            {
                _ = guard.Evaluate(countingSmall);
                _ = guard.Evaluate(countingLarge);
            }
            catch (InvalidOperationException)
            {
                touchedTheRead = "touched";
            }

            expected.Add($"{kind}: the function never touched the read");
            said.Add($"{kind}: the function {(touchedTheRead == "never" ? "never touched" : "touched")} the read");
            // Linear in the delta: 18 more events cost a whole number of passes over them (the two endpoint reads of Extend are a constant), and the passes are few.
            var extra = countingLarge.Reads - countingSmall.Reads;
            var passes = extra % 18 == 0 ? extra / 18 : -1;
            expected.Add($"{kind}: a fixed number of passes (0 to 4) over the delta");
            said.Add($"{kind}: {(passes is >= 0 and <= 4 ? "a fixed number of passes (0 to 4)" : $"{extra} more inspections for 18 more events")} over the delta");
        }

        // Amended by ruling 31 and ruling 49: neither leg reads a post of the read under the lock, and neither copies the read's posts, so what a leg allocates does not grow with the read.
        // (The plan asked for a counter on LogFacts.Extend; there is no seam that counts a call without production code that exists for a test. The allocation is what the call costs, and
        // SubmissionGuardFenceTests holds that SubmissionGuard has no call to it.) The original is a post of the read, so the successor leg has work to do.
        foreach (var kind in new[] { PostKind.Vote, PostKind.Revision })
        {
            var allocated = new List<long>();
            foreach (var posts in new[] { 1, 400 })
            {
                var grown = new LateCommitLog();
                await grown.AppendPostAsync("Q0000", "question", "https://agents.example/alice", "{\"kind\":\"question\"}", "sha256:" + new string('a', 64));
                for (var i = 1; i < posts; i++) await grown.AppendPostAsync($"Q{i:D4}", "question", Author, "{\"kind\":\"question\"}");
                var grownRead = await grown.ReadAsync();
                await grown.AppendOtherAsync("test.unrelated");
                await grown.AppendOtherAsync("test.unrelated");
                var grownDelta = (await grown.ReadAsync()).Skip(grownRead.Count).ToArray();
                var legGuard = SubmissionGuard.For(LogFacts.Of(grownRead), await SubmissionOf.KindAsync(kind, Author));
                allocated.Add(AllocatedBy(legGuard, grownDelta));
            }

            expected.Add($"{kind}: the function's allocation at a read of 400 posts within {AllocationSlack} bytes of its allocation at a read of 1");
            said.Add($"{kind}: the function's allocation at a read of 400 posts {(allocated[1] <= allocated[0] + AllocationSlack ? $"within {AllocationSlack} bytes of" : $"{allocated[1]} bytes against {allocated[0]} bytes, beyond")} its allocation at a read of 1");
        }

        Said(string.Join(" | ", expected), string.Join(" | ", said));
    }

    /// <summary>
    /// <paramref name="count"/> unrelated events appended to the same log after the read: a delta that lies above the facts' position, as a late commit does (changed from a delta built on a fresh
    /// log, whose sequences began at 1 and so lay at or below the position, which Extend refuses).
    /// </summary>
    private static async Task<IReadOnlyList<AppendedEvent>> LateDeltaAsync(LateCommitLog log, int skip, int count)
    {
        for (var i = 0; i < count; i++) await log.AppendOtherAsync("test.unrelated");
        return [.. (await log.ReadAsync()).Skip(skip)];
    }

    /// <summary>A list that throws on any access once armed: the evidence that the guard's function never reads the route's read.</summary>
    private sealed class ArmedList(IReadOnlyList<AppendedEvent> inner) : IReadOnlyList<AppendedEvent>
    {
        private volatile bool _armed;

        internal void Arm() => _armed = true;

        private void Check()
        {
            if (_armed) throw new InvalidOperationException("the guard's function touched the read");
        }

        public AppendedEvent this[int index]
        {
            get
            {
                Check();
                return inner[index];
            }
        }

        public int Count
        {
            get
            {
                Check();
                return inner.Count;
            }
        }

        public IEnumerator<AppendedEvent> GetEnumerator()
        {
            Check();
            return inner.GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CountingList(IReadOnlyList<AppendedEvent> inner) : IReadOnlyList<AppendedEvent>
    {
        private int _reads;

        internal int Reads => Volatile.Read(ref _reads);

        public AppendedEvent this[int index]
        {
            get
            {
                Interlocked.Increment(ref _reads);
                return inner[index];
            }
        }

        public int Count => inner.Count;

        public IEnumerator<AppendedEvent> GetEnumerator()
        {
            foreach (var e in inner)
            {
                Interlocked.Increment(ref _reads);
                yield return e;
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// LCB30. The digest-only reading names the same post as the whole fold. Over generated logs holding posts with unparseable canonical text, payloads missing each required member in turn, non-post events
    /// carrying a <c>digest</c> member, duplicate digests and a digest that is absent, <c>FirstWithDigest</c> returns the same post id and sequence as the first post of <c>Fold</c> with that digest.
    /// Green on the seam: <c>FirstWithDigest</c> is rewritten in the seam and the whole fold is the control (red under FLCB35). The cost rule P2 is a measurement and is not asserted here.
    /// </summary>
    [Fact]
    public void R8_86_LCB30_TheDigestOnlyReadingNamesTheSamePostAsTheWholeFold()
    {
        var members = new[] { "post_id", "canonical", "signature", "digest", "author", "board", "kind" };
        var problems = new List<string>();
        Gen.Select(Gen.Int[0, 12], Gen.Int[0, 3]).List[1, 14].Sample(script =>
        {
            var log = new LateCommitLog();
            var digests = new List<string>();
            for (var i = 0; i < script.Count; i++)
            {
                var (shape, digestPick) = script[i];
                var digest = digestPick == 0 || digests.Count == 0 ? $"sha256:d{i}" : digests[digestPick % digests.Count];
                digests.Add(digest);
                var payload = new List<KeyValuePair<string, JsonValue>>();
                foreach (var m in members)
                {
                    if (shape >= 1 && shape <= members.Length && members[shape - 1] == m) continue;
                    payload.Add(new(m, new JsonValue.String(m switch { "digest" => digest, "canonical" => shape == 9 ? "not json {{{" : "{\"kind\":\"question\"}", "kind" => "question", _ => $"{m}-{i}" })));
                }

                var type = shape == 10 ? "test.other" : PostProjector.PostAcceptedType;
                log.AppendRawAsync($"agg{i}", type, new JsonValue.Object([.. payload])).GetAwaiter().GetResult();
            }

            var events = log.ReadAsync().GetAwaiter().GetResult();
            var folded = PostProjector.Fold(events);
            foreach (var digest in digests.Append("sha256:absent").Distinct())
            {
                var expectedPost = folded.FirstOrDefault(p => p.Digest == digest);
                var found = PostProjector.FirstWithDigest(events, digest);
                var expected = expectedPost is null ? "none" : expectedPost.PostId;
                var actual = found is null ? "none" : found.PostId;
                if (expected != actual) problems.Add($"{digest}: whole fold {expected}, digest-only {actual}");
            }
        }, iter: 80, threads: 1);

        Said("the same post for every digest", problems.Count == 0 ? "the same post for every digest" : $"{problems.Count} differences, the first: {problems[0]}");
    }
}
