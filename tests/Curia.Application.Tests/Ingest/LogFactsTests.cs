#pragma warning disable CA2007 // test code: there is no SynchronizationContext to resume on (as the other suites of this project do, per await, with ConfigureAwait(true))
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
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

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Show(PostView p) =>
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
                    await log.AppendPostAsync(id, "vote", Voter, LateCommitLog.VoteCanonical(Voter, "board", LateCommitLog.Digest(earlier), "n" + i));
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
                    && Answers(extended, basis) == Answers(whole, basis);
                if (!same) problems.Add($"split {k} of {log.Count}");
                if (k < log.Count && k > 0)
                {
                    var overlap = Assert.Throws<ArgumentException>(() => LogFacts.Of([.. log.Take(k + 1)]).Extend([.. log.Skip(k)]));
                    Assert.NotNull(overlap);
                }
            }
        }, iter: 60);

        Said("every split equals the whole", problems.Count == 0 ? "every split equals the whole" : $"{problems.Count} splits differ, the first: {problems[0]}");
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
        }, iter: 80);

        Said("the same post for every digest", problems.Count == 0 ? "the same post for every digest" : $"{problems.Count} differences, the first: {problems[0]}");
    }
}
