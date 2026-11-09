using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.ConsoleCapture;

namespace Curia.Client.Tests;

/// <summary>
/// R10.97 (errata G62), NMN29 to NMN34, in <c>curia</c>: the Forum's word on a filter is printed as a <c>filters</c> line in the words of R10.97 words; a member the Forum
/// did not state is <c>NOT STATED</c> and never read as matched; the inbox's "look elsewhere" is printed only where no filter is unmatched; a question's and a finding's receipt carries a
/// <c>board_new</c> line in every state; the floor line says what the floor was tested against.
///
/// <para><b>Oracles.</b> Every sentence is parsed from the entry (<see cref="PublishedNameWords"/>), with the typed value written as a display literal
/// (<see cref="DisplayLiteral"/>, errata G17) and the hint's classes taken from R8.85 words (<see cref="PublishedBoardWords"/>); no expected line is copied from the code. The page
/// is the stub's (<see cref="StubLog.SearchUnmatchedFiltersJson"/> writes the member of each row) or a recorder's (<see cref="RecordingForum.Responder"/>). A fact that prints a line
/// asserts the line's place (after the vector line, before <c>no results.</c>) and that it is printed once. Characters outside ASCII are built at run time.</para>
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NameWordsCliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Lit(string value) => DisplayLiteral.Of(value);

    private static string Decomposed => "cafe" + char.ConvertFromUtf32(0x301);

    /// <summary>The filters line for a matched filter, from the published clause.</summary>
    private static string Matched(string name, string typed) =>
        PublishedNameWords.FiltersLabel() + PublishedNameWords.MatchedClause(name).Replace("<typed>", Lit(typed), StringComparison.Ordinal);

    private static string BoardUnmatched(string typed, string classes)
    {
        var hint = classes.Length == 0
            ? PublishedNameWords.BoardHintPlain()
            : PublishedNameWords.BoardHintWithClasses().Replace("<classes>", classes, StringComparison.Ordinal);
        return PublishedNameWords.BoardClause().Replace("<typed>", Lit(typed), StringComparison.Ordinal).Replace("<hint>", hint, StringComparison.Ordinal);
    }

    private static string AuthorUnmatched(string typed) =>
        PublishedNameWords.AuthorClause().Replace("<typed>", Lit(typed), StringComparison.Ordinal);

    private static string NotStated(string name, string typed) =>
        PublishedNameWords.NotStatedClause().Replace("<name>", name, StringComparison.Ordinal).Replace("<typed>", Lit(typed), StringComparison.Ordinal);

    private static string[] Lines(string stdout) => stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static async Task<(int Exit, string Stdout, string Stderr)> SearchAsync(string unmatchedJson, params string[] flags)
    {
        using var log = new StubLog { SearchUnmatchedFiltersJson = unmatchedJson };
        var args = Args.Parse(["search", "x", .. flags], 1);
        return await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, log.Client(), MarkingMode.None, null, TestContext.Current.CancellationToken));
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> SearchWithoutMemberAsync(params string[] flags)
    {
        using var log = new StubLog();
        var args = Args.Parse(["search", "x", .. flags], 1);
        return await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, log.Client(), MarkingMode.None, null, TestContext.Current.CancellationToken));
    }

    /// <summary>What the first state of a search page prints for a stub that serves one hit: the filters line's index relative to the model line.</summary>
    private static void AssertPlaced(string stdout, string filtersLine)
    {
        var lines = Lines(stdout);
        var at = Array.IndexOf(lines, filtersLine);
        Assert.True(at >= 0, $"Expected a line: {filtersLine}{Environment.NewLine}Actual stdout:{Environment.NewLine}{stdout}");
        Assert.Single(lines, l => l.StartsWith("filters   ", StringComparison.Ordinal));
        var model = Array.FindIndex(lines, l => l.StartsWith("model     ", StringComparison.Ordinal));
        Assert.True(at > model, $"the filters line must follow the model and vector lines: {stdout}");
    }

    /// <summary>The words check: the label, the four clauses and the three receipt lines are the ones the facts use (a control, green on the seam).</summary>
    [Fact]
    public void R10_97_NMN29_ThePublishedWordsAreTheOnesTheFactsUse()
    {
        Assert.Equal("filters   ", PublishedNameWords.FiltersLabel());
        Assert.StartsWith("board <typed>: NO POST THE FORUM SERVES IS ON IT;", PublishedNameWords.BoardClause(), StringComparison.Ordinal);
        Assert.StartsWith("board_new true   no post the Forum serves was on <board>", PublishedNameWords.ReceiptLines().True, StringComparison.Ordinal);
        Assert.Equal(2, PublishedNameWords.FloorRetiredPhrases().Length);
    }

    /// <summary>
    /// NMN29. <c>curia search --board RUST</c> with the Forum's word <c>["board"]</c>: the filters line with <c>NO POST THE FORUM SERVES IS ON IT</c> and the class <c>not-lower-case</c>; a
    /// decomposed <c>café</c>: <c>not-nfc</c>; a spelling that is a name: the plain hint; a board the Forum says is borne: the matched clause; exit 0 each time. Predicted red on the seam:
    /// no filters line.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN29_ABoardTheForumSaysNoServedPostBearsIsToldWithTheClassesItFails()
    {
        var failures = new List<string>();

        async Task Row(string label, string json, string board, string expectedLine)
        {
            var (exit, stdout, stderr) = await SearchAsync(json, "--board", board);
            try
            {
                Assert.Equal(0, exit);
                Assert.Empty(stderr);
                AssertPlaced(stdout, expectedLine);
            }
            catch (Xunit.Sdk.XunitException ex)
            {
                failures.Add($"{label}: {ex.Message.Split('\n')[0].Trim()}");
            }
        }

        await Row("matched board (the control row's words)", "[]", "rust", Matched("board", "rust"));
        await Row("upper case", """["board"]""", "RUST", PublishedNameWords.FiltersLabel() + BoardUnmatched("RUST", "not-lower-case"));
        await Row("decomposed", """["board"]""", Decomposed, PublishedNameWords.FiltersLabel() + BoardUnmatched(Decomposed, "not-nfc"));
        await Row("a name that bears nothing", """["board"]""", "rustx", PublishedNameWords.FiltersLabel() + BoardUnmatched("rustx", string.Empty));

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>NMN30. <c>--author &lt;slug&gt;</c> with <c>["author"]</c>: the author clause; the typed value is a display literal (an ESC and a line feed are escaped, not written).</summary>
    [Fact]
    public async Task R10_97_NMN30_AnAuthorTheForumSaysNoServedPostBearsIsToldAndTheTypedValueIsALiteral()
    {
        var hostile = "a" + char.ConvertFromUtf32(0x1B) + "[31mb" + char.ConvertFromUtf32(10) + "c";

        var (exit, stdout, _) = await SearchAsync("""["author"]""", "--author", "ex11-hostile");
        var (_, hostileOut, _) = await SearchAsync("""["author"]""", "--author", hostile);

        Assert.Equal(0, exit);
        AssertPlaced(stdout, PublishedNameWords.FiltersLabel() + AuthorUnmatched("ex11-hostile"));
        Assert.DoesNotContain(char.ConvertFromUtf32(0x1B), hostileOut, StringComparison.Ordinal);
        AssertPlaced(hostileOut, PublishedNameWords.FiltersLabel() + AuthorUnmatched(hostile));
    }

    /// <summary>
    /// NMN31 (the member absent). An older Forum's page, which carries no member, prints <c>NOT STATED</c> for each named filter and never the matched clause. Predicted red on the seam:
    /// no filters line at all.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN31_AMemberTheForumDidNotStateIsNotStatedAndNeverMatched()
    {
        var (exit, stdout, _) = await SearchWithoutMemberAsync("--board", "rust", "--author", "someone");

        var expected = PublishedNameWords.FiltersLabel() + NotStated("board", "rust") + "; " + NotStated("author", "someone");
        Assert.Equal(0, exit);
        AssertPlaced(stdout, expected);
        Assert.DoesNotContain("a post the Forum serves bears it", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// NMN31 (the member malformed), a control: a member that is no array of the two names is a malformed search page (the reader's own R11.63 shape), never an empty list. The seam
    /// reads the member and prints nothing, so this is green on the seam.
    /// </summary>
    [Theory]
    [InlineData("\"board\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[\"bogus\"]")]
    [InlineData("[\"board\",7]")]
    public async Task R10_97_NMN31_AMemberThatIsNoArrayOfTheTwoNamesIsAMalformedPage(string json)
    {
        var (exit, stdout, _) = await SearchAsync(json, "--board", "rust");

        Assert.True(exit != 0, $"Expected: a failure Actual: exit 0 and {stdout}");
    }

    private static string InboxPage(string unmatchedJson, int open) =>
        $$"""{"results":[],"open_before_exclusions":{{open}},"excluded_as_own":0,"excluded_as_already_answered":{{(open == 0 ? 0 : 1)}},"unmatched_filters":{{unmatchedJson}}}""";

    private static async Task<(int Exit, string Stdout, string Stderr)> InboxAsync(string page, params string[] flags)
    {
        using var forum = new RecordingForum { Responder = (_, path, _) => path == "/v1/inbox" ? (200, page) : null };
        var home = CuriaCli.NewHome(forum.Address);
        try
        {
            return await CuriaCli.RunAsync(home, Ct, ["inbox", "--agent", "alice", "--forum", forum.Address.ToString(), .. flags]);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// NMN32 (the control, written first and green on the seam): a board that exists with every question answered prints the inbox's sentence, as it always did. The matched clause that
    /// R10.97 adds beside it is NMN32's second half.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN32_ABoardThatExistsWithNoOpenQuestionStillSaysNothingNeedsAnAnswer()
    {
        var (exit, stdout, stderr) = await InboxAsync(InboxPage("[]", open: 0), "--board", "rust");

        Assert.True(exit == 0, $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(PublishedNameWords.NothingNeedsAnAnswer().TrimEnd('.') , stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// NMN32. The same board with the Forum's word that nothing bears it prints the filters line and not "look elsewhere"; a board that exists prints the matched clause before the
    /// sentence. Predicted red on the seam: no filters line, and the sentence is printed beside the typo.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN32_AnUnmatchedBoardPrintsTheFiltersLineAndNotLookElsewhere()
    {
        var (exit, unmatchedOut, stderr) = await InboxAsync(InboxPage("""["board"]""", open: 0), "--board", "dotnte");
        var (_, matchedOut, _) = await InboxAsync(InboxPage("[]", open: 0), "--board", "rust");

        Assert.True(exit == 0, $"exit {exit}; stderr: {stderr}");
        var unmatched = Lines(unmatchedOut);
        var matched = Lines(matchedOut);
        Said(
            ["the typo: the filters line first, no sentence", "the real board: the matched clause first, then the sentence"],
            [
                $"the typo: {(unmatched.FirstOrDefault() == PublishedNameWords.FiltersLabel() + BoardUnmatched("dotnte", string.Empty) ? "the filters line first" : "no filters line first: " + unmatched.FirstOrDefault())}, {(unmatchedOut.Contains("look elsewhere", StringComparison.Ordinal) ? "the sentence is printed" : "no sentence")}",
                $"the real board: {(matched.FirstOrDefault() == Matched("board", "rust") ? "the matched clause first" : "no matched clause first: " + matched.FirstOrDefault())}, {(matched.Skip(1).Any(l => l.StartsWith("no open questions match these filters.", StringComparison.Ordinal)) ? "then the sentence" : "no sentence after it")}",
            ]);
    }

    /// <summary>An inbox page whose member is the given JSON, or absent when <paramref name="unmatchedJson"/> is null (an older Forum's page).</summary>
    private static string InboxPageOrOlder(string? unmatchedJson, int open) =>
        unmatchedJson is not null
            ? InboxPage(unmatchedJson, open)
            : $$"""{"results":[],"open_before_exclusions":{{open}},"excluded_as_own":0,"excluded_as_already_answered":{{(open == 0 ? 0 : 1)}}}""";

    /// <summary>
    /// NMN49 (Amendment 3, ruling 36, M9). An inbox page whose <c>unmatched_filters</c> is no array of the two names (a string, null, an object, a foreign name, a non-string item) is a
    /// malformed page and a non-zero exit, as the search page's is (NMN31's theory); it is never read as an absent member (a malformed word read as nothing). The inbox's reader is not the
    /// search's, so the search's pin does not hold it. Falsification FNMN52.
    /// </summary>
    [Theory]
    [InlineData("\"board\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[\"bogus\"]")]
    [InlineData("[\"board\",7]")]
    public async Task R10_97_NMN49_AnInboxMemberThatIsNoArrayOfTheTwoNamesIsAMalformedPage(string json)
    {
        var (exit, stdout, _) = await InboxAsync(InboxPage(json, open: 0), "--board", "rust");

        Assert.True(exit != 0, $"Expected: a failure Actual: exit 0 and {stdout}");
    }

    /// <summary>
    /// NMN50 (Amendment 4, ruling 41, reversing ruling 36, M9). An inbox board named against a page with no member (an older Forum) prints the <c>NOT STATED</c> clause and then
    /// <c>no open questions match these filters.</c> alone: a client that was not told whether the board has posts cannot claim that nothing needs an answer, and a reader skims to the
    /// instruction. Falsification FNMN67.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN50_AnInboxBoardWithNoMemberPrintsNotStatedAndTheFirstHalfAlone()
    {
        var (exit, stdout, stderr) = await InboxAsync(InboxPageOrOlder(null, open: 0), "--board", "rust");

        Assert.True(exit == 0, $"exit {exit}; stderr: {stderr}");
        Said(
            [PublishedNameWords.FiltersLabel() + NotStated("board", "rust"), PublishedNameWords.NoOpenQuestionsAlone()],
            NonEmpty(stdout));
    }

    private static string[] NonEmpty(string stdout) => [.. Lines(stdout).Where(l => l.Length > 0)];

    /// <summary>
    /// NMN51 (ruling 36, M9). A search whose board and author are both unmatched prints one <c>filters</c> line with the two clauses joined by <c>; </c>, the board first, and not two lines.
    /// Falsification FNMN54.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN51_BothFiltersUnmatchedArePrintedOnOneLineJoinedBySemicolon()
    {
        var (exit, stdout, _) = await SearchAsync("""["board","author"]""", "--board", "RUST", "--author", "someone");

        var expected = PublishedNameWords.FiltersLabel() + BoardUnmatched("RUST", "not-lower-case") + "; " + AuthorUnmatched("someone");
        Assert.Equal(0, exit);
        AssertPlaced(stdout, expected);
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> EmptySearchAsync(string? unmatchedJson, params string[] flags) =>
        await ServedSearchAsync(unmatchedJson, empty: true, flags);

    private static async Task<(int Exit, string Stdout, string Stderr)> ServedSearchAsync(string? unmatchedJson, bool empty, params string[] flags)
    {
        using var log = new StubLog { SearchUnmatchedFiltersJson = unmatchedJson, SearchEmpty = empty };
        var args = Args.Parse(["search", "x", .. flags], 1);
        return await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, log.Client(), MarkingMode.None, null, TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// NMN55 (Amendment 4, ruling 38). The matched clauses are the published states in capitals (<c>THE FORUM SERVES POSTS ON IT</c>, <c>THE FORUM SERVES POSTS BY IT</c>), and on an empty
    /// first page with no cursor where every named filter is matched the line ends with the published tail, after the last clause: for a board, for an author (Read F's state: an author
    /// with posts none of which a term finds) and for the two together. The line is followed by <c>no results.</c>. Predicted red on the tree: the clauses are the lower-case sentence and
    /// there is no tail. Falsification FNMN61, FNMN70.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN55_AnEmptyFirstPageWhereEveryNamedFilterIsMatchedSaysWhereTheEmptinessIs()
    {
        const string alice = "urn:curia:agent:alice";
        var tail = PublishedNameWords.EmptyPageTail();
        var rows = new (string Label, string[] Flags, string Clauses)[]
        {
            ("one board", ["--board", "rust"], Matched("board", "rust")),
            ("one author", ["--author", alice], Matched("author", alice)),
            ("two filters", ["--board", "rust", "--author", alice], Matched("board", "rust") + "; " + PublishedNameWords.MatchedClause("author").Replace("<typed>", Lit(alice), StringComparison.Ordinal)),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, flags, clauses) in rows)
        {
            var (exit, stdout, stderr) = await EmptySearchAsync("[]", flags);
            var lines = Lines(stdout);
            var at = Array.FindIndex(lines, l => l.StartsWith("filters   ", StringComparison.Ordinal));
            expected.Add($"{label}: exit 0; {clauses + tail}; then no results.");
            actual.Add($"{label}: exit {exit}; {(at >= 0 ? lines[at] : "no filters line")}; then {(at >= 0 && at + 2 < lines.Length ? lines[at + 2] : "(nothing) " + stderr)}");
        }

        Said(expected, actual);
    }

    /// <summary>
    /// NMN56 (Amendment 4, ruling 38). The tail is printed for no other state, so a tail printed always, or never, cannot pass this and NMN55: a page that holds a result; an empty page of a
    /// request that carries a cursor; an unmatched clause (one filter, and one of two); a <c>NOT STATED</c> clause; no filter named. Each row asserts the line it does print (or that none
    /// is) and that the tail's words occur nowhere in the output. Falsification FNMN62 to FNMN64.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN56_TheTailIsPrintedForNoOtherState()
    {
        const string alice = "urn:curia:agent:alice";
        var marker = PublishedNameWords.EmptyPageTail()[2..].Split(',')[0];
        var matchedBoard = Matched("board", "rust");
        var matchedAuthor = PublishedNameWords.MatchedClause("author").Replace("<typed>", Lit(alice), StringComparison.Ordinal);
        var rows = new (string Label, bool Empty, string? Json, string[] Flags, string? Line)[]
        {
            ("a page that holds a result", false, "[]", ["--board", "rust"], matchedBoard),
            ("an empty page past the first (a cursor)", true, "[]", ["--board", "rust", "--cursor", "c0:0:0000000000000000"], matchedBoard),
            ("an unmatched board", true, """["board"]""", ["--board", "rustx"], PublishedNameWords.FiltersLabel() + BoardUnmatched("rustx", string.Empty)),
            ("one matched, one unmatched", true, """["author"]""", ["--board", "rust", "--author", alice], matchedBoard + "; " + AuthorUnmatched(alice).Replace(PublishedNameWords.FiltersLabel(), string.Empty, StringComparison.Ordinal)),
            ("a NOT STATED clause", true, null, ["--board", "rust"], PublishedNameWords.FiltersLabel() + NotStated("board", "rust")),
            ("no filter named", true, "[]", [], null),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, empty, json, flags, line) in rows)
        {
            var (exit, stdout, _) = await ServedSearchAsync(json, empty, flags);
            var printed = Lines(stdout).FirstOrDefault(l => l.StartsWith("filters   ", StringComparison.Ordinal));
            var expectedLine = line is null ? "no filters line" : line.StartsWith("filters   ", StringComparison.Ordinal) ? line : PublishedNameWords.FiltersLabel() + line;
            expected.Add($"{label}: exit 0; {expectedLine}; the tail occurs nowhere");
            actual.Add($"{label}: exit {exit}; {printed ?? "no filters line"}; the tail {(stdout.Contains(marker, StringComparison.Ordinal) ? "occurs" : "occurs nowhere")}");
        }

        Said(expected, actual);
    }

    /// <summary>
    /// NMN57 (Amendment 4, ruling 40). A board whose only posts are withheld reads like a board that does not exist, so the two words that tell the reader what to do say so: the hint for a
    /// spelling that is a name says a board whose posts are all withheld reads the same way, and the <c>true</c> receipt line names the second cause with the opposite remedy (this post is on
    /// that board). Both are the published words (parsed) and are asserted to say <c>withheld</c>, so a hint that went back to "check the spelling" alone fails. Falsification FNMN65, FNMN66.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN57_TheHintAndTheTrueReceiptLineSayThatAWithheldBoardReadsTheSame()
    {
        var hint = PublishedNameWords.BoardHintPlain();
        var receiptTrue = PublishedNameWords.ReceiptLines().True.Replace("<board>", Lit("rust"), StringComparison.Ordinal);
        Assert.Contains("withheld", hint, StringComparison.Ordinal);
        Assert.Contains("withheld", receiptTrue, StringComparison.Ordinal);

        var (_, search, _) = await SearchAsync("""["board"]""", "--board", "rustx");
        var (_, post, _) = await PostAsync("ask", "true", "rust");

        Said(
            [$"the search prints the hint: {hint}", $"the receipt prints: {receiptTrue}"],
            [$"the search {(Lines(search).Any(l => l.EndsWith(hint, StringComparison.Ordinal)) ? "prints the hint: " + hint : "does not print the hint")}", $"the receipt {(Lines(post).Contains(receiptTrue) ? "prints: " + receiptTrue : "does not print the line")}"]);
        Assert.DoesNotContain("posts on it are not served", search + post, StringComparison.Ordinal);
    }

    /// <summary>
    /// NMN58 (Amendment 4, ruling 41). The inbox's three states over an empty page, by the published words: a matched board prints its clause and the whole sentence; an unmatched board prints the
    /// filters line and nothing after it; a <c>NOT STATED</c> board prints its clause and the first half alone. Falsification FNMN67.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN58_TheInboxSaysNothingNeedsAnAnswerOnlyWhereTheBoardIsMatched()
    {
        var (_, matched, _) = await InboxAsync(InboxPage("[]", open: 0), "--board", "rust");
        var (_, unmatched, _) = await InboxAsync(InboxPage("""["board"]""", open: 0), "--board", "dotnte");
        var (_, notStated, _) = await InboxAsync(InboxPageOrOlder(null, open: 0), "--board", "rust");

        Said(
            [
                "matched: " + Matched("board", "rust") + " | " + PublishedNameWords.NothingNeedsAnAnswer(),
                "unmatched: " + PublishedNameWords.FiltersLabel() + BoardUnmatched("dotnte", string.Empty),
                "not stated: " + PublishedNameWords.FiltersLabel() + NotStated("board", "rust") + " | " + PublishedNameWords.NoOpenQuestionsAlone(),
            ],
            [
                "matched: " + string.Join(" | ", NonEmpty(matched)),
                "unmatched: " + string.Join(" | ", NonEmpty(unmatched)),
                "not stated: " + string.Join(" | ", NonEmpty(notStated)),
            ]);
    }

    /// <summary>
    /// NMN54. The words reader's paragraph stops where the next one begins: a text in which two <c>**R... words**</c> paragraphs follow one another with no blank line between them
    /// gives each its own spans only (the planted text), and on the errata itself R9.57's paragraph holds none of R11.86 and R11.87's spans (the bleed the review found when a blank line
    /// was missing). The planted text is red without the stop rule; the errata row is a control. Falsification FNMN57.
    /// </summary>
    [Fact]
    public void R10_97_NMN54_AWordsParagraphStopsWhereTheNextOneBegins()
    {
        const string planted = "**R9.55 words.** the member `unmatched_filters`, names `board` and `author`.\n**R9.57 words.** the types `curia/posts/x` and `curia/threads/y`.\n\nProse.\n";

        var first = string.Join(" | ", PublishedNameWords.SpansIn(planted, "**R9.55 words.**"));
        var second = string.Join(" | ", PublishedNameWords.SpansIn(planted, "**R9.57 words.**"));
        var onErrata = PublishedNameWords.UnknownDetailTail();

        Said(
            ["unmatched_filters | board | author", "curia/posts/x | curia/threads/y", "this route reads exactly these: marking"],
            [first, second, onErrata]);
        Assert.DoesNotContain("A string argument is text:", PublishedNameWords.UnknownTypes().Posts, StringComparison.Ordinal);
    }

    private static void Said(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var (e, a) = (string.Join(" | ", expected), string.Join(" | ", actual));
        if (!string.Equals(e, a, StringComparison.Ordinal)) throw new Xunit.Sdk.XunitException($"Expected: {e}{Environment.NewLine}Actual:   {a}");
    }

    private static string Receipt(string digest, string? boardNewJson) =>
        $$"""{"post_id":"01HXNMN33RECEIPT000000000","digest":"{{digest}}","server_ts":"1970-01-01T00:00:00.0000000+00:00","risk_flags":[],"vector_index":"indexed"{{(boardNewJson is null ? string.Empty : ",\"board_new\":" + boardNewJson)}}}""";

    private static async Task<(int Exit, string Stdout, string Stderr)> PostAsync(string verb, string? boardNewJson, string board)
    {
        // Errata G63 (R11.88): the world this receipt is about. A receipt that says the board was new (true) is a world without the board and the post says it means to make it; any other receipt is a
        // world that holds the board, so the check passes on the Forum's list and the line printed beside the receipt is the receipt's.
        var newBoard = boardNewJson == "true";
        using var forum = new RecordingForum
        {
            Boards = newBoard ? [] : [board],
            Responder = (method, path, body) => method == "POST" && path == "/v1/posts" && ReceivedAct.IsSubmission(body)
                ? (201, Receipt(ReceivedAct.DigestOf(body), boardNewJson))
                : null,
        };
        var home = CuriaCli.NewHome(forum.Address);
        try
        {
            string[] argv = verb is "ask" or "finding"
                ? [verb, "--agent", "alice", "--board", board, "--title", "A title", "--body", "A body", .. newBoard ? new[] { "--new-board" } : [], "--forum", forum.Address.ToString()]
                : [verb, "--agent", "alice", "--board", board, "--parent", "01ARZ3NDEKTSV4RRFFQ69G5FAV", "--body", "A body", "--forum", forum.Address.ToString()];
            return await CuriaCli.RunAsync(home, Ct, argv);
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// NMN33. The receipt of a question and a finding: one <c>board_new</c> line after the <c>kind</c> line in each of the three states (true, false, <c>NOT STATED</c> for a receipt
    /// that had none), with the board a display literal; <c>curia answer</c> and <c>curia comment</c> print none even where the receipt carries the member. Predicted red on the seam: no
    /// line for the question and the finding. The resend's receipt (the same <c>PrintPostReceipt</c>) is NMN45's; the one definition's fence is NMN42.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN33_AQuestionAndAFindingPrintTheBoardNewLineInEveryStateAndNoOtherKindDoes()
    {
        var (lineTrue, lineFalse, lineNotStated) = PublishedNameWords.ReceiptLines();
        string Of(string line, string board) => line.Replace("<board>", Lit(board), StringComparison.Ordinal);
        var rows = new (string Verb, string? Json, string Expected)[]
        {
            ("ask", "false", Of(lineFalse, "rust")),
            ("ask", "true", Of(lineTrue, "rust")),
            ("ask", null, Of(lineNotStated, "rust")),
            ("finding", "false", Of(lineFalse, "rust")),
            ("finding", "true", Of(lineTrue, "rust")),
            ("finding", null, Of(lineNotStated, "rust")),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (verb, json, line) in rows)
        {
            var (exit, stdout, stderr) = await PostAsync(verb, json, "rust");
            var lines = Lines(stdout);
            var kindAt = Array.FindIndex(lines, l => l.StartsWith("kind      ", StringComparison.Ordinal));
            expected.Add($"{verb} {json ?? "(no member)"}: exit 0, the line right after the kind line is: {line}");
            actual.Add($"{verb} {json ?? "(no member)"}: exit {exit}, the line right after the kind line is: {(kindAt >= 0 && kindAt + 1 < lines.Length ? lines[kindAt + 1] : "(none) stderr: " + stderr)}");
        }

        foreach (var verb in new[] { "answer", "comment" })
        {
            var (exit, stdout, _) = await PostAsync(verb, "true", "rust");
            expected.Add($"{verb}: exit 0, no board_new line");
            actual.Add($"{verb}: exit {exit}, {(stdout.Contains("board_new", StringComparison.Ordinal) ? "a board_new line" : "no board_new line")}");
        }

        Said(expected, actual);
    }

    /// <summary>A kept question, finding or answer, planted as a host leaves one after a send that was not answered, sent by <c>curia resend</c> to a recorder that answers a receipt with the given <c>board_new</c>.</summary>
    private static async Task<(int Exit, string Stdout, string Stderr)> ResendAsync(string kind, string? boardNewJson, string board)
    {
        using var forum = new RecordingForum
        {
            Responder = (method, path, body) => method == "POST" && path == "/v1/posts" && ReceivedAct.IsSubmission(body)
                ? (201, Receipt(ReceivedAct.DigestOf(body), boardNewJson))
                : null,
        };
        var home = CuriaCli.NewHome(forum.Address);
        try
        {
            var store = new ProfileStore(home);
            Assert.True(store.Load("alice").TryGetValue(out var agent, out var loadError), loadError?.Type);
            using (agent)
            {
                var wire = kind switch
                {
                    "question" => PostKind.Question,
                    "finding" => PostKind.Finding,
                    _ => PostKind.Answer,
                };
                var draft = new PostDraft
                {
                    Kind = wire,
                    Board = board,
                    Title = kind == "answer" ? null : "A title",
                    Parent = kind == "answer" ? "01ARZ3NDEKTSV4RRFFQ69G5FAV" : null,
                    Body = "A kept body",
                };
                Assert.True(SubmissionBuilder.Build(agent!, draft, DateTimeOffset.UtcNow).TryGetValue(out var signed, out var error), error?.Type);
                KeptActFiles.Plant(Path.Combine(store.DirectoryFor("alice"), "acts"), signed!, kind, forum.Address.ToString().TrimEnd('/'), agent!.Profile.AgentId, attempts: 1, lastAnswer: "503", board: board);

                return await CuriaCli.RunAsync(home, Ct, ["resend", signed.PrefixedDigest, "--agent", "alice", "--forum", forum.Address.ToString()]);
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    /// <summary>
    /// NMN45 (Amendment 3, ruling 34). R10.97 says every receipt carries the line: <c>curia resend</c> of a kept question and of a kept finding prints the <c>board_new</c> line after the
    /// <c>kind</c> line for <c>true</c>, <c>false</c> and a receipt that had none, in the published words (parsed from the errata, the board a display literal), and a kept answer prints
    /// none even where the receipt carries the member. The verb's own send is NMN33; this is the same line through <c>PrintPostReceipt</c> from the kept act's header. <b>A control on
    /// the tree</b> (the resend already prints the line; the build left the probe unwritten): its value is FNMN49.
    /// </summary>
    [Fact]
    public async Task R10_97_NMN45_AResendOfAKeptQuestionAndOfAKeptFindingPrintsTheBoardNewLineInEveryState_Control()
    {
        var (lineTrue, lineFalse, lineNotStated) = PublishedNameWords.ReceiptLines();
        string Of(string line, string board) => line.Replace("<board>", Lit(board), StringComparison.Ordinal);
        var rows = new (string Kind, string? Json, string Expected)[]
        {
            ("question", "false", Of(lineFalse, "rust")),
            ("question", "true", Of(lineTrue, "rust")),
            ("question", null, Of(lineNotStated, "rust")),
            ("finding", "false", Of(lineFalse, "rust")),
            ("finding", "true", Of(lineTrue, "rust")),
            ("finding", null, Of(lineNotStated, "rust")),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (kind, json, line) in rows)
        {
            var (exit, stdout, stderr) = await ResendAsync(kind, json, "rust");
            var lines = Lines(stdout);
            var kindAt = Array.FindIndex(lines, l => l.StartsWith("kind      ", StringComparison.Ordinal));
            expected.Add($"resend {kind} {json ?? "(no member)"}: exit 0, the line right after the kind line is: {line}");
            actual.Add($"resend {kind} {json ?? "(no member)"}: exit {exit}, the line right after the kind line is: {(kindAt >= 0 && kindAt + 1 < lines.Length ? lines[kindAt + 1] : "(none) stderr: " + stderr)}");
        }

        var (answerExit, answerOut, _) = await ResendAsync("answer", "true", "rust");
        expected.Add("resend answer: exit 0, no board_new line");
        actual.Add($"resend answer: exit {answerExit}, {(answerOut.Contains("board_new", StringComparison.Ordinal) ? "a board_new line" : "no board_new line")}");

        Said(expected, actual);
    }

    private static string FloorLine(string stdout) => Lines(stdout).Single(l => l.StartsWith("floor     ", StringComparison.Ordinal));

    /// <summary>
    /// NMN34. The floor line says what the floor was tested against: the published template with its lists as display literals, <c>not stated</c> for a missing list, neither
    /// <c>applies to</c> nor <c>; not to </c>. Predicted red on the seam: the old words (CHN58's bytes).
    /// </summary>
    [Fact]
    public async Task R10_97_NMN34_TheFloorLineSaysWhatTheFloorWasTestedAgainst()
    {
        using var served = new StubLog();
        using var oneMissing = new StubLog { FloorListsJson = "\"applies_to\":[\"answer\"]" };
        var template = PublishedNameWords.FloorTemplate();

        async Task<string> LineAsync(StubLog log)
        {
            var args = Args.Parse(["search", "x"], 1);
            var (_, stdout, _) = await CaptureAsync(() => Program.SearchPageAsync(args, "x", null, log.Client(), MarkingMode.None, null, Ct));
            return FloorLine(stdout);
        }

        string Expected(string applies, string notApplies) => template
            .Replace("<level>", "\"V0\"", StringComparison.Ordinal)
            .Replace("(<source>, <surface>)", "(\"published\", \"mcp-search\")", StringComparison.Ordinal)
            .Replace("<applies_to>", applies, StringComparison.Ordinal)
            .Replace("<not_applicable_to>", notApplies, StringComparison.Ordinal)
            .Replace("removed by floor <n>", "removed by floor not stated", StringComparison.Ordinal)
            .Replace("disputed <n>", "disputed 0", StringComparison.Ordinal);

        var both = await LineAsync(served);
        var missing = await LineAsync(oneMissing);

        Said(
            [Expected("\"answer\", \"finding\"", "\"question\", \"comment\""), Expected("\"answer\"", "not stated"), "neither retired phrase occurs"],
            [both, missing, PublishedNameWords.FloorRetiredPhrases().Any(p => both.Contains(p, StringComparison.Ordinal) || missing.Contains(p, StringComparison.Ordinal)) ? "a retired phrase occurs" : "neither retired phrase occurs"]);
    }
}
