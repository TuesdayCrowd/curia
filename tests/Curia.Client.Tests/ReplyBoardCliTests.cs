#pragma warning disable CA1416 // the kept-act files are mode 0600 and 0700 (R11.79): the probes read and set Unix modes, as PrivateFiles does

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
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
public sealed class ReplyBoardCliTests : IDisposable
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
    /// (an answer and a comment: the draft is sent with the typed board, because the read is advisory for the board; a revision is never sent without its read, ruling 28,
    /// and prints the read's refusal and the published revision sentence, which does not offer <c>--board</c>). (c) <c>--board</c> absent and the read refused: nothing is sent, the output carries the
    /// read's own refusal (what <c>curia read</c> prints on the same stub) and its exit code, and then the published second sentence (an answer, a comment) or the revision sentence.
    /// Predicted red (amendment 3): for <c>revision</c> the revision sentence is absent and, in (c), the answer sentence is present; in (b) the output holds the read's refusal alone.
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

        // (b) given, read refused. An answer and a comment are sent with the typed board; a revision is never sent, whatever the flag (ruling 28).
        Reset();
        var given = await RunAsync(Reply(verb, "dotnet"));
        if (verb != "revision")
        {
            var onGiven = Sent();
            if (given.Exit != 0) problems.Add($"(b) exit {given.Exit}");
            if (onGiven.Count != 1 || onGiven[0].Board != "dotnet") problems.Add($"(b) {onGiven.Count} act(s), board {string.Join(",", onGiven.Select(s => Literal(s.Board)))}, expected one on {Literal("dotnet")}");
        }
        else
        {
            problems.AddRange(RevisionUnread("(b)", given, read, readFirstLine));
        }

        // (c) absent, read refused.
        Reset();
        var absent = await RunAsync(Reply(verb, board: null));
        var all = Fold(absent.Stderr + " " + absent.Stdout);
        if (_log.Requests.Any(r => r == "POST /v1/posts")) problems.Add("(c) a POST /v1/posts was made");
        if (absent.Exit != read.Exit) problems.Add($"(c) exit {absent.Exit}, the read's own is {read.Exit}");
        if (!all.Contains(readFirstLine, StringComparison.Ordinal)) problems.Add($"(c) lacks the read's own refusal \"{readFirstLine}\" (it said: {all})");
        if (verb != "revision")
        {
            if (!all.Contains(Fold(PublishedReplyBoardWords.UnreadSentence()), StringComparison.Ordinal)) problems.Add("(c) lacks the published second sentence");
        }
        else
        {
            problems.AddRange(RevisionUnread("(c)", absent, read, readFirstLine));
        }

        Red.None(problems, $"{verb}: (a) one read and one act on the equal board; (b) the typed board sent when the read failed; (c) nothing sent, the read's refusal and exit code, then the second sentence");
    }

    /// <summary>
    /// Ruling 28, for a revision whose post could not be read, in either <c>--board</c> arm: no POST at all, the read's own first line and exit code (from <c>curia read</c> on the same
    /// stub state), the published revision sentence, and not the answer and comment sentence, which offers a flag that fails identically.
    /// </summary>
    private List<string> RevisionUnread(string arm, CliRun run, CliRun read, string readFirstLine)
    {
        var problems = new List<string>();
        var all = Fold(run.Stderr + " " + run.Stdout);
        if (_log.Requests.Any(r => r == "POST /v1/posts")) problems.Add($"{arm} a POST /v1/posts was made");
        if (run.Exit != read.Exit) problems.Add($"{arm} exit {run.Exit}, the read's own is {read.Exit}");
        if (!all.Contains(readFirstLine, StringComparison.Ordinal)) problems.Add($"{arm} lacks the read's own refusal \"{readFirstLine}\" (it said: {all})");
        if (!all.Contains(Fold(PublishedReplyBoardWords.RevisionUnreadSentence()), StringComparison.Ordinal)) problems.Add($"{arm} lacks the published revision sentence (it said: {all})");
        if (all.Contains(Fold(PublishedReplyBoardWords.UnreadSentence()), StringComparison.Ordinal)) problems.Add($"{arm} carries the answer and comment sentence, which offers --board to a verb it cannot help");
        return problems;
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
    /// the typed board is sent. The revision row (ruling 38) is a <b>control</b> on the tree it was added to (the call sits after the verified read, the ownership check and the supersession
    /// check, on a parent that is the agent's own, verified and unsuperseded in the stub); its value is FRPB37.
    /// </summary>
    [Theory]
    [InlineData("answer")]
    [InlineData("comment")]
    [InlineData("revision")]
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
    /// RPB26, the receipt half, a <b>control</b> (ruling 13) driven through the real path (ruling 25): <c>--board</c> omitted and a parent whose served board holds a line feed and an
    /// ESC, so the board is taken from the post and the receipt <c>PostAsync</c> prints writes it as a display literal, with no raw control character on either stream.
    /// </summary>
    [Theory]
    [InlineData("answer")]
    [InlineData("comment")]
    public async Task R11_85_RPB26_TheReceiptWritesAStoredBoardAsALiteral_Control(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);
        _log.ServedBoard = Stored;

        var run = await RunAsync(Reply(verb, board: null));

        var problems = new List<string>();
        if (run.Exit != 0) problems.Add($"exit {run.Exit} ({Fold(run.Stdout + " " + run.Stderr)})");
        if (!run.Stdout.Contains("board " + Literal(Stored), StringComparison.Ordinal)) problems.Add("the receipt does not name the stored board as a display literal");
        if (HasRawControl(run.Stdout) || HasRawControl(run.Stderr)) problems.Add("a raw control character reached the terminal");
        Red.None(problems, $"{verb}: the receipt names the stored board as a display literal and no raw control character reaches the terminal");
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

    /// <summary>
    /// RPB29. The helps: the usage of <c>answer</c>, <c>comment</c> and <c>revision</c> shows <c>[--board &lt;b&gt;]</c> and carries R11.85's help sentence; the help of <c>ask</c> and
    /// <c>finding</c> and <c>curia help</c>'s board paragraph carry the sentence "a reply never makes a board", character for character, parsed from R11.85 words (ruling 26). Predicted red: <c>--board &lt;b&gt;</c> is unbracketed and the sentence is absent.
    /// Amendment 3 (rulings 32 and 43): <c>curia revision</c>'s help sentence and <c>curia help</c>'s clause are parsed from the errata too; the revision help sentence is red on the tree it was added to.
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

        var revisionSentence = Fold(PublishedReplyBoardWords.RevisionHelpSentence());
        if (!Fold(Help.For("revision")!).Contains(revisionSentence, StringComparison.Ordinal)) problems.Add($"revision: lacks \"{revisionSentence}\"");

        var never = Fold(PublishedReplyBoardWords.ReplyNeverMakesSentence());
        foreach (var verb in new[] { "ask", "finding" })
            if (!Fold(Help.For(verb)!).Contains(never, StringComparison.Ordinal)) problems.Add($"{verb}: lacks \"{never}\"");
        if (!Fold(Help.Usage).Contains(never, StringComparison.Ordinal)) problems.Add($"curia help: the board paragraph lacks \"{never}\"");
        var clause = Fold(PublishedReplyBoardWords.HelpClause());
        if (!Fold(Help.Usage).Contains(sentence + " " + clause, StringComparison.Ordinal)) problems.Add($"curia help: the board paragraph lacks \"{clause}\" after the help sentence");

        Red.None(problems, "[--board <b>] and the published help sentence on the three verbs; \"a reply never makes\" a board on ask, finding and curia help");
    }

    // ---- RPB35 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB35 (ruling 33). The typed board and the post's are compared in NFC, the form the route compares: a parent served on <c>caf</c> and U+00E9 and a board typed as <c>cafe</c> and U+0301
    /// (the same board after NFC) are equal, so the verb sends one act, on the post's board, and does not refuse with two literals that look identical. Predicted red on the current tree: the
    /// typed board is compared ordinally and refused.
    /// </summary>
    [Theory]
    [MemberData(nameof(Verbs))]
    public async Task R11_85_RPB35_ABoardTypedInADifferentNormalFormIsTheSameBoard(string verb)
    {
        ArgumentNullException.ThrowIfNull(verb);
        var composed = "caf" + (char)0xE9;
        var decomposed = "cafe" + (char)0x301;
        _log.ServedBoard = composed;

        var run = await RunAsync(Reply(verb, decomposed));

        var sent = Sent();
        var problems = new List<string>();
        if (run.Exit != 0) problems.Add($"exit {run.Exit} ({Fold(run.Stdout + " " + run.Stderr)})");
        if (sent.Count != 1) problems.Add($"{sent.Count} act(s) were sent, expected 1");
        else if (sent[0].Board != composed) problems.Add($"the envelope's board is {Literal(sent[0].Board)}, expected {Literal(composed)}");
        Red.None(problems, $"{verb}: a board typed in NFD equal to the post's in NFC is sent, once, on the post's board");
    }

    // ---- RPB36 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB36, a <b>control</b> (ruling 39). A resend consults no board rule: a kept comment signed on <c>dotnet</c> under a parent the stub serves on <c>rust</c>, which a verb now refuses before
    /// signing, is sent exactly as it was signed, once (a send is a <c>POST /v1/posts</c> and its challenge), with no read of the parent. The means to seed such an act is <see cref="KeptActFiles.Plant"/>,
    /// as RPB27 uses it; the board is the one the verbs refuse. Its value is FRPB33.
    /// </summary>
    [Fact]
    public async Task R11_85_RPB36_AResendConsultsNoBoardRule_Control()
    {
        Assert.True(new ProfileStore(_log.Home).Load("alice").TryGetValue(out var agent, out var loadError), loadError?.Type);
        using (agent)
        {
            var draft = new PostDraft { Kind = PostKind.Comment, Board = "dotnet", Parent = StubLog.PostId, Body = "A kept comment." };
            Assert.True(SubmissionBuilder.Build(agent!, draft, DateTimeOffset.UtcNow).TryGetValue(out var signed, out var error), error?.Type);
            KeptActFiles.Plant(Path.Combine(_log.Store.DirectoryFor("alice"), "acts"), signed!, "comment", Forum, agent!.Profile.AgentId, attempts: 1, lastAnswer: "503", board: "dotnet");
            Reset();

            var refused = await RunAsync(Reply("comment", "dotnet"));
            Reset();
            var resent = await RunAsync("resend", signed!.PrefixedDigest, "--agent", "alice", "--forum", Forum);

            var problems = new List<string>();
            if (refused.Exit != PublishedReplyBoardWords.RefusalExit()) problems.Add($"the verb did not refuse the board {Literal("dotnet")} (exit {refused.Exit}), so the seeded act is not one a verb now refuses");
            if (resent.Exit != 0) problems.Add($"exit {resent.Exit} ({Fold(resent.Stdout + " " + resent.Stderr)})");
            if (ParentReads != 0) problems.Add($"{ParentReads} read(s) of the parent, expected none");
            if (!_log.Requests.Any(r => r == "POST /v1/posts")) problems.Add("nothing was sent");
            var sent = Sent();
            if (sent.Count != 1 || sent[0].Board != "dotnet" || sent[0].Digest != signed.PrefixedDigest) problems.Add($"{sent.Count} distinct act(s) were sent, expected the kept bytes once ({string.Join(",", sent.Select(s => Literal(s.Board)))})");
            Red.None(problems, "the kept comment on dotnet sent as signed under a parent served on rust, with no read of the parent");
        }
    }

    // ---- RPB37 ----------------------------------------------------------------------------------

    /// <summary>
    /// RPB37, a <b>control</b> (ruling 43). An empty or valueless <c>--board</c> on an answer, a comment or a revision is refused with the published sentence and exit code, before any request: the stub
    /// is asked for nothing at all. Green on the current tree, because <c>Program.cs</c> already refuses it before any read; its value is FRPB35.
    /// </summary>
    [Theory]
    [InlineData("answer", "empty")]
    [InlineData("answer", "valueless")]
    [InlineData("comment", "empty")]
    [InlineData("comment", "valueless")]
    [InlineData("revision", "empty")]
    [InlineData("revision", "valueless")]
    public async Task R11_85_RPB37_AnEmptyBoardIsRefusedBeforeAnyRequest_Control(string verb, string shape)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(shape);
        string[] args = shape == "empty"
            ? [verb, "--agent", "alice", "--board", "", "--parent", StubLog.PostId, "--body", "A reply.", "--forum", Forum]
            : [verb, "--agent", "alice", "--parent", StubLog.PostId, "--body", "A reply.", "--forum", Forum, "--board"];

        var run = await RunAsync(args);

        var problems = new List<string>();
        if (run.Exit != PublishedReplyBoardWords.EmptyBoardExit()) problems.Add($"exit {run.Exit}, expected {PublishedReplyBoardWords.EmptyBoardExit()}");
        if (!Fold(run.Stdout + " " + run.Stderr).Contains(Fold(PublishedReplyBoardWords.EmptyBoardSentence()), StringComparison.Ordinal))
            problems.Add($"lacks the published sentence \"{Fold(PublishedReplyBoardWords.EmptyBoardSentence())}\" (it said: {Fold(run.Stdout + " " + run.Stderr)})");
        if (_log.Requests.Count > 0) problems.Add($"{_log.Requests.Count} request(s) were made: {string.Join(", ", _log.Requests)}");
        Red.None(problems, $"{verb} ({shape}): exit 1, the published sentence, no request at all");
    }
}
