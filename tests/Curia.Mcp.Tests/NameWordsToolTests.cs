using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R10.97 and R11.87 (errata G62) at the adapter, NMN37, NMN38 and NMN41: <c>curia_search</c>'s first block carries <c>unmatched_filters=</c> in each of its states and the very
/// line <c>curia search</c> prints; the receipts of <c>curia_ask</c>, <c>curia_answer</c>, <c>curia_comment</c> and <c>curia_revise</c> carry the <c>board_new</c> line for a question
/// and for no other kind; the version is 0.10.4, the instructions say a string argument must not hold an unpaired surrogate, and the SDK pin is a tripwire.
///
/// <para><b>Oracles.</b> The sentences are parsed from the entry (<see cref="PublishedNameWords"/>), the classes from R8.85 words (<see cref="PublishedBoardWords"/>), the typed value as
/// a display literal. A printed line is compared whitespace-folded (the adapter's spacing is its own). The page and the receipt are the stub's
/// (<see cref="StubLog.SearchUnmatchedFiltersJson"/>, <see cref="StubLog.ReceiptBoardNewJson"/>). The CLI's half is <c>NameWordsCliTests</c> in <c>Curia.Client.Tests</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class NameWordsToolTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-g62-").FullName;

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    private static string Lit(string value) => DisplayLiteral.Of(value);

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private ForumTools ToolsOver(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var writer = new ForumWriter(agent!, new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System), TimeProvider.System);
        return new ForumTools(log.Client(), MarkingMode.None, new HeadStore(_home), writer);
    }

    private static string FiltersLine(string clauses) => PublishedNameWords.FiltersLabel() + clauses;

    private static string BoardUnmatched(string typed, string classes) =>
        PublishedNameWords.BoardClause().Replace("<typed>", Lit(typed), StringComparison.Ordinal)
            .Replace("<hint>", PublishedNameWords.BoardHintWithClasses().Replace("<classes>", classes, StringComparison.Ordinal), StringComparison.Ordinal);

    private static string Matched(string name, string typed) =>
        PublishedNameWords.MatchedClause(name).Replace("<typed>", Lit(typed), StringComparison.Ordinal);

    private async Task<string> FirstBlockAsync(StubLog log, SearchCriteria criteria)
    {
        var result = await ToolsOver(log).SearchAsync(criteria, Ct);
        return Assert.IsType<TextContentBlock>(result.Content[0]).Text;
    }

    /// <summary>
    /// NMN37. <c>curia_search</c>'s first block: <c>unmatched_filters=board</c> and the CLI's own filters line for an unmatched board; <c>unmatched_filters=none</c> and the matched
    /// clause when the Forum says <c>[]</c>; <c>unmatched_filters=NOT STATED</c> and the not-stated clause for a page with no member; <c>none</c> and no filters line when no filter was
    /// named. Predicted red on the seam: no <c>unmatched_filters=</c> in any.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN37_TheFirstBlockStatesUnmatchedFiltersAndTheLineTheCliPrints()
    {
        using var unmatched = new StubLog { SearchUnmatchedFiltersJson = """["board"]""" };
        using var matched = new StubLog { SearchUnmatchedFiltersJson = "[]" };
        using var older = new StubLog();
        using var unnamed = new StubLog { SearchUnmatchedFiltersJson = "[]" };

        var blocks = new (string Name, string Block, string Member, string? Line)[]
        {
            ("an unmatched board", await FirstBlockAsync(unmatched, new SearchCriteria { Board = "RUST" }), "unmatched_filters=board", FiltersLine(BoardUnmatched("RUST", "not-lower-case"))),
            ("a matched board", await FirstBlockAsync(matched, new SearchCriteria { Board = "rust" }), "unmatched_filters=none", FiltersLine(Matched("board", "rust"))),
            ("a page that stated none", await FirstBlockAsync(older, new SearchCriteria { Board = "rust" }), "unmatched_filters=NOT STATED", FiltersLine(PublishedNameWords.NotStatedClause().Replace("<name>", "board", StringComparison.Ordinal).Replace("<typed>", Lit("rust"), StringComparison.Ordinal))),
            ("no filter named", await FirstBlockAsync(unnamed, new SearchCriteria { Query = "x" }), "unmatched_filters=none", null),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (name, block, member, line) in blocks)
        {
            expected.Add($"{name}: {member}; {(line is null ? "no filters line" : "the line: " + line)}");
            var lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Fold).ToArray();
            var hasMember = Fold(block).Contains(member, StringComparison.Ordinal);
            var hasLine = line is null ? !lines.Any(l => l.StartsWith("filters ", StringComparison.Ordinal)) : lines.Contains(Fold(line));
            actual.Add($"{name}: {(hasMember ? member : "no " + member)}; {(line is null ? (hasLine ? "no filters line" : "a filters line") : (hasLine ? "the line: " + line : "not the line"))}");
        }

        var (e, a) = (string.Join(" | ", expected), string.Join(" | ", actual));
        if (!string.Equals(e, a, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {e}{Environment.NewLine}Actual:   {a}");
    }

    private async Task<string[]> BlockLinesAsync(StubLog log, SearchCriteria criteria) =>
        [.. (await FirstBlockAsync(log, criteria)).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Fold)];

    /// <summary>
    /// NMN55 (Amendment 4, ruling 38), at the adapter. The first block of an empty first page with no cursor whose every named filter is matched carries the filters line with the published
    /// tail after its last clause, the very line <c>curia search</c> prints: for a board, for an author and for the two together. Predicted red on the tree: the lower-case clause and no tail.
    /// Falsification FNMN61, FNMN70.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN55_TheFirstBlockOfAnEmptyFirstPageWhereEveryNamedFilterIsMatchedCarriesTheTail()
    {
        const string alice = "urn:curia:agent:alice";
        var tail = PublishedNameWords.EmptyPageTail();
        var author = PublishedNameWords.MatchedClause("author").Replace("<typed>", Lit(alice), StringComparison.Ordinal);
        var rows = new (string Label, SearchCriteria Criteria, string Clauses)[]
        {
            ("one board", new SearchCriteria { Board = "rust" }, Matched("board", "rust")),
            ("one author", new SearchCriteria { Author = alice }, author),
            ("two filters", new SearchCriteria { Board = "rust", Author = alice }, Matched("board", "rust") + "; " + author),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, criteria, clauses) in rows)
        {
            using var log = new StubLog { SearchUnmatchedFiltersJson = "[]", SearchEmpty = true };
            var line = FiltersLine(clauses + tail);
            var lines = await BlockLinesAsync(log, criteria);
            expected.Add($"{label}: the line {Fold(line)}");
            actual.Add($"{label}: {(lines.Contains(Fold(line)) ? "the line " + Fold(line) : "not the line; " + string.Join(" / ", lines.Where(l => l.StartsWith("filters ", StringComparison.Ordinal)).DefaultIfEmpty("no filters line")))}");
        }

        var (e, a) = (string.Join(" | ", expected), string.Join(" | ", actual));
        if (!string.Equals(e, a, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {e}{Environment.NewLine}Actual:   {a}");
    }

    /// <summary>
    /// NMN56 (Amendment 4, ruling 38), at the adapter: the tail is in no other first block: a page that holds a result, an empty page with a cursor, an unmatched clause, a
    /// <c>NOT STATED</c> clause, and no filter named. Falsification FNMN62 to FNMN64.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN56_TheFirstBlockCarriesTheTailInNoOtherState()
    {
        var marker = PublishedNameWords.EmptyPageTail()[2..].Split(',')[0];
        var rows = new (string Label, bool Empty, string? Json, SearchCriteria Criteria)[]
        {
            ("a page that holds a result", false, "[]", new SearchCriteria { Board = "rust" }),
            ("a cursor", true, "[]", new SearchCriteria { Board = "rust", Cursor = "c0:0:0000000000000000" }),
            ("an unmatched board", true, """["board"]""", new SearchCriteria { Board = "RUST" }),
            ("one matched, one unmatched", true, """["author"]""", new SearchCriteria { Board = "rust", Author = "urn:curia:agent:alice" }),
            ("a NOT STATED clause", true, null, new SearchCriteria { Board = "rust" }),
            ("no filter named", true, "[]", new SearchCriteria { Query = "x" }),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, empty, json, criteria) in rows)
        {
            using var log = new StubLog { SearchUnmatchedFiltersJson = json, SearchEmpty = empty };
            var block = await FirstBlockAsync(log, criteria);
            expected.Add($"{label}: the tail occurs nowhere");
            actual.Add($"{label}: the tail {(Fold(block).Contains(marker, StringComparison.Ordinal) ? "occurs" : "occurs nowhere")}");
        }

        var (e, a) = (string.Join(" | ", expected), string.Join(" | ", actual));
        if (!string.Equals(e, a, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {e}{Environment.NewLine}Actual:   {a}");
    }

    /// <summary>
    /// NMN59 (Amendment 4, ruling 39). <c>curia_search</c>'s description does not say a board no post names answers "like an empty one" (beside the <c>unmatched_filters</c> paragraph it reads as
    /// two statements about whether the agent can tell), and its blank-criterion sentence names every criterion <see cref="SearchRequestRules.BlankCriterion(string?, string?, string?, string?, string?, string?, string?)"/>
    /// can name (read from the rule, each slot made blank in turn, not from the text; <c>kind</c> is the tool's <c>kinds</c>). Falsification FNMN68, FNMN69.
    /// </summary>
    [Fact]
    public void R11_78_NMN59_TheSearchDescriptionDoesNotSayLikeAnEmptyOneAndNamesEveryBlankCriterion()
    {
        string?[] named =
        [
            SearchRequestRules.BlankCriterion(" ", null, null, null),
            SearchRequestRules.BlankCriterion(null, " ", null, null),
            SearchRequestRules.BlankCriterion(null, null, " ", null),
            SearchRequestRules.BlankCriterion(null, null, null, " "),
            SearchRequestRules.BlankCriterion(null, null, null, null, kind: ","),
            SearchRequestRules.BlankCriterion(null, null, null, null, tags: ","),
            SearchRequestRules.BlankCriterion(null, null, null, null, cursor: " "),
        ];
        var names = named.Select(n => n ?? throw new Xunit.Sdk.XunitException("a blank slot named no criterion")).Distinct(StringComparer.Ordinal).ToArray();
        Assert.Equal(7, names.Length);

        using var log = new StubLog();
        var description = Descriptions(log)["curia_search"];
        var marker = description.IndexOf("present and empty or blank", StringComparison.Ordinal);
        Assert.True(marker >= 0, "the description has no blank-criterion sentence");
        var start = description.LastIndexOf(". ", marker, StringComparison.Ordinal) + 2;
        var end = description.IndexOf("omit it.", marker, StringComparison.Ordinal) + "omit it.".Length;
        var sentence = description[start..end];

        var missing = names.Where(n => !sentence.Contains(n == "kind" ? "kinds" : n, StringComparison.Ordinal)).ToArray();
        var expected = "no 'like an empty one'; every criterion the rule can name is in the blank-criterion sentence";
        var actual = $"{(description.Contains("like an empty one", StringComparison.Ordinal) ? "'like an empty one' is said" : "no 'like an empty one'")}; {(missing.Length == 0 ? "every criterion the rule can name is in the blank-criterion sentence" : "the sentence names none of: " + string.Join(", ", missing))}";
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>NMN37, the description: <c>curia_search</c>'s description states the member (R9.55). Predicted red on the seam.</summary>
    [Fact]
    public void R10_97_NMN37_TheSearchDescriptionStatesTheMember()
    {
        using var log = new StubLog();
        var description = Descriptions(log)["curia_search"];

        Assert.Contains(PublishedNameWords.UnmatchedMember(), description, StringComparison.Ordinal);
    }

    private Dictionary<string, string> Descriptions(StubLog log) =>
        ToolCatalogue.Build(ToolsOver(log)).ToDictionary(t => t.ProtocolTool.Name, t => t.ProtocolTool.Description ?? string.Empty, StringComparer.Ordinal);

    /// <summary>
    /// NMN38. The receipt of <c>curia_ask</c> carries the <c>board_new</c> line in each of the three states (true, false, <c>NOT STATED</c>) with the board a display literal;
    /// <c>curia_answer</c>, <c>curia_comment</c> and <c>curia_revise</c> carry none even where the receipt holds the member. Predicted red on the seam: no line for the question. The
    /// resend's receipt is NMN46's; the one definition's fence is NMN42.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN38_TheReceiptOfAQuestionCarriesTheBoardNewLineAndNoOtherToolDoes()
    {
        var (lineTrue, lineFalse, lineNotStated) = PublishedNameWords.ReceiptLines();
        string Of(string line) => Fold(line.Replace("<board>", Lit("b"), StringComparison.Ordinal));
        var expected = new List<string>();
        var actual = new List<string>();

        foreach (var (json, line) in new (string?, string)[] { ("false", lineFalse), ("true", lineTrue), (null, lineNotStated) })
        {
            using var log = new StubLog { ReceiptBoardNewJson = json };
            var result = await ToolsOver(log).AskAsync("b", "A title?", "A body.", null, null, Ct);
            var lines = Text(result).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Fold).ToArray();
            expected.Add($"curia_ask {json ?? "(no member)"}: the line {Of(line)}");
            actual.Add($"curia_ask {json ?? "(no member)"}: {(lines.Contains(Of(line)) ? "the line " + Of(line) : "not the line; " + string.Join(" / ", lines.Where(l => l.StartsWith("board_new", StringComparison.Ordinal)).DefaultIfEmpty("no board_new line")))}");
        }

        using var other = new StubLog { ReceiptBoardNewJson = "true" };
        var tools = ToolsOver(other);
        var others = new (string Name, CallToolResult Result)[]
        {
            ("curia_answer", await tools.AnswerAsync(StubLog.PostId, "An answer.", null, Ct)),
            ("curia_comment", await tools.CommentAsync(StubLog.PostId, "A comment.", null, Ct)),
            ("curia_revise", await tools.ReviseAsync(StubLog.PostId, "A corrected body.", null, null, null, Ct)),
        };
        foreach (var (name, result) in others)
        {
            expected.Add($"{name}: no board_new line");
            actual.Add($"{name}: {(Text(result).Contains("board_new", StringComparison.Ordinal) ? "a board_new line" : "no board_new line")}");
        }

        var (e, a) = (string.Join(" | ", expected), string.Join(" | ", actual));
        if (!string.Equals(e, a, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {e}{Environment.NewLine}Actual:   {a}");
    }

    /// <summary>A kept act of the agent's own (a question, a finding or an answer), planted in the published form for <c>curia_resend</c>; its digest.</summary>
    private static string PlantKept(StubLog log, string kind, string board)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var loadError), loadError?.Detail);
        using (agent)
        {
            var draft = new PostDraft
            {
                Kind = kind switch { "question" => PostKind.Question, "finding" => PostKind.Finding, _ => PostKind.Answer },
                Board = board,
                Title = kind == "answer" ? null : "A title?",
                Parent = kind == "answer" ? StubLog.PostId : null,
                Body = "A kept body " + Guid.NewGuid().ToString("N"),
            };
            Assert.True(SubmissionBuilder.Build(agent!, draft, DateTimeOffset.UtcNow).TryGetValue(out var signed, out var error), error?.Type);
            KeptActFiles.Plant(Path.Combine(log.Store.DirectoryFor("alice"), "acts"), signed!, kind, HeadStore.Origin(StubLog.Forum), agent!.Profile.AgentId, attempts: 1, lastAnswer: "503", board: board);
            return signed!.PrefixedDigest;
        }
    }

    /// <summary>The text of <c>curia_resend</c>'s result for a kept act of <paramref name="kind"/> sent to a Forum whose receipt carries the given <c>board_new</c>.</summary>
    private async Task<string> ResentTextAsync(string kind, string? boardNewJson)
    {
        using var log = new StubLog { ReceiptBoardNewJson = boardNewJson };
        var digest = PlantKept(log, kind, "b");
        return Text(await ToolsOver(log).ResendAsync(digest, Ct));
    }

    /// <summary>
    /// NMN46 (Amendment 3, ruling 34). The receipt of <c>curia_resend</c> of a kept question and of a kept finding carries the <c>board_new</c> line in each of the three states, the board a
    /// display literal from the kept act's header, and a kept answer's carries none even where the receipt holds the member. <b>A control on the tree</b> (<c>ResendTools</c> already writes the
    /// line; the build left the probe unwritten, and a comment is not a probe): its value is FNMN48.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN46_TheReceiptOfAResendOfAKeptQuestionAndOfAKeptFindingCarriesTheBoardNewLine_Control()
    {
        var (lineTrue, lineFalse, lineNotStated) = PublishedNameWords.ReceiptLines();
        string Of(string line) => Fold(line.Replace("<board>", Lit("b"), StringComparison.Ordinal));
        var expected = new List<string>();
        var actual = new List<string>();

        var rows = new (string Kind, string? Json, string Line)[]
        {
            ("question", "false", lineFalse), ("question", "true", lineTrue), ("question", null, lineNotStated),
            ("finding", "false", lineFalse), ("finding", "true", lineTrue), ("finding", null, lineNotStated),
        };
        foreach (var (kind, json, line) in rows)
        {
            var lines = (await ResentTextAsync(kind, json)).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(Fold).ToArray();
            expected.Add($"resend {kind} {json ?? "(no member)"}: the line {Of(line)}");
            actual.Add($"resend {kind} {json ?? "(no member)"}: {(lines.Contains(Of(line)) ? "the line " + Of(line) : "not the line; " + string.Join(" / ", lines.Where(l => l.StartsWith("board_new", StringComparison.Ordinal)).DefaultIfEmpty("no board_new line")))}");
        }

        var answered = await ResentTextAsync("answer", "true");
        expected.Add("resend answer: no board_new line");
        actual.Add($"resend answer: {(answered.Contains("board_new", StringComparison.Ordinal) ? "a board_new line" : "no board_new line")}");

        var (e, a) = (string.Join(" | ", expected), string.Join(" | ", actual));
        if (!string.Equals(e, a, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {e}{Environment.NewLine}Actual:   {a}");
    }

    /// <summary>
    /// NMN41. The version of the adapter is 0.10.4 (R10.97 and R11.87 change what its tools print and describe), and its instructions, with an identity and without, carry the sentence
    /// R11.86 and R11.87 words publishes (read from the errata by <see cref="PublishedNameWords.InstructionsSentence"/>, character for character, whitespace folded), which says that a string
    /// argument is text and must not hold an unpaired surrogate escape, which the protocol layer refuses before the adapter sees the call (item 95 is the SDK's: ruling 20). Amendment 3, ruling 36
    /// (M5): the check is the published sentence and not two of its words. Predicted red on the seam: 0.10.3 and no sentence; on the tree the sentence is the published one, so FNMN60 is its row.
    /// </summary>
    [Fact]
    public void R11_87_NMN41_TheVersionIs0110AndTheInstructionsSayWhatAStringArgumentMayNotHold()
    {
        using var log = new StubLog();
        var tools = ToolsOver(log);
        var names = ToolCatalogue.WriteToolNames(tools);
        var withIdentity = Fold(ToolText.ServerInstructions("urn:curia:agent:alice", names));
        var withoutIdentity = Fold(ToolText.ServerInstructions(null, names));
        var sentence = Fold(PublishedNameWords.InstructionsSentence());

        var expected = "version 0.12.0; the instructions with an identity carry the published sentence; the instructions without one carry the published sentence";
        var actual = $"version {ToolCatalogue.Version}; the instructions with an identity {(withIdentity.Contains(sentence, StringComparison.Ordinal) ? "carry" : "do not carry")} the published sentence; the instructions without one {(withoutIdentity.Contains(sentence, StringComparison.Ordinal) ? "carry" : "do not carry")} the published sentence";
        if (!string.Equals(expected, actual, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>
    /// The pinned SDK's tripwire (ruling 20), as a function that takes the text of <c>Directory.Packages.props</c>: null while <c>ModelContextProtocol.Core</c> is the version item 95
    /// was recorded against, and otherwise the sentence that says the lone-surrogate session must be run again before the pin is moved. A tripwire is a stop with a reason, not a block.
    /// </summary>
    internal static string? SdkTripwire(string propsText)
    {
        const string recorded = "2.2.0";
        var match = SdkPin().Match(propsText);
        var version = match.Success ? match.Groups["v"].Value : "(no pin found)";
        return string.Equals(version, recorded, StringComparison.Ordinal)
            ? null
            : $"ModelContextProtocol.Core is {version}; D61 item 95 was recorded as {recorded}'s (a JSON-RPC call whose arguments hold an unpaired surrogate escape answers -32603 before any handler or filter). Run that session again against this version, then edit this pin and the record in the same change.";
    }

    [GeneratedRegex("""<PackageVersion Include="ModelContextProtocol\.Core" Version="(?<v>[^"]+)"\s*/>""", RegexOptions.CultureInvariant)]
    private static partial Regex SdkPin();

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("no Directory.Packages.props above " + AppContext.BaseDirectory);
    }

    /// <summary>NMN41's tripwire, on the tree: the pin in <c>Directory.Packages.props</c> is the version item 95 was recorded against. A control, green on the seam.</summary>
    [Fact]
    public void R11_87_NMN41_TheSdkPinIsTheVersionItem95WasRecordedAgainst()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Packages.props"));

        Assert.Null(SdkTripwire(props));
    }

    /// <summary>NMN41's tripwire is not a vacuous check: a props text naming another version, or none, trips it with the sentence that says what to do. A control, green on the seam.</summary>
    [Theory]
    [InlineData("""<PackageVersion Include="ModelContextProtocol.Core" Version="2.2.1" />""")]
    [InlineData("""<PackageVersion Include="ModelContextProtocol.Core" Version="3.0.0"/>""")]
    [InlineData("<Project></Project>")]
    public void R11_87_NMN41_TheTripwireTripsOnAnotherPin(string props)
    {
        ArgumentNullException.ThrowIfNull(props);
        var message = SdkTripwire(props);

        Assert.NotNull(message);
        Assert.Contains("item 95", message, StringComparison.Ordinal);
        Assert.Contains("run that session again", message, StringComparison.OrdinalIgnoreCase);
    }
}
