using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// PR 3 (errata G47, R11.62), probes CHN21 to CHN32 and CHN57: what <c>curia_answer</c> says about a question that was revised, and what
/// <c>curia_read</c>'s first block says about the root of the thread it presents.
///
/// <para><b>Oracles.</b> The chain each fact builds: it signs a question and its revisions, serves each with the <c>superseded_by</c> the Forum would serve,
/// and computes the expected statement from the chain it built (the head of a chain is the revision with no successor; the revision that supersedes
/// another is the next one built), never from the tool's own functions. The phrases are R11.62's code spans, read from the errata by
/// <see cref="PublishedChainWords"/>; the number of reads is counted from the stub's list of requests.</para>
///
/// <para>Every fact here is red by assertion on the unmodified tree except CHN25, CHN26 and CHN29, which are controls.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "A fact that needs one scene per case builds each in a using declaration; the analyzer does not follow a test class constructed by the test.")]
public sealed class QuestionStandingTests : IDisposable
{
    private const string RefusalType = "curia/ingest/parent-not-a-question";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-g47-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>One signed, servable post of a chain: its id, its signed form and the draft it came from.</summary>
    private sealed record Link(string Id, PostDraft Draft, SignedSubmission Signed)
    {
        public string Digest => Signed.PrefixedDigest;

        public Successor AsSuccessor(bool served = true) => new(Digest, served ? Id : null);
    }

    private static string IdOf(string stem, int n) => $"01CHN{stem}{n:D2}".PadRight(26, '0');

    private Link Question(string stem)
    {
        var draft = new PostDraft { Kind = PostKind.Question, Board = "b", Title = $"A question about {stem}", Body = $"Which finch is this? {stem}" };
        return new Link(IdOf(stem + "Q", 0), draft, _log.SignDraft(draft));
    }

    /// <summary>A revision of <paramref name="of"/>, honestly parented, with a body that makes every revision distinct.</summary>
    private Link Revise(Link of, string stem, int n, string? parent = null)
    {
        var draft = new PostDraft { Kind = PostKind.Revision, Board = "b", Parent = parent ?? of.Id, Prev = of.Digest, Body = $"Correction {stem} {n}." };
        return new Link(IdOf(stem + "R", n), draft, _log.SignDraft(draft));
    }

    /// <summary>A question and a linear chain of <paramref name="depth"/> revisions, all served with the lists the Forum would serve.</summary>
    private Link[] Chain(string stem, int depth)
    {
        var links = new List<Link> { Question(stem) };
        for (var i = 1; i <= depth; i++) links.Add(Revise(links[i - 1], stem, i));
        for (var i = 0; i <= depth; i++)
            _log.ServePost(links[i].Id, links[i].Draft, links[i].Signed, i < depth ? [links[i + 1].AsSuccessor()] : []);
        return [.. links];
    }

