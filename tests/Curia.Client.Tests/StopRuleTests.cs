#pragma warning disable CA1416 // the kept-act files are mode 0600 and 0700 (R11.79): the probes read Unix modes, as PrivateFiles sets them

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// ASA52 to ASA56 (errata G58, R11.79, R11.80; plan Amendment 5, rulings 46 to 51), <c>curia</c>'s half: the stop sentence counts only the sends the Forum did not answer, the definite words follow
/// a resend refused by an answer, the header carries <c>unanswered</c>, and the listing counts damaged files apart. Run at the real command in process against a scripted Forum. The classes
/// (unanswered: no answer read, or any <c>5xx</c>; answered: any <c>4xx</c>) are written into <see cref="StopRuleOracle"/> from G58's published list, and every word is read from the errata.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each kit is a using declaration; the analyzer loses the ownership across the awaits.")]
[Collection("console")]
public sealed class StopRuleTests
{
    private static string Fold(string text) => StopRuleOracle.Fold(text);

    private static HttpResponseMessage Respond(ScriptedSend send)
    {
        if (send.Unheard) throw new HttpRequestException(HttpRequestError.Unknown, "reset by the stub");
        if (send.Type is null)
            return new HttpResponseMessage((HttpStatusCode)send.Status) { Content = new StringContent("<html>an upstream said so</html>") };
        return ScriptedForum.Problem(send.Status, send.Type, "A refusal", send.Type == "curia/authz/denied" ? StopRuleOracle.BudgetDetail : null);
    }

