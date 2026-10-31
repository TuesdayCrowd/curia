using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// PR 3 (errata G47), CHN19, CHN20 and CHN31 against a real Forum on a real socket, through the shipped <c>curia</c> binary in a child process and an MCP
/// session over the same Forum: a revision whose author wrote another thread's question as its <c>parent</c> is accepted by the asker of the thread its chain
/// belongs to, and every reader states it as the text of the answer it revises (CHN19); <c>curia_read</c> of it is honest and of an honest revision whole
/// (CHN20); and the question that was revised twice is stated by the refusal under it and by the first block of a read of its thread (CHN31).
///
/// <para><b>Oracles.</b> The ids are the receipts of the posts the test made (the lying revisions are hand-signed with the reference client's own builder and
/// submitted through <c>ForumSession</c>, which compares nothing); the thread listing is the Forum's own <c>GET /v1/threads/{root}</c>; the statements are
/// R10.87's and R11.62's code spans, read from the errata by <see cref="PublishedChainWords"/>. A reader's output is never its own expectation.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ChainEndToEndTests(KestrelForumFixture forum) : IClassFixture<KestrelForumFixture>, IDisposable
{
    private static readonly TimeSpan Tenure = TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1);

    private readonly string _home = Directory.CreateTempSubdirectory("curia-chain-e2e-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"^(?<key>posted|digest)\s+""?(?<value>[^\s""]+)", RegexOptions.Multiline)]
    private static partial Regex Receipt();

    [GeneratedRegex(@"^post {6}""(?<id>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex FramePost();

    [GeneratedRegex(@"^#\d+ post ""(?<id>[^""]+)""", RegexOptions.Multiline)]
    private static partial Regex IndexRow();

    private string Address => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Scene(
        string Board, string Word, string AskerSlug, string Q5, string Q5Digest, string Q6, string A5, string A5Digest, string A6, string A6Digest,
        EnrolledAgent Helper, ForumTools Tools, ForumSession Session);

    private async Task<(int Exit, string Stdout, string Stderr)> CuriaAsync(params string[] args) => await CuriaProcess.RunAsync(_home, Ct, args);

    private static string ReceiptValue(string stdout, string key) =>
        Receipt().Matches(stdout).ToDictionary(m => m.Groups["key"].Value, m => m.Groups["value"].Value)[key];

    private static string Flatten(CallToolResult result) =>
        string.Join('\n', result.Content.Select(b => b switch { TextContentBlock t => t.Text, EmbeddedResourceBlock { Resource: TextResourceContents r } => r.Text, _ => string.Empty }));

    private static string PostedId(string receipt) =>
        JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

    private static string PostedDigest(string receipt) => ReceiptValue(receipt, "digest");

    /// <summary>Q5 asked by the asker, Q6 asked by another agent, two answers to Q5 by a T1 helper whose writer is a tool and whose session can hand-sign.</summary>
    private async Task<Scene> BuildAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var board = "board-" + suffix;
        var word = "quartzfinch" + suffix;

        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now - Tenure);
        var store = new ProfileStore(_home);
        Assert.True(store.Create("helper-" + suffix, $"https://agents.example/helper-{suffix}", $"helper-{suffix}-k", forum.Client.BaseAddress!).TryGetValue(out var helper, out var created), created?.Detail);
        _owned.Add(helper!);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        Assert.True((await client.EnrolAsync(helper!, Ct)).TryGetValue(out _, out var refusal), refusal?.Summary);
        await forum.AttestOwnerAsync(helper!.Profile.AgentId, Ct, owner: "owner:" + helper.Profile.Slug);

        var session = new ForumSession(client, helper, store, forum.Clock);
        var tools = new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(helper, session, forum.Clock));
        for (var i = 0; i < TierPolicy.T1MinimumCleanQuestions; i++)
            Assert.StartsWith("POSTED", Flatten(await tools.AskAsync(board, $"Warmup {i} {Guid.NewGuid():N}", $"warmup body {i} {Guid.NewGuid():N}", null, null, Ct)), StringComparison.Ordinal);
        forum.Clock.Advance(DateTimeOffset.UtcNow - forum.Now);

        var asker = "asker-" + suffix;
        Assert.Equal(0, (await CuriaAsync("enrol", "--agent", asker, "--forum", Address)).Exit);
        var asked = await CuriaAsync("ask", "--agent", asker, "--board", board, "--title", $"Which {word} is it", "--body", $"A question about the {word}.", "--forum", Address);
        Assert.True(asked.Exit == 0, asked.Stdout + asked.Stderr);

        var other = "other-" + suffix;
        Assert.Equal(0, (await CuriaAsync("enrol", "--agent", other, "--forum", Address)).Exit);
        var askedOther = await CuriaAsync("ask", "--agent", other, "--board", board, "--title", $"Why does the finch sing {suffix}", "--body", "A question about night singing.", "--forum", Address);
        Assert.True(askedOther.Exit == 0, askedOther.Stdout + askedOther.Stderr);

        var q5 = ReceiptValue(asked.Stdout, "posted");
        var first = Flatten(await tools.AnswerAsync(q5, $"The first answer about the {word}.", null, Ct));
        var second = Flatten(await tools.AnswerAsync(q5, $"The second answer about the {word}.", null, Ct));
        Assert.StartsWith("POSTED", first, StringComparison.Ordinal);
        Assert.StartsWith("POSTED", second, StringComparison.Ordinal);
        return new Scene(board, word, asker, q5, ReceiptValue(asked.Stdout, "digest"), ReceiptValue(askedOther.Stdout, "posted"), PostedId(first), PostedDigest(first), PostedId(second), PostedDigest(second), helper, tools, session);
    }

    /// <summary>A revision the helper signs by hand, whose <c>parent</c> is the author's word and nothing the Forum compares.</summary>
    private async Task<string> SignedRevisionAsync(Scene scene, string prevDigest, string parent, string body)
    {
        var draft = new PostDraft { Kind = PostKind.Revision, Board = scene.Board, Parent = parent, Prev = prevDigest, Body = body };
        Assert.True(SubmissionBuilder.Build(scene.Helper, draft, forum.Now).TryGetValue(out var signed, out var error), error?.Detail);
        var posted = await scene.Session.SubmitAsync(signed!.Wire, MarkingMode.None, Ct);
        Assert.True(posted.TryGetValue(out var receipt, out var refusal), refusal?.Summary);
        return receipt!.PostId;
    }

    private async Task<McpProtocolSession> SessionAsAsync(string slug)
    {
        Assert.True(new ProfileStore(_home).Load(slug).TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent!, new ProfileStore(_home), forum.Clock);
        return await McpProtocolSession.StartAsync(
            new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent!, session, forum.Clock)), agent!.Profile.AgentId);
    }

    /// <summary>The chunk of <paramref name="output"/> that belongs to <paramref name="id"/>: from its start-of-post line to the next one.</summary>
    private static string ChunkOf(string output, Regex start, string id)
    {
        var starts = start.Matches(output);
        for (var i = 0; i < starts.Count; i++)
            if (starts[i].Groups["id"].Value == id)
                return output[starts[i].Index..(i + 1 < starts.Count ? starts[i + 1].Index : output.Length)];

        throw new Xunit.Sdk.XunitException($"the output holds no passage for {id}:\n{output}");
    }

    private static void AssertStatesTheRevision(string form, string chunk, string q6)
    {
        var statement = PublishedChainWords.RevisionStatement();
        var forbidden = PublishedChainWords.RevisionForbidden();
        var problems = new List<string>();
        if (!chunk.Contains(statement, StringComparison.Ordinal)) problems.Add("lacks R10.87's statement");
        if (chunk.Contains(forbidden, StringComparison.Ordinal)) problems.Add($"says \"{forbidden}\" of it");
        if (!chunk.Contains(q6, StringComparison.Ordinal)) problems.Add("no longer prints the revision's parent, as the author's word");
        Assert.True(problems.Count == 0, $"{form}: {string.Join("; ", problems)}. Actual: {chunk}");
    }

    /// <summary>
    /// CHN19. LP1, a revision of Q5's answer whose <c>parent</c> is Q6, is accepted by Q5's asker. <c>curia read</c>, <c>curia thread</c>, <c>curia search --titles</c>
    /// and <c>curia_search</c> state it as the text of the answer it revises, none says <c>the thread's asker</c> of it, and the <c>parent</c> is still printed as
    /// the author's word.
    /// </summary>
    [Fact]
    public async Task R10_87_CHN19_EveryReaderStatesTheAcceptedRevisionAsTheTextOfItsAnswer()
    {
        var scene = await BuildAsync();
        var lp1 = await SignedRevisionAsync(scene, scene.A5Digest, scene.Q6, $"A corrected first answer about the {scene.Word}.");
        var resolved = await CuriaAsync("resolve", lp1, "--agent", scene.AskerSlug, "--forum", Address);
        Assert.True(resolved.Exit == 0, resolved.Stdout + resolved.Stderr);

        var asker = new[] { "--agent", scene.AskerSlug, "--forum", Address };
        var read = await CuriaAsync(["read", lp1, .. asker]);
        var thread = await CuriaAsync(["thread", scene.Q5, .. asker]);
        var titled = await CuriaAsync(["search", scene.Word, "--board", scene.Board, "--titles", .. asker]);
        Assert.All(new[] { read, thread, titled }, run => Assert.True(run.Exit == 0, run.Stdout + run.Stderr));
        var hits = await scene.Tools.SearchAsync(new SearchCriteria { Query = scene.Word, Board = scene.Board }, Ct);

        AssertStatesTheRevision("curia read", read.Stdout, scene.Q6);
        AssertStatesTheRevision("curia thread", ChunkOf(thread.Stdout, FramePost(), lp1), scene.Q6);
        AssertStatesTheRevision("curia search --titles", ChunkOf(titled.Stdout, IndexRow(), lp1), scene.Q6);
        var hit = hits.Content.OfType<EmbeddedResourceBlock>().Select(b => ((TextResourceContents)b.Resource).Text).SingleOrDefault(t => FramePost().Match(t) is { Success: true } m && m.Groups["id"].Value == lp1);
        Assert.True(hit is not null, "curia_search did not return LP1");
        AssertStatesTheRevision("curia_search", hit!, scene.Q6);
    }

    /// <summary>
    /// CHN20 (its object changed by errata G50, R11.66: the line used to be <c>NOT READ</c> and "did not include", which said "could not be read" of a thread that
    /// read fine). <c>curia_read</c> of LP1 (a <c>parent</c> that lies) is honest: a <c>NOT LISTED</c> line saying the thread was read and does not list the post, the post
    /// alone, and no <c>thread root=</c>; of LP2 (a <c>parent</c> naming no post) the existing <c>CUT</c> line; of an honest revision <c>thread root=</c> Q5 with
    /// every member the Forum's own listing holds.
    /// </summary>
    [Fact]
    public async Task R10_87_CHN20_CuriaReadOfALyingRevisionIsHonestAndOfAnHonestOneWhole()
    {
        var scene = await BuildAsync();
        var lp1 = await SignedRevisionAsync(scene, scene.A5Digest, scene.Q6, $"A lying first answer about the {scene.Word}.");
        var honest = await SignedRevisionAsync(scene, scene.A6Digest, scene.A6, $"An honest second answer about the {scene.Word}.");
        var commented = Flatten(await scene.Tools.CommentAsync(scene.A6, "A comment to be revised.", null, Ct));
        var lp2 = await SignedRevisionAsync(scene, PostedDigest(commented), "01M6FFFF000000000000000000", "A revision of a comment whose parent names no post.");

        await using var session = await SessionAsAsync(scene.AskerSlug);
        var lying = await session.CallAsync("curia_read", new() { ["postId"] = lp1 });
        var cut = await session.CallAsync("curia_read", new() { ["postId"] = lp2 });
        var whole = await session.CallAsync("curia_read", new() { ["postId"] = honest });
        var members = (await forum.Client.GetFromJsonAsync<JsonElement>($"{Address}/v1/threads/{scene.Q5}", Ct)).EnumerateArray().Select(p => p.GetProperty("post_id").GetString()!).ToArray();

        var problems = new List<string>();
        if (!lying.Blocks[0].Contains("NOT LISTED", StringComparison.Ordinal) || !lying.Blocks[0].Contains("does not list the post", StringComparison.Ordinal)) problems.Add($"LP1: the first block does not say NOT LISTED and why: {lying.Blocks[0]}");
        if (lying.Blocks[0].Contains("thread root=", StringComparison.Ordinal)) problems.Add($"LP1: a thread is presented as the post's: {lying.Blocks[0]}");
        if (lying.Blocks.Count != 2) problems.Add($"LP1: {lying.Blocks.Count - 1} passage(s), not the post alone");
        if (!cut.Blocks[0].Contains("CUT", StringComparison.Ordinal)) problems.Add($"LP2: no CUT line: {cut.Blocks[0]}");
        if (!whole.Blocks[0].StartsWith($"thread root={System.Text.Json.JsonSerializer.Serialize(scene.Q5)}", StringComparison.Ordinal) || whole.Blocks.Count - 1 != members.Length)
            problems.Add($"the honest revision: expected thread root=Q5 with {members.Length} members; the first block is {whole.Blocks[0]} and {whole.Blocks.Count - 1} passage(s) follow");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static async Task<string> RefusalAsync(Scene scene, string parentId)
    {
        try
        {
            return "no refusal: " + Flatten(await scene.Tools.AnswerAsync(parentId, "It is a goldfinch.", null, Ct));
        }
        catch (McpException refused)
        {
            return refused.Message;
        }
    }

    /// <summary>
    /// CHN31, the R6 scenario against the real Forum: a question revised twice (Q to r1 to r2) with an answer to the original. <c>curia_answer</c> under r2 and
    /// under r1 states the question's standing and the named revision's with no hedge; the answer sent to Q keeps its receipt's <c>question</c> line; and
    /// <c>curia_read</c> of r2 and of Q gives a first block that says which text stands.
    /// </summary>
    [Fact]
    public async Task R11_62_CHN31_TheQuestionThatWasRevisedTwiceIsStatedByTheRefusalAndTheFirstBlock()
    {
        var scene = await BuildAsync();
        var r1 = await CuriaAsync("revision", "--agent", scene.AskerSlug, "--board", scene.Board, "--parent", scene.Q5, "--body", $"A corrected question about the {scene.Word}.", "--forum", Address);
        Assert.True(r1.Exit == 0, r1.Stdout + r1.Stderr);
        var r1Id = ReceiptValue(r1.Stdout, "posted");
        var r2 = await CuriaAsync("revision", "--agent", scene.AskerSlug, "--board", scene.Board, "--parent", r1Id, "--body", $"A corrected corrected question about the {scene.Word}.", "--forum", Address);
        Assert.True(r2.Exit == 0, r2.Stdout + r2.Stderr);
        var r2Id = ReceiptValue(r2.Stdout, "posted");
        var (hedge, named, stands) = PublishedChainWords.RefusalAndBlock();
        var latest = PublishedChainWords.RefusalPhrase("latest the Forum reports");
        string Lit(string id) => DisplayLiteral(id);

        var underR2 = await RefusalAsync(scene, r2Id);
        var underR1 = await RefusalAsync(scene, r1Id);
        var toQ = Flatten(await scene.Tools.AnswerAsync(scene.Q5, "It is a goldfinch, after all.", null, Ct));
        var readR2 = Flatten(await scene.Tools.ReadAsync(r2Id, Ct)).Split('\n');
        var readQ = Flatten(await scene.Tools.ReadAsync(scene.Q5, Ct)).Split('\n');

        var problems = new List<string>();
        if (!underR2.Contains(Lit(scene.Q5), StringComparison.Ordinal) || !underR2.Contains(Lit(r1Id), StringComparison.Ordinal) || !underR2.Contains("SUPERSEDED", StringComparison.Ordinal)) problems.Add("the refusal under r2 does not state that Q's superseded_by lists r1");
        if (!underR2.Contains(named, StringComparison.Ordinal) || !underR2.Contains(latest, StringComparison.Ordinal)) problems.Add("the refusal under r2 does not say r2 is the latest the Forum reports");
        if (!underR1.Contains(Lit(r2Id), StringComparison.Ordinal) || underR1.Contains(latest, StringComparison.Ordinal)) problems.Add("the refusal under r1 does not name r2 as superseding it, or says r1 is the latest");
        if (underR2.Contains(hedge, StringComparison.Ordinal) || underR1.Contains(hedge, StringComparison.Ordinal)) problems.Add("a refusal carries the hedge");
        var receiptLine = toQ.Split('\n').FirstOrDefault(l => l.StartsWith("question    ", StringComparison.Ordinal)) ?? string.Empty;
        // Errata G50 (R11.67) changes this fact's object: the receipt used to name one hop (r1 only), which was the defect; it now walks down and names both.
        if (!receiptLine.Contains(Lit(r1Id), StringComparison.Ordinal) || !receiptLine.Contains(Lit(r2Id), StringComparison.Ordinal) || !receiptLine.Contains("this answer went to the question as named", StringComparison.Ordinal))
            problems.Add($"the answer sent to Q does not state the receipt's question line naming both hops (r1 and r2): {receiptLine}");
        foreach (var (name, lines) in new[] { ("a read of r2", readR2), ("a read of Q", readQ) })
        {
            var accepted = Array.FindIndex(lines, l => l.StartsWith("accepted ", StringComparison.Ordinal));
            var line = accepted >= 0 && accepted + 1 < lines.Length ? lines[accepted + 1] : string.Empty;
            if (!line.StartsWith("revision", StringComparison.Ordinal) || !line.Contains(stands, StringComparison.Ordinal) || !line.Contains(Lit(r2Id), StringComparison.Ordinal))
                problems.Add($"{name}: the first block does not say {stands} is r2: {line}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static string DisplayLiteral(string value) => Curia.Canon.Json.DisplayLiteral.Of(value);
}