    private async Task<McpProtocolSession> StartAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
        return await McpProtocolSession.StartAsync(tools, writer.Agent.Profile.AgentId, agent);
    }

    /// <summary>The refusal <c>curia_answer</c> gives under <paramref name="named"/>, which the Forum refuses as a revision.</summary>
    private async Task<string> RefusalUnderAsync(Link named)
    {
        _log.RefusesAsParentNotAQuestion = (named.Id, "revision");
        await using var session = await StartAsync();
        var answer = await session.CallAsync("curia_answer", new() { ["parentPostId"] = named.Id, ["body"] = "It is a goldfinch." });
        Assert.True(answer.IsError, answer.Text);
        Assert.Contains(RefusalType, answer.Text, StringComparison.Ordinal);
        return answer.Text;
    }

    private int ReadsOfPosts() => _log.Requests.Count(r => r.StartsWith("GET /v1/posts/", StringComparison.Ordinal));

    private static string Literal(string value) => DisplayLiteral.Of(value);

    /// <summary>CHN21. The refusal under r2 of Q, r1, r2 names the question by post id, states that its <c>superseded_by</c> lists r1, and carries no hedge.</summary>
    [Fact]
    public async Task R11_62_CHN21_TheRefusalStatesTheQuestionsStanding()
    {
        var chain = Chain("A", 2);
        var hedge = PublishedChainWords.RefusalAndBlock().Hedge;

        var text = await RefusalUnderAsync(chain[2]);

        var problems = new List<string>();
        if (!text.Contains(Literal(chain[0].Id), StringComparison.Ordinal)) problems.Add("does not name the question by post id");
        if (!text.Contains("SUPERSEDED", StringComparison.Ordinal) || !text.Contains(Literal(chain[1].Id), StringComparison.Ordinal))
            problems.Add("does not state that the question's superseded_by lists the first revision, by post id");
        if (text.Contains(hedge, StringComparison.Ordinal)) problems.Add($"carries the hedge \"{hedge}\"");
        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Actual: {text}");
    }

    /// <summary>
    /// CHN22, data-driven over depth. For chains of depth 1 to 4 and each revision named in turn: naming the head says it is the latest the Forum
    /// reports; naming an earlier one names the revision that supersedes it and does not say it is the latest.
    /// </summary>
    [Fact]
    public async Task R11_62_CHN22_TheNamedRevisionsOwnStandingIsStated()
    {
        var latest = PublishedChainWords.RefusalPhrase("latest the Forum reports");
        var named = PublishedChainWords.RefusalAndBlock().Named;
        var problems = new List<string>();
        foreach (var depth in new[] { 1, 2, 3, 4 })
        {
            for (var k = 1; k <= depth; k++)
            {
                using var scene = new QuestionStandingTests();
                var chain = scene.Chain($"D{depth}K{k}", depth);
                var text = await scene.RefusalUnderAsync(chain[k]);
                var expectsLatest = k == depth;
                var where = $"depth {depth}, naming r{k}";
                if (!text.Contains(named, StringComparison.Ordinal)) problems.Add($"{where}: lacks \"{named}\"");
                if (expectsLatest && !text.Contains(latest, StringComparison.Ordinal)) problems.Add($"{where}: does not say it is the {latest}");
                if (!expectsLatest && text.Contains(latest, StringComparison.Ordinal)) problems.Add($"{where}: says it is the {latest}");
                if (!expectsLatest && !text.Contains(Literal(chain[k + 1].Id), StringComparison.Ordinal)) problems.Add($"{where}: does not name r{k + 1}, which supersedes it");
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems.Take(8)) + (problems.Count > 8 ? $"; and {problems.Count - 8} more" : string.Empty));
    }

    /// <summary>CHN23. A forked question (two served revisions): the refusal names both and says FORKED and that the Forum picks none.</summary>
    [Fact]
    public async Task R11_62_CHN23_AForkedQuestionIsNamedForked()
    {
        var q = Question("F");
        var a = Revise(q, "F", 1);
        var b = Revise(q, "F", 2);
        _log.ServePost(q.Id, q.Draft, q.Signed, [a.AsSuccessor(), b.AsSuccessor()]);
        _log.ServePost(a.Id, a.Draft, a.Signed, []);
        _log.ServePost(b.Id, b.Draft, b.Signed, []);

        var text = await RefusalUnderAsync(b);

        var problems = new List<string>();
        if (!text.Contains("FORKED", StringComparison.Ordinal)) problems.Add("does not say FORKED");
        if (!text.Contains(Literal(a.Id), StringComparison.Ordinal) || !text.Contains(Literal(b.Id), StringComparison.Ordinal)) problems.Add("does not name both revisions");
        if (!text.Contains("does not pick one", StringComparison.Ordinal) && !text.Contains("picks none", StringComparison.Ordinal)) problems.Add("does not say the Forum picks none");
        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Actual: {text}");
    }

    /// <summary>
    /// CHN24. The core is shared and the tail is the receipt's: the refusal carries the core text the answer's receipt carries for the same list, and
    /// does not carry the receipt's tail. The receipt's tail is asserted present in the receipt (the control).
    /// </summary>
    [Fact]
    public async Task R11_62_CHN24_TheCoreIsSharedAndTheTailIsTheReceipts()
    {
        const string Tail = "this answer went to the question as named";
        var chain = Chain("C", 1);

        // The receipt: an answer sent to the original, whose list holds the revision.
        await using (var session = await StartAsync())
        {
            var receipt = await session.CallAsync("curia_answer", new() { ["parentPostId"] = chain[0].Id, ["body"] = "It is a goldfinch." });
            var line = receipt.Text.Split('\n').Single(l => l.StartsWith("question    ", StringComparison.Ordinal));
            Assert.Contains(Tail, line, StringComparison.Ordinal);

            // The core is the receipt's line up to the first clause after the list it words: the walk down the chain adds its own (R11.67, errata G50:
            // "; the latest revision the Forum reports is ..."), and then the clause that is about this answer and the read before it.
            var body = line["question    ".Length..];
            var tailAt = body.IndexOf("; ", StringComparison.Ordinal) is var clause and > 0 ? clause : body.LastIndexOf(" (the Forum's word", StringComparison.Ordinal);
            Assert.True(tailAt > 0, $"the receipt's question line has no tail clause: {line}");
            var core = body[..tailAt];
            Assert.StartsWith("SUPERSEDED", core, StringComparison.Ordinal);

            var refusal = await RefusalUnderAsync(chain[1]);

            Assert.True(refusal.Contains(core, StringComparison.Ordinal), $"Expected: the refusal carries the receipt's core \"{core}\" Actual: {refusal}");
            Assert.DoesNotContain(Tail, refusal, StringComparison.Ordinal);
        }
    }

    /// <summary>CHN25 (a control). Where the question's list is empty, absent or holds another digest, the pointer is the walk's fixed words: no question named, no standing claimed.</summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("absent")]
    [InlineData("other-digest")]
    public async Task R11_62_CHN25_WhereTheListDoesNotHoldTheDigestNoStandingIsClaimed(string how)
    {
        var q = Question("N");
        var r = Revise(q, "N", 1);
        ImmutableArray<Successor> list = how switch
        {
            "empty" => [],
            "absent" => default,
            _ => [new Successor("sha256:" + new string('9', 64), "01CHNOTHER000000000000000A")],
        };
        _log.ServePost(q.Id, q.Draft, q.Signed, list);
        _log.ServePost(r.Id, r.Draft, r.Signed, []);

        var text = await RefusalUnderAsync(r);

        Assert.Contains("The post you named is a revision; no question is named because", text, StringComparison.Ordinal);
        Assert.DoesNotContain(q.Id, text, StringComparison.Ordinal);
        Assert.DoesNotContain("SUPERSEDED", text, StringComparison.Ordinal);
        Assert.DoesNotContain(PublishedChainWords.RefusalPhrase("latest the Forum reports"), text, StringComparison.Ordinal);
    }

    /// <summary>CHN26 (a control). No read is added: for chains of depth 1 to the bound, the refusal made one read per step and the named post's, never more.</summary>
    [Fact]
    public async Task R11_62_CHN26_NoReadIsAdded()
    {
        var problems = new List<string>();
        foreach (var depth in new[] { 1, 2, 3, 4, ForumTools.WalkBound })
        {
            using var scene = new QuestionStandingTests();
            var chain = scene.Chain($"G{depth}", depth);
            _ = await scene.RefusalUnderAsync(chain[depth]);
            var reads = scene.ReadsOfPosts();
            if (reads != depth + 1) problems.Add($"depth {depth}: {reads} reads, expected {depth + 1} (one per step and the named post's)");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>CHN57 (a). A fork with a branch the Forum does not serve: the refusal names F2 by post id and F1 by its digest as not served, and says FORKED.</summary>
    [Fact]
    public async Task R11_62_CHN57_TheRefusalNamesABranchTheForumDoesNotServe()
    {
        var q = Question("W");
        var f1 = Revise(q, "W", 1);
        var f2 = Revise(q, "W", 2);
        _log.ServePost(q.Id, q.Draft, q.Signed, [f1.AsSuccessor(served: false), f2.AsSuccessor()]);
        _log.ServePost(f2.Id, f2.Draft, f2.Signed, []);

        var text = await RefusalUnderAsync(f2);

        var problems = new List<string>();
        if (!text.Contains(Literal(f2.Id), StringComparison.Ordinal)) problems.Add("does not name F2 by post id");
        if (!text.Contains(Literal(f1.Digest), StringComparison.Ordinal) || !text.Contains("not served", StringComparison.Ordinal)) problems.Add("does not name F1's digest as not served");
        if (!text.Contains("FORKED", StringComparison.Ordinal)) problems.Add("does not say FORKED");
        if (text.Contains(PublishedChainWords.RefusalAndBlock().Hedge, StringComparison.Ordinal)) problems.Add("carries the hedge");
        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Actual: {text}");
    }

    /// <summary>
    /// CHN61. Naming the latest revision of a twice-revised question: the named revision's own <c>superseded_by</c> is empty while the question's lists r1.
    /// The clause about the revision named states it as the latest the Forum reports, and names no revision as superseding it (not r1, which the question's
    /// list holds and the named revision's does not).
    /// </summary>
    [Fact]
    public async Task R11_62_CHN61_TheLatestRevisionIsNeverSaidToBeSupersededByAnEarlierOne()
    {
        var chain = Chain("L", 2);
        var latest = PublishedChainWords.RefusalPhrase("latest the Forum reports");
        var named = PublishedChainWords.RefusalAndBlock().Named;

        var text = await RefusalUnderAsync(chain[2]);

        var at = text.IndexOf(named, StringComparison.Ordinal);
        Assert.True(at >= 0, $"lacks \"{named}\". Actual: {text}");
        var clause = text[at..];
        var problems = new List<string>();
        if (!clause.Contains(latest, StringComparison.Ordinal)) problems.Add($"the clause on the revision named does not say it is the {latest}");
        if (clause.Contains(Literal(chain[1].Id), StringComparison.Ordinal)) problems.Add("the clause on the revision named names r1, which does not supersede it");
        if (clause.Contains("superseded by", StringComparison.OrdinalIgnoreCase)) problems.Add("the clause on the revision named says it is superseded");
        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Clause: {clause}");
    }

    // ----- the first block (CHN27 to CHN30, CHN57 (b)) -----

    private void ServeThread(string rootId, params Link[] members) =>
        _log.ServedThreads[rootId] = "[" + string.Join(",", members.Select(m => _log.ServedPosts[m.Id])) + "]";

    private async Task<string> FirstBlockAsync(string postId)
    {
        var read = await new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home)).ReadAsync(postId, Ct);
        return Assert.IsType<TextContentBlock>(read.Content[0]).Text;
    }

    private static string[] Lines(string block) => [.. block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')];

    /// <summary>The line after the <c>accepted</c> line: the root's <c>revision</c> line, or null where the block holds none.</summary>
    private static string? RevisionLine(string block)
    {
        var lines = Lines(block);
        var accepted = Array.FindIndex(lines, l => l.StartsWith("accepted ", StringComparison.Ordinal));
        return accepted >= 0 && accepted + 1 < lines.Length && lines[accepted + 1].StartsWith("revision", StringComparison.Ordinal) ? lines[accepted + 1] : null;
    }

    /// <summary>
    /// CHN27. A thread of a question revised to depth 1 to 5, read through the root, the head and (where there is one) a middle revision: the line after
    /// <c>accepted</c> is the same in all three, names the root's revision, and names <c>the text that stands</c> as the chain's head, computed from the chain built.
    /// </summary>
    [Fact]
    public async Task R11_62_CHN27_TheFirstBlockStatesTheRootsRevisionFromTheRootAtAnyDepth()
    {
        var stands = PublishedChainWords.RefusalAndBlock().Stands;
        var problems = new List<string>();
        foreach (var depth in new[] { 1, 2, 3, 4, 5 })
        {
            using var scene = new QuestionStandingTests();
            var chain = scene.Chain($"B{depth}", depth);
            scene.ServeThread(chain[0].Id, chain);
            var reads = new List<(string Name, string? Line)>
            {
                ("root", RevisionLine(await scene.FirstBlockAsync(chain[0].Id))),
                ("head", RevisionLine(await scene.FirstBlockAsync(chain[depth].Id))),
            };
            if (depth >= 2) reads.Add(("middle", RevisionLine(await scene.FirstBlockAsync(chain[depth / 2].Id))));

            foreach (var (name, line) in reads)
            {
                if (line is null) problems.Add($"depth {depth}, read through the {name}: no revision line after the accepted line");
                else if (!line.Contains(stands, StringComparison.Ordinal) || !line.Contains(Literal(chain[depth].Id), StringComparison.Ordinal) || !line.Contains(Literal(chain[1].Id), StringComparison.Ordinal))
                    problems.Add($"depth {depth}, read through the {name}: the line does not name r1 and \"{stands}\" r{depth}: {line}");
            }

            if (reads.Select(r => r.Line).Distinct(StringComparer.Ordinal).Count() > 1) problems.Add($"depth {depth}: the line differs by the post read");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems.Take(6)) + (problems.Count > 6 ? $"; and {problems.Count - 6} more" : string.Empty));
    }

    /// <summary>CHN28. Silence and NOT STATED are both probed: an unrevised root prints no revision line; a root whose list the Forum did not serve prints <c>revision  NOT STATED</c>.</summary>
    [Fact]
    public async Task R11_62_CHN28_SilenceForAnUnrevisedRootAndNotStatedForAnUnservedList()
    {
        var plain = Question("S");
        _log.ServePost(plain.Id, plain.Draft, plain.Signed, []);
        ServeThread(plain.Id, plain);
        var unstated = Question("T");
        _log.ServePost(unstated.Id, unstated.Draft, unstated.Signed);
        ServeThread(unstated.Id, unstated);

        var silent = await FirstBlockAsync(plain.Id);
        var notStated = await FirstBlockAsync(unstated.Id);

        Assert.Contains("thread root=", silent, StringComparison.Ordinal);
        Assert.DoesNotContain(Lines(silent), l => l.StartsWith("revision", StringComparison.Ordinal));
        var line = RevisionLine(notStated);
        Assert.True(line is not null && line.Contains(PublishedChainWords.ServedWords().NotStated, StringComparison.Ordinal), $"Expected: a revision line saying NOT STATED Actual: {line ?? "none"} in {notStated}");
    }

    /// <summary>CHN29 (a control). Where the thread is cut or unread the first block prints no revision line, whatever the post it holds says.</summary>
    [Fact]
    public async Task R11_62_CHN29_ACutOrUnreadThreadClaimsNothing()
    {
        // Cut: a revision, listed as superseded, whose parent the Forum refuses as missing.
        var q = Question("U");
        var r1 = Revise(q, "U", 1, parent: "01CHNMISSING0000000000000A");
        var r2 = Revise(r1, "U", 2);
        _log.ServePost(r1.Id, r1.Draft, r1.Signed, [r2.AsSuccessor()]);
        _log.RefusedPosts["01CHNMISSING0000000000000A"] = (System.Net.HttpStatusCode.NotFound, "curia/posts/not-found");
        ServeThread(r1.Id, r1);
        var cut = await FirstBlockAsync(r1.Id);

        // Not listed: the thread route serves a thread that does not hold the post (errata G50, R11.66: NOT LISTED, where it used to say NOT READ).
        var q2 = Question("V");
        var r3 = Revise(q2, "V", 1);
        _log.ServePost(q2.Id, q2.Draft, q2.Signed, [r3.AsSuccessor()]);
        _log.ServePost(r3.Id, r3.Draft, r3.Signed, []);
        _log.ServedThreads[q2.Id] = "[" + _log.ServedPosts[q2.Id] + "]";
        var unread = await FirstBlockAsync(r3.Id);

        Assert.Contains("CUT", cut, StringComparison.Ordinal);
        Assert.Contains("NOT LISTED", unread, StringComparison.Ordinal);
        Assert.DoesNotContain(Lines(cut), l => l.StartsWith("revision", StringComparison.Ordinal));
        Assert.DoesNotContain(Lines(unread), l => l.StartsWith("revision", StringComparison.Ordinal));
    }

    /// <summary>CHN30. A fork in the thread: the line says FORKED, names both heads in log order, and says the Forum picks none.</summary>
    [Fact]
    public async Task R11_62_CHN30_AForkNamesEveryHeadAndPicksNone()
    {
        var q = Question("K");
        var a = Revise(q, "K", 1);
        var b = Revise(q, "K", 2);
        _log.ServePost(q.Id, q.Draft, q.Signed, [a.AsSuccessor(), b.AsSuccessor()]);
        _log.ServePost(a.Id, a.Draft, a.Signed, []);
        _log.ServePost(b.Id, b.Draft, b.Signed, []);
        ServeThread(q.Id, q, a, b);

        var line = RevisionLine(await FirstBlockAsync(q.Id));

        Assert.True(line is not null, "Expected: a revision line Actual: none");
        var problems = new List<string>();
        if (!line!.Contains("FORKED", StringComparison.Ordinal)) problems.Add("does not say FORKED");
        var (ia, ib) = (line.IndexOf(Literal(a.Id), StringComparison.Ordinal), line.IndexOf(Literal(b.Id), StringComparison.Ordinal));
        if (ia < 0 || ib < 0 || ia > ib) problems.Add("does not name both heads in log order");
        if (!line.Contains("does not pick one", StringComparison.Ordinal) && !line.Contains("picks none", StringComparison.Ordinal)) problems.Add("does not say the Forum picks none");
        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Actual: {line}");
    }

    /// <summary>
    /// CHN62. A question forked into two heads, both held in the thread: the clause after the root's own superseded statement ("the text that stands")
    /// names every head and says FORKED by itself, so it does not lean on the root statement, which already names both successors (CHN30).
    /// </summary>
    [Fact]
    public async Task R11_62_CHN62_TheTextThatStandsClauseNamesEveryHeadAndSaysForked()
    {
        var q = Question("M");
        var a = Revise(q, "M", 1);
        var b = Revise(q, "M", 2);
        _log.ServePost(q.Id, q.Draft, q.Signed, [a.AsSuccessor(), b.AsSuccessor()]);
        _log.ServePost(a.Id, a.Draft, a.Signed, []);
        _log.ServePost(b.Id, b.Draft, b.Signed, []);
        ServeThread(q.Id, q, a, b);
        var stands = PublishedChainWords.RefusalAndBlock().Stands;

        var line = RevisionLine(await FirstBlockAsync(q.Id));

        Assert.True(line is not null, "Expected: a revision line Actual: none");
        var at = line!.IndexOf(stands, StringComparison.Ordinal);
        Assert.True(at >= 0, $"the line has no \"{stands}\" clause. Actual: {line}");
        var clause = line[at..];
        var problems = new List<string>();
        if (!clause.Contains("FORKED", StringComparison.Ordinal)) problems.Add("the clause does not say FORKED");
        if (!clause.Contains(Literal(a.Id), StringComparison.Ordinal)) problems.Add("the clause does not name the first head");
        if (!clause.Contains(Literal(b.Id), StringComparison.Ordinal)) problems.Add("the clause does not name the second head");
        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Clause: {clause}");
    }

    /// <summary>
    /// CHN57 (b). A fork with a branch the thread does not hold (F1 withheld; the question's list holds its digest alone): the first block says FORKED, names
    /// F2 as a head, says F1's digest leads outside what the thread holds, and claims no head for it.
    /// </summary>
    [Fact]
    public async Task R11_62_CHN57_TheFirstBlockSaysADigestLeadsOutsideTheThread()
    {
        var q = Question("X");
        var f1 = Revise(q, "X", 1);
        var f2 = Revise(q, "X", 2);
        _log.ServePost(q.Id, q.Draft, q.Signed, [f1.AsSuccessor(served: false), f2.AsSuccessor()]);
        _log.ServePost(f2.Id, f2.Draft, f2.Signed, []);
        ServeThread(q.Id, q, f2);

        var line = RevisionLine(await FirstBlockAsync(q.Id));

        Assert.True(line is not null, "Expected: a revision line Actual: none");
        var problems = new List<string>();
        if (!line!.Contains("FORKED", StringComparison.Ordinal)) problems.Add("does not say FORKED");
        if (!line.Contains(Literal(f2.Id), StringComparison.Ordinal)) problems.Add("does not name F2 as a head");
        if (!line.Contains(Literal(f1.Digest), StringComparison.Ordinal) || !line.Contains("outside", StringComparison.Ordinal)) problems.Add("does not say F1's digest leads outside what the thread holds");
        // Not published span: R11.62 covers "leads outside ... claims no head" and "a fork names every head", not one held head beside a digest outside.
        // The sentence must not read as though the fork were resolved: one head is held here and another successor leads outside.
        if (line.Contains("stands among", StringComparison.Ordinal) || !line.Contains("one head", StringComparison.Ordinal) || !line.Contains("not settled", StringComparison.Ordinal))
            problems.Add("reads as though the fork were resolved (expected: not settled, one head held here, another leading outside)");
        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Actual: {line}");
    }

    // ----- the descriptions (CHN32) -----

    private List<(string Name, string Description)> Registered()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        var tools = new ForumTools(_log.Client(), MarkingMode.Datamark, new HeadStore(_home), writer);
        agent!.Dispose();
        return [.. ToolCatalogue.Build(tools).Select(t => (t.ProtocolTool.Name, t.ProtocolTool.Description ?? string.Empty))];
    }

    /// <summary>CHN32. <c>curia_read</c>'s description names the first block's revision line and the text that stands; <c>curia_answer</c>'s says the refusal states the standing and the named revision's.</summary>
    [Fact]
    public void R11_62_CHN32_TheDescriptionsStateWhatTheyNowDo()
    {
        var (_, named, stands) = PublishedChainWords.RefusalAndBlock();
        var tools = Registered();
        var read = tools.Single(t => t.Name == "curia_read").Description;
        var answer = tools.Single(t => t.Name == "curia_answer").Description;

        var problems = new List<string>();
        if (!read.Contains(stands, StringComparison.Ordinal)) problems.Add($"curia_read's description lacks \"{stands}\"");
        if (!read.Contains("revision", StringComparison.Ordinal)) problems.Add("curia_read's description does not name the revision line");
        if (!answer.Contains(named, StringComparison.Ordinal)) problems.Add($"curia_answer's description lacks \"{named}\"");
        if (answer.Contains(PublishedChainWords.RefusalAndBlock().Hedge, StringComparison.Ordinal)) problems.Add("curia_answer's description carries the hedge");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