    /// <summary>Runs the script: the first send through <c>curia ask</c>, every other through <c>curia resend</c> of the digest the file names. Returns what each printed and what the file said after it.</summary>
    private static async Task<(IReadOnlyList<StepResult> Steps, string Digest, SessionKit Kit)> RunAsync(IReadOnlyList<ScriptedSend> script, SessionKit? use = null)
    {
        var kit = use ?? new SessionKit();
        kit.Forum.OnPost = (n, _, _) => Task.FromResult(Respond(script[Math.Min(n, script.Count - 1)]));
        var steps = new List<StepResult>();
        var first = await CliKit.RunAsync(kit, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");
        var files = kit.KeptFiles();
        var digest = files.Length == 1 ? "sha256:" + Path.GetFileNameWithoutExtension(files[0]) : "sha256:" + new string('0', 64);
        steps.Add(files.Length == 1 ? StopRuleOracle.Result(first.Text, files[0]) : new StepResult(first.Text, null, null, false));
        for (var i = 1; i < script.Count && files.Length == 1; i++)
        {
            var run = await CliKit.RunAsync(kit, "resend", digest);
            steps.Add(StopRuleOracle.Result(run.Text, files[0]));
        }

        return (steps, digest, kit);
    }

    private static async Task<List<string>> ProblemsAsync(string label, IReadOnlyList<ScriptedSend> script, params string[] categories)
    {
        var (steps, digest, kit) = await RunAsync(script);
        using (kit)
            return [.. StopRuleOracle.Problems(label, script, steps, digest).Where(p => categories.Any(c => p.StartsWith(c + ":", StringComparison.Ordinal)))];
    }

    private static readonly ScriptedSend Store503 = ScriptedSend.Typed(503, "curia/store/unavailable");

    /// <summary>
    /// ASA52. No stop after an answered refusal: an act sent unheard (1 send), then resent and answered <c>403</c> (a spent budget), <c>401</c>, <c>400</c>, <c>404</c>, <c>409 agent-in-flight</c> or
    /// <c>429</c>: no line says to stop, that the Forum is not answering or a wait of 5 minutes; the definite words occur exactly once; the file says 2 sends and 0 unanswered. Red on the tree of
    /// Task 8: the stop sentence printed at 2 sends whatever the answer (capture e).
    /// </summary>
    [Fact]
    public async Task R11_80_ASA52_NoStopSentenceAfterAnAnsweredRefusalAndTheDefiniteWordsOnce()
    {
        var problems = new List<string>();
        foreach (var answer in StopRuleOracle.DefiniteAnswers)
            problems.AddRange(await ProblemsAsync("unheard then " + answer.Name, [ScriptedSend.NoAnswer, answer], "stop", "definite", "header"));

        Red.None(problems, "after each answered refusal of a resend: no stop sentence, the definite words once, the file at 2 sends and 0 unanswered");
    }

    /// <summary>
    /// ASA53. The stop sentence after the second unanswered send, and only then: unheard then <c>503 store/unavailable</c>; unheard then <c>503 log/unreadable</c>; <c>503</c> then unheard; <c>500</c>, <c>502</c>
    /// and <c>504</c> untyped, each repeated: the stop sentence on the second with u=2 and n=2; unheard, <c>403</c>, unheard: none (u=1); unheard, unheard, <c>403</c>: none on the <c>403</c>; three unheard: u=3; and
    /// 503, 403, 503, 503: stop only on the last, u=2 and n=4.
    /// </summary>
    [Fact]
    public async Task R11_80_ASA53_TheStopSentenceFromTheSecondUnansweredSendAndOnlyThen()
    {
        var problems = new List<string>();
        var log503 = ScriptedSend.Typed(503, "curia/log/unreadable");
        (string Label, ScriptedSend[] Script)[] runs =
        [
            ("unheard, 503 store/unavailable", [ScriptedSend.NoAnswer, Store503]),
            ("unheard, 503 log/unreadable", [ScriptedSend.NoAnswer, log503]),
            ("503 store/unavailable, unheard", [Store503, ScriptedSend.NoAnswer]),
            ("500, 500 untyped", [ScriptedSend.Untyped(500), ScriptedSend.Untyped(500)]),
            ("502, 502 untyped", [ScriptedSend.Untyped(502), ScriptedSend.Untyped(502)]),
            ("504, 504 untyped", [ScriptedSend.Untyped(504), ScriptedSend.Untyped(504)]),
            ("unheard, 403, unheard", [ScriptedSend.NoAnswer, StopRuleOracle.Budget403, ScriptedSend.NoAnswer]),
            ("unheard, unheard, 403", [ScriptedSend.NoAnswer, ScriptedSend.NoAnswer, StopRuleOracle.Budget403]),
            ("unheard, unheard, unheard", [ScriptedSend.NoAnswer, ScriptedSend.NoAnswer, ScriptedSend.NoAnswer]),
            ("503, 403, 503, 503", [Store503, StopRuleOracle.Budget403, Store503, Store503]),
        ];

        foreach (var (label, script) in runs)
            problems.AddRange(await ProblemsAsync(label, script, "stop", "header"));

        Red.None(problems, "the stop sentence from the second consecutive unanswered send, naming that count and the sends in all, on a refusal that is itself unanswered, and nowhere else");
    }

    /// <summary>
    /// ASA54. The header: <c>attempts</c> counts every send asked, <c>unanswered</c> the trailing run; the file's other members, its mode and the digest recomputation are unchanged by a send; a header with no
    /// <c>unanswered</c> reads as 0; the listing's "sent n time(s)" equals <c>attempts</c>.
    /// </summary>
    [Fact]
    public async Task R11_80_ASA54_TheHeaderCountsEverySendAndTheTrailingRunUnanswered()
    {
        var problems = new List<string>();
        ScriptedSend[] script = [Store503, StopRuleOracle.Budget403, ScriptedSend.NoAnswer, ScriptedSend.NoAnswer];
        var (steps, digest, kit) = await RunAsync(script);
        using (kit)
        {
            problems.AddRange(StopRuleOracle.Problems("a mixed run", script, steps, digest).Where(p => p.StartsWith("header:", StringComparison.Ordinal)));

            var path = kit.KeptFiles().Single();
            var header = StopRuleOracle.HeaderOf(path);
            string[] members = ["v", "digest", "kind", "board", "forum", "author", "kept_at", "attempts", "last_attempt_at", "last_answer", "unanswered"];
            var names = header.Keys.Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(members.Order(StringComparer.Ordinal))) problems.Add($"header: the members are {string.Join(",", names)}, expected {string.Join(",", members.Order(StringComparer.Ordinal))}");
            if (header.TryGetValue("digest", out var d) && d.GetString() != digest) problems.Add("header: the digest member is not the digest of the file");
            if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) problems.Add($"header: the mode is {File.GetUnixFileMode(path)}, expected 0600");

            var listing = await CliKit.RunAsync(kit, "acts");
            if (!listing.Text.Contains("sent 4 time(s)", StringComparison.Ordinal)) problems.Add($"header: the listing does not say sent 4 time(s) ({listing.Text})");

            // The digest is still recomputed from the bytes before a send: a resend of the same file is accepted, a changed byte is not.
            var resent = await CliKit.RunAsync(kit, "resend", digest);
            if (kit.Forum.Posts.Count != 5) problems.Add($"header: the resend after the settled steps sent {kit.Forum.Posts.Count - 4} request(s), expected 1 ({resent.Text})");
        }

        // A header with no `unanswered` reads as 0: a file of 3 sends planted without the member, resent unheard, says 1 and 4.
        using var old = new SessionKit();
        var act = old.Build();
        old.Plant(act, attempts: 3, lastAnswer: "503");
        old.Forum.OnPost = (_, _, _) => Task.FromResult(Respond(ScriptedSend.NoAnswer));
        var run = await CliKit.RunAsync(old, "resend", act.PrefixedDigest);
        var after = StopRuleOracle.Result(run.Text, old.FileOf(act));
        if (after.Attempts != 4) problems.Add($"header: a planted file of 3 sends says {after.Attempts?.ToString(CultureInfo.InvariantCulture) ?? "missing"} after one more, expected 4");
        if (after.Unanswered != 1) problems.Add($"header: a planted file with no unanswered member says {after.Unanswered?.ToString(CultureInfo.InvariantCulture) ?? "missing"} after one unheard send, expected 1 (absent reads as 0)");
        if (StopRuleOracle.SaysStop(run.Text)) problems.Add("header: a file with no unanswered member and one unheard send printed a stop sentence");

