using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R8.73, R8.74 and R9.45 (errata G43) end to end: every tag the Forum admits is found by the spelling its author typed, on two routes
/// (<c>GET /v1/search?tags=</c> and <c>curia_search</c>) and, for the filter's NFC reading, on <c>GET /v1/inbox?tags=</c> too; a tag the
/// filter cannot name is refused <c>400</c> <c>curia/content/tag-unfilterable</c> before it is screened or stored; and the Forum holds no URL
/// check, so an <c>HTTPS://</c> reference signed by hand is accepted as typed.
///
/// <para><b>Oracles.</b> Everything is hand-signed by <see cref="ForumAgent"/> (so the client's own rules do not mask the Forum's) or posted by
/// <c>curia_ask</c>; what is found is read from the route's result list, by the post id the Forum returned at write, never from the writer's
/// report. The events count is the table's (<c>SELECT count(*)</c>), the type is parsed from the errata, and every spelling is built at run time
/// (no raw control character in a file). Each negative has its control: an ordinary tag posts and is found.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class TagFindabilityEndToEndTests(ForumFixture forum) : IClassFixture<ForumFixture>, IDisposable
{
    private readonly InboxProbe _probe = new(forum);
    private readonly string _home = Directory.CreateTempSubdirectory("curia-tag-find-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Board() => "board-" + InboxProbe.Suffix();

    // ---- helpers --------------------------------------------------------------------------------

    /// <summary>A hand-signed question with exactly these tags by a fresh agent (so no daily budget is shared); the Forum's status, body and the agent.</summary>
    private async Task<(HttpStatusCode Status, string Body, InboxProbe.Member Member)> PostAsync(
        string board, string[] tags, string? body = null, Func<byte[], byte[]>? tamper = null)
    {
        var member = await _probe.EnrolAsync("tagger", Ct);
        var wire = member.Agent.SignQuestion(board, body ?? "a body " + Guid.NewGuid().ToString("N"), "Title " + InboxProbe.Suffix(), forum.Now, tags);
        var (status, text) = await _probe.PostAsync(member, tamper is null ? wire : tamper(wire), Ct);
        return (status, text, member);
    }

    private static string IdOf(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("post_id").GetString()!;
    }

    private async Task<string[]> RestSearchAsync(string board, string tagSpelling)
    {
        using var response = await forum.Client.GetAsync(
            new Uri($"/v1/search?board={Uri.EscapeDataString(board)}&tags={Uri.EscapeDataString(tagSpelling)}&limit=50", UriKind.Relative), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {text}");
        using var json = JsonDocument.Parse(text);
        return [.. json.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!)];
    }

    private async Task<string[]> RestInboxAsync(InboxProbe.Member reader, string board, string tagSpelling)
    {
        var page = await _probe.InboxPageAsync(reader, $"?board={Uri.EscapeDataString(board)}&tags={Uri.EscapeDataString(tagSpelling)}&limit=50", Ct);
        return page.Ids;
    }

    private async Task<EnrolledAgent> EnrolMcpAgentAsync()
    {
        var slug = $"mcp-{Guid.NewGuid().ToString("N")[..8]}";
        Assert.True(
            new ProfileStore(_home).Create(slug, $"https://agents.example/{slug}", $"{slug}-k", forum.Client.BaseAddress!).TryGetValue(out var agent, out var error),
            error?.Detail);
        _owned.Add(agent!);
        var enrolled = await new ForumClient(forum.Client, forum.Client.BaseAddress!).EnrolAsync(agent!, Ct);
        Assert.True(enrolled.TryGetValue(out _, out var refusal), refusal?.Summary);
        return agent!;
    }

    private ForumTools ToolsFor(EnrolledAgent agent)
    {
        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var session = new ForumSession(client, agent, new ProfileStore(_home), forum.Clock);
        return new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent, session, forum.Clock));
    }

    /// <summary>The post ids <c>curia_search</c> returned, read from its result blocks (the resource URIs and the text).</summary>
    private static string[] IdsOf(CallToolResult result)
    {
        var all = string.Join(
            '\n',
            result.Content.OfType<EmbeddedResourceBlock>().Select(b => b.Resource.Uri)
                .Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text))
                .Concat(result.Content.OfType<EmbeddedResourceBlock>().Select(b => (b.Resource as TextResourceContents)?.Text ?? string.Empty)));
        return [.. System.Text.RegularExpressions.Regex.Matches(all, "[0-9A-HJKMNP-TV-Z]{26}", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(2)).Select(m => m.Value).Distinct(StringComparer.Ordinal)];
    }

    private async Task<long> EventCountAsync()
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    // ---- the corpus -----------------------------------------------------------------------------

    /// <summary>
    /// 30 spellings: the first 20 are admitted and must be found by their own spelling; the last 10 hold a comma, are empty, are padded or hold U+0000
    /// and must be refused at write. Built at run time. Asserted to be 30 before use.
    /// </summary>
    private static (string Tag, bool Admitted)[] Corpus() =>
    [
        ("x", true), ("X", true), ("jcs", true), ("a b", true), ("a-b", true), ("a&b", true), ("a+b", true), ("a%2Cb", true), ("#a", true),
        ("a:b", true), ("a/b", true),
        ("é", true), // NFC
        ("e" + (char)0x0301, true), // NFD, the spelling a hand-built client may type; the canonical form is NFC
        (new string('t', 200), true),
        ("a" + (char)0xFF0C + "b", true), // fullwidth comma
        ("a" + (char)0x200B + "b", true), // zero width space inside
        ("ß", true), ("İ", true), ("日本語", true), ("a.b", true),
        ("a,b", false), (",", false), ("", false), (" ", false), (" x", false), ("x ", false),
        ("x" + (char)9, false), ((char)0xA0 + "x", false), ("b" + (char)0 + "c", false), ("a, b", false),
    ];

    public static TheoryData<int> Rows()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Corpus().Length; i++) data.Add(i);
        return data;
    }

    [Fact]
    public void R8_73_TheCorpusHoldsThirtySpellings() => Assert.Equal(30, Corpus().Length);

    // ---- PJ30 -----------------------------------------------------------------------------------

    /// <summary>
    /// PJ30: a tag the Forum admits is found by its own spelling on <c>GET /v1/search</c> and on <c>curia_search</c>; a tag the filter cannot name
    /// is refused <c>400</c> (comma, empty, padded, U+0000), and a refusal costs the author no post.
    /// </summary>
    [Theory]
    [MemberData(nameof(Rows))]
    public async Task R8_73_EveryTagTheForumAdmitsIsFoundByItsOwnSpelling(int row)
    {
        var (tag, admitted) = Corpus()[row];
        var board = Board();
        var before = await EventCountAsync();

        var (status, body, _) = await PostAsync(board, ["jcs-anchor", tag]);

        if (!admitted)
        {
            Assert.True(status == HttpStatusCode.BadRequest, $"row {row}: {(int)status}: {body}");
            using var problem = JsonDocument.Parse(body);
            Assert.Equal(PublishedSmallWords.TagUnfilterableType(), problem.RootElement.GetProperty("type").GetString());
            Assert.Equal(before, await EventCountAsync());
            return;
        }

        Assert.True(status == HttpStatusCode.Created, $"row {row}: {(int)status}: {body}");
        var id = IdOf(body);

        Assert.Contains(id, await RestSearchAsync(board, tag));

        var mcp = ToolsFor(await EnrolMcpAgentAsync());
        var viaTool = await mcp.SearchAsync(new SearchCriteria { Board = board, Tags = [tag] }, Ct);
        Assert.True(viaTool.IsError != true, string.Join('\n', viaTool.Content.OfType<TextContentBlock>().Select(b => b.Text)));
        Assert.Contains(id, IdsOf(viaTool));
    }

    // ---- PJ25 -----------------------------------------------------------------------------------

    /// <summary>
    /// PJ25: a hand-signed comma tag is refused <c>400</c> with the type R8.73 names and the index, nothing is stored, and no budget is spent: the
    /// same agent's next three posts all succeed (a T0 agent has three a day).
    /// </summary>
    [Fact]
    public async Task R8_73_ASignedCommaTagIsRefusedAndNothingIsStored()
    {
        var board = Board();
        var member = await _probe.EnrolAsync("comma", Ct);
        var before = await EventCountAsync();

        var refused = await _probe.PostAsync(member, member.Agent.SignQuestion(board, "a body", "A title " + InboxProbe.Suffix(), forum.Now, ["a,b"]), Ct);

        Assert.Equal((HttpStatusCode)PublishedSmallWords.TagUnfilterableStatus(), refused.Status);
        using var problem = JsonDocument.Parse(refused.Body);
        Assert.Equal(PublishedSmallWords.TagUnfilterableType(), problem.RootElement.GetProperty("type").GetString());
        Assert.Equal("tags[0]", problem.RootElement.GetProperty("detail").GetString());
        Assert.Equal(before, await EventCountAsync());

        for (var i = 0; i < 3; i++)
        {
            var ok = await _probe.PostAsync(member, member.Agent.SignQuestion(board, "a body " + i, $"Allowed {i} {InboxProbe.Suffix()}", forum.Now, ["fine"]), Ct);
            Assert.True(ok.Status == HttpStatusCode.Created, $"post {i}: {(int)ok.Status}: {ok.Body}");
        }
    }

    // ---- PJ26 -----------------------------------------------------------------------------------

    /// <summary>
    /// PJ26: the refusal is made before the signature and before SCREEN: a comma tag with a credential in the body, and a comma tag with a corrupted
    /// signature, each answer <c>tag-unfilterable</c>. (Task 0 item 8 read the order in <c>IngestPipeline.VerifyAsync</c>; this holds it.)
    /// </summary>
    [Theory]
    [InlineData("credential")]
    [InlineData("signature")]
    public async Task R8_73_ItIsMadeBeforeScreenAndBeforeTheSignature(string case_)
    {
        ArgumentNullException.ThrowIfNull(case_);
        var board = Board();
        var before = await EventCountAsync();

        var (status, body, _) = case_ == "credential"
            ? await PostAsync(board, ["a,b"], body: "the key is AKIAIOSFODNN7EXAMPLE and it is live")
            : await PostAsync(board, ["a,b"], tamper: CorruptSignature);

        Assert.True(status == HttpStatusCode.BadRequest, $"{(int)status}: {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(PublishedSmallWords.TagUnfilterableType(), problem.RootElement.GetProperty("type").GetString());
        Assert.Equal(before, await EventCountAsync());
    }

    /// <summary>PJ26's controls: without the comma, the credential is screened and the corrupted signature is refused as it always was.</summary>
    [Theory]
    [InlineData("credential")]
    [InlineData("signature")]
    public async Task R8_73_WithoutTheCommaTheLaterPhasesStillRefuse(string case_)
    {
        ArgumentNullException.ThrowIfNull(case_);
        var board = Board();

        var (status, body, _) = case_ == "credential"
            ? await PostAsync(board, ["ab"], body: "the key is AKIAIOSFODNN7EXAMPLE and it is live")
            : await PostAsync(board, ["ab"], tamper: CorruptSignature);

        Assert.True(status is HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized, $"{(int)status}: {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.NotEqual(PublishedSmallWords.TagUnfilterableType(), problem.RootElement.GetProperty("type").GetString());
    }

    private static byte[] CorruptSignature(byte[] wire)
    {
        var text = Encoding.UTF8.GetString(wire);
        const string Key = "\"signature\":\"";
        var at = text.IndexOf(Key, StringComparison.Ordinal) + Key.Length;
        var flipped = text[at + 40] == 'A' ? 'B' : 'A';
        return Encoding.UTF8.GetBytes(string.Concat(text.AsSpan(0, at + 40), flipped.ToString(), text.AsSpan(at + 41)));
    }

    // ---- PJ31 -----------------------------------------------------------------------------------

    /// <summary>
    /// PJ31, R9.45: a tag typed in NFD is stored in NFC, and is found by either spelling on <c>/v1/search</c>, <c>/v1/inbox</c> and
    /// <c>curia_search</c>; the comparison is still case-blind. An NFD tag posted by hand and an NFC tag posted by <c>curia_ask</c> are found together.
    /// </summary>
    [Fact]
    public async Task R9_45_ATagTypedInNfdIsFoundByEitherSpelling()
    {
        var board = Board();
        var nfc = "café-" + InboxProbe.Suffix();
        var nfd = nfc.Replace("é", "é", StringComparison.Ordinal);
        Assert.NotEqual(nfc, nfd);

        var (status, body, _) = await PostAsync(board, [nfd]);
        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {body}");
        var handId = IdOf(body);

        var asker = await EnrolMcpAgentAsync();
        var ask = await ToolsFor(asker).AskAsync(board, "Asked by the tool " + InboxProbe.Suffix(), "A body " + Guid.NewGuid().ToString("N"), [nfc], null, Ct);
        Assert.True(ask.IsError != true, string.Join('\n', ask.Content.OfType<TextContentBlock>().Select(b => b.Text)));
        var toolId = IdsOf(ask).First(id => id != handId);

        var reader = await _probe.EnrolAsync("reader", Ct);
        var reader2 = await EnrolMcpAgentAsync();
        string[] both = [handId, toolId];

        foreach (var spelling in new[] { nfc, nfd, nfc.ToUpperInvariant() })
        {
            var search = await RestSearchAsync(board, spelling);
            foreach (var id in both) Assert.True(search.Contains(id), $"/v1/search tags={spelling.Normalize(NormalizationForm.FormD).Length} chars: {string.Join(',', search)} lacks {id}");

            var inbox = await RestInboxAsync(reader, board, spelling);
            foreach (var id in both) Assert.True(inbox.Contains(id), $"/v1/inbox: {string.Join(',', inbox)} lacks {id}");

            var viaTool = IdsOf(await ToolsFor(reader2).SearchAsync(new SearchCriteria { Board = board, Tags = [spelling] }, Ct));
            foreach (var id in both) Assert.True(viaTool.Contains(id), $"curia_search: {string.Join(',', viaTool)} lacks {id}");
        }
    }

    // ---- PJ19 -----------------------------------------------------------------------------------

    /// <summary>
    /// PJ19, R8.74: the Forum holds no URL check. A hand-signed reference <c>{kind: url, target: HTTPS://example.org/a}</c> is accepted, stored as typed
    /// and unannotated. Green first; held by FPJ15 (a Forum-side check added).
    /// </summary>
    [Fact]
    public async Task R8_74_TheForumHoldsNoUrlCheck()
    {
        var board = Board();
        var member = await _probe.EnrolAsync("urlref", Ct);
        var refs = new JsonValue.Array([new JsonValue.Object([new("kind", new JsonValue.String("url")), new("target", new JsonValue.String("HTTPS://example.org/a"))])]);
        var question = await _probe.PostAsync(member, member.Agent.SignQuestion(board, "a body", "Q " + InboxProbe.Suffix(), forum.Now, ["fine"]), Ct);
        Assert.Equal(HttpStatusCode.Created, question.Status);

        var wire = member.Agent.Sign(PostKind.Comment, board, "a comment " + Guid.NewGuid().ToString("N"), null, IdOf(question.Body), forum.Now, tags: [], refs: refs);
        var (status, body) = await _probe.PostAsync(member, wire, Ct);

        Assert.True(status == HttpStatusCode.Created, $"{(int)status}: {body}");
        using var receipt = JsonDocument.Parse(body);
        Assert.Empty(receipt.RootElement.GetProperty("risk_flags").EnumerateArray());

        using var served = await forum.Client.GetAsync(new Uri($"/v1/posts/{IdOf(body)}", UriKind.Relative), Ct);
        using var post = JsonDocument.Parse(await served.Content.ReadAsStringAsync(Ct));
        using var canonical = JsonDocument.Parse(post.RootElement.GetProperty("canonical").GetString()!);
        var target = canonical.RootElement.GetProperty("refs")[0];
        Assert.Equal("url", target.GetProperty("kind").GetString());
        Assert.Equal("HTTPS://example.org/a", target.GetProperty("target").GetString());
    }
}
