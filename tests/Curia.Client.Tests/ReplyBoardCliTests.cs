#pragma warning disable CA1416 // the kept-act files are mode 0600 and 0700 (R11.79): the probes read and set Unix modes, as PrivateFiles does

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.85 (errata G61), RPB22 to RPB29, at the real command: <c>curia answer</c>, <c>curia comment</c> and <c>curia revision</c> run in process
/// (<c>Program.RunAsync</c>) against the stub Forum (<see cref="StubLog"/>) that serves the parent (a question the agent <c>alice</c> signed) and records every request
/// and every body it was sent. The board the stub serves for the parent is a string chosen here (<c>rust</c>, or one holding a line feed and an ESC), so the expected
/// board is never the one the command computed.
///
/// <para><b>Oracles.</b> What was sent is the stub's account, not the command's: the requests it recorded, and the envelopes it received (distinct by the digest computed
/// from the received bytes, <see cref="ReceivedAct"/>, because a nonce challenge repeats the bytes). The refusal's words, the exit code, the second sentence and the help
/// sentence are parsed from R11.85 words (<see cref="PublishedReplyBoardWords"/>); the read's own refusal is what <c>curia read</c> prints on the same stub in the same
/// state. A probe is in its failing form: on the seam <c>--board</c> is required, a differing board is sent, and <c>comment</c> reads nothing.</para>
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The stub is owned by the class and disposed with it.")]
[Collection("console")]
public sealed partial class ReplyBoardCliTests : IDisposable
{
    private const string Served = "rust";
    private const string Forum = "http://forum.test";

    private readonly StubLog _log = new() { ServedBoard = Served };

    public void Dispose()
    {
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static string Fold(string text) => CliRun.Fold(text).Trim();

    private static string Literal(string value) => DisplayLiteral.Of(value);

    /// <summary>The scripted stub outlives each command's client, which disposes its handler.</summary>
    private sealed class NonDisposing(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing) => base.Dispose(false);
    }

    private async Task<CliRun> RunAsync(params string[] args)
    {
        var savedHome = Environment.GetEnvironmentVariable("CURIA_CLIENT_HOME");
        var savedFactory = Program.HttpClientFactory;
        try
        {
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", _log.Home);
            Program.HttpClientFactory = forum => new HttpClient(new NonDisposing(_log.Handler)) { BaseAddress = forum, Timeout = TimeSpan.FromSeconds(30) };
            var run = await ConsoleCapture.CaptureAsync(() => Program.RunAsync(args)).ConfigureAwait(false);
            return new CliRun(run.Exit, run.Stdout, run.Stderr);
        }
        finally
        {
            Program.HttpClientFactory = savedFactory;
            Environment.SetEnvironmentVariable("CURIA_CLIENT_HOME", savedHome);
        }
    }

    private static string[] Reply(string verb, string? board) =>
        board is null
            ? [verb, "--agent", "alice", "--parent", StubLog.PostId, "--body", "A reply.", "--forum", Forum]
            : [verb, "--agent", "alice", "--board", board, "--parent", StubLog.PostId, "--body", "A reply.", "--forum", Forum];

    private void Reset()
    {
        _log.Requests.Clear();
        _log.WrittenBodies.Clear();
    }

    /// <summary>The envelopes the stub was sent, one per distinct act (a nonce challenge repeats the same bytes), each with its board read from the bytes received.</summary>
    private List<(string Digest, string Board)> Sent() =>
        [.. _log.WrittenBodies.Where(ReceivedAct.IsSubmission)
            .GroupBy(ReceivedAct.DigestOf)
            .Select(g => (g.Key, JsonDocument.Parse(g.First()).RootElement.GetProperty("envelope").GetProperty("board").GetString()!))];

    private int ParentReads => _log.Requests.Count(r => r == "GET /v1/posts/" + StubLog.PostId);

    public static TheoryData<string> Verbs() => new() { "answer", "comment", "revision" };

