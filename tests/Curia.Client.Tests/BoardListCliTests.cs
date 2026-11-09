using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.BoardListHarness;
using static Curia.Client.Tests.ConsoleCapture;

namespace Curia.Client.Tests;

/// <summary>
/// R10.98 and R11.88 (errata G63), LOB19 to LOB31, in <c>curia</c>: the listing's words, the board line every <c>ask</c> and <c>finding</c> prints in one of four states, the refusal of a board with no served post, the
/// refusal of a list that could not be read, <c>--new-board</c>, the older Forum read by type, the order of checks, the number of list requests, the empty board's words, the helps and a hostile list.
///
/// <para><b>Oracles.</b> Every sentence is parsed from the errata (<see cref="PublishedBoardListWords"/>) with the typed or served name written as a display literal (<see cref="DisplayLiteral"/>, errata G17); no
/// expected line is copied from the code. The Forum is a recorder that serves a table (<see cref="BoardListHarness"/>), so a fact reads what a command <i>sent</i> as well as what it printed. A refusing fact has its
/// control (the same command on a board that is listed) in the same assertion, and the four states are asserted together (LOB21), so a positive printed for every board or never cannot pass.
/// Predicted red on the seam: the client reads no list and prints no board line, so every fact that expects a line, a refusal or a request is red; LOB23's unknown-flag half, LOB24's and LOB25's "post goes"
/// halves where they are asserted last, and LOB27's request-free orders are green controls.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class BoardListCliTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Board Rust = new("rust", 41, 6);

    private static string Lit(string name) => DisplayLiteral.Of(name);

    /// <summary>Whether the text refuses <c>--new-board</c> as an unknown flag (the client writes the flag as a display literal).</summary>
    private static bool UnknownFlag(string text) => text.Contains("unknown flag", StringComparison.Ordinal) && text.Contains("--new-board", StringComparison.Ordinal);

    /// <summary>The words check: every span the facts use is found exactly once, and the four states and four types are in the published order (a control, green on the seam).</summary>
    [Fact]
    public void R10_98_LOB20_ThePublishedWordsAreTheOnesTheFactsUse()
    {
        var (listed, byFlag, notStated, withFlag) = PublishedBoardListWords.States();
        Assert.StartsWith("THE FORUM SERVES POSTS ON IT (", listed, StringComparison.Ordinal);
        Assert.Contains("--new-board was given", byFlag, StringComparison.Ordinal);
        Assert.StartsWith("NOT STATED (", notStated, StringComparison.Ordinal);
        Assert.EndsWith("--new-board was not needed and changes nothing", withFlag, StringComparison.Ordinal);
        Assert.Equal("boards    ", PublishedBoardListWords.LineLabel());
        Assert.Equal(20, PublishedBoardListWords.RefusalCap());
        Assert.Equal("there is no endpoint that enumerates boards", PublishedBoardListWords.RetiredPhrase());
    }

    // ---- LOB19 ----

    /// <summary>
    /// LOB19, the page reader (<see cref="ForumClient.GetBoardsAsync"/> over the stub): a page with a member absent (<c>boards</c>, <c>total</c>, <c>corpus_bound</c>), a <c>boards</c> that is not an array, a row
    /// without <c>posts</c> or with a <c>posts</c> that is not a whole number, is a malformed page and never an empty list; the control, a well-formed page, reads. Predicted red on the seam: the lenient reader
    /// reads each malformed page as what it has.
    /// </summary>
    [Fact]
    public async Task R11_88_LOB19_AMalformedPageIsNeverAnEmptyList()
    {
        const string row = """{"board":"rust","posts":3,"authors":2,"latest_post_at":"2026-11-05T12:00:00.0000000+00:00","provenance":{"content_type":"agent-authored/untrusted","warning":"w","reader_contract":"/c"}}""";
        var pages = new (string Label, string Json, bool Reads)[]
        {
            ("well formed", $$"""{"boards":[{{row}}],"total":1,"corpus_bound":9}""", true),
            ("no boards", """{"total":0,"corpus_bound":9}""", false),
            ("no total", """{"boards":[],"corpus_bound":9}""", false),
            ("no corpus_bound", """{"boards":[],"total":0}""", false),
            ("boards not an array", """{"boards":{},"total":0,"corpus_bound":9}""", false),
            ("a row with no posts", """{"boards":[{"board":"rust","authors":1}],"total":1,"corpus_bound":9}""", false),
            ("posts not a whole number", """{"boards":[{"board":"rust","posts":1.5,"authors":1}],"total":1,"corpus_bound":9}""", false),
            ("not an object", "[]", false),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, json, reads) in pages)
        {
            using var log = new StubLog { BoardListBody = json };
            var result = await log.Client().GetBoardsAsync(null, null, null, Ct);
            var read = result.TryGetValue(out var page, out _);
            expected.Add($"{label}: {(reads ? "read" : "malformed")}");
            actual.Add($"{label}: {(read ? (page!.Boards.IsEmpty ? "read as an empty list" : "read") : "malformed")}");
        }

        Said(expected, actual);
    }

    // ---- LOB20 ----

    /// <summary>
    /// LOB20, the listing's words. <c>curia boards</c> over a page of three of 213 rows (a hostile name, a row with no <c>latest_post_at</c>): the header, a row line each in the published shape with the name as a display
    /// literal, the counts written bare (no quotation mark touches a count: a <c>FrameText</c> hole would quote it), <c>NOT STATED</c> for the member the Forum did not state, a preface before the header, and the
    /// <c>more</c> line with the cursor written as a shell word; a last page says <c>no further pages</c>. Predicted red on the seam: nothing is printed.
    /// </summary>
    [Fact]
    public async Task R10_98_LOB20_TheListingIsPrintedInThePublishedWordsWithTheCountsBareAndTheNamesAsLiterals()
    {
        var hostile = "esc" + char.ConvertFromUtf32(0x1B) + "[31mred";
        var rows = new[] { new Board("rust", 41, 6), new Board(hostile, 3, 2, null), new Board("go", 2, 2) };
        using var forum = Forum(listAnswer: _ => (200, PageJson(rows, 213, "bmV4dA==")));
        using var last = Forum(listAnswer: _ => (200, PageJson(rows, 3)));

        var page = await RunAsync(forum, Ct, "boards");
        var final = await RunAsync(last, Ct, "boards");
        var lines = Lines(page.Stdout);

        var header = PublishedBoardListWords.ListingHeader().Replace("<k>", "3", StringComparison.Ordinal).Replace("<total>", "213", StringComparison.Ordinal);
        string RowLine(Board b, string latest) => PublishedBoardListWords.ListingRow().Replace("<name>", Lit(b.Name), StringComparison.Ordinal)
            .Replace("<posts>", b.Posts.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("<authors>", b.Authors.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("<latest_post_at>", latest, StringComparison.Ordinal);
        // The cursor is a value in a command the reader may run, so it is written as a shell word (R10.65, errata G17): the oracle is a shell, which is neither the errata nor Hints (Amendment 2, ruling 27, trap 196).
        var moreTemplate = PublishedBoardListWords.MoreLine();
        var morePrefix = moreTemplate[..moreTemplate.IndexOf("<next_cursor>", StringComparison.Ordinal)];
        var moreLine = lines.FirstOrDefault(l => l.StartsWith(morePrefix, StringComparison.Ordinal));
        var shellRead = moreLine is null ? ("no more line", string.Empty) : await BoardListArgumentTests.ShellAsync(moreLine[morePrefix.Length..]);
        var more = "the more line: the template and a word the shell reads as bmV4dA==";
        string[] expected =
        [
            "exit 0",
            header,
            RowLine(rows[0], "2026-11-05T12:00:00.0000000+00:00"),
            RowLine(rows[1], PublishedBoardListWords.NotStatedWord()),
            RowLine(rows[2], "2026-11-05T12:00:00.0000000+00:00"),
            more,
            "a preface before the header",
            "no count is quoted",
            "the last page: " + PublishedBoardListWords.NoFurtherPages(),
        ];
        var headerAt = Array.IndexOf(lines, header);
        var bare = !Regex.IsMatch(page.Stdout, "\"\\d+\"");
        string[] actual =
        [
            "exit " + page.Exit,
            headerAt >= 0 ? header : "no header line",
            .. Enumerable.Range(1, 3).Select(i => headerAt >= 0 && headerAt + i < lines.Length ? lines[headerAt + i] : "no row line"),
            moreLine is null ? "no more line" : shellRead is ("bmV4dA==", "") ? more : $"the more line: the shell read {Lit(shellRead.Item1)} and said {Lit(shellRead.Item2)}",
            headerAt > 0 && lines[..headerAt].Any(l => l.Length > 0) ? "a preface before the header" : "no preface",
            bare && page.Stdout.Length > 0 ? "no count is quoted" : "a count is quoted or nothing is printed",
            "the last page: " + (Lines(final.Stdout).Contains(PublishedBoardListWords.NoFurtherPages()) ? PublishedBoardListWords.NoFurtherPages() : "no closing line"),
        ];
        Said(expected, actual);
    }

    // ---- LOB21 ----

    /// <summary>
    /// LOB21, the four states together (a positive beside its negation). One test runs a board the Forum lists, one it does not with <c>--new-board</c>, an older Forum, and a listed board with <c>--new-board</c>; each
    /// prints its published board line, exits 0 and sends the post once. A positive printed for every board, or never, cannot pass. Predicted red on the seam: no board line in any state.
    /// </summary>
    [Fact]
    public async Task R10_98_LOB21_TheFourStatesOfTheBoardLineAreEachPrintedForTheirOwnCase()
    {
        var (listed, byFlag, notStated, withFlag) = PublishedBoardListWords.States();
        using var healthy = Forum([Rust]);
        using var older = Forum(listAnswer: _ => (404, """{"type":"curia/request/no-route","title":"No such route"}"""));

        var a = await RunAsync(healthy, Ct, Ask("rust"));
        var b = await RunAsync(healthy, Ct, Ask("rsut", "--new-board"));
        var c = await RunAsync(older, Ct, Ask("rust"));
        var d = await RunAsync(healthy, Ct, Ask("rust", "--new-board"));

        var cases = new (string Label, Outcome Run, string Line)[]
        {
            ("listed", a, BoardLine(listed, "rust", 41, 6)),
            ("not listed, flag given", b, BoardLine(byFlag, "rsut")),
            ("older Forum", c, BoardLine(notStated, "rust")),
            ("listed, flag given", d, BoardLine(withFlag, "rust", 41, 6)),
        };
        Said(
            cases.Select(x => $"{x.Label}: exit 0, 1 post, the line {x.Line}"),
            cases.Select(x => $"{x.Label}: exit {x.Run.Exit}, {x.Run.Posts} post, {(Lines(x.Run.Stdout).Contains(x.Line) ? "the line " + x.Line : "no such line")}"));
    }

    // ---- LOB22 ----

    /// <summary>
    /// LOB22, the refusal of a board with no served post, in the published words: <c>NOT SENT.</c>, the sentence naming the board as a display literal and the kind, "cannot be moved or taken back", at most 20 boards
    /// with their counts, <c>(20 of 25; curia boards lists the rest)</c> where there are more, both ways forward and the withheld clause; exit 1 and <b>nothing sent</b>: no <c>POST</c>, no token request, no kept act. The
    /// control, the same command on a listed board, posts. Predicted red on the seam: the post goes.
    /// </summary>
    [Fact]
    public async Task R11_88_LOB22_ABoardWithNoServedPostIsRefusedInThePublishedWordsAndNothingIsSent()
    {
        var many = Enumerable.Range(0, 25).Select(i => new Board($"board-{i:D2}", 30 - i, 2)).ToArray();
        using var forum = Forum([.. many, Rust]);
        using var smallForum = Forum([Rust, new Board("go", 5, 2)]);

        var control = await RunAsync(forum, Ct, Ask("rust"));
        var refused = await RunAsync(forum, Ct, Ask("rsut"));
        var small = await RunAsync(smallForum, Ct, Ask("rsut"));
        var text = Fold(refused.All);
        var smallText = Fold(small.All);

        var sentence = PublishedBoardListWords.RefusalSentence().Replace("<name>", Lit("rsut"), StringComparison.Ordinal).Replace("<kind>", "question", StringComparison.Ordinal);
        var ways = PublishedBoardListWords.RefusalWaysForward().Replace("<name>", Lit("rsut"), StringComparison.Ordinal);
        var more = PublishedBoardListWords.RefusalMoreClause().Replace("<n>", "20", StringComparison.Ordinal).Replace("<total>", "26", StringComparison.Ordinal);
        var named = string.Join(", ", many.Concat([Rust]).OrderByDescending(b => b.Posts).ThenBy(b => b.Name, StringComparer.Ordinal).Take(20).Select(b => $"{Lit(b.Name)} {b.Posts}"));

        string[] expected =
        [
            "control: exit 0, 1 post",
            "refused: exit 1, no post, no token, no kept act",
            $"says: {PublishedBoardListWords.NotSent()}; {sentence}; {PublishedBoardListWords.RefusalBoardsLead()} {named}; {more}; {ways}",
            "a list of two names carries no count clause",
        ];
        string[] actual =
        [
            $"control: exit {control.Exit}, {control.Posts} post",
            $"refused: exit {refused.Exit}, {(refused.Posts == 0 ? "no post" : refused.Posts + " post")}, {(refused.Tokens == 0 ? "no token" : "a token was requested")}, {(refused.KeptActs.Length == 0 ? "no kept act" : "a kept act")}",
            "says: " + string.Join("; ", new[]
            {
                text.Contains(PublishedBoardListWords.NotSent(), StringComparison.Ordinal) ? PublishedBoardListWords.NotSent() : "no NOT SENT.",
                text.Contains(sentence, StringComparison.Ordinal) ? sentence : "not the sentence",
                text.Contains(PublishedBoardListWords.RefusalBoardsLead() + " " + named, StringComparison.Ordinal) ? PublishedBoardListWords.RefusalBoardsLead() + " " + named : "not the twenty boards",
                text.Contains(more, StringComparison.Ordinal) ? more : "no count clause",
                text.Contains(ways, StringComparison.Ordinal) ? ways : "not the ways forward",
            }),
            smallText.Contains("lists the rest", StringComparison.Ordinal) ? "a list of two names carries a count clause" : "a list of two names carries no count clause",
        ];
        Said(expected, actual);
    }

    // ---- LOB23 ----

    /// <summary>
    /// LOB23, <c>--new-board</c>: on an unlisted board the post is sent; <c>ask</c> and <c>finding</c> accept the flag; <c>answer</c>, <c>comment</c>, <c>revision</c> and <c>endorse</c> refuse it as an unknown flag, exit 1,
    /// nothing sent. Predicted red on the seam for the first two (<c>ask</c> and <c>finding</c> refuse the flag as unknown); the four refusals are green controls.
    /// </summary>
    [Fact]
    public async Task R11_88_LOB23_TheFlagIsAcceptedByAskAndFindingAndByNoOtherVerb()
    {
        using var forum = Forum([Rust]);
        var ask = await RunAsync(forum, Ct, Ask("fresh", "--new-board"));
        var finding = await RunAsync(forum, Ct, "finding", "--agent", "alice", "--board", "fresh", "--title", "t", "--body", "b", "--new-board");

        string[] others = ["answer", "comment", "revision"];
        var expected = new List<string> { "ask: exit 0, 1 post", "finding: exit 0, 1 post" };
        var actual = new List<string> { $"ask: exit {ask.Exit}, {ask.Posts} post", $"finding: exit {finding.Exit}, {finding.Posts} post" };
        foreach (var verb in others)
        {
            var run = await RunAsync(forum, Ct, verb, "--agent", "alice", "--parent", "01ARZ3NDEKTSV4RRFFQ69G5FAV", "--body", "b", "--new-board");
            expected.Add($"{verb}: exit 1, unknown flag --new-board, no post");
            actual.Add($"{verb}: exit {run.Exit}, {(UnknownFlag(run.Stderr) ? "unknown flag --new-board" : "not refused as unknown")}, {(run.Posts == 0 ? "no post" : "a post")}");
        }

        var endorse = await RunAsync(forum, Ct, "endorse", "sha256:" + new string('a', 64), "--agent", "alice", "--board", "b", "--new-board");
        expected.Add("endorse: exit 1, unknown flag --new-board, no post");
        actual.Add($"endorse: exit {endorse.Exit}, {(UnknownFlag(endorse.Stderr) ? "unknown flag --new-board" : "not refused as unknown")}, {(endorse.Posts == 0 ? "no post" : "a post")}");
        Said(expected, actual);
    }

    // ---- LOB24 ----

    /// <summary>
    /// LOB24, the older Forum is read by type. <c>404</c> with <c>curia/request/no-route</c> is <c>NOT STATED</c> and the post goes; a bare <c>404</c>, <c>404</c> <c>curia/posts/not-found</c> and <c>405</c> are exit 8
    /// with no post (a proxy's or another route's <c>404</c> is not an older Forum). Predicted red on the seam: no <c>NOT STATED</c> line, and the post goes for the three that must not.
    /// </summary>
    [Fact]
    public async Task R11_88_LOB24_OnlyTheNoRouteTypeIsAnOlderForum()
    {
        var notStated = BoardLine(PublishedBoardListWords.States().NotStated, "rust");
        var answers = new (string Label, int Status, string Body, bool Passes)[]
        {
            ("404 curia/request/no-route", 404, """{"type":"curia/request/no-route","title":"No such route"}""", true),
            ("a bare 404", 404, string.Empty, false),
            ("404 curia/posts/not-found", 404, """{"type":"curia/posts/not-found","title":"No such post"}""", false),
            ("405", 405, string.Empty, false),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, status, body, passes) in answers)
        {
            using var forum = Forum(listAnswer: _ => (status, body));
            var run = await RunAsync(forum, Ct, Ask("rust"));
            expected.Add($"{label}: {(passes ? "exit 0, 1 post, NOT STATED" : "exit 8, no post")}");
            actual.Add($"{label}: exit {run.Exit}, {(run.Posts == 0 ? "no post" : "1 post")}{(Lines(run.Stdout).Contains(notStated) ? ", NOT STATED" : string.Empty)}");
        }

        Said(expected, actual);
    }

    // ---- LOB25 ----

    /// <summary>
    /// LOB25, every fault. A <c>503</c> <c>curia/log/unreadable</c>, a <c>503</c> <c>curia/store/unavailable</c>, a <c>500</c>, a <c>403</c> and a <c>200</c> that is not the document are each exit 8, name the cause, say
    /// the published tail (this run signed nothing and kept nothing, when to run again and the act-line condition), never mention <c>--new-board</c>, send no post and keep no act; and the same command posts once the list is served again.
    /// (A reset and a stall past the check's own bound are the live run's L5; the stub cannot reset.) Predicted red on the seam: the post goes for every fault.
    /// </summary>
    [Fact]
    public async Task R11_88_LOB25_AListThatCannotBeReadIsRefusedWithTheCauseAndTheFlagIsNotOffered()
    {
        var tail = PublishedBoardListWords.FaultTail();
        var lead = PublishedBoardListWords.FaultSentence()[..PublishedBoardListWords.FaultSentence().IndexOf("(<cause>)", StringComparison.Ordinal)];
        var flag = PublishedBoardListWords.FaultForbiddenFlag();
        var faults = new (string Label, int Status, string Body)[]
        {
            ("503 log unreadable", 503, """{"type":"curia/log/unreadable","title":"The event log could not be read"}"""),
            ("503 store unavailable", 503, """{"type":"curia/store/unavailable","title":"A store the Forum owns is unavailable"}"""),
            ("500", 500, """{"type":"curia/server/fault","title":"Fault"}"""),
            ("403", 403, """{"type":"curia/authz/denied","title":"Denied"}"""),
            ("a 200 that is no listing", 200, """{"hello":"world"}"""),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, status, body) in faults)
        {
            using var forum = Forum(listAnswer: _ => (status, body));
            var run = await RunAsync(forum, Ct, Ask("rust"));
            var text = Fold(run.All);
            expected.Add($"{label}: exit 8, {PublishedBoardListWords.NotSent()}, the lead, the published tail, no flag, no post, no kept act");
            actual.Add($"{label}: exit {run.Exit}, {(text.Contains(PublishedBoardListWords.NotSent(), StringComparison.Ordinal) ? PublishedBoardListWords.NotSent() : "no NOT SENT.")}, {(text.Contains(lead.Replace("<name>", Lit("rust"), StringComparison.Ordinal), StringComparison.Ordinal) ? "the lead" : "no lead")}, {(text.Contains(tail, StringComparison.Ordinal) ? "the published tail" : "no published tail")}, {(text.Contains(flag, StringComparison.Ordinal) ? "the flag is offered" : "no flag")}, {(run.Posts == 0 ? "no post" : "a post")}, {(run.KeptActs.Length == 0 ? "no kept act" : "a kept act")}");
        }

        using var recovering = Forum([Rust]);
        var again = await RunAsync(recovering, Ct, Ask("rust"));
        expected.Add("once the list is served: exit 0, 1 post");
        actual.Add($"once the list is served: exit {again.Exit}, {again.Posts} post");
        Said(expected, actual);
    }

    // ---- LOB26 ----

    /// <summary>
    /// LOB26, by name and not by membership. A Forum of 213 boards whose first page of 50 does not hold the typed board answers the <c>board=</c> read with its row: the post goes with the listed line. A client that
    /// compared with page one would refuse it. Predicted red on the seam: no line (the post goes, which is the wrong thing for the right reason).
    /// </summary>
    [Fact]
    public async Task R11_88_LOB26_ABoardPastTheFirstPageIsFoundByItsOwnName()
    {
        var boards = Enumerable.Range(0, 212).Select(i => new Board($"many-{i:D3}", 400 - i, 2)).Append(new Board("rust", 1, 1)).ToArray();
        using var forum = Forum(boards);

        var run = await RunAsync(forum, Ct, Ask("rust"));
        var line = BoardLine(PublishedBoardListWords.States().Listed, "rust", 1, 1);

        Said(
            [$"exit 0, 1 post, the line {line}"],
            [$"exit {run.Exit}, {run.Posts} post, {(Lines(run.Stdout).Contains(line) ? "the line " + line : "no such line")}"]);
    }

    // ---- LOB27 ----

    /// <summary>
    /// LOB27, the order of checks. The check follows every refusal the client makes on its own and precedes the signature: <c>RUST</c> (R8.85), a missing title, an empty body and a profile that cannot be resolved (the tag and null-item rows of R11.88 are the adapter's, in <c>BoardToolTests</c>: the CLI's <c>--tags</c> drops an empty item before any rule sees it)
    /// are each refused for their own fault, send no <c>GET /v1/boards</c> and no post; an answer, a comment, a revision and a resend make no <c>GET /v1/boards</c>. The control is the same command on a listed board.
    /// Predicted: green controls on the seam (the client reads no list at all); the rows are FLOB32, FLOB33, FLOB34 and FLOB53's first forms.
    /// </summary>
    [Fact]
    public async Task R11_88_LOB27_TheListIsReadAfterEveryLocalRefusalAndForNoReply_Control()
    {
        using var forum = Forum([Rust]);
        var rows = new (string Label, string[] Argv, string Mentions)[]
        {
            ("upper case", Ask("RUST"), "lower"),
            ("no title", ["ask", "--agent", "alice", "--board", "fresh", "--body", "b"], "title"),
            ("an empty body", ["ask", "--agent", "alice", "--board", "fresh", "--title", "t", "--body", string.Empty], "body"),
            ("an unknown profile", ["ask", "--agent", "nobody", "--board", "fresh", "--title", "t", "--body", "b"], "nobody"),
            ("an answer", ["answer", "--agent", "alice", "--parent", "01ARZ3NDEKTSV4RRFFQ69G5FAV", "--body", "b"], string.Empty),
            ("a comment", ["comment", "--agent", "alice", "--parent", "01ARZ3NDEKTSV4RRFFQ69G5FAV", "--body", "b"], string.Empty),
        };

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, argv, _) in rows)
        {
            var run = await RunAsync(forum, Ct, argv);
            expected.Add($"{label}: no list request, no post, not the list's refusal");
            actual.Add($"{label}: {(run.ListRequests == 0 ? "no list request" : run.ListRequests + " list request(s)")}, {(run.Posts == 0 ? "no post" : "a post")}, {(run.All.Contains("would start a new board", StringComparison.Ordinal) ? "the list's refusal" : "not the list's refusal")}");
        }

        Said(expected, actual);
    }

    // ---- LOB28 ----

    /// <summary>LOB28, request count: a pass makes one list request (the filtered read), a refusal two (the filtered read, then page one to name the boards). Predicted red on the seam: none made.</summary>
    [Fact]
    public async Task R11_88_LOB28_APassReadsTheListOnceAndARefusalTwice()
    {
        using var forum = Forum([Rust, new Board("go", 5, 2)]);

        var pass = await RunAsync(forum, Ct, Ask("rust"));
        var refusal = await RunAsync(forum, Ct, Ask("rsut"));

        Said(["pass: 1 list request, 1 post", "refusal: 2 list requests, 0 posts"], [$"pass: {pass.ListRequests} list request, {pass.Posts} post", $"refusal: {refusal.ListRequests} list requests, {refusal.Posts} posts"]);
    }

    // ---- LOB29 ----

    /// <summary>
    /// LOB29, an empty board's words: <c>curia board &lt;name&gt;</c> and the screen's <c>EmptyBoard</c> print the two published lines with the name as a display literal, and the sentence "there is no endpoint that
    /// enumerates boards" occurs in neither (asserted by content, not by identifier). Predicted red on the seam: the old sentence is still printed.
    /// </summary>
    [Fact]
    public async Task R10_98_LOB29_AnEmptyBoardIsToldWhatIsTrueNow()
    {
        using var forum = new RecordingForum { Responder = (m, p, _) => m == "GET" && p == "/v1/boards/nothing-here/posts" ? (200, "[]") : null };
        var line = await RunAsync(forum, Ct, "board", "nothing-here");
        var screen = await CaptureAsync(() => Task.FromResult(Program.EmptyBoard("nothing-here")));

        var first = PublishedBoardListWords.EmptyFirstLine().Replace("<name>", Lit("nothing-here"), StringComparison.Ordinal);
        var second = PublishedBoardListWords.EmptySecondLine();
        var retired = PublishedBoardListWords.RetiredPhrase();

        string Describe(string stdout) => $"{(Lines(stdout).Contains(first) ? "first line" : "no first line")}, {(Fold(stdout).Contains(second, StringComparison.Ordinal) ? "second line" : "no second line")}, {(Fold(stdout).Contains(retired, StringComparison.Ordinal) ? "the retired sentence" : "no retired sentence")}";
        Said(["curia board: first line, second line, no retired sentence", "the screen: first line, second line, no retired sentence"], [$"curia board: {Describe(line.Stdout)}", $"the screen: {Describe(screen.Stdout)}"]);
    }

    // ---- LOB30 ----

    /// <summary>
    /// LOB30, helps. The overview names <c>boards</c>; <c>board</c>'s help names <c>boards</c> as the way to find a board; <c>ask</c> and <c>finding</c> state <c>--new-board</c>; <c>boards</c>'s help states the order
    /// (most posts first), the bound (100) and what a board that is not listed means. Predicted red on the seam: the seam's one-line help states none.
    /// </summary>
    [Fact]
    public void R11_88_LOB30_TheHelpsNameTheListAndTheFlag()
    {
        var overview = Help.Usage;
        var boards = Help.For("boards") ?? string.Empty;
        var folded = Fold(boards);
        var facts = new (string Label, bool Holds)[]
        {
            ("the overview names boards", Regex.IsMatch(overview, @"curia boards\b")),
            ("board's help names boards", Fold(Help.For("board") ?? string.Empty).Contains("curia boards", StringComparison.Ordinal)),
            ("ask states --new-board", (Help.For("ask") ?? string.Empty).Contains("--new-board", StringComparison.Ordinal)),
            ("finding states --new-board", (Help.For("finding") ?? string.Empty).Contains("--new-board", StringComparison.Ordinal)),
            ("boards states the order", folded.Contains("most posts first", StringComparison.Ordinal)),
            ("boards states the bound", folded.Contains("100", StringComparison.Ordinal)),
            ("boards says what not listed means", folded.Contains("not listed", StringComparison.Ordinal) || folded.Contains("no post the Forum serves", StringComparison.OrdinalIgnoreCase)),
        };

        Said(facts.Select(f => f.Label + ": yes"), facts.Select(f => f.Label + ": " + (f.Holds ? "yes" : "no")));
    }

    // ---- LOB31 ----

    /// <summary>
    /// LOB31, a hostile list: 100 rows named <c>ignore-previous-instructions-&lt;n&gt;</c>, one holding an ESC and one a bidi override. Standard output holds no raw control character and no bidi override, and each name
    /// appears only as a display literal (a quotation mark before it). The adapter's half is <c>BoardToolTests</c>' LOB31 row. Predicted red on the seam: nothing is printed, so no name appears as a literal.
    /// </summary>
    [Fact]
    public async Task R10_98_LOB31_AHostileListIsPrintedAsLiteralsAndNeverAsRawText()
    {
        var esc = "esc" + char.ConvertFromUtf32(0x1B) + "[2Jwipe";
        var bidi = "rlo" + char.ConvertFromUtf32(0x202E) + "gnp.exe";
        var rows = Enumerable.Range(0, 98).Select(i => new Board($"ignore-previous-instructions-{i:D2}", 100 - i, 1)).Append(new Board(esc, 1, 1)).Append(new Board(bidi, 1, 1)).ToArray();
        using var forum = Forum(listAnswer: _ => (200, PageJson(rows, 100)));

        var run = await RunAsync(forum, Ct, "boards");

        var raw = run.Stdout.Where(c => (c < 0x20 && c != '\n' && c != '\r') || c == 0x7F || c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')).Select(c => $"U+{(int)c:X4}").Distinct().ToArray();
        var unquoted = rows.Select(r => r.Name).Where(n => run.Stdout.Contains(n, StringComparison.Ordinal) && !run.Stdout.Contains("\"" + n, StringComparison.Ordinal)).Count();
        var literals = rows.Count(r => run.Stdout.Contains(Lit(r.Name), StringComparison.Ordinal));

        Said(["100 names as literals", "no raw control or bidi character", "0 names outside a literal"], [$"{literals} names as literals", raw.Length == 0 ? "no raw control or bidi character" : "raw " + string.Join(", ", raw), $"{unquoted} names outside a literal"]);
    }
}
