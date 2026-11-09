using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.BoardListHarness;

namespace Curia.Client.Tests;

/// <summary>
/// Task 5's fix round (Amendment 2, rulings 25 to 39), LOB42 and LOB46 to LOB52 in the CLI and the library: the argument surface of <c>curia boards</c> and the filtered line (S1, Q8), the library's refusals (Q1), the
/// <c>BoardLine</c> outcomes (Q5), the <c>more</c> line read by a shell (S3), the published sentences of "the rest" (S4) and the words of a list that could not be read (ruling 39).
///
/// <para><b>Oracles.</b> Every sentence is parsed from the errata (<see cref="PublishedBoardListWords"/>). A request is counted at the recorder (<see cref="Outcome.Requests"/>), so "nothing was sent" is read from what
/// the Forum received and a refusing row has its served control in the same fact. The <c>more</c> line's cursor is read by <c>/bin/sh</c>, which is neither the errata nor <see cref="Hints"/>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class BoardListArgumentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Board Rust = new("rust", 41, 6);

    private const string MoreLabel = BoardListWording.MoreLabel;

    private static string Lit(string name) => DisplayLiteral.Of(name);

    private static int BoardRequests(Outcome run) => run.Requests.Count(r => r.StartsWith("GET /v1/boards", StringComparison.Ordinal));

    // ---- LOB42 ----

    /// <summary>
    /// LOB42, the filtered line: <c>--board &lt;unlisted&gt;</c> exits 0 and prints the banner and then the published filtered line (the name a display literal), and neither <c>more</c> nor <c>no further pages</c>;
    /// <c>--board &lt;listed&gt;</c> prints the header and the row. A control on the current behaviour; FLOB63's first form is a filtered read that tells the agent a new board is fine.
    /// </summary>
    [Fact]
    public async Task R10_98_LOB42_AFilteredReadSaysNoPostIsOnTheNameAndAListedOneShowsItsRow()
    {
        using var forum = Forum([Rust]);
        var unlisted = await RunAsync(forum, Ct, "boards", "--board", "rsut");
        var listed = await RunAsync(forum, Ct, "boards", "--board", "rust");

        var filtered = PublishedBoardListWords.FilteredEmpty().Replace("<name>", Lit("rsut"), StringComparison.Ordinal);
        var header = PublishedBoardListWords.ListingHeader().Replace("<k>", "1", StringComparison.Ordinal).Replace("<total>", "1", StringComparison.Ordinal);
        var row = PublishedBoardListWords.ListingRow().Replace("<name>", Lit("rust"), StringComparison.Ordinal).Replace("<posts>", "41", StringComparison.Ordinal)
            .Replace("<authors>", "6", StringComparison.Ordinal).Replace("<latest_post_at>", "2026-11-05T12:00:00.0000000+00:00", StringComparison.Ordinal);

        string Describe(Outcome run, params string[] wanted) =>
            $"exit {run.Exit}, {(Fold(run.Stdout).Contains(Fold(PublishedBoardListWords.RestPreface()), StringComparison.Ordinal) ? "the banner" : "no banner")}, "
            + string.Join(", ", wanted.Select(w => Lines(run.Stdout).Contains(w) ? "has " + (w == filtered ? "the filtered line" : w == header ? "the header" : "the row") : "lacks " + (w == filtered ? "the filtered line" : w == header ? "the header" : "the row")))
            + $", {(Lines(run.Stdout).Any(l => l.StartsWith(MoreLabel, StringComparison.Ordinal)) ? "a more line" : "no more line")}, "
            + $"{(Lines(run.Stdout).Contains(PublishedBoardListWords.NoFurtherPages()) ? "no further pages" : "no closing line")}";

        Said(
            [
                "unlisted: exit 0, the banner, has the filtered line, no more line, no closing line",
                "listed: exit 0, the banner, has the header, has the row, no more line, no further pages",
            ],
            [
                "unlisted: " + Describe(unlisted, filtered),
                "listed: " + Describe(listed, header, row),
            ]);
    }

    /// <summary>
    /// LOB42, the page size and the refusals: <c>--limit 1</c> and <c>--limit 100</c> reach the request as <c>limit=1</c> and <c>limit=100</c>; <c>--limit 0</c>, <c>101</c>, <c>x</c>, <c>-1</c>, <c>" 5"</c> and the empty
    /// value, a blank <c>--cursor</c> and a blank <c>--board</c> exit 1 with <b>no request</b>; <c>--cursor c</c> reaches the request as <c>cursor=c</c>; an unknown flag exits 1. Each refusal is beside the served control
    /// <c>--limit 5</c> in the same assertion, because a bare exit 1 is also what an unknown flag returns (FLOB64's first form: <c>--limit 101</c> is sent and the route's refusal is the only word).
    /// </summary>
    [Fact]
    public async Task R10_98_LOB42_TheLimitTheCursorAndTheBoardReachTheRequestOrAreRefusedBeforeOne()
    {
        using var forum = Forum([Rust]);
        var control = await RunAsync(forum, Ct, "boards", "--limit", "5");
        var expected = new List<string> { "control --limit 5: exit 0, GET /v1/boards?limit=5" };
        var actual = new List<string> { $"control --limit 5: exit {control.Exit}, {string.Join(" ", control.Requests)}" };

        async Task Sent(string label, string[] argv, string wanted)
        {
            var run = await RunAsync(forum, Ct, argv);
            expected.Add($"{label}: exit 0, {wanted}");
            actual.Add($"{label}: exit {run.Exit}, {string.Join(" ", run.Requests)}");
        }

        await Sent("--limit 1", ["boards", "--limit", "1"], "GET /v1/boards?limit=1");
        await Sent("--limit 100", ["boards", "--limit", "100"], "GET /v1/boards?limit=100");
        await Sent("--cursor c", ["boards", "--cursor", "c"], "GET /v1/boards?cursor=c");

        async Task Refused(string label, params string[] argv)
        {
            var run = await RunAsync(forum, Ct, argv);
            expected.Add($"{label}: exit 1, no request");
            actual.Add($"{label}: exit {run.Exit}, {(BoardRequests(run) == 0 && run.Requests.Length == 0 ? "no request" : run.Requests.Length + " request(s)")}");
        }

        await Refused("--limit 0", "boards", "--limit", "0");
        await Refused("--limit 101", "boards", "--limit", "101");
        await Refused("--limit x", "boards", "--limit", "x");
        await Refused("--limit -1", "boards", "--limit", "-1");
        await Refused("--limit \" 5\"", "boards", "--limit", " 5");
        await Refused("--limit \"\"", "boards", "--limit", string.Empty);
        await Refused("--cursor \" \"", "boards", "--cursor", " ");
        await Refused("--board \" \"", "boards", "--board", " ");
        await Refused("an unknown flag", "boards", "--nope", "x");
        Said(expected, actual);
    }

    /// <summary>
    /// LOB42, the positional and the agent: <c>curia boards rust</c> exits 1 naming <c>--board &lt;name&gt;</c> with no request (it would otherwise list everything and ignore <c>rust</c>: FLOB66), and
    /// <c>--agent nosuch</c>, a profile this host does not hold, exits non-zero with no request instead of reading the default Forum under the words "the boards the Forum serves" (FLOB67); the control,
    /// <c>--agent alice</c>, reads. A blank <c>--agent</c> is refused too.
    /// </summary>
    [Fact]
    public async Task R10_98_LOB42_APositionalAndAnAgentThatCannotBeLoadedAreRefusedAndNothingIsSent()
    {
        using var forum = Forum([Rust]);
        var control = await RunAsync(forum, Ct, "boards", "--agent", "alice");
        var positional = await RunAsync(forum, Ct, "boards", "rust");
        var stranger = await RunAsync(forum, Ct, "boards", "--agent", "nosuch");
        var blank = await RunAsync(forum, Ct, "boards", "--agent", " ");

        Said(
            [
                "--agent alice: exit 0, 1 request",
                "a positional: exit 1, names --board <name>, no request",
                "--agent nosuch: refused (exit not 0), no request, no listing",
                "--agent blank: refused (exit not 0), no request, no listing",
            ],
            [
                $"--agent alice: exit {control.Exit}, {control.Requests.Length} request",
                $"a positional: exit {positional.Exit}, {(positional.All.Contains("--board <name>", StringComparison.Ordinal) ? "names --board <name>" : "does not name --board <name>")}, {(positional.Requests.Length == 0 ? "no request" : positional.Requests.Length + " request(s)")}",
                $"--agent nosuch: {(stranger.Exit != 0 ? "refused (exit not 0)" : "served (exit 0)")}, {(stranger.Requests.Length == 0 ? "no request" : stranger.Requests.Length + " request(s)")}, {(stranger.Stdout.Contains("boards    ", StringComparison.Ordinal) ? "a listing" : "no listing")}",
                $"--agent blank: {(blank.Exit != 0 ? "refused (exit not 0)" : "served (exit 0)")}, {(blank.Requests.Length == 0 ? "no request" : blank.Requests.Length + " request(s)")}, {(blank.Stdout.Contains("boards    ", StringComparison.Ordinal) ? "a listing" : "no listing")}",
            ]);
    }

    // ---- LOB46 ----

    /// <summary>
    /// LOB46, the library is the net under both front ends: <c>GetBoardsAsync</c> with a present blank <c>board</c> or <c>cursor</c> and a <c>limit</c> of 0, 101 or -1 is refused locally, before a request (the stub
    /// counts none), as <c>SearchAsync</c> refuses a blank criterion; each has its served control; and <c>BoardCheck.CheckAsync(client, "  ")</c> is a refusal and never the unfiltered page (FLOB73 and FLOB74).
    /// </summary>
    [Fact]
    public async Task R11_78_LOB46_TheLibraryRefusesABlankBoardOrCursorAndALimitOutsideItsRangeBeforeARequest()
    {
        using var log = new StubLog();
        var client = log.Client();
        var cases = new (string Label, string? Board, int? Limit, string? Cursor)[]
        {
            ("a blank board", "  ", null, null),
            ("an empty board", string.Empty, null, null),
            ("a blank cursor", null, null, " "),
            ("a limit of 0", null, 0, null),
            ("a limit of 101", null, 101, null),
            ("a limit of -1", null, -1, null),
        };
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, board, limit, cursor) in cases)
        {
            log.Requests.Clear();
            var read = await client.GetBoardsAsync(board, limit, cursor, Ct);
            var refused = !read.TryGetValue(out _, out var refusal);
            expected.Add($"{label}: refused locally, no request");
            actual.Add($"{label}: {(refused && refusal!.Kind is RefusalKind.Local ? "refused locally" : refused ? "refused by the Forum" : "read")}, {(log.Requests.Count == 0 ? "no request" : log.Requests.Count + " request(s)")}");
        }

        foreach (var (label, board, limit, cursor) in new (string, string?, int?, string?)[] { ("limit 1", null, 1, null), ("limit 100", null, 100, null), ("board b", "b", null, null), ("cursor c", null, null, "c") })
        {
            log.Requests.Clear();
            var read = await client.GetBoardsAsync(board, limit, cursor, Ct);
            expected.Add($"control {label}: read, 1 request");
            actual.Add($"control {label}: {(read.TryGetValue(out _, out _) ? "read" : "not read")}, {log.Requests.Count} request(s)");
        }

        log.Requests.Clear();
        var check = await BoardCheck.CheckAsync(client, "  ", Ct);
        expected.Add("CheckAsync of a blank name: not OnList and not NotOnList, no request");
        actual.Add($"CheckAsync of a blank name: {(check.Outcome is BoardCheckOutcome.OnList or BoardCheckOutcome.NotOnList ? "read as " + check.Outcome : "not OnList and not NotOnList")}, {(log.Requests.Count == 0 ? "no request" : log.Requests.Count + " request(s)")}");
        Said(expected, actual);
    }

    // ---- LOB48 ----

    /// <summary>
    /// LOB48, <c>BoardLine</c> has no silent outcome but the two refusals. Over every <c>(outcome, flagGiven, adapter)</c> the enum yields (enumerated from the type, so an added outcome fails here): text for
    /// <c>OnList</c> with its row, <c>NotStated</c>, and <c>NotOnList</c> with the flag; null only for <c>NotOnList</c> without the flag and for <c>Unreadable</c>, each of which prints its own refusal; and a
    /// <c>BoardCheckResult</c> cannot be built as <c>OnList</c> with no row (FLOB77).
    /// </summary>
    [Fact]
    public async Task R10_98_LOB48_BoardLineHasNoSilentOutcomeButTheTwoRefusals()
    {
        using var log = new StubLog();
        var read = await log.Client().GetBoardsAsync("b", null, null, Ct);
        Assert.True(read.TryGetValue(out var page, out _) && page!.Boards.Length == 1, "NOT REACHED: the stub's one-board list was not read");
        var row = page!.Boards[0];
        var fault = new Refusal(RefusalKind.Transport, 0, ClientErrors.Transport("x"));
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var outcome in Enum.GetValues<BoardCheckOutcome>())
        {
            foreach (var flag in new[] { false, true })
            {
                foreach (var adapter in new[] { false, true })
                {
                    var wantsText = outcome switch
                    {
                        BoardCheckOutcome.OnList or BoardCheckOutcome.NotStated => true,
                        BoardCheckOutcome.NotOnList => flag,
                        BoardCheckOutcome.Unreadable => false,
                        _ => throw new InvalidOperationException("an outcome the test does not know: " + outcome),
                    };
                    expected.Add($"{outcome} flag={flag} adapter={adapter}: {(wantsText ? "a line" : "null, its own refusal")}");
                    string said;
                    try
                    {
                        var result = outcome switch
                        {
                            BoardCheckOutcome.OnList => new BoardCheckResult(outcome, row),
                            BoardCheckOutcome.Unreadable => new BoardCheckResult(outcome, Fault: fault),
                            BoardCheckOutcome.NotOnList or BoardCheckOutcome.NotStated => new BoardCheckResult(outcome),
                            _ => throw new InvalidOperationException("an outcome the test does not know: " + outcome),
                        };
                        said = BoardListWording.BoardLine("rust", result, flag, adapter) is null ? "null, its own refusal" : "a line";
                    }
                    catch (InvalidOperationException)
                    {
                        said = "threw";
                    }

                    actual.Add($"{outcome} flag={flag} adapter={adapter}: {said}");
                }
            }
        }

        string built;
        try
        {
            _ = new BoardCheckResult(BoardCheckOutcome.OnList);
            built = "built";
        }
        catch (ArgumentException)
        {
            built = "refused";
        }
        catch (InvalidOperationException)
        {
            built = "refused";
        }

        expected.Add("OnList with no row: refused at construction");
        actual.Add($"OnList with no row: {built} at construction");
        Said(expected, actual);
    }

    // ---- LOB50 ----

    /// <summary>
    /// LOB50, the <c>more</c> line's cursor is one shell word, by a shell. A Forum serves <c>next_cursor</c> as <c>YjE6MA==</c>, <c>YjEwMDow</c> and four values a client does not trust: <c>a b</c>, <c>$(id)</c>,
    /// <c>x;y</c> and one holding a single quotation mark. Where the line is G63's template with a cursor, <c>/bin/sh -c 'printf %s &lt;word&gt;'</c> must give the served value exactly and run nothing (no output on
    /// standard error); where the value cannot be a shell word the line carries no runnable <c>--cursor</c> command and holds the value as a display literal. The oracle is a shell, neither the errata nor
    /// <c>Hints</c> (FLOB79).
    /// </summary>
    [Fact]
    public async Task R10_98_LOB50_TheMoreLinesCursorIsOneShellWordWhateverTheForumServed()
    {
        var template = PublishedBoardListWords.MoreLine();
        var prefix = template[..template.IndexOf("<next_cursor>", StringComparison.Ordinal)];
        var cursors = new[] { "YjE6MA==", "YjEwMDow", "a b", "$(id)", "x;y", "it's" };
        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var cursor in cursors)
        {
            using var forum = Forum(listAnswer: _ => (200, PageJson([Rust], 9, cursor)));
            var run = await RunAsync(forum, Ct, "boards");
            var line = Lines(run.Stdout).FirstOrDefault(l => l.StartsWith(PublishedBoardListWords.MoreLine()[..PublishedBoardListWords.MoreLine().IndexOf("curia", StringComparison.Ordinal)], StringComparison.Ordinal)) ?? string.Empty;
            expected.Add(cursor.Contains('\'', StringComparison.Ordinal)
                ? $"{Lit(cursor)}: no runnable command, the value as a literal, nothing run"
                : $"{Lit(cursor)}: the template and a word the shell reads back as the value, nothing run");

            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                var (output, error) = await ShellAsync(line[prefix.Length..]);
                actual.Add($"{Lit(cursor)}: " + (cursor.Contains('\'', StringComparison.Ordinal)
                    ? "a runnable command"
                    : output == cursor && error.Length == 0 ? "the template and a word the shell reads back as the value, nothing run" : $"the shell read {Lit(output)} and said {Lit(error)}"));
            }
            else
            {
                actual.Add($"{Lit(cursor)}: " + (cursor.Contains('\'', StringComparison.Ordinal)
                    ? (line.Contains(Lit(cursor), StringComparison.Ordinal) && run.Exit == 0 ? "no runnable command, the value as a literal, nothing run" : "no runnable command and no literal")
                    : "no template line"));
            }
        }

        Said(expected, actual);
    }

    /// <summary>The shell's reading of <paramref name="word"/>: standard output and standard error of <c>/bin/sh -c 'printf %s &lt;word&gt;'</c>, captured apart.</summary>
    internal static async Task<(string Output, string Error)> ShellAsync(string word)
    {
        var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("printf %s " + word);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (await output, await error);
    }

    // ---- LOB51 ----

    /// <summary>
    /// LOB51 (the verb's half): the preface <c>curia boards</c> prints, the refusal's <c>none</c> where the Forum serves no board, the line that replaces the list where the names could not be read, and the
    /// help of <c>ask</c> and <c>finding</c> are each the sentence "R10.98 words (the rest)" publishes (a line break inside is not part of its words). The first read of a refusal is the filtered one; the second
    /// reads page one (so a Forum that answers the second with a fault is the names-unreadable case).
    /// </summary>
    [Fact]
    public async Task R10_98_LOB51_TheRestOfTheWordsArePrintedAsPublished()
    {
        using var empty = Forum([]);
        using var halfDown = Forum(listAnswer: query => query.Contains("board=", StringComparison.Ordinal) ? (200, PageJson([], 0)) : (503, """{"type":"curia/log/unreadable","title":"The event log could not be read"}"""));
        var preface = await RunAsync(empty, Ct, "boards");
        var none = await RunAsync(empty, Ct, Ask("rsut"));
        var unreadable = await RunAsync(halfDown, Ct, Ask("rsut"));

        var lead = PublishedBoardListWords.RefusalBoardsLead();
        var rest = new (string Label, bool Holds)[]
        {
            ("the preface", Fold(preface.Stdout).Contains(Fold(PublishedBoardListWords.RestPreface()), StringComparison.Ordinal)),
            ("none after the colon", Fold(none.All).Contains(lead + " " + PublishedBoardListWords.RestNone(), StringComparison.Ordinal)),
            ("the names-unreadable line", Fold(unreadable.All).Contains(PublishedBoardListWords.RestNamesUnreadable(), StringComparison.Ordinal)),
            ("the help of ask", Fold(Help.For("ask") ?? string.Empty).Contains(Fold(PublishedBoardListWords.RestNewBoardHelp()), StringComparison.Ordinal)),
            ("the help of finding", Fold(Help.For("finding") ?? string.Empty).Contains(Fold(PublishedBoardListWords.RestNewBoardHelp()), StringComparison.Ordinal)),
        };
        Said(rest.Select(r => r.Label + ": printed as published"), rest.Select(r => r.Label + ": " + (r.Holds ? "printed as published" : "not as published")));
    }

    // ---- LOB52 ----

    /// <summary>
    /// LOB52, the unreadable-list words carry the condition (ruling 39). Over a Forum that answers the list <c>503</c> <c>curia/log/unreadable</c>, a reset and a <c>200</c> that is no listing: <c>curia ask</c>'s
    /// refusal holds the published lead and the published tail (this run signed nothing and kept nothing, run again when the list can be read, unless an earlier run printed an act line), never
    /// <c>again is safe</c>, never <c>--new-board</c> and no command of its own; and the help of <c>ask</c> and <c>finding</c> carries the same condition wherever it says the command may be run again (FLOB82, FLOB83
    /// and FLOB86).
    /// </summary>
    [Fact]
    public async Task R10_98_LOB52_TheRefusalOfAListThatCannotBeReadCarriesTheActLineConditionAndNoPermission()
    {
        var fault = PublishedBoardListWords.FaultSentence();
        var lead = fault[..fault.IndexOf("(<cause>)", StringComparison.Ordinal)].Replace("<name>", Lit("rust"), StringComparison.Ordinal);
        var tail = PublishedBoardListWords.FaultTail();
        var command = new Regex(@"\bcuria (ask|finding|resend|acts|boards|recheck|discard)\b|--agent", RegexOptions.CultureInvariant);

        var runs = new List<(string Label, Outcome Run)>();
        using (var down = Forum(listAnswer: _ => (503, """{"type":"curia/log/unreadable","title":"The event log could not be read"}""")))
            runs.Add(("503", await RunAsync(down, Ct, Ask("rust"))));
        using (var junk = Forum(listAnswer: _ => (200, """{"hello":"world"}""")))
            runs.Add(("a 200 that is no listing", await RunAsync(junk, Ct, Ask("rust"))));
        using (var reset = new RawStubForum(UnheardBehaviour.Fin) { ResetBoardListReads = true })
        {
            var home = CuriaCli.NewHome(reset.Address);
            try
            {
                var (exit, stdout, stderr) = await CuriaCli.RunAsync(home, Ct, [.. Ask("rust"), "--forum", reset.Address.ToString()]);
                runs.Add(("a reset", new Outcome(exit, stdout, stderr, [.. reset.Requests], [])));
            }
            finally
            {
                Directory.Delete(home, recursive: true);
            }
        }

        var expected = new List<string>();
        var actual = new List<string>();
        foreach (var (label, run) in runs)
        {
            var text = Fold(run.All);
            expected.Add($"{label}: exit 8, the lead, the tail, no permission, no flag, no command");
            actual.Add($"{label}: exit {run.Exit}, {(text.Contains(lead, StringComparison.Ordinal) ? "the lead" : "no lead")}, {(text.Contains(tail, StringComparison.Ordinal) ? "the tail" : "no tail")}, "
                + $"{(text.Contains("again is safe", StringComparison.Ordinal) ? "says it is safe" : "no permission")}, {(text.Contains("--new-board", StringComparison.Ordinal) ? "the flag is offered" : "no flag")}, "
                + $"{(command.IsMatch(text) ? "a command" : "no command")}");
        }

        foreach (var verb in new[] { "ask", "finding" })
        {
            var help = Fold(Help.For(verb) ?? string.Empty);
            var unconditional = Regex.Matches(help, "may be run again", RegexOptions.CultureInvariant)
                .Count(m => !help[m.Index..Math.Min(help.Length, m.Index + 120)].Contains("unless an earlier run", StringComparison.Ordinal));
            expected.Add($"help of {verb}: no unconditional permission");
            actual.Add($"help of {verb}: {(unconditional == 0 ? "no unconditional permission" : unconditional + " unconditional permission(s)")}");
        }

        Said(expected, actual);
    }
}

