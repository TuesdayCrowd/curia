using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.78 (errata G56), NAM23 to NAM25: <c>curia search</c> and <c>curia inbox</c> refuse a search criterion that is present and blank, exit 1,
/// with the published words and before a profile is loaded or a request built. Every positional of <c>search</c> is tested (ruling 20), so
/// <c>""</c> and white space are blank terms; a blank <c>--board</c>, <c>--author</c> or <c>--min-verification</c> is refused, and is no filter for the
/// bare-search test either.
///
/// <para><b>Oracles.</b> The request list is a recorder's (<see cref="RecordingForum"/>), a listener the command was pointed at: "nothing was sent"
/// is its account and not the command's. The words are parsed from R11.78 words (<see cref="PublishedBoardWords.CliBlankRefusals"/>, five, order asserted
/// by their content). The refusals run on a home with <b>no agent enrolled</b>, so a check made after the profile is loaded would print another message
/// (R11.58's order); the controls run enrolled. The existing bare-search text is pinned as it is today (NAM25), built with U+2026 at run time.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class CliBlankCriterionTests : IDisposable
{
    private readonly RecordingForum _forum = new();
    private readonly List<string> _homes = [];

    public void Dispose()
    {
        _forum.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private string Home(bool enrolled)
    {
        var home = CuriaCli.NewHome(enrolled ? _forum.Address : null);
        _homes.Add(home);
        return home;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>The five published refusals, picked by the flag each names (terms have none).</summary>
    private static string Words(string criterion)
    {
        var lines = PublishedBoardWords.CliBlankRefusals();
        var inbox = lines[4];
        return criterion switch
        {
            "terms" => lines.Single(l => l.Contains("search terms", StringComparison.Ordinal)),
            "--board" => lines[1],
            "--author" => lines[2],
            "--min-verification" => lines[3],
            "inbox --board" => inbox,
            _ => throw new ArgumentOutOfRangeException(nameof(criterion)),
        };
    }

    /// <summary>NAM23's words check: the five published lines are the ones the table below picks, each naming what it should.</summary>
    [Fact]
    public void R11_78_NAM23_ThePublishedLinesAreTheOnesTheTableUses()
    {
        Assert.Equal(1, PublishedBoardWords.BlankExit());
        Assert.Contains("--board", Words("--board"), StringComparison.Ordinal);
        Assert.Contains("--author", Words("--author"), StringComparison.Ordinal);
        Assert.Contains("--min-verification", Words("--min-verification"), StringComparison.Ordinal);
        Assert.Contains("--board", Words("inbox --board"), StringComparison.Ordinal);
        Assert.Contains("list every board", Words("inbox --board"), StringComparison.Ordinal);
        Assert.StartsWith("error:", Words("terms"), StringComparison.Ordinal);
    }

    public static TheoryData<int> SearchRows()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < SearchCases().Length; i++) data.Add(i);
        return data;
    }

    /// <summary>The refused command lines of <c>search</c>: the argv after <c>search</c>, and the criterion whose words are expected. Each also carries a valid other criterion where the line has room.</summary>
    private static (string Name, string[] Argv, string Criterion)[] SearchCases() =>
    [
        ("a blank term (spaces) alone", ["  "], "terms"),
        ("a blank term (tab) alone", ["\t"], "terms"),
        ("a blank term beside a board filter (ruling 20: every positional)", ["", "--board", "b"], "terms"),
        ("a blank term beside a tags filter", ["  ", "--tags", "a"], "terms"),
        ("an empty term beside a real one", ["x", ""], "terms"),
        ("an empty --board beside a term", ["x", "--board", ""], "--board"),
        ("a white-space --board beside a term", ["x", "--board", "  "], "--board"),
        ("an empty --board and no term (a blank filter is no filter)", ["--board", ""], "--board"),
        ("an empty --author beside a term", ["x", "--author", ""], "--author"),
        ("a white-space --author beside a term", ["x", "--author", " \t "], "--author"),
        ("an empty --min-verification beside a term", ["x", "--min-verification", ""], "--min-verification"),
        ("a white-space --min-verification beside a term", ["x", "--min-verification", "  "], "--min-verification"),
    ];

    /// <summary>NAM23: each refused line exits 1 with the published words and sends nothing, on a home with no agent (the check is before the profile).</summary>
    [Theory]
    [MemberData(nameof(SearchRows))]
    public async Task R11_78_NAM23_SearchRefusesABlankCriterionBeforeAProfileAndSendsNothing(int row)
    {
        var (name, argv, criterion) = SearchCases()[row];
        var home = Home(enrolled: false);

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(home, Ct, ["search", .. argv, "--forum", _forum.Address.ToString()]);

        Assert.True(exit == PublishedBoardWords.BlankExit(), $"{name}: exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(Fold(Words(criterion)), Fold(stderr), StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);
    }

    /// <summary>NAM23's controls: a term alone and a filter alone are sent (so the refusals are the blank's and not a general failure).</summary>
    [Theory]
    [InlineData("x")]
    [InlineData("--board=b")]
    public async Task R11_78_NAM23_ATermAloneAndAFilterAloneAreSent(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var home = Home(enrolled: true);
        var argv = argument.StartsWith("--", StringComparison.Ordinal) ? argument.Split('=') : [argument];

        await CuriaCli.RunAsync(home, Ct, ["search", .. argv, "--forum", _forum.Address.ToString()]);

        Assert.Contains(_forum.Lines, l => l.StartsWith("GET /v1/search", StringComparison.Ordinal));
    }

    /// <summary>NAM24: <c>curia inbox --board</c> with an empty or white-space value is refused the same way: exit 1, the published inbox words, no profile, nothing sent.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("\t")]
    public async Task R11_78_NAM24_InboxRefusesABlankBoardBeforeAProfileAndSendsNothing(string blank)
    {
        ArgumentNullException.ThrowIfNull(blank);
        var home = Home(enrolled: false);

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(home, Ct, "inbox", "--board", blank, "--forum", _forum.Address.ToString());

        Assert.True(exit == PublishedBoardWords.BlankExit(), $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(Fold(Words("inbox --board")), Fold(stderr), StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);
    }

    /// <summary>NAM24's control: an inbox with a real board is not refused for its blankness (it is sent once a token is asked for).</summary>
    [Fact]
    public async Task R11_78_NAM24_AnInboxWithABoardIsSent()
    {
        var home = Home(enrolled: true);

        await CuriaCli.RunAsync(home, Ct, "inbox", "--agent", "alice", "--board", "b", "--forum", _forum.Address.ToString());

        Assert.NotEmpty(_forum.Lines);
    }

    /// <summary>The usage text a bare <c>curia search</c> prints today, built at run time (it holds U+2026).</summary>
    private static string BareText() =>
        "error: usage: curia search <terms" + (char)0x2026 + "> [--board b] [--kind k] [--tags a,b] [--author a]\n"
        + "       Some term or filter is required; a query with neither is a listing, not a search.";

    /// <summary>
    /// NAM25: a bare search is still <c>curia</c>'s refusal, unchanged byte for byte (exit 1, nothing sent); <c>curia search ""</c> alone is exit 1 as
    /// today, but its text becomes the blank-terms words (ruling 20); <c>curia search --board x</c> is sent. Green on the seam for the first and
    /// third; the second leg is red by text (today it prints the usage text), so the fact as a whole is red until the build.
    /// </summary>
    [Fact]
    public async Task R11_78_NAM25_ABareSearchKeepsItsTextAndAnEmptyTermAloneGetsTheBlankWords()
    {
        var home = Home(enrolled: false);
        var forum = _forum.Address.ToString();

        var (bareExit, _, bareErr) = await CuriaCli.RunAsync(home, Ct, "search", "--forum", forum);
        Assert.Equal(1, bareExit);
        Assert.Contains(BareText(), bareErr.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);

        var (emptyExit, _, emptyErr) = await CuriaCli.RunAsync(home, Ct, "search", "", "--forum", forum);
        Assert.Equal(1, emptyExit);
        Assert.Contains(Fold(Words("terms")), Fold(emptyErr), StringComparison.Ordinal);
        Assert.DoesNotContain("Some term or filter is required", emptyErr, StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);

        var enrolled = Home(enrolled: true);
        await CuriaCli.RunAsync(enrolled, Ct, "search", "--board", "x", "--forum", forum);
        Assert.Contains(_forum.Lines, l => l.StartsWith("GET /v1/search", StringComparison.Ordinal));
    }

    /// <summary>NAM22 through the command: the verdict of <c>curia search</c> on a blank position equals the function's (a refusal exactly where the function names a criterion).</summary>
    [Theory]
    [MemberData(nameof(SearchRequestRulesTests.BlankRows), MemberType = typeof(SearchRequestRulesTests))]
    public async Task R11_78_NAM22_TheCommandAgreesWithTheFunction(int row)
    {
        var blank = SearchRequestRulesTests.Blanks()[row];
        var home = Home(enrolled: false);
        var forum = _forum.Address.ToString();

        // The function says "board" for a blank board beside a valid term; the command must refuse and name --board, and send nothing.
        Assert.Equal("board", SearchRequestRules.BlankCriterion("x", blank, null, null));
        var (exit, _, stderr) = await CuriaCli.RunAsync(home, Ct, "search", "x", "--board", blank, "--forum", forum);

        Assert.Equal(1, exit);
        Assert.Contains("--board is empty or blank", stderr, StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);
    }
}
