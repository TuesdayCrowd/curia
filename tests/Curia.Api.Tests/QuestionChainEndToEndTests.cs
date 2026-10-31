using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Canon.Json;
using Curia.Tests.Shared;
using ModelContextProtocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// PR 3 (errata G50, R11.67), Task 0's probes RLW24 to RLW28, RLW30, RLW31, RLW44 and RLW45 against a real Forum on a real socket: the answer's receipt, from
/// <c>curia_answer</c> and from the shipped <c>curia answer</c> in a child process, follows a revised question's chain down by <c>superseded_by</c>, at most eight
/// posts after the question, before anything is signed, and never stops the answer; the CLI makes one advisory read of its <c>--parent</c>.
///
/// <para><b>Oracles.</b> The ids are the receipts of the posts the scene made (the revisions are hand-signed and submitted through <c>ForumSession</c>, so the
/// Forum's own gates apply); the chain a probe expects is walked over the <c>events</c> table by SQL (<see cref="ReadersLastWordsScene.ChainFromEventsAsync"/>),
/// never by <c>CorrectionIndex</c>; <b>the reads are counted over HTTP by <see cref="CountingProxy"/></b>, by path (only <c>GET /v1/posts/{id}</c> counts), so a
/// probe that counted every <c>GET</c> would move with the token cache; the words are R11.67's, parsed from the errata
/// (<see cref="PublishedReadersLastWords"/>). A fork is made by withholding the first revision, hand-signing the second and restoring the first.</para>
///
/// <para><b>Controls (green on the seam tree):</b> RLW26 (the baseline already names both branches of a fork by post id and says FORKED, with one read), RLW30
/// (an unrevised question costs no read beyond the one it was), RLW31 (the refusal under a revision is untouched) and RLW44 (a parent the Forum refuses is
/// refused in the Forum's words).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class QuestionChainEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>
{
    private const string NoSuchPost = "01M6FFFF000000000000000000";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^question\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Label();

    private static string[] QuestionLines(string text) => [.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Where(l => l.StartsWith("question", StringComparison.Ordinal))];

    private static string Standing(string line) => Label().Replace(line, string.Empty);

    private static string Id(string id) => DisplayLiteral.Of(id);

    private static string Latest => PublishedReadersLastWords.LatestPhrase().Split(' ')[1];

    private async Task<(ReadersLastWordsScene Scene, string Board, RlwAgent Asker, RlwAgent Answerer)> SceneAsync(string stem)
    {
        var scene = new ReadersLastWordsScene(forum);
        var board = "board-" + ReadersLastWordsScene.Suffix();
        var asker = await scene.T1Async("asker", board);
        var answerer = await scene.T1Async("answerer", board);
        _ = stem;
        return (scene, board, asker, answerer);
    }

    private static string[] CliAnswer(RlwAgent agent, string board, string parent, string address) =>
        ["answer", "--agent", agent.Slug, "--board", board, "--parent", parent, "--body", $"It is a goldfinch {Guid.NewGuid():N}.", "--forum", address];

    /// <summary>RLW24. A question revised twice, answered through <c>curia_answer</c>: the receipt names both revisions by post id and says the second is the latest the Forum reports; the chain is the test's own walk over the events.</summary>
    [Fact]
    public async Task R11_67_RLW24_TheReceiptNamesBothHopsOfAQuestionRevisedTwice()
    {
        var (scene, board, asker, answerer) = await SceneAsync("rlw24");
        using var _ = scene;
        var chain = await scene.QuestionWithRevisionsAsync(asker, board, "finch", 2);
        var walked = await scene.ChainFromEventsAsync(chain[0]);
        Assert.Equal([chain[1].Id, chain[2].Id], walked.Select(w => w.Id));

        var text = ReadersLastWordsScene.Flatten(await answerer.Tools.AnswerAsync(chain[0].Id, "It is a goldfinch.", null, Ct));
        var line = Assert.Single(QuestionLines(text));

        var problems = new List<string>();
        foreach (var hop in walked) if (!line.Contains(Id(hop.Id), StringComparison.Ordinal)) problems.Add($"Expected: both hops named ({Id(hop.Id)}) Actual: {line}");
        var at = line.IndexOf(Latest, StringComparison.Ordinal);
        if (at < 0 || line.IndexOf(Id(walked[^1].Id), at, StringComparison.Ordinal) is var second && (second < 0 || second - at > 160)) problems.Add($"Expected: the second revision stated as the latest the Forum reports Actual: {line}");
        Assert.True(problems.Count == 0, string.Join("; ", problems.Distinct()));
    }

    /// <summary>
    /// RLW25. The reads, counted over HTTP by the loopback proxy under the tool, by path: for depths 0, 1, 2 and 8 the <c>GET /v1/posts/{id}</c> requests are 1 plus
    /// the depth (the question and each hop), at depth 9 they are 1 plus the bound, and a question never revised is exactly the one read it was.
    /// </summary>
    [Fact]
    public async Task R11_67_RLW25_TheReadsAreOnePlusTheDepthAndNeverMoreThanOnePlusTheBound()
    {
        var bound = PublishedReadersLastWords.WalkBound();
        var scene = new ReadersLastWordsScene(forum);
        using var _ = scene;
        var board = "board-" + ReadersLastWordsScene.Suffix();
        var askerA = await scene.T1Async("askera", board);
        var askerB = await scene.T1Async("askerb", board);
        var answerer = await scene.T1Async("answerer", board);

        var questions = new List<(int Depth, RlwPost Question)>();
        foreach (var depth in new[] { 0, 1, 2, bound }) questions.Add((depth, (await scene.QuestionWithRevisionsAsync(askerA, board, "finch" + depth, depth))[0]));
        questions.Add((bound + 1, (await scene.QuestionWithRevisionsAsync(askerB, board, "finch" + (bound + 1), bound + 1))[0]));

        using var proxy = CountingProxy.In(forum.Client.BaseAddress!);
        var tools = scene.ToolsThrough(answerer, proxy.Address);
        var problems = new List<string>();
        foreach (var (depth, question) in questions)
        {
            var before = proxy.PostReads;
            ReadersLastWordsScene.Flatten(await tools.AnswerAsync(question.Id, $"It is a goldfinch {Guid.NewGuid():N}.", null, Ct));
            var reads = proxy.PostReads - before;
            var expected = 1 + Math.Min(depth, bound);
            if (reads != expected) problems.Add($"depth {depth}: Expected: {expected} reads of GET /v1/posts/{{id}} Actual: {reads}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW26, a control (the seam tree prints the fork in full already). Two served revisions of one question, made by withholding the first, hand-signing the
    /// second and restoring the first: <c>curia_answer</c>'s line names both by post id and says FORKED, with one <c>GET</c> of a post and neither branch read.
    /// The part this PR adds is RLW45. Red under FRLW30, which follows both branches.
    /// </summary>
    [Fact]
    public async Task R11_67_RLW26_AForkNamesBothBranchesAndFollowsNeither()
    {
        var (scene, board, asker, answerer) = await SceneAsync("rlw26");
        using var _ = scene;
        var (question, first, second) = await scene.ForkAsync(asker, board, "finch");
        Assert.Equal(2, (await scene.RevisionsOfAsync(question.Digest)).Length);

        using var proxy = CountingProxy.In(forum.Client.BaseAddress!);
        var text = ReadersLastWordsScene.Flatten(await scene.ToolsThrough(answerer, proxy.Address).AnswerAsync(question.Id, "It is a goldfinch.", null, Ct));
        var line = Assert.Single(QuestionLines(text));

        var problems = new List<string>();
        if (!line.Contains(Id(first.Id), StringComparison.Ordinal) || !line.Contains(Id(second.Id), StringComparison.Ordinal)) problems.Add($"both branches are not named: {line}");
        if (!line.Contains(PublishedReadersLastWords.WalkWords().Forked, StringComparison.Ordinal)) problems.Add($"FORKED is not said: {line}");
        if (proxy.PostReads != 1) problems.Add($"Expected: one GET of a post Actual: {proxy.PostReads} ({string.Join(", ", proxy.PostReadIds)})");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>RLW27. A withheld first revision: the receipt says it is not served in R10.91's words beside its digest, makes no further read, and claims no latest revision.</summary>
    [Fact]
    public async Task R11_67_RLW27_AWithheldSuccessorEndsTheWalkInTheDefinitionsWords()
    {
        var (scene, board, asker, answerer) = await SceneAsync("rlw27");
        using var _ = scene;
        var chain = await scene.QuestionWithRevisionsAsync(asker, board, "finch", 2);
        await forum.WithholdAsync(chain[1].Id, Ct);

        using var proxy = CountingProxy.In(forum.Client.BaseAddress!);
        var text = ReadersLastWordsScene.Flatten(await scene.ToolsThrough(answerer, proxy.Address).AnswerAsync(chain[0].Id, "It is a goldfinch.", null, Ct));
        var line = Assert.Single(QuestionLines(text));

        var sentence = PublishedReadersLastWords.DefinitionSentence();
        var problems = new List<string>();
        var digest = Id(chain[1].Digest);
        var at = line.IndexOf(digest, StringComparison.Ordinal);
        if (at < 0 || line.IndexOf(sentence, at, StringComparison.Ordinal) is var found && (found < 0 || found - at > 80)) problems.Add($"Expected: \"{sentence}\" beside the digest Actual: {line}");
        if (proxy.PostReads != 1) problems.Add($"Expected: no read past the question Actual: {proxy.PostReads} reads ({string.Join(", ", proxy.PostReadIds)})");
        if (line.Contains(Latest, StringComparison.Ordinal) && !line.Contains("no latest", StringComparison.Ordinal)) problems.Add($"a head is claimed past a post the Forum does not serve: {line}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW28 (amended 2026-10-30, M3). <c>curia answer</c> end to end: its <c>question</c> line is the tool's for the same state (the standing, one definition) and,
    /// for an unrevised question, is the tool's <c>none was reported</c> line; the command makes exactly one added <c>GET</c> of <c>--parent</c> (the proxy counts
    /// it, plus the hops); and where the read fails while the post would store (the proxy fails that one <c>GET</c> and forwards the <c>POST</c>) the line says
    /// <c>NOT CHECKED</c>, the exit is 0 and the answer is stored (SQL).
    /// </summary>
    [Fact]
    public async Task R11_67_RLW28_CuriaAnswerStatesTheSameLineAndNeverBlocksOnItsRead()
    {
        var (scene, board, asker, answerer) = await SceneAsync("rlw28");
        using var _ = scene;
        var cliAnswerer = await scene.T1Async("cli", board);
        var revised = await scene.QuestionWithRevisionsAsync(asker, board, "finch", 2);
        var plain = await scene.AskAsync(asker, board, "wren");
        var transient = await scene.AskAsync(asker, board, "robin");

        using var proxy = CountingProxy.In(forum.Client.BaseAddress!);
        var problems = new List<string>();

        foreach (var (name, question, readsExpected) in new[] { ("a question revised twice", revised[0], 3), ("a question never revised", plain, 1) })
        {
            var tool = ReadersLastWordsScene.Flatten(await scene.ToolsThrough(answerer, proxy.Address).AnswerAsync(question.Id, $"It is a goldfinch {Guid.NewGuid():N}.", null, Ct));
            var before = proxy.PostReads;
            var cli = await scene.CuriaAsync(CliAnswer(cliAnswerer, board, question.Id, proxy.Address));
            var reads = proxy.PostReads - before;
            var toolLines = QuestionLines(tool);
            var cliLines = QuestionLines(cli.Stdout);
            if (cli.Exit != 0) { problems.Add($"{name}: exit {cli.Exit} {cli.Stderr.Trim()}"); continue; }
            if (cliLines.Length != 1) problems.Add($"{name}: Expected: one question line from curia answer Actual: {cliLines.Length} in {cli.Stdout.Trim()}");
            else if (toolLines.Length == 1 && Standing(cliLines[0]) != Standing(toolLines[0])) problems.Add($"{name}: the lines differ. Tool: {toolLines[0]} CLI: {cliLines[0]}");
            if (reads != readsExpected) problems.Add($"{name}: Expected: {readsExpected} reads of a post Actual: {reads}");
        }

        // A read that fails while the post would store: NOT CHECKED, exit 0, stored.
        var storedBefore = await scene.AnswersTo(transient.Id);
        proxy.FailNextReadOf(transient.Id);
        var failed = await scene.CuriaAsync(CliAnswer(cliAnswerer, board, transient.Id, proxy.Address));
        var storedAfter = await scene.AnswersTo(transient.Id);
        var notChecked = PublishedReadersLastWords.WalkWords().NotChecked;
        if (failed.Exit != 0) problems.Add($"a read that fails: Expected: exit 0 Actual: exit {failed.Exit} {failed.Stderr.Trim()}");
        if (!QuestionLines(failed.Stdout).Any(l => l.Contains(notChecked, StringComparison.Ordinal))) problems.Add($"a read that fails: Expected: a question line with {notChecked} Actual: {failed.Stdout.Trim()}");
        if (storedAfter - storedBefore != 1) problems.Add($"a read that fails: Expected: the answer stored once Actual: {storedAfter - storedBefore}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// RLW30, a control. An answer to a question that was never revised through <c>curia_answer</c> costs no read beyond the one it was, and its <c>question</c> line
    /// is the line it always was, byte for byte. Green on the seam tree; red under FRLW34, which reads once for an empty list.
    /// </summary>
    [Fact]
    public async Task R11_67_RLW30_AQuestionNotRevisedCostsNothingMore()
    {
        var (scene, board, asker, answerer) = await SceneAsync("rlw30");
        using var _ = scene;
        var question = await scene.AskAsync(asker, board, "finch");

        using var proxy = CountingProxy.In(forum.Client.BaseAddress!);
        var text = ReadersLastWordsScene.Flatten(await scene.ToolsThrough(answerer, proxy.Address).AnswerAsync(question.Id, "It is a goldfinch.", null, Ct));
        var line = Assert.Single(QuestionLines(text));

        Assert.Equal(1, proxy.PostReads);
        Assert.Equal("question    none was reported (the Forum's word on the question at the read before this answer; a revision that landed since is not seen)", line);
    }

    /// <summary>
    /// RLW31, a control. The refusal path is untouched: an answer to a revision is refused with the question's standing worded by the one core, and the receipt for
    /// an answer to the original words the same one revision the same way (<c>SUPERSEDED by 1 revision(s) of the question: ...</c>), so one list is not worded two
    /// ways (R11.62's rule). Green on the seam tree; red under FRLW53.
    /// </summary>
    [Fact]
    public async Task R11_67_RLW31_TheRefusalPathIsUntouchedAndSharesItsWords()
    {
        var (scene, board, asker, answerer) = await SceneAsync("rlw31");
        using var _ = scene;
        var chain = await scene.QuestionWithRevisionsAsync(asker, board, "finch", 1);
        var core = $"SUPERSEDED by 1 revision(s) of the question: {Id(chain[1].Digest)} (post {Id(chain[1].Id)})";

        string refusal;
        try
        {
            refusal = "no refusal: " + ReadersLastWordsScene.Flatten(await answerer.Tools.AnswerAsync(chain[1].Id, "It is a goldfinch.", null, Ct));
        }
        catch (McpException refused)
        {
            refusal = refused.Message;
        }

        var receipt = ReadersLastWordsScene.Flatten(await answerer.Tools.AnswerAsync(chain[0].Id, "It is a goldfinch, after all.", null, Ct));
        var problems = new List<string>();
        if (!refusal.Contains(core, StringComparison.Ordinal)) problems.Add($"the refusal does not carry the core: {refusal}");
        if (!QuestionLines(receipt).Any(l => l.Contains(core, StringComparison.Ordinal))) problems.Add($"the receipt does not word the same list the same way: {string.Join(" | ", QuestionLines(receipt))}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW44 (added 2026-10-30, M3), a control. <c>curia answer</c> to a withheld parent and to an invented one: the Forum's <c>400</c> is printed in its own words
    /// (<c>curia/ingest/parent-withheld</c>, <c>curia/ingest/parent-not-found</c>), the exit is 3, the <c>POST</c> was sent (the proxy counts one), nothing is
    /// stored (SQL), no <c>question</c> line is printed, and at most one <c>GET</c> of the parent was made. Red under FRLW54, the client that refuses on its own read.
    /// </summary>
    [Fact]
    public async Task R11_67_RLW44_AParentTheForumRefusesIsRefusedInTheForumsWords()
    {
        var (scene, board, asker, answerer) = await SceneAsync("rlw44");
        using var _ = scene;
        var withheld = await scene.AskAsync(asker, board, "finch");
        await forum.WithholdAsync(withheld.Id, Ct);

        using var proxy = CountingProxy.In(forum.Client.BaseAddress!);
        var problems = new List<string>();
        foreach (var (name, parent, type) in new[] { ("a withheld parent", withheld.Id, "curia/ingest/parent-withheld"), ("an invented parent", NoSuchPost, "curia/ingest/parent-not-found") })
        {
            var submissions = proxy.Submissions;
            var readsBefore = proxy.PostReadIds.Count(id => id == parent);
            var run = await scene.CuriaAsync(CliAnswer(answerer, board, parent, proxy.Address));
            if (run.Exit != 3) problems.Add($"{name}: Expected: exit 3 Actual: exit {run.Exit} {run.Stderr.Trim()}");
            if (!(run.Stderr + run.Stdout).Contains(type, StringComparison.Ordinal)) problems.Add($"{name}: Expected: the Forum's {type} Actual: {run.Stderr.Trim()}");
            if (proxy.Submissions - submissions != 1) problems.Add($"{name}: Expected: the POST sent once Actual: {proxy.Submissions - submissions}");
            if (await scene.AnswersTo(parent) != 0) problems.Add($"{name}: an answer was stored");
            if (QuestionLines(run.Stdout).Length != 0) problems.Add($"{name}: a question line was printed beside a refusal: {run.Stdout.Trim()}");
            if (proxy.PostReadIds.Count(id => id == parent) - readsBefore > 1) problems.Add($"{name}: more than one GET of the parent");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// RLW45 (added 2026-10-30, ruling 17). For a question whose list holds two successors, from <c>curia_answer</c> and from <c>curia answer</c>, the line says FORKED,
    /// names each by post id, says that no branch was followed (R11.67's sentence) and names no latest revision; a single-successor chain in the same fact states its
    /// latest as before (the positive).
    /// </summary>
    [Fact]
    public async Task R11_67_RLW45_AForkIsStatedAsNotFollowedByBothClients()
    {
        var (scene, board, asker, answerer) = await SceneAsync("rlw45");
        using var _ = scene;
        var cliAnswerer = await scene.T1Async("cli", board);
        var (question, first, second) = await scene.ForkAsync(asker, board, "finch");
        var single = await scene.QuestionWithRevisionsAsync(asker, board, "wren", 1);

        var forked = PublishedReadersLastWords.WalkWords().Forked;
        var none = PublishedReadersLastWords.NoneFollowed();
        var tool = QuestionLines(ReadersLastWordsScene.Flatten(await answerer.Tools.AnswerAsync(question.Id, $"It is a goldfinch {Guid.NewGuid():N}.", null, Ct)));
        var cli = await scene.CuriaAsync(CliAnswer(cliAnswerer, board, question.Id, scene.Address));
        var singleLine = QuestionLines(ReadersLastWordsScene.Flatten(await answerer.Tools.AnswerAsync(single[0].Id, $"It is a wren {Guid.NewGuid():N}.", null, Ct)));

        var problems = new List<string>();
        foreach (var (who, lines) in new[] { ("curia_answer", tool), ("curia answer", QuestionLines(cli.Stdout)) })
        {
            if (lines.Length != 1) { problems.Add($"{who}: Expected: one question line Actual: {lines.Length} (exit {cli.Exit}) {cli.Stderr.Trim()}"); continue; }
            var line = lines[0];
            if (!line.Contains(forked, StringComparison.Ordinal)) problems.Add($"{who}: Expected: FORKED Actual: {line}");
            if (!line.Contains(Id(first.Id), StringComparison.Ordinal) || !line.Contains(Id(second.Id), StringComparison.Ordinal)) problems.Add($"{who}: both branches are not named: {line}");
            if (!line.Contains(none, StringComparison.Ordinal)) problems.Add($"{who}: Expected: the none-followed sentence \"{none}\" Actual: {line}");
            if (line.Contains("is the latest", StringComparison.Ordinal)) problems.Add($"{who}: a fork names a latest revision: {line}");
        }

        if (singleLine.Length != 1 || !singleLine[0].Contains(Latest, StringComparison.Ordinal) || singleLine[0].Contains(none, StringComparison.Ordinal)) problems.Add($"the control: a single-successor chain states its latest and follows it: {string.Join(" | ", singleLine)}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
