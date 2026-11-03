#pragma warning disable CA2007 // test code: there is no SynchronizationContext to resume on (as the other suites of this project do, per await, with ConfigureAwait(true))
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using CsCheck;
using Xunit;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// Errata G57, ruling 49 to 53: the guard's two legs build a view only of what they could use. LCB34 (the guard's answer is the cold route's answer), LCB35 and LCB36 (the work under
/// the lock does not grow with other agents' posts or with the read; no canonical text is parsed for an event that is not the submitter's), LCB37 (the successor leg's candidates) and
/// LCB38 (the filtered extension is the unfiltered one, restricted). The submitter is the agent <see cref="SubmissionOf"/> signs as; the original a revision names is a post of that agent whose
/// digest is the one <see cref="SubmissionOf"/> puts in <c>prev</c>. <b>The instrument of LCB35 and LCB36 is the bytes the thread allocates</b> (<c>GC.GetAllocatedBytesForCurrentThread</c>, the least of
/// 20 calls after a warm-up): the plan asked for a parse counter, there is no seam that counts parses without production code that exists for a test (LCB32 says the same), and every parse of a canonical
/// text allocates at least its bytes, so the canonical texts here are large enough that one parse cannot hide in the bound. No timing is asserted.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class GuardFilterTests
{
    private const string Sub = "https://agents.example/alice";
    private const string Other = "https://agents.example/other";
    private const string Third = "https://agents.example/third";
    private static readonly string OriginalDigest = "sha256:" + new string('a', 64);

    private static void Said(string expected, string actual) => LogFactsTests.Said(expected, actual);

    private static string Word(GuardVerdict verdict) => verdict.Match(() => "proceed", e => e.Type + " " + e.Detail, () => "redecide");

    private static string Word(Error? error) => error is null ? "proceed" : error.Type + " " + error.Detail;

    private static string AlreadyRevised(string postId) => "curia/ingest/already-revised successor=" + postId;

    private static int _unique;

    private static string Fresh(string stem) => stem + Interlocked.Increment(ref _unique).ToString("D7", System.Globalization.CultureInfo.InvariantCulture);

    private static string Pad(int bytes) => new('x', bytes);

    private static string Canonical(string kind, string author, string? prev, int padBytes) =>
        $"{{\"author\":\"{author}\",\"board\":\"board\",\"body\":\"{Pad(padBytes)}\",\"kind\":\"{kind}\"" + (prev is null ? string.Empty : $",\"prev\":\"{prev}\"") + "}";

    // ---- LCB34

    /// <summary>One generated operation of LCB34's logs; see the cases.</summary>
    private static async Task<IReadOnlyList<AppendedEvent>> BuildMixedAsync(List<(int Op, int Pick)> script, string submissionDigest)
    {
        var log = new LateCommitLog();
        var digestOf = new Dictionary<string, string> { ["P000"] = OriginalDigest };
        var posts = new List<string> { "P000" };
        var originalRevisions = new List<string>();
        var withheld = new List<string>();
        await log.AppendPostAsync("P000", "question", Sub, "{\"kind\":\"question\"}", OriginalDigest);
        var members = new[] { "post_id", "canonical", "signature", "digest", "author", "board", "kind" };

        for (var i = 0; i < script.Count; i++)
        {
            var (op, pick) = script[i];
            var id = $"P{i + 1:D3}";
            var earlier = posts[pick % posts.Count];
            string Prev() => digestOf[earlier];
            async Task Add(string kind, string author, string canonical, string? digest = null)
            {
                await log.AppendPostAsync(id, kind, author, canonical, digest);
                digestOf[id] = digest ?? LateCommitLog.Digest(id);
                posts.Add(id);
            }

            switch (op)
            {
                case 0: await Add("question", Sub, "{\"kind\":\"question\"}"); break;
                case 1: await Add("revision", Sub, LateCommitLog.RevisionCanonical(Sub, "board", Prev())); break;
                case 2: await Add("revision", Other, LateCommitLog.RevisionCanonical(Other, "board", Prev())); break;
                case 3: await Add("vote", Sub, LateCommitLog.VoteCanonical(Sub, "board", Prev(), "n" + i)); break;
                case 4: await Add("vote", Other, LateCommitLog.VoteCanonical(Other, "board", Prev(), "n" + i)); break;
                case 5: await log.AppendModerationAsync(earlier, ModerationEffects.Wire(ModerationEffect.Withhold)); withheld.Add(earlier); break;
                case 6: await log.AppendModerationAsync(earlier, ModerationEffects.Wire(ModerationEffect.Restore)); break;
                case 7: await Add("comment", Sub, LateCommitLog.RevisionCanonical(Sub, "board", OriginalDigest)); break;
                case 8:
                    {
                        // a post event that misses one required member in turn (a revision of the original by the submitter, otherwise)
                        var skip = members[pick % members.Length];
                        var payload = new List<KeyValuePair<string, JsonValue>>();
                        foreach (var m in members)
                        {
                            if (m == skip) continue;
                            payload.Add(new(m, new JsonValue.String(m switch
                            {
                                "post_id" => id,
                                "canonical" => LateCommitLog.RevisionCanonical(Sub, "board", OriginalDigest),
                                "digest" => LateCommitLog.Digest(id),
                                "author" => Sub,
                                "kind" => "revision",
                                _ => m + "-" + i,
                            })));
                        }

                        await log.AppendRawAsync("agg" + i, PostProjector.PostAcceptedType, new JsonValue.Object([.. payload]));
                        break;
                    }

                case 9: await log.AppendOtherAsync("test.unrelated"); break;
                case 10: await Add("revision", Sub, "not json {{{"); break;
                case 11: await log.AppendModerationAsync("ZZZ-" + i, ModerationEffects.Wire(ModerationEffect.Withhold)); break;
                case 12: await Add("revision", Sub, LateCommitLog.RevisionCanonical(Sub, "board", OriginalDigest)); originalRevisions.Add(id); break;
                case 13: await Add("revision", Other, LateCommitLog.RevisionCanonical(Other, "board", OriginalDigest)); break;
                case 14: await Add("question", Other, "{\"kind\":\"question\"}", submissionDigest); break;

                // the composite operations: the states the route's checks and the guard disagree over if "served" is read from the wrong moderation
                case 15:
                    // a revision of the original by the submitter, withheld at once
                    await Add("revision", Sub, LateCommitLog.RevisionCanonical(Sub, "board", OriginalDigest));
                    originalRevisions.Add(id);
                    await log.AppendModerationAsync(id, ModerationEffects.Wire(ModerationEffect.Withhold));
                    withheld.Add(id);
                    break;
                case 16:
                    // the restore of the post withheld last (of any post, when none was withheld)
                    var restored = withheld.Count > 0 ? withheld[^1] : earlier;
                    withheld.Remove(restored);
                    await log.AppendModerationAsync(restored, ModerationEffects.Wire(ModerationEffect.Restore));
                    break;
                default:
                    // the withhold of the first revision of the original by the submitter (of any post, when there is none): a fork loses its first branch
                    var first = originalRevisions.Count > 0 ? originalRevisions[0] : earlier;
                    await log.AppendModerationAsync(first, ModerationEffects.Wire(ModerationEffect.Withhold));
                    withheld.Add(first);
                    break;
            }
        }

        return await log.ReadAsync();
    }

    /// <summary>
    /// LCB34. The guard's answer is the cold route's answer. Over generated logs (the submitter's and others' revisions, votes, comments and questions; withhold and restore records for posts of the read and
    /// of the delta and for posts that do not exist; a revision by someone else naming the same <c>prev</c>; post events that miss each required member in turn; a canonical text that does not parse; a
    /// post bearing the submission's own digest; and three composite operations, a revision withheld at once, the restore of the post withheld last and the withhold of the first revision of the original, so that the read's candidates are withheld, restored and forked across the seam), split at every position into a read and a delta, both legs' verdict (and the refusal's error) equals
    /// <c>RevisionRefusal(LogFacts.Of(read ++ delta), prev, digest)</c> and <c>AlreadyVoted(LogFacts.Of(read ++ delta), author, target)</c>, the route's own checks on the whole list: a different path from
    /// the filtered view and the candidates. <b>Green on the built tree</b> (the built legs are correct): it is the regression guard the filter must not break, and says so. A split whose read already holds
    /// the submission's digest is skipped for the successor leg (the route refuses that before a guard exists).
    /// </summary>
    [Fact]
    public async Task R8_86_LCB34_TheGuardsAnswerIsTheColdRoutesAnswer()
    {
        var submission = await SubmissionOf.KindAsync(PostKind.Revision, Sub);
        var digest = Digests.Sha256(submission.Canonical).ToPrefixed();
        var scripts = new List<List<(int Op, int Pick)>>();
        Gen.Select(Gen.Int[0, 17], Gen.Int[0, 50]).List[0, 13].Sample(scripts.Add, iter: 150, seed: "0pj9aDvFX1M7", threads: 1);

        var problems = new List<string>();
        var splits = 0;
        int restoreInDelta = 0, withholdInDelta = 0, forkAcrossSeam = 0;
        foreach (var script in scripts)
        {
            var log = await BuildMixedAsync(script, digest);
            var whole = LogFacts.Of(log);
            var targets = whole.Posts.Select(p => p.Digest).Distinct().Append("sha256:" + new string('c', 64)).ToArray();
            var expectedRevision = Word(SubmissionChecks.RevisionRefusal(whole, OriginalDigest, digest));
            for (var k = 1; k < log.Count; k++)
            {
                splits++;
                var read = log.Take(k).ToArray();
                var delta = log.Skip(k).ToArray();
                var readFacts = LogFacts.Of(read);
                if (!readFacts.Posts.Any(p => p.Digest == digest))
                {
                    var actual = Word(SubmissionGuard.For(readFacts, submission).Evaluate(delta));
                    if (actual != expectedRevision) problems.Add($"successor leg, split {k} of {log.Count}: the whole says {expectedRevision}, the guard {actual}");

                    // the hard classes, by what the read and the whole say of the read's candidates (the submitter's revisions of the original)
                    const string marker = "successor=";
                    var named = expectedRevision.Contains(marker, StringComparison.Ordinal) ? expectedRevision[(expectedRevision.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..] : null;
                    var candidates = readFacts.Posts.Where(p => p.Prev == OriginalDigest && p.Author == Sub && p.Kind == "revision").Select(p => p.PostId).ToArray();
                    if (named is not null && candidates.Contains(named) && !readFacts.Servable(named)) restoreInDelta++;
                    if (named is not null && candidates.Any(c => c != named && readFacts.Servable(c) && !whole.Servable(c))) withholdInDelta++;
                    if (named is not null && candidates.Contains(named)
                        && whole.Posts.Any(p => p.Prev == OriginalDigest && p.Author == Sub && p.Kind == "revision" && !candidates.Contains(p.PostId) && whole.Servable(p.PostId)))
                        forkAcrossSeam++;
                }

                foreach (var author in new[] { Sub, Other })
                    foreach (var target in targets)
                    {
                        var expected = Word(SubmissionChecks.AlreadyVoted(whole, author, target));
                        var actual = Word(SubmissionGuard.ForVote(readFacts, "sha256:new", author, target).Evaluate(delta));
                        if (actual != expected) problems.Add($"vote leg, split {k} of {log.Count}, {author} for {target[..14]}: the whole says {expected}, the guard {actual}");
                    }
            }
        }

        var classes = $"; already-revised through a restore in the delta {(restoreInDelta > 0 ? "reached" : "NOT REACHED")}, through a withhold in the delta that moves the name {(withholdInDelta > 0 ? "reached" : "NOT REACHED")}, through a fork across the read and the delta {(forkAcrossSeam > 0 ? "reached" : "NOT REACHED")}";
        var said = $"both legs answer as the whole log does at {splits} splits" + classes;
        Said(said.Replace("NOT REACHED", "reached", StringComparison.Ordinal), problems.Count == 0 ? said : $"{problems.Count} differences, the first: {problems[0]}");
    }

    // ---- LCB35 and LCB36

    private const long Limit = 8 * 1024;
    private const long ParseBound = 4096;
    private static readonly SemaphoreSlim BigGate = new(1, 1);
    private static (LateCommitLog Log, IReadOnlyList<AppendedEvent> Read)? _big;

    /// <summary>A read of 20,000 posts (small canonical texts: the read is not what is measured, its size is), shared by LCB35 and LCB36; each test appends its own delta above it.</summary>
    private static async Task<(LateCommitLog Log, IReadOnlyList<AppendedEvent> Read)> BigReadAsync()
    {
        if (_big is { } existing) return existing;
        var log = new LateCommitLog();
        await log.AppendPostAsync("P000", "question", Sub, "{\"kind\":\"question\"}", OriginalDigest);
        for (var i = 1; i < 20_000; i++)
            await log.AppendPostAsync("R" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture), "question", Other, "{\"kind\":\"question\"}");
        _big = (log, await log.ReadAsync());
        return _big.Value;
    }

    /// <summary>
    /// <paramref name="total"/> events appended to <paramref name="log"/>: 72 percent <c>post.accepted</c> by agents other than the submitter (questions, revisions of originals that do not exist, votes; each canonical text
    /// <paramref name="canonicalBytes"/> long), the rest unrelated events; and the submitter's <paramref name="revisions"/> revisions of other originals, <paramref name="votes"/> votes for other targets and
    /// <paramref name="comments"/> comments, placed at even spacing among them. None of the submitter's acts refuses either leg.
    /// </summary>
    private static async Task<IReadOnlyList<AppendedEvent>> AppendDeltaAsync(LateCommitLog log, int total, int canonicalBytes, int revisions, int votes, int comments)
    {
        var skip = (await log.ReadAsync()).Count;
        var mine = new Queue<string>(Enumerable.Repeat("revision", revisions).Concat(Enumerable.Repeat("vote", votes)).Concat(Enumerable.Repeat("comment", comments)));
        var every = mine.Count == 0 ? int.MaxValue : total / mine.Count;
        for (var i = 0; i < total; i++)
        {
            var id = Fresh("D");
            if (mine.Count > 0 && i % every == every - 1)
            {
                var kind = mine.Dequeue();
                await log.AppendPostAsync(id, kind, Sub, kind switch
                {
                    "vote" => LateCommitLog.VoteCanonical(Sub, "board", LateCommitLog.Digest("other-target-" + id), Pad(canonicalBytes)),
                    "revision" => Canonical("revision", Sub, LateCommitLog.Digest("other-original-" + id), canonicalBytes),
                    _ => Canonical("comment", Sub, null, canonicalBytes),
                });
            }
            else if (i % 100 < 72)
            {
                var author = i % 2 == 0 ? Other : Third;
                switch (i % 3)
                {
                    case 0: await log.AppendPostAsync(id, "question", author, Canonical("question", author, null, canonicalBytes)); break;
                    case 1: await log.AppendPostAsync(id, "revision", author, Canonical("revision", author, LateCommitLog.Digest("no-original-" + id), canonicalBytes)); break;
                    default: await log.AppendPostAsync(id, "vote", author, LateCommitLog.VoteCanonical(author, "board", LateCommitLog.Digest("vote-target-" + id), Pad(canonicalBytes))); break;
                }
            }
            else
            {
                await log.AppendOtherAsync("test.unrelated");
            }
        }

        return [.. (await log.ReadAsync()).Skip(skip)];
    }

    private static long Least(AppendGuard guard, IReadOnlyList<AppendedEvent> delta)
    {
        for (var i = 0; i < 3; i++) _ = guard.Evaluate(delta);
        var least = long.MaxValue;
        for (var i = 0; i < 20; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var verdict = guard.Evaluate(delta);
            least = Math.Min(least, GC.GetAllocatedBytesForCurrentThread() - before);
            if (Word(verdict) != "proceed") throw new Xunit.Sdk.XunitException("a probe delta refused: " + Word(verdict));
        }

        return least;
    }

    /// <summary>
    /// LCB35. The work under the lock does not grow with other agents' posts or with the read. A read of 20,000 posts and a delta of 2,000 events (72 percent <c>post.accepted</c> by other authors, 1.35 KiB of
    /// canonical text each): the function of each leg, after a warm-up, allocates at most 8 KiB (the least of 20 calls: about ten times the 600 to 680 bytes measured, and far below the copy of the read's 20,000 posts, two arrays of 160 KB), so the base
    /// copy is caught as well as the views, and a per-event allocation of about 4 bytes or more fails. A positive control in the same test: the same delta with 100 revisions and 100 votes of the submitter's allocates at least ten times as much, so a counter that reads zero
    /// fails. <b>Red on the built tree</b> (megabytes).
    /// </summary>
    [Fact]
    public async Task R8_86_LCB35_TheWorkUnderTheLockDoesNotGrowWithOtherAgentsPostsOrTheRead()
    {
        await BigGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var (log, read) = await BigReadAsync();
            var facts = LogFacts.Of(read).Force();
            var successor = SubmissionGuard.For(facts, await SubmissionOf.KindAsync(PostKind.Revision, Sub));
            var vote = SubmissionGuard.ForVote(facts, "sha256:new", Sub, OriginalDigest);
            var others = await AppendDeltaAsync(log, 2000, 1382, 0, 0, 0);
            var control = await AppendDeltaAsync(log, 2000, 1382, 100, 100, 0);

            var line = new List<string>();
            var expected = new List<string>();
            foreach (var (name, guard) in new[] { ("successor", successor), ("vote", vote) })
            {
                var baseBytes = Least(guard, others);
                var controlBytes = Least(guard, control);
                expected.Add($"{name} leg, 2000 events by others on a read of 20000: within 8 KiB");
                line.Add($"{name} leg, 2000 events by others on a read of 20000: {(baseBytes <= Limit ? "within 8 KiB" : baseBytes + " bytes")}");
                expected.Add($"{name} leg, 100 revisions and 100 votes of the submitter's: at least ten times the base");
                line.Add($"{name} leg, 100 revisions and 100 votes of the submitter's: {(controlBytes >= 10 * Math.Max(baseBytes, 1024) ? "at least ten times the base" : $"{controlBytes} bytes against a base of {baseBytes}")}");
            }

            Said(string.Join(" | ", expected), string.Join(" | ", line));
        }
        finally
        {
            BigGate.Release();
        }
    }

    /// <summary>
    /// LCB36. No canonical text is parsed for an event that is not the submitter's. Each canonical text here is 8 KiB, so one parse allocates more than <see cref="ParseBound"/> bytes (its bytes alone) and cannot hide: a
    /// delta of 2,000 events by other authors allocates at most that bound on either leg, and a delta that mixes the submitter's five kept acts (revisions for the successor leg, votes for the vote leg) among
    /// the others' and among the submitter's five acts of the other kinds allocates what the delta restricted to the kept acts allocates, within the bound (so exactly the kept count of parses is paid for). The
    /// restricted delta is also at least five canonical texts, so a counter that reads zero fails. <b>Red on the built tree</b>.
    /// </summary>
    [Fact]
    public async Task R8_86_LCB36_NoCanonicalTextIsParsedForAnEventThatIsNotTheSubmitters()
    {
        await BigGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var (log, read) = await BigReadAsync();
            var facts = LogFacts.Of(read).Force();
            var successor = SubmissionGuard.For(facts, await SubmissionOf.KindAsync(PostKind.Revision, Sub));
            var vote = SubmissionGuard.ForVote(facts, "sha256:new", Sub, OriginalDigest);
            var others = await AppendDeltaAsync(log, 2000, 8192, 0, 0, 0);
            var mixed = await AppendDeltaAsync(log, 2000, 8192, 5, 5, 5);

            var line = new List<string>();
            var expected = new List<string>();
            foreach (var (name, guard, keptKind) in new[] { ("successor", successor, "revision"), ("vote", vote, "vote") })
            {
                var baseBytes = Least(guard, others);
                expected.Add($"{name} leg, 2000 events by others: at most {ParseBound} bytes");
                line.Add($"{name} leg, 2000 events by others: {(baseBytes <= ParseBound ? $"at most {ParseBound} bytes" : baseBytes + " bytes")}");

                var restricted = mixed.Where(a => a.Event.Payload is JsonValue.Object o && Member(o, "author") == Sub && Member(o, "kind") == keptKind).ToArray();
                var mixedBytes = Least(guard, mixed);
                var restrictedBytes = Least(guard, restricted);
                expected.Add($"{name} leg, the mixed delta costs what its 5 kept acts cost, which is at least 5 texts");
                line.Add($"{name} leg, the mixed delta costs " + (Math.Abs(mixedBytes - restrictedBytes) <= ParseBound
                    ? (restricted.Length == 5 && restrictedBytes >= 5 * 8192 ? "what its 5 kept acts cost, which is at least 5 texts" : $"what its {restricted.Length} kept acts cost, {restrictedBytes} bytes")
                    : $"{mixedBytes} bytes where its {restricted.Length} kept acts cost {restrictedBytes}"));
            }

            Said(string.Join(" | ", expected), string.Join(" | ", line));
        }
        finally
        {
            BigGate.Release();
        }
    }

    private static string? Member(JsonValue.Object o, string key)
    {
        string? found = null;
        foreach (var m in o.Members)
            if (m.Key == key) found = (m.Value as JsonValue.String)?.Value;
        return found;
    }

    // ---- LCB37

    private static async Task<string> SuccessorAsync(Func<LateCommitLog, Task> read, Func<LateCommitLog, Task> delta)
    {
        var log = new LateCommitLog();
        await read(log);
        var readEvents = await log.ReadAsync();
        await delta(log);
        var deltaEvents = (await log.ReadAsync()).Skip(readEvents.Count).ToArray();
        var guard = SubmissionGuard.For(LogFacts.Of(readEvents), await SubmissionOf.KindAsync(PostKind.Revision, Sub));
        return Word(guard.Evaluate(deltaEvents));
    }

    private static Task Original(LateCommitLog log) => log.AppendPostAsync("P000", "question", Sub, "{\"kind\":\"question\"}", OriginalDigest);

    private static Task Revision(LateCommitLog log, string id, string author) =>
        log.AppendPostAsync(id, "revision", author, LateCommitLog.RevisionCanonical(author, "board", OriginalDigest));

    /// <summary>
    /// LCB37. The successor leg's candidates, named and not left to the generator. (i) a candidate the read held withheld and the delta restores: refuse naming it; (ii) a candidate the delta withholds: proceed;
    /// (iii) a revision of the original by its author in the delta: refuse naming it; (iv) a revision of it by another author in the delta, and a comment naming the digest: proceed; (v) the reachable fork: a candidate withheld in the read, a delta revision by the same author and the candidate's restore in the delta: the candidate (the first in log order) is named; (vi) a moderation record in the delta naming another post, with the read's only candidate withheld: proceed; (vii) the original absent from the facts: no throw, no refusal; (viii) a
    /// revision in the delta that a later record of the delta withholds: proceed. <b>Green on the built tree</b> where the built leg answers so (the candidates form must keep every one of these).
    /// </summary>
    [Fact]
    public async Task R8_86_LCB37_TheSuccessorLegsCandidatesAnswerAsTheTableSaysTheyDo()
    {
        var withhold = ModerationEffects.Wire(ModerationEffect.Withhold);
        var restore = ModerationEffects.Wire(ModerationEffect.Restore);
        var said = new List<string>
        {
            "(i) " + await SuccessorAsync(async l => { await Original(l); await Revision(l, "P001", Sub); await l.AppendModerationAsync("P001", withhold); }, l => l.AppendModerationAsync("P001", restore)),
            "(ii) " + await SuccessorAsync(async l => { await Original(l); await Revision(l, "P001", Sub); }, l => l.AppendModerationAsync("P001", withhold)),
            "(iii) " + await SuccessorAsync(Original, l => Revision(l, "P002", Sub)),
            "(iv) " + await SuccessorAsync(Original, async l => { await Revision(l, "P002", Other); await l.AppendPostAsync("P003", "comment", Sub, LateCommitLog.RevisionCanonical(Sub, "board", OriginalDigest)); }),
            "(v) " + await SuccessorAsync(async l => { await Original(l); await Revision(l, "P001", Sub); await l.AppendModerationAsync("P001", withhold); }, async l => { await Revision(l, "P003", Sub); await l.AppendModerationAsync("P001", restore); }),
            "(vi) " + await SuccessorAsync(async l => { await Original(l); await Revision(l, "P001", Sub); await l.AppendModerationAsync("P001", withhold); await l.AppendPostAsync("P004", "question", Other, "{\"kind\":\"question\"}"); }, l => l.AppendModerationAsync("P004", withhold)),
            "(vii) " + await SuccessorAsync(l => l.AppendPostAsync("P005", "question", Other, "{\"kind\":\"question\"}"), l => Revision(l, "P002", Sub)),
            "(viii) " + await SuccessorAsync(Original, async l => { await Revision(l, "P002", Sub); await l.AppendModerationAsync("P002", withhold); }),
        };

        Said(
            string.Join(" | ",
            [
                "(i) " + AlreadyRevised("P001"),
                "(ii) proceed",
                "(iii) " + AlreadyRevised("P002"),
                "(iv) proceed",
                "(v) " + AlreadyRevised("P001"),
                "(vi) proceed",
                "(vii) proceed",
                "(viii) proceed",
            ]),
            string.Join(" | ", said));
    }

    // ---- LCB38

    private static readonly Func<string, string, bool>[] Predicates =
    [
        static (_, _) => true,
        static (_, _) => false,
        static (author, _) => author == Sub,
        static (_, kind) => kind == "vote",
        static (author, kind) => author == Sub && kind == "revision",
        static (author, kind) => author != Sub && kind != "question",
    ];

    /// <summary>
    /// LCB38. The filtered extension is the unfiltered one, restricted. Over generated deltas (post events by three authors of four kinds, each required member missing in turn, a canonical text that does not
    /// parse, an author that is not a string, a kind that is not a string, an event of another type, a payload that is not an object), for each of six predicates, the views of
    /// <c>PostProjector.Extend(Empty, delta, keep)</c> equal the views of the unfiltered <c>Extend</c> kept by the same predicate afterward (same views, same order); and a delta with two adjacent events swapped is
    /// refused (<see cref="ArgumentException"/>) whatever the predicate drops, the predicate that drops everything included. It holds the equivalence and the order check that the filtered form (one loop that reads
    /// an event's author and kind before it builds anything) must keep, and is red under FLCB53.
    /// </summary>
    [Fact]
    public void R8_86_LCB38_TheFilteredExtensionIsTheUnfilteredOneRestricted()
    {
        var members = new[] { "post_id", "canonical", "signature", "digest", "author", "board", "kind" };
        var authors = new[] { Sub, Other, Third };
        var kinds = new[] { "question", "revision", "vote", "comment" };
        var problems = new List<string>();
        var swaps = 0;

        Gen.Select(Gen.Int[0, 13], Gen.Int[0, 2], Gen.Int[0, 3]).List[1, 14].Sample(script =>
        {
            var log = new LateCommitLog();
            for (var i = 0; i < script.Count; i++)
            {
                var (shape, a, k) = script[i];
                var payload = new List<KeyValuePair<string, JsonValue>>();
                foreach (var m in members)
                {
                    if (shape >= 1 && shape <= members.Length && members[shape - 1] == m) continue;
                    payload.Add(new(m, m switch
                    {
                        "author" when shape == 10 => new JsonValue.Number(1),
                        "kind" when shape == 8 => new JsonValue.Number(1),
                        "author" => new JsonValue.String(authors[a]),
                        "kind" => new JsonValue.String(kinds[k]),
                        "canonical" => new JsonValue.String(shape == 9 ? "not json {{{" : LateCommitLog.RevisionCanonical(authors[a], "board", LateCommitLog.Digest("P" + (i / 2)))),
                        _ => new JsonValue.String($"{m}-{i}"),
                    }));
                }

                JsonValue body = shape == 12 ? new JsonValue.String("not an object") : new JsonValue.Object([.. payload]);
                log.AppendRawAsync("agg" + i, shape == 11 ? "test.other" : PostProjector.PostAcceptedType, body).GetAwaiter().GetResult();
            }

            var events = log.ReadAsync().GetAwaiter().GetResult();
            var all = PostProjector.Extend(ImmutableArray<PostView>.Empty, events);
            for (var p = 0; p < Predicates.Length; p++)
            {
                var keep = Predicates[p];
                var filtered = PostProjector.Extend(ImmutableArray<PostView>.Empty, events, keep).Select(LogFactsTests.Show).ToArray();
                var expected = all.Where(v => keep(v.Author, v.Kind)).Select(LogFactsTests.Show).ToArray();
                if (!filtered.SequenceEqual(expected)) problems.Add($"predicate {p}: {filtered.Length} views where the unfiltered extension kept {expected.Length}");

                for (var j = 0; j + 1 < events.Count; j++)
                {
                    var swapped = events.ToArray();
                    (swapped[j], swapped[j + 1]) = (swapped[j + 1], swapped[j]);
                    swaps++;
                    try
                    {
                        _ = PostProjector.Extend(ImmutableArray<PostView>.Empty, swapped, keep);
                        problems.Add($"predicate {p}: events {j} and {j + 1} swapped and the list was accepted");
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
        }, iter: 60, seed: "0pj9aDvFX1M7", threads: 1);

        Said($"the filtered extension equals the unfiltered one restricted, and refuses {swaps} swapped lists", problems.Count == 0 ? $"the filtered extension equals the unfiltered one restricted, and refuses {swaps} swapped lists" : $"{problems.Count} differences, the first: {problems[0]}");
    }
}