        Red.None(problems, "attempts counts every send, unanswered the trailing run, nothing else in the header moves, absent reads as 0, and the listing's sent n time(s) is attempts");
    }

    /// <summary>
    /// ASA55. The definite words are not on the wrong refusals: printed once after each definite refusal of a resend (ASA52's list, and a resend that has been sent unheard three times), (a) of R11.81 first;
    /// never after an unanswered refusal (a <c>503</c>, an unheard send, an untyped <c>502</c>), and never after a first send's refusal (<c>429</c>, which keeps the act, and <c>403</c>, which removes it).
    /// </summary>
    [Fact]
    public async Task R11_80_ASA55_TheDefiniteWordsFollowADefiniteRefusalOfAResendAndNoOther()
    {
        var problems = new List<string>();
        foreach (var answer in StopRuleOracle.DefiniteAnswers)
            problems.AddRange(await ProblemsAsync("unheard x3 then " + answer.Name, [ScriptedSend.NoAnswer, ScriptedSend.NoAnswer, ScriptedSend.NoAnswer, answer], "definite"));

        problems.AddRange(await ProblemsAsync("unanswered refusals", [ScriptedSend.Untyped(502), Store503, ScriptedSend.NoAnswer], "definite"));

        // An unanswered refusal that is the first of its run (u=1) gets neither the stop sentence nor the definite words: after an answered send, and after a first send the Forum answered.
        problems.AddRange(await ProblemsAsync("answered, then unanswered", [ScriptedSend.NoAnswer, StopRuleOracle.Budget403, Store503, ScriptedSend.NoAnswer, StopRuleOracle.Budget403, ScriptedSend.Untyped(502)], "definite"));
        problems.AddRange(await ProblemsAsync("a first send answered 429, then unanswered", [ScriptedSend.Typed(429, "curia/rate/limited"), Store503], "definite"));

        var definite = Fold(PublishedResendWords.DefiniteWords);
        foreach (var first in new[] { ScriptedSend.Typed(429, "curia/rate/limited"), StopRuleOracle.Budget403 })
        {
            using var kit = new SessionKit();
            kit.Forum.OnPost = (_, _, _) => Task.FromResult(Respond(first));
            var run = await CliKit.RunAsync(kit, "ask", "--board", "b", "--title", "A title?", "--body", "A body.");
            if (StopRuleOracle.Count(run.Text, definite) != 0) problems.Add($"definite: a first send answered {first.Name} printed the definite words");
            if (StopRuleOracle.SaysStop(run.Text)) problems.Add($"definite: a first send answered {first.Name} printed a stop sentence");
        }

        Red.None(problems, "the definite words once after a definite refusal of a resend, (a) before them, and nowhere else");
    }

    private static string[] LastLines(string stdout) => [.. stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd())];

    /// <summary>
    /// ASA56. The listing with a damaged file: two acts and one damaged file read <c>2 kept, 1 damaged (3 files; at most 64 files are held)</c> and the published rest; 63 acts and one damaged file, <c>63 kept, 1
    /// damaged (64 files; ...)</c>; no damaged file, the ordinary last line byte for byte. The numbers equal the probe's own count of <c>*.act</c> files in the directory.
    /// </summary>
    [Fact]
    public async Task R11_79_ASA56_TheListingCountsFilesAndSaysHowManyAreDamaged()
    {
        var problems = new List<string>();
        foreach (var (acts, damaged) in new[] { (2, 1), (63, 1), (2, 0) })
        {
            using var kit = new SessionKit();
            for (var i = 0; i < acts; i++) kit.Plant(kit.Build(body: "act " + i.ToString(CultureInfo.InvariantCulture)));
            if (damaged == 1) await File.WriteAllTextAsync(Path.Combine(kit.ActsDirectory, new string('c', 64) + ".act"), "not a header\n", TestContext.Current.CancellationToken);

            var (files, found) = StopRuleOracle.CountFiles(kit.ActsDirectory);
            var expected = damaged == 0
                ? Fold(PublishedResendWords.ListingFooterWords.Replace("<n>", files.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                : Fold(PublishedResendWords.ListingFooterDamagedWords
                    .Replace("<a>", (files - found).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("<k>", found.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("<n>", files.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            var run = await CliKit.RunAsync(kit, "acts");
            var last = Fold(LastLines(run.Stdout).LastOrDefault() ?? string.Empty);
            if (!string.Equals(last, expected, StringComparison.Ordinal)) problems.Add($"{acts.ToString(CultureInfo.InvariantCulture)} acts and {damaged.ToString(CultureInfo.InvariantCulture)} damaged: the last line is \"{last}\", expected \"{expected}\"");
        }

        Red.None(problems, "a last line that counts the acts that can be sent, the damaged files and the files held, equal to the probe's own count, and the ordinary line when none is damaged");
    }
}