    // ---- RPB22 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB22. <c>--board</c> omitted: the command takes the board from the parent it read, so the envelope the stub received (parsed from the bytes) carries the served
    /// board for an answer, a comment and a revision, and the receipt names it. Predicted red: <c>error: --board &lt;name&gt; is required.</c>, exit 1, nothing sent.
    /// </summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task R11_85_RPB22_TheBoardIsTheParentsWhenNoneIsGiven(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);

        var run = await RunAsync(Reply(verb, board: null));

        var sent = Sent();
        var problems = new List<string>();
        if (run.Exit != 0) problems.Add($"exit {run.Exit} ({Fold(run.Stdout + " " + run.Stderr)})");
        if (sent.Count != 1) problems.Add($"{sent.Count} act(s) were sent, expected 1");
        else if (sent[0].Board != Served) problems.Add($"the envelope's board is {Literal(sent[0].Board)}, expected {Literal(Served)}");
        if (!run.Text.Contains("board " + Literal(Served), StringComparison.Ordinal)) problems.Add("the receipt does not name the board as a display literal");
        Red.None(problems, $"{verb}: exit 0, one act on the parent's board {Literal(Served)}, the receipt naming it");
    }

    // ---- RPB23 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB23. <c>--board</c> differs from the parent's: exit 1, no POST at all (nothing was signed or sent), and the published words with the typed board, the post id and the
    /// parent's board as display literals. Predicted red: the typed board is sent (one act).
    /// </summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task R11_85_RPB23_ADifferingBoardIsRefusedBeforeAnythingIsSigned(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);

        var run = await RunAsync(Reply(verb, "dotnet"));

        var expected = PublishedReplyBoardWords.RefusalLine(Literal("dotnet"), Literal(StubLog.PostId), Literal(Served));
        var problems = new List<string>();
        if (run.Exit != PublishedReplyBoardWords.RefusalExit()) problems.Add($"exit {run.Exit}, expected {PublishedReplyBoardWords.RefusalExit()}");
        if (_log.Requests.Any(r => r == "POST /v1/posts")) problems.Add($"a POST /v1/posts was made ({Sent().Count} act(s) sent)");
        if (!Fold(run.Stdout + " " + run.Stderr).Contains(Fold(expected), StringComparison.Ordinal))
            problems.Add($"lacks the published refusal \"{Fold(expected)}\" (it said: {Fold(run.Stdout + " " + run.Stderr)})");
        Red.None(problems, $"{verb}: exit 1, no POST, the published refusal naming the typed board, the post and the parent's board");
    }

    // ---- RPB24 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB24. The three arms. (a) <c>--board</c> equal to the parent's: one read of the parent, one act on that board. (b) <c>--board</c> given and the read refused <c>503</c>
    /// (an answer and a comment; a revision cannot be chained without its read): the draft is sent with the typed board, because the read is advisory for the board. (c)
    /// <c>--board</c> absent and the read refused: nothing is sent, the output carries the read's own refusal (what <c>curia read</c> prints on the same stub) and its exit code, and
    /// then the published second sentence. Predicted red: (c) is <c>--board &lt;name&gt; is required.</c> (the other arms are controls on the seam, except a comment's one read).
    /// </summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task R11_85_RPB24_TheBoardIsSentWhenItIsEqualOrTheReadFailedAndNothingIsSentWhenItIsUnknown(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);
        var problems = new List<string>();

        // (a) equal.
        var equal = await RunAsync(Reply(verb, Served));
        var onEqual = Sent();
        if (equal.Exit != 0) problems.Add($"(a) exit {equal.Exit}");
        if (ParentReads != 1) problems.Add($"(a) {ParentReads} read(s) of the parent, expected 1");
        if (onEqual.Count != 1 || onEqual[0].Board != Served) problems.Add($"(a) {onEqual.Count} act(s), board {string.Join(",", onEqual.Select(s => Literal(s.Board)))}");

        // The read's own refusal and exit code, from `curia read` on the stub in the state (c) will meet.
        _log.PostReadFailsWith = HttpStatusCode.ServiceUnavailable;
        Reset();
        var read = await RunAsync("read", StubLog.PostId, "--agent", "alice", "--forum", Forum);
        var readFirstLine = Fold((read.Stderr + "\n" + read.Stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0]);

        // (b) given, read refused (not a revision).
        if (verb != "revision")
        {
            Reset();
            var given = await RunAsync(Reply(verb, "dotnet"));
            var onGiven = Sent();
            if (given.Exit != 0) problems.Add($"(b) exit {given.Exit}");
            if (onGiven.Count != 1 || onGiven[0].Board != "dotnet") problems.Add($"(b) {onGiven.Count} act(s), board {string.Join(",", onGiven.Select(s => Literal(s.Board)))}, expected one on {Literal("dotnet")}");
        }

        // (c) absent, read refused.
        Reset();
        var absent = await RunAsync(Reply(verb, board: null));
        var all = Fold(absent.Stderr + " " + absent.Stdout);
        if (_log.Requests.Any(r => r == "POST /v1/posts")) problems.Add("(c) a POST /v1/posts was made");
        if (absent.Exit != read.Exit) problems.Add($"(c) exit {absent.Exit}, the read's own is {read.Exit}");
        if (!all.Contains(readFirstLine, StringComparison.Ordinal)) problems.Add($"(c) lacks the read's own refusal \"{readFirstLine}\" (it said: {all})");
        if (!all.Contains(Fold(PublishedReplyBoardWords.UnreadSentence()), StringComparison.Ordinal)) problems.Add("(c) lacks the published second sentence");
        Red.None(problems, $"{verb}: (a) one read and one act on the equal board; (b) the typed board sent when the read failed; (c) nothing sent, the read's refusal and exit code, then the second sentence");
    }

    // ---- RPB25 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB25. The reads of the parent are counted by the stub: <c>comment</c> makes exactly one before its one POST; <c>answer</c> one (not two: the advisory read serves the
    /// question's standing and the board); <c>revision</c> its verified read and no second. Predicted red: <c>comment</c> makes none.
    /// </summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task R11_85_RPB25_EachVerbReadsItsParentOnceBeforeItPosts(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);

        var run = await RunAsync(Reply(verb, Served));

        var firstRead = _log.Requests.IndexOf("GET /v1/posts/" + StubLog.PostId);
        var firstPost = _log.Requests.IndexOf("POST /v1/posts");
        var problems = new List<string>();
        if (run.Exit != 0) problems.Add($"exit {run.Exit} ({Fold(run.Stdout + " " + run.Stderr)})");
        if (ParentReads != 1) problems.Add($"{ParentReads} read(s) of the parent");
        if (firstRead < 0 || firstPost < 0 || firstRead > firstPost) problems.Add("the read does not come before the post");
        Red.None(problems, $"{verb}: exactly one read of the parent, before the one POST");
    }

    // ---- RPB26 ----------------------------------------------------------------------------------

    private static readonly string Stored = "Repro" + (char)10 + "sec" + (char)27 + "[31mred";

    private static bool HasRawControl(string text) => text.Any(c => char.IsControl(c) && c is not '\n' and not '\r');

    /// <summary>
    /// RPB26, the refusal half. The parent's served board holds a line feed and an ESC: the refusal writes the post id, the typed board and that board as display literals
    /// (the stub-computed <see cref="DisplayLiteral"/> forms), and neither stream holds a raw control character but the line ends. Predicted red: the seam has no refusal, so
    /// the typed board is sent.
    /// </summary>
    [Theory]
    [InlineData("answer")]
    [InlineData("comment")]
    public async Task R11_85_RPB26_ARefusalNamingAStoredBoardWritesItAsALiteral(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);
        _log.ServedBoard = Stored;

        var run = await RunAsync(Reply(verb, "dotnet"));

        var expected = PublishedReplyBoardWords.RefusalLine(Literal("dotnet"), Literal(StubLog.PostId), Literal(Stored));
        var problems = new List<string>();
        if (run.Exit != PublishedReplyBoardWords.RefusalExit()) problems.Add($"exit {run.Exit}, expected {PublishedReplyBoardWords.RefusalExit()}");
        if (!Fold(run.Stdout + " " + run.Stderr).Contains(Fold(expected), StringComparison.Ordinal)) problems.Add("the refusal does not hold the three values as display literals");
        if (HasRawControl(run.Stdout) || HasRawControl(run.Stderr)) problems.Add("a raw control character reached the terminal");
        if (_log.Requests.Any(r => r == "POST /v1/posts")) problems.Add("a POST /v1/posts was made");
        Red.None(problems, $"{verb}: exit 1, the refusal with the stored board as a literal, no raw control character, nothing sent");
    }

    /// <summary>
    /// RPB26, the receipt half, a <b>control</b> (ruling 13): the receipt's <c>board</c> line writes a stored board as a display literal, because <c>PrintPostReceipt</c> writes
    /// through <c>FrameText</c>, which quotes every string hole. The function is private, so it is called by reflection, with a receipt chosen here. Green on the seam by design.
    /// </summary>
    [Fact]
    public async Task R11_85_RPB26_TheReceiptWritesAStoredBoardAsALiteral_Control()
    {
        var method = typeof(Program).GetMethod("PrintPostReceipt", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        var receipt = new PostReceipt("01TESTRECEIPTPOST000000000A", "sha256:" + new string('a', 64), "1970-01-01T00:00:00.0000000+00:00", []);

        var run = await ConsoleCapture.CaptureAsync(() =>
        {
            method.Invoke(null, [receipt, "comment", Stored, new OwnText("sha256:" + new string('a', 64)), null, null, "alice"]);
            return Task.FromResult(0);
        });

        Assert.Contains("board " + Literal(Stored), run.Stdout, StringComparison.Ordinal);
        Assert.False(HasRawControl(run.Stdout) || HasRawControl(run.Stderr), "a raw control character reached the terminal");
    }

    // ---- RPB27 ----------------------------------------------------------------------------------

    private static string KindLine(string text) => text.Split('\n').First(l => l.StartsWith("kind ", StringComparison.Ordinal)).TrimEnd();

    /// <summary>
    /// RPB27, a <b>control</b> (ruling 13): <c>curia resend</c> of a kept act prints the same <c>kind ... board ...</c> line a first send of the same kind and board prints, because
    /// both call one function. Green on the seam by design.
    /// </summary>
    [Fact]
    public async Task R11_85_RPB27_AResendPrintsTheReceiptLineAFirstSendDoes_Control()
    {
        var first = await RunAsync(Reply("comment", Served));
        Assert.Equal(0, first.Exit);

        Assert.True(new ProfileStore(_log.Home).Load("alice").TryGetValue(out var agent, out var loadError), loadError?.Type);
        using (agent)
        {
            var draft = new PostDraft { Kind = PostKind.Comment, Board = Served, Parent = StubLog.PostId, Body = "A kept comment." };
            Assert.True(SubmissionBuilder.Build(agent!, draft, DateTimeOffset.UtcNow).TryGetValue(out var signed, out var error), error?.Type);
            KeptActFiles.Plant(Path.Combine(_log.Store.DirectoryFor("alice"), "acts"), signed!, "comment", Forum, agent!.Profile.AgentId, attempts: 1, lastAnswer: "503", board: Served);

            var resent = await RunAsync("resend", signed!.PrefixedDigest, "--agent", "alice", "--forum", Forum);

            Assert.True(resent.Exit == 0, resent.Stdout + resent.Stderr);
            Assert.Equal(KindLine(first.Stdout), KindLine(resent.Stdout));
        }
    }

    // ---- RPB28 ----------------------------------------------------------------------------------

    /// <summary>RPB28, a <b>control</b>: <c>--board</c> stays required on <c>ask</c> and <c>finding</c>, and on <c>endorse</c>, <c>reproduce</c> and <c>contradict</c> it is as it was.</summary>
    [Fact]
    public async Task R11_85_RPB28_TheOtherVerbsStillRequireABoard_Control()
    {
        var digest = "sha256:" + new string('a', 64);
        (string Verb, string[] Args)[] rows =
        [
            ("ask", ["ask", "--agent", "alice", "--title", "A title", "--body", "A body", "--forum", Forum]),
            ("finding", ["finding", "--agent", "alice", "--title", "A title", "--body", "A body", "--forum", Forum]),
            ("endorse", ["endorse", digest, "--agent", "alice", "--forum", Forum]),
            ("reproduce", ["reproduce", digest, "--agent", "alice", "--method", "m", "--body", "b", "--refs", "https://example.org/x", "--forum", Forum]),
            ("contradict", ["contradict", digest, "--agent", "alice", "--method", "m", "--body", "b", "--refs", "https://example.org/x", "--forum", Forum]),
        ];

        var problems = new List<string>();
        foreach (var (verb, args) in rows)
        {
            Reset();
            var run = await RunAsync(args);
            if (run.Exit != 1) problems.Add($"{verb}: exit {run.Exit}");
            if (!Fold(run.Stderr).Contains("--board <name> is required", StringComparison.Ordinal)) problems.Add($"{verb}: did not say --board is required ({Fold(run.Stderr)})");
            if (_log.Requests.Any(r => r == "POST /v1/posts")) problems.Add($"{verb}: sent something");
        }

        Red.None(problems, "exit 1, \"--board <name> is required\", nothing sent, for ask, finding, endorse, reproduce and contradict");
    }

    // ---- RPB29 ----------------------------------------------------------------------------------

    [GeneratedRegex(@"reply\b[^.]*\bnever\b[^.]*\b(?:makes|creates|make|create)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReplyNeverMakes();

    /// <summary>
    /// RPB29. The helps: the usage of <c>answer</c>, <c>comment</c> and <c>revision</c> shows <c>[--board &lt;b&gt;]</c> and carries R11.85's help sentence; the help of <c>ask</c> and
    /// <c>finding</c> and <c>curia help</c>'s board paragraph say that a reply never makes a board. The sentence is parsed from R11.85 words; "a reply never makes one" is
    /// matched as a phrase of those words, not as a literal. Predicted red: <c>--board &lt;b&gt;</c> is unbracketed and the sentence is absent.
    /// </summary>
    [Fact]
    public void R11_85_RPB29_TheHelpsSayTheBoardOfAReplyIsItsPosts()
    {
        var sentence = Fold(PublishedReplyBoardWords.HelpSentence());
        var problems = new List<string>();
        foreach (var verb in new[] { "answer", "comment", "revision" })
        {
            var help = Help.For(verb)!;
            if (!help.Contains("[--board <b>]", StringComparison.Ordinal)) problems.Add($"{verb}: the usage does not show [--board <b>]");
            if (!Fold(help).Contains(sentence, StringComparison.Ordinal)) problems.Add($"{verb}: lacks \"{sentence}\"");
        }

        foreach (var verb in new[] { "ask", "finding" })
            if (!ReplyNeverMakes().IsMatch(Fold(Help.For(verb)!))) problems.Add($"{verb}: does not say a reply never makes a board");
        if (!ReplyNeverMakes().IsMatch(Fold(Help.Usage))) problems.Add("curia help: the board paragraph does not say a reply never makes a board");

        Red.None(problems, "[--board <b>] and the published help sentence on the three verbs; \"a reply never makes\" a board on ask, finding and curia help");
    }
}
