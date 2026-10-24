using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R14.11 (errata G27) against a real Forum: <c>curia-mcp</c>'s tools called through <c>tools/call</c> over the
/// protocol, on a server built by <see cref="ServerComposition.Options"/>, and held to the Forum's own REST documents
/// for the same request (ruling 8d), never to the adapter's walk or output. The stub cannot settle these rows: it
/// serves one thread, one cursor and one floor.
///
/// <para><b>Tokens these facts pin for the stage that fixes them.</b> A contract refusal begins
/// <c>NOT SENT: this call does not match</c>; a search result's adapter block holds <c>next_cursor="..."</c> or
/// <c>no_further_pages</c>; <c>curia_read</c>'s first block names the thread root on a line beginning <c>thread</c>,
/// says <c>CUT</c> and names the unreadable parent when the walk stopped, and <c>NOT READ</c> when the thread fetch
/// failed.</para>
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class McpToolContractEndToEndTests(ForumFixture forum) : IClassFixture<ForumFixture>, IDisposable
{
    private const string PostScheme = "curia://post/";

    /// <summary>R10.2 (revised): the kinds a verification floor is scoped to.</summary>
    private static readonly string[] FloorKinds = ["answer", "finding"];

    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-contract-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    // ---- P16 to P18: curia_read names and reads the whole thread (R11.38) --------------------------------------------

    /// <summary>A question, a comment, a comment on it, by three agents; the read of the last is the Forum's own thread.</summary>
    [Fact]
    public async Task R11_38_AReplyToAReplyIsReadWithItsWholeThread()
    {
        var board = Board();
        var chain = await ChainAsync(board, depth: 3);

        await AssertReadsWholeThreadAsync(chain, chain[^1]);
    }

    [Fact]
    public async Task R11_38_AThreadFourLevelsDeepIsReadWhole()
    {
        var board = Board();
        var chain = await ChainAsync(board, depth: 4);

        await AssertReadsWholeThreadAsync(chain, chain[^1]);
    }

    /// <summary>R11.38: a walk that stops at a post the Forum will not serve says so, naming the parent it could not read.</summary>
    [Fact]
    public async Task R11_38_AThreadAboveAWithheldAncestorIsSaidToBeCut()
    {
        var board = Board();
        var chain = await ChainAsync(board, depth: 3);
        var (question, middle, leaf) = (chain[0].PostId, chain[1].PostId, chain[2].PostId);

        await forum.WithholdAsync(middle, Ct);
        using var withheld = await forum.Client.GetAsync(new Uri($"/v1/posts/{middle}", UriKind.Relative), Ct);
        Assert.False(withheld.IsSuccessStatusCode, "the fixture's withheld post is still served, so the walk has nothing to stop at");

        await using var session = await SessionAsync(chain[2].Agent);
        var answer = await session.CallAsync("curia_read", new() { ["postId"] = leaf });

        Assert.False(answer.IsError, answer.Text);
        Assert.Contains(IdsOf(answer), id => id == leaf);
        var first = answer.Blocks[0];
        Assert.True(answer.Uris[0] is null && first.Split('\n').Any(l => l.StartsWith("CUT", StringComparison.Ordinal)),
            $"the model was not told, on a line beginning CUT, that the thread was cut above a post the Forum would not serve:\n{first}");
        Assert.Contains(Quoted(middle), first, StringComparison.Ordinal);
        Assert.DoesNotContain(Quoted(question), first, StringComparison.Ordinal);
    }

    // ---- P19, P20: cursor and why_ranked through the protocol (R11.39) -----------------------------------------------

    [Fact]
    public async Task R11_39_AnAgentPagesThroughSearchResultsWithTheCursorsItWasGiven()
    {
        var board = Board();
        var agent = await EnrolAsync("pager");
        var asked = new List<string>();
        foreach (var topic in new[] { "alpha", "bravo", "charlie" })
            asked.Add(await AskAsync(agent, board, $"Paging {topic} {Guid.NewGuid():N}", $"A question about {topic} for the paging fact {Guid.NewGuid():N}."));

        var oracle = await ForumSearchAsync($"board={board}&limit=100");
        var expected = oracle.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(asked.Order(StringComparer.Ordinal), expected);

        await using var session = await SessionAsync(agent);
        var seen = new List<string>();
        string? cursor = null;
        var last = string.Empty;
        for (var page = 0; page < 6; page++)
        {
            var criteria = new Dictionary<string, object?> { ["board"] = board, ["limit"] = 1 };
            if (cursor is not null) criteria["cursor"] = cursor;
            var answer = await session.CallAsync("curia_search", new() { ["criteria"] = criteria });
            Assert.False(answer.IsError, answer.Text);
            seen.AddRange(IdsOf(answer));

            Assert.True(answer.Uris[0] is null, $"page {page + 1}: the first block is a passage, not the adapter's own words:\n{answer.Blocks[0]}");
            last = answer.Blocks[0];
            var match = Regex.Match(last, "next_cursor=(\"(?:[^\"\\\\]|\\\\.)*\")");
            if (!match.Success)
            {
                Assert.True(
                    seen.Count == expected.Length,
                    $"no next_cursor in the adapter's text after page {page + 1}, with {seen.Count} of the Forum's {expected.Length} posts read:\n{last}");
                break;
            }

            cursor = JsonSerializer.Deserialize<string>(match.Groups[1].Value);
        }

        Assert.Equal(expected.Length, seen.Count);
        Assert.Equal(expected, seen.Order(StringComparer.Ordinal));
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("no_further_pages", last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_39_WhyRankedIsRenderedWhenAskedAndAbsentWhenNot()
    {
        var board = Board();
        var term = "zwhy" + Guid.NewGuid().ToString("N")[..10];
        var agent = await EnrolAsync("why");
        await AskAsync(agent, board, $"First {term} {Guid.NewGuid():N}", $"The first question mentions {term} plainly.");
        await AskAsync(agent, board, $"Second {term} {Guid.NewGuid():N}", $"The second question also mentions {term}, differently.");

        var oracle = await ForumSearchAsync($"q={term}&board={board}&why=true");
        var hits = oracle.GetProperty("results").EnumerateArray().ToArray();
        Assert.NotEmpty(hits);

        await using var session = await SessionAsync(agent);
        var asked = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = term, ["board"] = board, ["why_ranked"] = true } });
        Assert.False(asked.IsError, asked.Text);
        var block = asked.Blocks[0];
        Assert.True(asked.Uris[0] is null, "the first block is a passage, not the adapter's own");

        var quotedIds = hits.Select(h => Quoted(h.GetProperty("post").GetProperty("post_id").GetString()!)).ToArray();
        var compared = 0;
        foreach (var hit in hits)
        {
            var id = hit.GetProperty("post").GetProperty("post_id").GetString()!;
            var why = hit.GetProperty("why_ranked");
            var at = block.IndexOf(Quoted(id), StringComparison.Ordinal);
            Assert.True(at >= 0, $"the adapter's block does not name {id} beside its why_ranked terms:\n{block}");

            // Only this hit's own span: up to the next hit's quoted id, or the block's end.
            var next = quotedIds.Select(q => block.IndexOf(q, at + 1, StringComparison.Ordinal)).Where(i => i > at).DefaultIfEmpty(block.Length).Min();
            var tail = block[at..next];
            if (why.TryGetProperty("lexical", out var lexical))
            {
                Assert.Contains($"lexical rank {lexical.GetProperty("rank").GetInt32()}", tail, StringComparison.Ordinal);
                compared++;
            }

            if (why.TryGetProperty("vector", out var vector))
            {
                Assert.Contains($"cosine {vector.GetProperty("cosine_bp").GetInt32()} bp", tail, StringComparison.Ordinal);
                compared++;
            }
        }

        Assert.True(compared > 0, "no why_ranked row was compared: a zero is a defect in this test, not a clean adapter");

        var unasked = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?> { ["query"] = term, ["board"] = board } });
        Assert.False(unasked.IsError, unasked.Text);
        Assert.DoesNotContain("why_ranked", unasked.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("cosine", unasked.Blocks[0], StringComparison.Ordinal);
    }

    // ---- P21: a floor set through the record (R11.37) ----------------------------------------------------------------

    [Fact]
    public async Task R11_37_AFloorSetThroughTheRecordIsAppliedAndStated()
    {
        var board = Board();
        var agent = await EnrolAsync("floor");
        await AskAsync(agent, board, $"Floor {Guid.NewGuid():N}", "A question on a board the floor fact searches.");

        var oracle = await ForumSearchAsync($"board={board}&kind=answer,finding&min_verification=V2");
        var floor = oracle.GetProperty("floor");
        Assert.Equal("requested", floor.GetProperty("source").GetString());
        Assert.Equal("V2", floor.GetProperty("min_verification").GetString());

        await using var session = await SessionAsync(agent);
        var answer = await session.CallAsync("curia_search", new()
        {
            ["criteria"] = new Dictionary<string, object?> { ["min_verification"] = "V2", ["kinds"] = FloorKinds, ["board"] = board },
        });

        Assert.False(answer.IsError, answer.Text);
        var firstLine = answer.Blocks[0].Split('\n')[0];
        Assert.True(
            firstLine.Contains("source=\"requested\"", StringComparison.Ordinal) && firstLine.Contains("min_verification=\"V2\"", StringComparison.Ordinal),
            $"the floor the agent set was not applied and stated; the Forum says source=requested, min_verification=V2, and the first line reads:\n{firstLine}");
    }

    // ---- P22: a tool called with only what it needs (R11.37) ---------------------------------------------------------

    [Fact]
    public async Task R11_37_AToolCalledWithOnlyWhatItNeedsWorksAgainstARealForum()
    {
        var board = Board();
        var asker = await EnrolAsync("needs-ask");
        await using var asking = await SessionAsync(asker);

        // Controls: every member the schema lists, each inert. The omission calls below are attributable to the omission
        // only if these are posted.
        var controlAsk = await asking.CallAsync("curia_ask", new()
        {
            ["board"] = board,
            ["title"] = $"Control {Guid.NewGuid():N}",
            ["body"] = "A question carrying every member curia_ask lists.",
            ["tags"] = Array.Empty<string>(),
            ["notDuplicateRationale"] = "",
        });
        Assert.True(!controlAsk.IsError && controlAsk.Text.StartsWith("POSTED", StringComparison.Ordinal), $"the control curia_ask (every member, inert) was not posted:\n{controlAsk.Text}");
        var controlComment = await asking.CallAsync("curia_comment", new()
        {
            ["parentPostId"] = ReceiptPostId(controlAsk.Text),
            ["body"] = "A comment carrying every member curia_comment lists.",
            ["refs"] = Array.Empty<string>(),
        });
        Assert.True(!controlComment.IsError && controlComment.Text.StartsWith("POSTED", StringComparison.Ordinal), $"the control curia_comment (every member, inert) was not posted:\n{controlComment.Text}");

        var asked = await asking.CallAsync("curia_ask", new() { ["board"] = board, ["title"] = $"Needs {Guid.NewGuid():N}", ["body"] = "A question carrying only what curia_ask needs." });
        Assert.False(asked.IsError, asked.Text);
        Assert.StartsWith("POSTED", asked.Text, StringComparison.Ordinal);
        var questionId = ReceiptPostId(asked.Text);
        AssertNoRefs(await CanonicalOfAsync(questionId));

        var commenter = await EnrolAsync("needs-comment");
        await using var commenting = await SessionAsync(commenter);
        var commented = await commenting.CallAsync("curia_comment", new() { ["parentPostId"] = questionId, ["body"] = "A comment carrying only what curia_comment needs." });
        Assert.False(commented.IsError, commented.Text);
        Assert.StartsWith("POSTED", commented.Text, StringComparison.Ordinal);
        var canonical = await CanonicalOfAsync(ReceiptPostId(commented.Text));
        Assert.Equal(questionId, canonical.GetProperty("parent").GetString());
        AssertNoRefs(canonical);
    }

    // ---- R11.42, R11.43 (errata G29) against a real Forum ------------------------------------------------------------

    /// <summary>The digest of a served document's canonical form, computed here with the BCL and not read from the Forum's <c>digest</c> member.</summary>
    private async Task<string> DigestOfServedAsync(string postId)
    {
        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", Ct);
        return "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(served.GetProperty("canonical").GetString()!)));
    }

    /// <summary>R11.42: a real Forum, a pin that is another real post's digest: an error, both digests, and no check line.</summary>
    [Fact]
    public async Task R11_42_ARealForumsPinnedVerifyFailsWithoutAVerdict()
    {
        var board = Board();
        var agent = await EnrolAsync("pin");
        var named = await AskAsync(agent, board, $"Named {Guid.NewGuid():N}", "The post the caller names.");
        var other = await AskAsync(agent, board, $"Other {Guid.NewGuid():N}", "A different post the caller holds the digest of.");
        var wrongDigest = await DigestOfServedAsync(other);
        var rightDigest = await DigestOfServedAsync(named);
        Assert.NotEqual(rightDigest, wrongDigest);

        await using var session = await SessionAsync(agent);
        var control = await session.CallAsync("curia_verify", new() { ["postId"] = named, ["expectedDigest"] = rightDigest });
        Assert.False(control.IsError, $"the control (the right digest) was an error:\n{control.Text}");
        Assert.Contains("signature   ", control.Text, StringComparison.Ordinal);

        var failed = await session.CallAsync("curia_verify", new() { ["postId"] = named, ["expectedDigest"] = wrongDigest });

        Assert.True(failed.IsError, $"a failed pin answered isError=false:\n{failed.Text}");
        Assert.Contains(Quoted(wrongDigest), failed.Text, StringComparison.Ordinal);
        Assert.Contains(Quoted(rightDigest), failed.Text, StringComparison.Ordinal);
        foreach (var label in new[] { "signature   ", "inclusion   ", "consistency ", "key         ", "lifecycle   " })
            Assert.DoesNotContain(failed.Text.Split('\n'), line => line.StartsWith(label, StringComparison.Ordinal));
        Assert.DoesNotContain(failed.Text.Split('\n'), line => line.StartsWith("VERIFIED", StringComparison.Ordinal));

        // Label-independent: no check content of the control's rendering appears, and the failed result is shorter.
        var blocks = control.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.True(blocks.Length >= 3, $"the control's rendering has {blocks.Length} blocks:\n{control.Text}");
        var contents = blocks[1].Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l[l.IndexOf(' ', StringComparison.Ordinal)..].TrimStart())
            .Append(blocks[2])
            .Select(c => c.Length > 40 ? c[..40] : c)
            .ToArray();
        Assert.Equal(6, contents.Length);
        var leaked = contents.Where(c => failed.Text.Contains(c, StringComparison.Ordinal)).ToArray();
        Assert.True(leaked.Length == 0, $"the failed pin's result still holds check content ({string.Join(" | ", leaked)}):\n{failed.Text}");
        Assert.True(
            failed.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length < control.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length,
            $"the failed result is no shorter than the control's:\n{failed.Text}");
    }

    /// <summary>R11.43: a null item of <c>refs</c> posts nothing, and the Forum's own thread document is the same after the call.</summary>
    [Fact]
    public async Task R11_43_ACommentWithANullRefsItemPostsNothing()
    {
        var board = Board();
        var agent = await EnrolAsync("nullref");
        var question = await AskAsync(agent, board, $"Null ref {Guid.NewGuid():N}", "A question to comment under.");
        var before = await forum.Client.GetStringAsync($"/v1/threads/{question}", Ct);

        await using var session = await SessionAsync(agent);
        var result = await session.CallAsync("curia_comment", new() { ["parentPostId"] = question, ["body"] = "A comment.", ["refs"] = new object?[] { null } });

        Assert.True(
            result.IsError && result.Text.StartsWith("NOT SENT:", StringComparison.Ordinal) && result.Text.Contains("refs[0]", StringComparison.Ordinal),
            $"a null refs item did not answer a NOT SENT naming refs[0]:\n{result.Text}");
        Assert.Equal(before, await forum.Client.GetStringAsync($"/v1/threads/{question}", Ct));
    }

    // ---- P23: no description promises what the Forum does not serve (R9.24 (rev.)) -----------------------------------

    [Fact]
    public async Task R9_24_NoDescriptionPromisesARemovalCountTheForumDoesNotServe()
    {
        var board = Board();
        var agent = await EnrolAsync("desc");
        await AskAsync(agent, board, $"Description {Guid.NewGuid():N}", "A question so the search answers a floor block.");

        var floor = (await ForumSearchAsync($"board={board}")).GetProperty("floor");
        var members = floor.EnumerateObject().Select(m => m.Name).ToArray();
        Assert.NotEmpty(members);

        // excluded_disputed counts R6.57's disputed posts alone; R9.24 (rev.)'s count is per criterion.
        var serves = members.Any(m => m != "excluded_disputed" && Regex.IsMatch(m, "remov|dropp|exclud|filtered", RegexOptions.IgnoreCase));

        await using var session = await SessionAsync(agent);
        var description = (await session.ListAsync()).Single(t => t.Name == "curia_search").Description ?? string.Empty;
        Assert.NotEmpty(description);
        var claims = Regex.IsMatch(description, "how many[^.]{0,80}remov|remov[^.]{0,80}how many", RegexOptions.IgnoreCase);

        Assert.True(
            claims == serves,
            $"R9.24 (rev.): curia_search's description {(claims ? "promises" : "does not promise")} a count of removed results; the Forum's floor block {(serves ? "serves" : "serves none")} (members: {string.Join(", ", members)}).\n{description}");
    }

    // ---- shared steps ------------------------------------------------------------------------------------------------

    private async Task AssertReadsWholeThreadAsync((string PostId, EnrolledAgent Agent)[] chain, (string PostId, EnrolledAgent Agent) from)
    {
        var root = chain[0].PostId;
        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/threads/{root}", Ct);
        var oracle = served.EnumerateArray().Select(p => p.GetProperty("post_id").GetString()!).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(chain.Length, oracle.Length);

        await using var session = await SessionAsync(from.Agent);
        var answer = await session.CallAsync("curia_read", new() { ["postId"] = from.PostId });

        Assert.False(answer.IsError, answer.Text);
        var read = IdsOf(answer).Order(StringComparer.Ordinal).ToArray();
        Assert.True(
            oracle.SequenceEqual(read),
            $"curia_read of {from.PostId} served {read.Length} of the {oracle.Length} posts GET /v1/threads/{root} serves.\nForum: {string.Join(", ", oracle)}\nRead:  {string.Join(", ", read)}");
        var first = answer.Blocks[0];
        var threadLine = first.Split('\n').FirstOrDefault(l => l.StartsWith("thread", StringComparison.Ordinal));
        Assert.True(answer.Uris[0] is null && threadLine is not null && threadLine.Contains(Quoted(root), StringComparison.Ordinal),
            $"the first block does not name the thread's root {root} on a line beginning 'thread':\n{first}");
    }

    /// <summary>A post per level, each by its own agent, each a comment on the one before; the first is the question.</summary>
    private async Task<(string PostId, EnrolledAgent Agent)[]> ChainAsync(string board, int depth)
    {
        var chain = new List<(string, EnrolledAgent)>();
        var asker = await EnrolAsync("thread-asker");
        chain.Add((await AskAsync(asker, board, $"Thread {Guid.NewGuid():N}", "The question at the root of the thread."), asker));
        for (var level = 1; level < depth; level++)
        {
            var author = await EnrolAsync($"thread-{level}");
            var receipt = Flatten(await ToolsFor(author).CommentAsync(chain[^1].Item1, $"A reply at level {level} to the post above.", null, Ct));
            Assert.StartsWith("POSTED", receipt, StringComparison.Ordinal);
            chain.Add((ReceiptPostId(receipt), author));
        }

        return [.. chain];
    }

    private async Task<string> AskAsync(EnrolledAgent agent, string board, string title, string body)
    {
        var receipt = Flatten(await ToolsFor(agent).AskAsync(board, title, body, null, null, Ct));
        Assert.StartsWith("POSTED", receipt, StringComparison.Ordinal);
        return ReceiptPostId(receipt);
    }

    private async Task<McpProtocolSession> SessionAsync(EnrolledAgent agent) =>
        await McpProtocolSession.StartAsync(ToolsFor(agent), agent.Profile.AgentId);

    private ForumTools ToolsFor(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_home), forum.Clock);
        return new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock));
    }

    private async Task<EnrolledAgent> EnrolAsync(string stem)
    {
        var slug = $"{stem}-{Guid.NewGuid().ToString("N")[..8]}";
        Assert.True(
            new ProfileStore(_home).Create(slug, $"https://agents.example/{slug}", $"{slug}-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error),
            error?.Detail);
        _owned.Add(agent!);

        var enrolled = await new ForumClient(forum.Client, forum.Client.BaseAddress!).EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var refusal), refusal?.Summary);
        return agent!;
    }

    /// <summary>The Forum's own search document for a query string (ruling 8d's oracle).</summary>
    private async Task<JsonElement> ForumSearchAsync(string query) =>
        await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/search?{query}", Ct);

    private async Task<JsonElement> CanonicalOfAsync(string postId)
    {
        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", Ct);
        return JsonDocument.Parse(served.GetProperty("canonical").GetString()!).RootElement.Clone();
    }

    private static void AssertNoRefs(JsonElement canonical)
    {
        if (canonical.TryGetProperty("refs", out var refs))
            Assert.Equal(0, refs.GetArrayLength());
    }

    /// <summary>The ids of the posts a result served, from the embedded resources' URIs.</summary>
    private static string[] IdsOf(McpAnswer answer) =>
        [.. answer.Uris.Where(u => u is not null).Select(u => Uri.UnescapeDataString(u![PostScheme.Length..]))];

    /// <summary>The id a receipt printed as a display literal (R10.63), recovered by a JSON parser.</summary>
    private static string ReceiptPostId(string receipt) =>
        JsonSerializer.Deserialize<string>(receipt.Split('\n').First(l => l.StartsWith("post ", StringComparison.Ordinal))["post".Length..].Trim())!;

    private static string Quoted(string value) => JsonSerializer.Serialize(value);

    private static string Flatten(CallToolResult result)
    {
        var builder = new StringBuilder();
        foreach (var block in result.Content)
        {
            switch (block)
            {
                case TextContentBlock text:
                    builder.AppendLine(text.Text);
                    break;
                case EmbeddedResourceBlock { Resource: TextResourceContents resource }:
                    builder.AppendLine(resource.Text);
                    break;
                default:
                    break;
            }
        }

        return builder.ToString();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Board() => "mcp-" + Guid.NewGuid().ToString("N")[..8];
}
