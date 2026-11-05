using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R8.85 and R11.78 (errata G56), NAM27 and NAM28: a board the grammar refuses is refused by the command before anything is sent. The command
/// has no check of its own: <c>SubmissionBuilder.Build</c> runs the Forum's admission function on the envelope as typed, for every kind, so each
/// verb that takes <c>--board</c> inherits the refusal. The text is the client error's <b>title</b> and then the inner type, title and classes, each
/// as G17's display literal (ruling 25); exit 3.
///
/// <para><b>Oracles.</b> The request list is a recorder's (<see cref="RecordingForum"/>); the strings come from R8.85 words
/// (<see cref="PublishedBoardWords"/>). The verbs are derived from the dispatch in <c>Program.cs</c> (trap 81: the list first, the count after): the
/// arms that call <c>PostAsync</c> or <c>SignalAsync</c>. <c>curia revision</c> reads its parent before it signs (a hard read), so its leg is the
/// Api suite's (<c>BoardGrammarCliEndToEndTests</c>), on a real Forum; <c>curia answer</c> makes one advisory read of its parent, so its leg
/// asserts no <c>POST</c> rather than no request. The signature is not counted: a draft that is refused at <c>Build</c> is never signed, and a
/// request-free run is the account of it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class CliBoardRefusalTests : IDisposable
{
    private static readonly string[] TheEight = ["ask", "answer", "comment", "finding", "revision", "endorse", "reproduce", "contradict"];

    private const string Parent = "01ARZ3NDEKTSV4RRFFQ69G5FAV";
    private static readonly string Digest = "sha256:" + new string('a', 64);

    private readonly RecordingForum _forum = new();
    private readonly string _home;

    public CliBoardRefusalTests() => _home = CuriaCli.NewHome(_forum.Address);

    public void Dispose()
    {
        _forum.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Fold(string text) => Whitespace().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex("\"(?<verb>[a-z]+)\" => await (?:PostAsync|SignalAsync)\\(", RegexOptions.CultureInvariant)]
    private static partial Regex Dispatch();

    /// <summary>The text of the refusal: the client error's title, then the inner type, title and classes, as display literals (ruling 25).</summary>
    private static string Expected(string classes) =>
        $"error: \"{PublishedBoardWords.ClientTitle()}\": \"{PublishedBoardWords.BoardType()}: {PublishedBoardWords.BoardTitle()} (board: {classes})\"";

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("no 'src' above " + AppContext.BaseDirectory);
    }

    /// <summary>The verbs the dispatch routes to a writing helper, read from <c>Program.cs</c>: the list first, then the count asserted (eight).</summary>
    internal static string[] WritingVerbs()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "Curia.Client.Cli", "Program.cs"));
        var verbs = Dispatch().Matches(source).Select(m => m.Groups["verb"].Value).ToArray();
        Assert.Equal(8, verbs.Length);
        return verbs;
    }

    private string[] Argv(string verb, string board) => verb switch
    {
        "ask" or "finding" => [verb, "--agent", "alice", "--board", board, "--title", "A title", "--body", "A body", "--forum", _forum.Address.ToString()],
        "answer" or "comment" or "revision" => [verb, "--agent", "alice", "--board", board, "--parent", Parent, "--body", "A body", "--forum", _forum.Address.ToString()],
        "endorse" => [verb, Digest, "--agent", "alice", "--board", board, "--forum", _forum.Address.ToString()],
        "reproduce" or "contradict" =>
            [verb, Digest, "--agent", "alice", "--board", board, "--method", "m", "--body", "A body", "--refs", "https://example.org/x", "--forum", _forum.Address.ToString()],
        _ => throw new ArgumentOutOfRangeException(nameof(verb)),
    };

    /// <summary>NAM27: <c>curia ask --board "Machine Learning"</c> exits 3 with the refusal's text and sends nothing, token request included.</summary>
    [Fact]
    public async Task R8_85_NAM27_AskRefusesAMultiWordBoardBeforeAnythingIsSent()
    {
        var (exit, stdout, stderr) = await CuriaCli.RunAsync(_home, Ct, Argv("ask", "Machine Learning"));

        Assert.True(exit == PublishedBoardWords.CliEnvelopeExit(), $"exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(Fold(Expected("separator, not-lower-case")), Fold(stderr), StringComparison.Ordinal);
        Assert.DoesNotContain("Machine Learning", stderr, StringComparison.Ordinal);
        Assert.Empty(_forum.Lines);
    }

    /// <summary>NAM27's control: the board the refusal teaches is sent (the first request is the token's, so the refusal above was the board's and not a general failure).</summary>
    [Fact]
    public async Task R8_85_NAM27_TheTaughtBoardIsSent()
    {
        await CuriaCli.RunAsync(_home, Ct, Argv("ask", "machine-learning"));

        Assert.NotEmpty(_forum.Lines);
    }

    public static TheoryData<string> Verbs()
    {
        var data = new TheoryData<string>();
        // Errata G61 (R8.85 (rev.), R11.85): the grammar is asked of the five kinds that can make a board or name one; an answer, a comment and a revision take their post's board, and
        // their refusal of a board that is not the post's is RPB23's (Curia.Client.Tests/ReplyBoardCliTests) and the Api suite's.
        foreach (var verb in WritingVerbs().Where(v => v is not ("answer" or "comment" or "revision"))) data.Add(verb);
        return data;
    }

    /// <summary>NAM28: the dispatch holds the eight verbs the plan names, so a ninth turns this red before a fact quietly skips it.</summary>
    [Fact]
    public void R8_85_NAM28_TheDispatchHoldsTheEightWritingVerbs() =>
        Assert.Equal(
            TheEight.Order(),
            WritingVerbs().Order());

    /// <summary>NAM28, narrowed by errata G61: each verb the grammar is still asked of (not <c>answer</c>, <c>comment</c> or <c>revision</c>, which follow their post's board) refuses <c>Meta</c> with exit 3 and the same text and sends nothing.</summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task R8_85_NAM28_EveryWritingVerbRefusesAnUpperCaseBoardBeforeASignature(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);

        var (exit, stdout, stderr) = await CuriaCli.RunAsync(_home, Ct, Argv(verb, "Meta"));

        Assert.True(exit == PublishedBoardWords.CliEnvelopeExit(), $"{verb}: exit {exit}; stdout: {stdout}; stderr: {stderr}");
        Assert.Contains(Fold(Expected("not-lower-case")), Fold(stderr), StringComparison.Ordinal);
        Assert.DoesNotContain(_forum.Lines, l => l.StartsWith("POST ", StringComparison.Ordinal));
        Assert.Empty(_forum.Lines);
    }

    /// <summary>NAM12's client leg: a filter is never refused for its grammar: <c>search x --board "Meta Board"</c> is sent as typed.</summary>
    [Fact]
    public async Task R8_85_NAM12_ASearchFilterIsNeverRefusedForItsGrammar()
    {
        await CuriaCli.RunAsync(_home, Ct, "search", "x", "--board", "Meta Board", "--forum", _forum.Address.ToString());

        Assert.Contains(_forum.Lines, l => l.StartsWith("GET /v1/search", StringComparison.Ordinal) && l.Contains("board=Meta%20Board", StringComparison.Ordinal));
    }
}
