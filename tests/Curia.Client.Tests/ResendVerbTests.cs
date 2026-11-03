#pragma warning disable CA1416 // modes are read and set as PrivateFiles does

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// ASA12 to ASA17, ASA19, ASA26 and ASA40 (errata G58, R11.79, R11.81): <c>curia resend</c> and <c>curia acts</c> at the real command, run in process against a scripted Forum
/// (<see cref="ScriptedForum"/>) in a profile store the kit owns, with the kept act planted in the published form where a probe needs one the seam does not make. Every word is
/// read from G58 (<see cref="PublishedResendWords"/>), folded for whitespace; the digest an act has is the one the kit's own signing produced and the one the stub computes
/// from what it received (<see cref="ReceivedAct"/>), never what the client prints of itself. Each probe is in its failing form: the seam answers <c>NOT SENT: not built</c>.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each kit is a using declaration; the analyzer loses the ownership across the awaits.")]
[Collection("console")]
public sealed partial class ResendVerbTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string RecordedPostId = "01TESTRECEIPTPOST000000000A";

    private static string Fold(string text) => CliRun.Fold(text);

    [GeneratedRegex(@"\d+\s+seconds?", RegexOptions.CultureInvariant)]
    private static partial Regex Seconds();

    private static HttpResponseMessage AlreadyRecorded() =>
        ScriptedForum.Problem(409, "curia/ingest/already-recorded", "The Forum already holds this act", "post=" + RecordedPostId);

    /// <summary>
    /// ASA12. The resend sends the kept bytes: the stub compares the bytes (equal to the kept ones), a proof of its own (a <c>jti</c>), the answer <c>201</c> prints the verb's
    /// receipt lines and removes the file, and the digest the stub computes from what it received equals the kept one. Predicted red: <c>NOT SENT: not built</c>.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA12_TheResendSendsTheKeptBytesAndRemovesTheFileOnA201()
    {
        using var kit = new SessionKit();
        var act = kit.Build();
        kit.Plant(act, attempts: 1, lastAnswer: "503");
        kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Receipt(act.PrefixedDigest, RecordedPostId));

        var run = await CliKit.RunAsync(kit, "resend", act.PrefixedDigest);

        var problems = new List<string>();
        if (kit.Forum.Posts.Count != 1) problems.Add($"{kit.Forum.Posts.Count} request(s) were sent, expected 1 ({run.Text})");
        else
        {
            var sent = kit.Forum.Posts[0];
            if (!sent.Body.AsSpan().SequenceEqual(act.Wire.Span)) problems.Add("the bytes sent are not the kept bytes");
            if (string.IsNullOrEmpty(sent.Jti)) problems.Add("no proof of its own (no jti)");
            if (ReceivedAct.DigestOf(System.Text.Encoding.UTF8.GetString(sent.Body)) != act.PrefixedDigest) problems.Add("the digest of what was received is not the kept digest");
        }

        if (run.Exit != 0) problems.Add($"exit {run.Exit}, expected 0");
        if (!run.Text.Contains("posted", StringComparison.Ordinal)) problems.Add("no receipt line (posted)");
        if (!run.Text.Contains(RecordedPostId, StringComparison.Ordinal)) problems.Add("the receipt names no post id");
        if (File.Exists(kit.FileOf(act))) problems.Add("the file was not removed");
        Red.None(problems, "one request with the kept bytes and a fresh proof, the verb's receipt lines, exit 0, the file removed");
    }

    /// <summary>
    /// ASA13. <c>already-recorded</c> answering a resend is exit 0: the first word is <c>already recorded</c>, the id is a display literal, the line says this command recorded
    /// nothing and points at <c>curia read</c>, and the file is removed (R11.81 (b)). Predicted red: <c>exit 3</c> (here, on the seam, exit 1 and <c>NOT SENT: not built</c>).
    /// </summary>
    [Fact]
    public async Task R11_81_ASA13_AlreadyRecordedAnsweringAResendIsASuccess()
    {
        using var kit = new SessionKit();
        var act = kit.Build();
        kit.Plant(act, attempts: 1, lastAnswer: "503");
        kit.Forum.OnPost = (_, _, _) => Task.FromResult(AlreadyRecorded());

        var run = await CliKit.RunAsync(kit, "resend", act.PrefixedDigest);

        var (firstLine, after) = PublishedResendWords.AlreadyRecordedWords;
        var expectedFirst = Fold(firstLine.Replace("<post id>", DisplayLiteral(RecordedPostId), StringComparison.Ordinal));
        var expectedAfter = Fold(after.Replace("<post id>", DisplayLiteral(RecordedPostId), StringComparison.Ordinal));
        var problems = new List<string>();
        if (run.Exit != 0) problems.Add($"exit {run.Exit}, expected 0");
        if (!Fold(run.Stdout).TrimStart().StartsWith("already recorded", StringComparison.Ordinal)) problems.Add($"the first word is not \"already recorded\" (stdout: \"{Fold(run.Stdout)}\")");
        if (!run.Text.Contains(expectedFirst, StringComparison.Ordinal)) problems.Add($"lacks the published first line \"{expectedFirst}\"");
        if (!run.Text.Contains(expectedAfter, StringComparison.Ordinal)) problems.Add("lacks the published sentence after it (this command recorded nothing; curia read)");
        if (File.Exists(kit.FileOf(act))) problems.Add("the file was not removed");
        Red.None(problems, "exit 0, `already recorded` first, the id as a display literal, nothing recorded by this command, the file removed");
    }

    private static string DisplayLiteral(string value) => "\"" + value + "\"";

    /// <summary>
    /// ASA14. The stop rule, both sides: a resend inside a stall shorter than the bound is answered <c>already-recorded</c> (a definite answer) and prints no stop sentence; the first
    /// unresolved refusal prints none; the second unresolved send prints it with the attempt count and the published wait, the digest and the instruction, the file stays and the
    /// exit is 8. (A third send after an hour is not run here: the command's clock is the system's.) Predicted red: <c>no stop sentence</c>.
    /// </summary>
    [Fact]
    public async Task R11_80_ASA14_TheStopSentenceComesFromTheSecondUnresolvedSendAndNotBefore()
    {
        var problems = new List<string>();
        var stopStart = Fold(PublishedResendWords.StopWords)[..30];

        using (var inside = new SessionKit())
        {
            var act = inside.Build();
            inside.Plant(act, attempts: 1, lastAnswer: "503");
            inside.Forum.OnPost = async (_, _, ct) => { await Task.Delay(300, ct); return AlreadyRecorded(); };
            var run = await CliKit.RunAsync(inside, "resend", act.PrefixedDigest);
            if (run.Text.Contains(stopStart, StringComparison.Ordinal)) problems.Add("a resend answered already-recorded inside a stall printed the stop sentence");
            if (run.Exit != 0) problems.Add($"the resend inside a stall exited {run.Exit}, expected 0");
        }

        using var kit = new SessionKit();
        var held = kit.Build();
        kit.Plant(held);
        kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(503, "curia/store/unavailable", "A store fault"));

        var first = await CliKit.RunAsync(kit, "resend", held.PrefixedDigest);
        if (first.Text.Contains(stopStart, StringComparison.Ordinal)) problems.Add("the first unresolved send printed the stop sentence");
        var second = await CliKit.RunAsync(kit, "resend", held.PrefixedDigest);

        var stop = Fold(PublishedResendWords.StopWords.Replace("<n>", "2", StringComparison.Ordinal).Replace("<digest>", held.PrefixedDigest, StringComparison.Ordinal));
        var (count, unit) = PublishedResendWords.StopWait;
        if (!second.Text.Contains(stop, StringComparison.Ordinal)) problems.Add("no stop sentence");
        if (!second.Text.Contains($"{count.ToString(CultureInfo.InvariantCulture)} {unit}", StringComparison.Ordinal)) problems.Add("the published wait is not in the output");
        if (!File.Exists(kit.FileOf(held))) problems.Add("the file was not kept");
        if (second.Exit != 8) problems.Add($"exit {second.Exit}, expected 8");
        Red.None(problems, "no stop sentence before the second unresolved send; from it, the published sentence with 2 attempts, the file kept, exit 8");
    }

    /// <summary>
    /// ASA15. A refused resend does not resolve: <c>403</c> budget, <c>409</c> <c>agent-in-flight</c> and <c>503</c> <c>log/unreadable</c> to a resend keep the file and the line says the
    /// refusal does not say whether the first send was recorded and names <c>recheck</c> and <c>acts --discard</c> (R11.81 (a)); the same refusals to a first send remove the file
    /// (the control, which is green on the seam). Predicted red: <c>file removed</c> (on the seam, the resend never reaches the Forum).
    /// </summary>
    [Fact]
    public async Task R11_81_ASA15_ARefusedResendKeepsTheActAndSaysItResolvesNothing()
    {
        var problems = new List<string>();
        var refusals = new (string Name, int Status, string Type, string? Detail)[]
        {
            ("403 budget", 403, "curia/authz/denied", "table-11/rate-budget-exhausted next_slot_at=2026-01-01T00:00:00Z"),
            ("409 agent-in-flight", 409, "curia/budget/agent-in-flight", null),
            ("503 log/unreadable", 503, "curia/log/unreadable", null),
        };

        foreach (var (name, status, type, detail) in refusals)
        {
            using var resent = new SessionKit();
            var act = resent.Build();
            resent.Plant(act);
            resent.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(status, type, "A refusal", detail));
            var run = await CliKit.RunAsync(resent, "resend", act.PrefixedDigest);
            if (!File.Exists(resent.FileOf(act))) problems.Add($"resend, {name}: file removed");
            // Compared after the substitution (trap 117): the words show <digest> and the printer fills it with the digest of the act kept.
            var words = Fold(PublishedResendWords.ResendRefusalWords.Replace("<digest>", act.PrefixedDigest, StringComparison.Ordinal));
            if (!run.Text.Contains(words, StringComparison.Ordinal)) problems.Add($"resend, {name}: lacks the published words that the refusal does not say whether the first send was recorded");

            // The control: the same refusal to a first send is a definite answer for it, so nothing is kept.
            using var first = new SessionKit();
            first.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(status, type, "A refusal", detail));
            await CliKit.RunAsync(first, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");
            if (first.KeptFiles().Length != 0) problems.Add($"first send, {name}: the file was kept (control)");
        }

        Red.None(problems, "the file kept and the published words after each refused resend; the same refusals to a first send leave no file");
    }

    /// <summary>
    /// ASA16. Definite content refusals of a resend (<c>400</c>, <c>401</c>) keep the act; <c>curia acts --discard &lt;digest&gt;</c> removes it; the control, a first send, removes
    /// it. Predicted red: <c>file removed</c> (on the seam the resend never reaches the Forum and the discard is not built).
    /// </summary>
    [Fact]
    public async Task R11_79_ASA16_ADefiniteRefusalOfAResendKeepsTheActUntilItIsDiscarded()
    {
        var problems = new List<string>();
        foreach (var (name, status, type) in new[] { ("400", 400, "curia/admit/invalid-envelope"), ("401", 401, "curia/authn/dpop-invalid") })
        {
            using var kit = new SessionKit();
            var act = kit.Build();
            kit.Plant(act);
            kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(status, type, "A refusal"));
            await CliKit.RunAsync(kit, "resend", act.PrefixedDigest);
            if (!File.Exists(kit.FileOf(act))) { problems.Add($"{name}: the resend did not keep the file"); continue; }
            var discard = await CliKit.RunAsync(kit, "acts", "--discard", act.PrefixedDigest);
            if (File.Exists(kit.FileOf(act))) problems.Add($"{name}: acts --discard did not remove the file ({discard.Text})");
        }

        Red.None(problems, "the file kept by a 400 and a 401 to a resend, then removed by acts --discard");
    }

    /// <summary>
    /// ASA17. <c>curia acts</c> lists digest, kind, board (a display literal), attempts and last answer, one line per act; <c>--discard &lt;digest&gt;</c> removes one and only that one; a
    /// digest not held is <c>NOT SENT</c>, exit 1. Predicted red: <c>unknown command acts</c> (on the seam: <c>NOT SENT: not built</c> for the listing).
    /// </summary>
    [Fact]
    public async Task R11_79_ASA17_ActsListsWhatIsHeldAndDiscardRemovesOneAndOnlyThatOne()
    {
        using var kit = new SessionKit();
        var one = kit.Build(body: "one");
        var two = kit.Build(PostKind.Comment, body: "two");
        kit.Plant(one, kind: "question", attempts: 2, lastAnswer: "503");
        kit.Plant(two, kind: "comment");

        var listing = await CliKit.RunAsync(kit, "acts");
        var problems = new List<string>();
        if (listing.Exit != 0) problems.Add($"acts exited {listing.Exit} ({listing.Text})");
        foreach (var act in new[] { one, two })
            if (!listing.Text.Contains(act.PrefixedDigest, StringComparison.Ordinal)) problems.Add($"the listing lacks {act.PrefixedDigest}");
        if (!listing.Text.Contains("\"b\"", StringComparison.Ordinal)) problems.Add("the board is not a display literal");
        if (!listing.Text.Contains("question", StringComparison.Ordinal) || !listing.Text.Contains("comment", StringComparison.Ordinal)) problems.Add("the kinds are not listed");

        var discarded = await CliKit.RunAsync(kit, "acts", "--discard", one.PrefixedDigest);
        if (discarded.Exit != 0) problems.Add($"--discard exited {discarded.Exit}");
        if (File.Exists(kit.FileOf(one))) problems.Add("the discarded file is still there");
        if (!File.Exists(kit.FileOf(two))) problems.Add("--discard removed the other act too");

        var unknown = await CliKit.RunAsync(kit, "acts", "--discard", "sha256:" + new string('e', 64));
        if (unknown.Exit != 1 || !unknown.Text.Contains("NOT SENT", StringComparison.Ordinal)) problems.Add($"a digest not held: exit {unknown.Exit}, \"{unknown.Text}\"; expected NOT SENT, exit 1");
        Red.None(problems, "both acts listed with kind and board, --discard removing one, an unheld digest NOT SENT exit 1");
    }

    /// <summary>
    /// ASA19. Nothing resends by itself (R11.64): after a <c>503</c> the stub sees exactly one submission (two with a nonce challenge) however long the command waits; and after
    /// a resend refused <c>503</c> likewise exactly one. Green on the seam (a control); red under FASA20. The tool's leg is in the Mcp suite.
    /// </summary>
    [Fact]
    public async Task R11_64_ASA19_NothingIsSentTwiceWithoutBeingAsked()
    {
        var problems = new List<string>();

        using (var kit = new SessionKit())
        {
            kit.Forum.OnPost = (_, _, _) => Task.FromResult(ScriptedForum.Problem(503, "curia/store/unavailable"));
            await CliKit.RunAsync(kit, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");
            await Task.Delay(1500, Ct);
            if (kit.Forum.Posts.Count != 1) problems.Add($"after a 503 the stub saw {kit.Forum.Posts.Count} submissions, expected 1");
        }

        using (var kit = new SessionKit())
        {
            kit.Forum.OnPost = (n, _, _) => Task.FromResult(n == 0
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Headers = { { "DPoP-Nonce", "n1" } }, Content = new StringContent("{}") }
                : ScriptedForum.Problem(503, "curia/store/unavailable"));
            await CliKit.RunAsync(kit, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");
            await Task.Delay(1500, Ct);
            if (kit.Forum.Posts.Count != 2) problems.Add($"with a nonce challenge the stub saw {kit.Forum.Posts.Count} submissions, expected 2");
        }

        Red.None(problems, "one submission after a 503 and two with a nonce challenge, however long the command waits");
    }

    /// <summary>
    /// ASA26. A signer that is down (plan ruling 25 (a)): the refusal of <c>resend</c> says the act is kept and that nothing was sent; the refusal of another write verb says
    /// nothing was sent and does not say kept; the file is untouched. Predicted red: <c>signer unusable</c> only.
    /// </summary>
    [Fact]
    public async Task R11_20_ASA26_ASignerThatIsDownRefusesAResendNamingTheKeptActAndTheOtherVerbsNeverSayKept()
    {
        using var held = TestSigner.Create("held-1");
        using var kit = new SessionKit(held: held);
        var act = kit.Build();
        var path = kit.Plant(act);
        var before = await File.ReadAllBytesAsync(path, Ct);
        File.Delete(held.Command);

        var resend = await CliKit.RunAsync(kit, "resend", act.PrefixedDigest);
        var ask = await CliKit.RunAsync(kit, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");

        var problems = new List<string>();
        if (kit.Forum.Posts.Count != 0) problems.Add($"{kit.Forum.Posts.Count} request(s) were sent");
        if (!resend.Text.Contains($"The act stays kept (digest {act.PrefixedDigest}). Nothing was sent.", StringComparison.Ordinal)) problems.Add($"the resend refusal does not say the act stays kept and nothing was sent: \"{resend.Text}\"");
        if (!ask.Text.Contains("Nothing was sent.", StringComparison.Ordinal)) problems.Add($"the ask refusal does not say nothing was sent: \"{ask.Text}\"");
        if (ask.Text.Contains("kept", StringComparison.Ordinal)) problems.Add("the ask refusal says kept");
        if (!(await File.ReadAllBytesAsync(path, Ct)).AsSpan().SequenceEqual(before)) problems.Add("the kept file was touched");
        Red.None(problems, "resend: the act stays kept and nothing was sent; ask: nothing was sent and no word of kept; the file untouched");
    }

    /// <summary>
    /// ASA40. The resend is the first caller of <c>already-recorded</c> (item 105): the verb (a first send answered <c>already-recorded</c>) keeps the old refusal text and exit 3
    /// (the control: green on the seam); the resend answers it as the <c>already recorded</c> line, exit 0. Predicted red: <c>exit 3 for a resend</c>.
    /// </summary>
    [Fact]
    public async Task R11_81_ASA40_AlreadyRecordedIsARefusalForTheVerbAndTheAnswerWantedForTheResend()
    {
        var problems = new List<string>();

        using (var verb = new SessionKit())
        {
            verb.Forum.OnPost = (_, _, _) => Task.FromResult(AlreadyRecorded());
            var run = await CliKit.RunAsync(verb, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");
            if (run.Exit != 3) problems.Add($"the verb answered already-recorded exits {run.Exit}, expected 3 (control)");
            if (!run.Text.Contains("That post is the one just sent", StringComparison.Ordinal)) problems.Add("the verb's refusal text is not the old one (control)");
        }

        using var kit = new SessionKit();
        var act = kit.Build();
        kit.Plant(act);
        kit.Forum.OnPost = (_, _, _) => Task.FromResult(AlreadyRecorded());
        var resend = await CliKit.RunAsync(kit, "resend", act.PrefixedDigest);
        if (resend.Exit != 0) problems.Add($"exit {resend.Exit} for a resend, expected 0");
        if (resend.Text.Contains("That post is the one just sent", StringComparison.Ordinal)) problems.Add("the resend printed the verb's refusal text");
        Red.None(problems, "the verb: the old refusal, exit 3; the resend: the already recorded line, exit 0");
    }
}
