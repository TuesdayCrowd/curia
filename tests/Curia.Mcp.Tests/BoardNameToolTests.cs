using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R8.85, R9.54 and R11.78 (errata G56) at the adapter, NAM22 (the tool's half), NAM26, NAM29, NAM31 (the descriptions), NAM32, NAM33 and NAM35:
/// <c>curia_search</c> refuses a present blank criterion, the query included, <c>NOT SENT</c>, naming the argument and sending nothing, while an absent
/// one lists (R9.25); <c>curia_ask</c> refuses a board the grammar refuses through the client's local refusal, in the words of a tag's; the descriptions
/// carry the board sentence once; the version is the one R11.78 names; the R14.12 gate's sites hold R11.78's list.
///
/// <para><b>Oracles.</b> The request list is the stub's (<see cref="StubLog.Requests"/>), so "nothing was sent" is the Forum's account and not the
/// tool's. Every sentence, the version and the site list are parsed from R11.78 words (<see cref="PublishedBoardWords"/>). The reply tools over a
/// stored refused board (NAM30) need a Forum that stores one and are the Api suite's (<c>BoardStoredReadersEndToEndTests</c>), where NAM30 is now the positive leg: the tools sign for a post on a
/// stored board the grammar refuses and the Forum accepts it (R8.87); the sentence R11.78 gave the reply tools is withdrawn (R11.85).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class BoardNameToolTests : IDisposable
{
    private const string NotSent = "NOT SENT:";

    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-board-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    private async Task<McpProtocolSession> StartAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var writer = new ForumWriter(agent!, new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System), TimeProvider.System);
        var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), writer);
        return await McpProtocolSession.StartAsync(tools, writer.Agent.Profile.AgentId, agent);
    }

    private static readonly string[] CriteriaNames = ["query", "board", "author", "min_verification"];

    private static readonly string[] RefusedBoards = [new string('a', 65), "Meta", " a"];

    private static Dictionary<string, object?> Search(string member, object? value) =>
        new() { ["criteria"] = new Dictionary<string, object?> { [member] = value } };

    private static Dictionary<string, object?> Ask(string board) => new() { ["board"] = board, ["title"] = "A question", ["body"] = "A body." };

    public static TheoryData<string, string> Blanks()
    {
        var data = new TheoryData<string, string>();
        foreach (var member in new[] { "query", "board", "author", "min_verification" })
            foreach (var blank in new[] { "", "  ", "\t", "\n", char.ConvertFromUtf32(0xA0), char.ConvertFromUtf32(0x3000) })
                data.Add(member, blank);
        return data;
    }

    /// <summary>NAM26: each criterion blank, in each spelling, is refused <c>NOT SENT</c> in the published sentence, naming <c>criteria.&lt;name&gt;</c>, with zero requests.</summary>
    [Theory]
    [MemberData(nameof(Blanks))]
    public async Task R11_78_NAM26_ABlankCriterionIsRefusedByNameAndNothingIsSent(string member, string blank)
    {
        await using var session = await StartAsync();
        var control = await session.CallAsync("curia_search", Search(member, member == "min_verification" ? "V1" : "jcs"));
        Assert.False(control.IsError, control.Text);
        _log.Requests.Clear();

        var result = await session.CallAsync("curia_search", Search(member, blank));

        Assert.True(result.IsError, $"criteria.{member} blank was not refused:\n{result.Text}");
        Assert.Contains(Fold(PublishedBoardWords.ToolBlankRefusal(member)), Fold(result.Text), StringComparison.Ordinal);
        Assert.Empty(_log.Requests);
    }

    /// <summary>NAM26: an absent criterion lists (R9.25): <c>{}</c>, <c>{"criteria":{}}</c> and <c>{"criteria":{"query":null}}</c> are sent as a listing.</summary>
    [Fact]
    public async Task R11_78_NAM26_AnAbsentCriterionStillLists()
    {
        await using var session = await StartAsync();

        var none = await session.CallAsync("curia_search", []);
        var empty = await session.CallAsync("curia_search", new() { ["criteria"] = new Dictionary<string, object?>() });
        var nullQuery = await session.CallAsync("curia_search", Search("query", null));

        Assert.False(none.IsError, none.Text);
        Assert.False(empty.IsError, empty.Text);
        Assert.False(nullQuery.IsError, nullQuery.Text);
        Assert.Equal(3, _log.Requests.Count(r => r.StartsWith("GET /v1/search", StringComparison.Ordinal)));
    }

    /// <summary>NAM22 through the tool: the tool refuses a position exactly where the function names it.</summary>
    [Fact]
    public async Task R11_78_NAM22_TheToolAgreesWithTheFunction()
    {
        await using var session = await StartAsync();
        foreach (var member in new[] { "query", "board", "author", "min_verification" })
        {
            _log.Requests.Clear();
            const string blank = " ";
            var named = SearchRequestRules.BlankCriterion(
                member == "query" ? blank : "x", member == "board" ? blank : null, member == "author" ? blank : null, member == "min_verification" ? blank : null);
            Assert.Equal(member, named);

            var result = await session.CallAsync("curia_search", Search(member, blank));

            Assert.True(result.IsError && result.Text.Contains($"criteria.{named}", StringComparison.Ordinal), $"the tool did not refuse {member}:\n{result.Text}");
            Assert.Empty(_log.Requests);
        }
    }

    /// <summary>
    /// NAM29: <c>curia_ask</c> with <c>Machine Learning</c> is <c>NOT SENT</c> in the words of a tag's refusal (the client error's title, then the
    /// inner type, title and classes), with zero requests; the board the refusal teaches posts and the receipt carries it.
    /// </summary>
    [Fact]
    public async Task R8_85_NAM29_AskRefusesAMultiWordBoardNotSentAndThenPostsTheTaughtOne()
    {
        await using var session = await StartAsync();

        var refused = await session.CallAsync("curia_ask", Ask("Machine Learning"));

        Assert.True(refused.IsError, refused.Text);
        Assert.StartsWith(NotSent, refused.Text, StringComparison.Ordinal);
        var folded = Fold(refused.Text);
        Assert.Contains(Fold($"\"{PublishedBoardWords.ClientTitle()}\""), folded, StringComparison.Ordinal);
        Assert.Contains(Fold($"\"{PublishedBoardWords.BoardType()}: {PublishedBoardWords.BoardTitle()} (board: separator, not-lower-case)\""), folded, StringComparison.Ordinal);
        Assert.Empty(_log.Requests);

        var posted = await session.CallAsync("curia_ask", Ask("machine-learning"));

        Assert.False(posted.IsError, posted.Text);
        Assert.Contains(_log.Requests, r => r == "POST /v1/posts");
        Assert.Contains("machine-learning", posted.Text, StringComparison.Ordinal);
    }

    /// <summary>NAM33: the gate's board boundary values go through <c>curia_ask</c>: 64 posts; 65, <c>Meta</c> and a leading space are <c>NOT SENT</c> and send nothing.</summary>
    [Fact]
    public async Task R14_12_NAM33_TheBoardBoundaryValuesAreSentThroughAsk()
    {
        await using var session = await StartAsync();

        var ok = await session.CallAsync("curia_ask", Ask(new string('a', 64)));
        Assert.False(ok.IsError, ok.Text);
        _log.Requests.Clear();

        foreach (var board in RefusedBoards)
        {
            var refused = await session.CallAsync("curia_ask", Ask(board));
            Assert.True(refused.IsError && refused.Text.StartsWith(NotSent, StringComparison.Ordinal), $"board of {board.Length} characters was not refused:\n{refused.Text}");
            Assert.Empty(_log.Requests);
        }
    }

    /// <summary>NAM33: the gate's sites are parsed from R11.78 (not R11.45): each <c>criteria.&lt;name&gt;</c> its sentences name is a site of the gate, and <c>criteria.query</c> is among them.</summary>
    [Fact]
    public void R14_12_NAM33_TheGatesSitesHoldR1178sList()
    {
        var named = CriteriaNames
            .Select(n => PublishedBoardWords.ToolBlankRefusal(n)).Select(s => Regex.Match(s, @"criteria\.\w+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)).Value).ToArray();
        Assert.Equal(4, named.Length);
        Assert.Contains("criteria.query", named);

        var missing = named.Where(path => !BlankValueTests.Sites.Any(s => s.Tool == "curia_search" && s.Path == path)).ToArray();
        Assert.True(missing.Length == 0, $"gate sites lack {string.Join(", ", missing)}");
    }

    /// <summary>
    /// NAM32: the version the catalogue reports is the one the newest entry names, read by the one parser (<c>PublishedReadersLastWords.Version()</c>); R11.78 named 0.9.7 when it was built,
    /// and the resend PR (errata G58, R11.81) bumped to 0.10.0, so the sentence "The version in R11.78 is" is history and is held only to be the version R11.78 was built at.
    /// </summary>
    [Fact]
    public void R11_78_NAM32_TheVersionIsTheOneTheErrataNames() =>
        Assert.True(
            string.Equals(PublishedReadersLastWords.Version(), ToolCatalogue.Version, StringComparison.Ordinal) && string.Equals(PublishedBoardWords.Version(), "0.9.7", StringComparison.Ordinal),
            $"expected {PublishedReadersLastWords.Version()} (R11.78 history: {PublishedBoardWords.Version()}), was {ToolCatalogue.Version}");

    /// <summary>NAM12's tool leg: a filter is never refused for its grammar: <c>criteria.board</c> of <c>Meta</c> and of a name with a space are sent as typed (a control, green on the seam).</summary>
    [Theory]
    [InlineData("Meta")]
    [InlineData("a b")]
    public async Task R8_85_NAM12_AFilterIsSentWhateverItsGrammar(string board)
    {
        ArgumentNullException.ThrowIfNull(board);
        await using var session = await StartAsync();

        var result = await session.CallAsync("curia_search", Search("board", board));

        Assert.False(result.IsError, result.Text);
        Assert.Contains(_log.Requests, r => r.StartsWith("GET /v1/search", StringComparison.Ordinal));
    }

    /// <summary>NAM31 and NAM35: the descriptions of <c>curia_ask</c> and <c>curia_search</c> carry the board sentence once, and the search description still says board names are case-sensitive (true of a stored board).</summary>
    [Theory]
    [InlineData("curia_ask")]
    [InlineData("curia_search")]
    public async Task R11_78_NAM31_TheDescriptionCarriesTheBoardSentenceOnce(string tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        await using var session = await StartAsync();

        var description = Fold((await session.ListAsync()).Single(t => t.Name == tool).Description ?? string.Empty);

        Assert.True(description.Contains(Fold(PublishedBoardWords.BoardSentence()), StringComparison.Ordinal), $"description of {tool} lacks the board sentence");
        Assert.Equal(1, Regex.Count(description, "always qualify", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)));
        if (tool == "curia_search") Assert.Contains("case-sensitive", description, StringComparison.Ordinal);
    }

    /// <summary>NAM31: the client's constant is the published sentence (a control: green on the seam).</summary>
    [Fact]
    public void R11_78_NAM31_TheClientConstantIsThePublishedSentence() =>
        Assert.Equal(Fold(PublishedBoardWords.BoardSentence()), Fold(BoardWording.Sentence));
}
