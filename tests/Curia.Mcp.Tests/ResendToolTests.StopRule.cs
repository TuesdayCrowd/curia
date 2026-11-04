#pragma warning disable CA1416 // the kept-act files are mode 0600 (R11.79): the probes read Unix modes, as PrivateFiles sets them

using System.Globalization;
using System.Net;
using System.Text.Json;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// ASA52 to ASA56, the tools' half (errata G58, R11.79, R11.80; plan Amendment 5, rulings 46 to 51): <c>curia_ask</c> and <c>curia_resend</c> against a stub Forum that answers as a script says, the same
/// oracle as <c>curia</c>'s half (<see cref="StopRuleOracle"/>), so the two clients are held to one rule.
/// </summary>
public sealed partial class ResendToolTests
{
    private static HttpResponseMessage Scripted(ScriptedSend send)
    {
        if (send.Unheard) throw new HttpRequestException(HttpRequestError.Unknown, "reset by the stub");
        if (send.Type is null) return new HttpResponseMessage((HttpStatusCode)send.Status) { Content = new StringContent("<html>an upstream said so</html>") };
        return Problem(send.Status, send.Type, "A refusal", send.Type == "curia/authz/denied" ? StopRuleOracle.BudgetDetail : null);
    }

    private async Task<(List<StepResult> Steps, string Digest)> RunScriptAsync(IReadOnlyList<ScriptedSend> script)
    {
        using var log = new StubLog();
        var kit = KitOver(log);
        var sent = 0;
        kit.Handler.Intercept = (r, _) => Task.FromResult<HttpResponseMessage?>(IsSubmission(r) ? Scripted(script[Math.Min(sent++, script.Count - 1)]) : null);

        var steps = new List<StepResult>();
        var first = await CallAsync(() => kit.Tools.AskAsync("b", "A title?", "A body.", null, null, Ct));
        var files = Directory.Exists(kit.ActsDirectory) ? Directory.GetFiles(kit.ActsDirectory) : [];
        var digest = files.Length == 1 ? "sha256:" + Path.GetFileNameWithoutExtension(files[0]) : "sha256:" + new string('0', 64);
        steps.Add(files.Length == 1 ? StopRuleOracle.Result(first.Text, files[0]) : new StepResult(first.Text, null, null, false));
        for (var i = 1; i < script.Count && files.Length == 1; i++)
        {
            var again = await CallAsync(() => kit.Tools.ResendAsync(digest, Ct));
            steps.Add(StopRuleOracle.Result(again.Text, files[0]));
        }

        return (steps, digest);
    }

    private async Task<List<string>> StopProblemsAsync(string label, IReadOnlyList<ScriptedSend> script, params string[] categories)
    {
        var (steps, digest) = await RunScriptAsync(script);
        return [.. StopRuleOracle.Problems(label, script, steps, digest).Where(p => categories.Any(c => p.StartsWith(c + ":", StringComparison.Ordinal)))];
    }

    private static readonly ScriptedSend StoreUnavailable503 = ScriptedSend.Typed(503, "curia/store/unavailable");

    /// <summary>ASA52, the tools' half. See <c>StopRuleTests</c>: after an answered refusal of <c>curia_resend</c> no stop sentence, the definite words once, 2 sends and 0 unanswered in the file.</summary>
    [Fact]
    public async Task R11_80_ASA52_NoStopSentenceAfterAnAnsweredRefusalAndTheDefiniteWordsOnce()
    {
        var problems = new List<string>();
        foreach (var answer in StopRuleOracle.DefiniteAnswers)
            problems.AddRange(await StopProblemsAsync("unheard then " + answer.Name, [ScriptedSend.NoAnswer, answer], "stop", "definite", "header"));

        None(problems, "after each answered refusal of a resend: no stop sentence, the definite words once, the file at 2 sends and 0 unanswered");
    }

    /// <summary>ASA53, the tools' half: the stop sentence from the second consecutive unanswered send, naming that count and the sends in all, and only on a refusal that is itself unanswered.</summary>
    [Fact]
    public async Task R11_80_ASA53_TheStopSentenceFromTheSecondUnansweredSendAndOnlyThen()
    {
        var problems = new List<string>();
        var log503 = ScriptedSend.Typed(503, "curia/log/unreadable");
        (string Label, ScriptedSend[] Script)[] runs =
        [
            ("unheard, 503 store/unavailable", [ScriptedSend.NoAnswer, StoreUnavailable503]),
            ("unheard, 503 log/unreadable", [ScriptedSend.NoAnswer, log503]),
            ("503 store/unavailable, unheard", [StoreUnavailable503, ScriptedSend.NoAnswer]),
            ("500, 500 untyped", [ScriptedSend.Untyped(500), ScriptedSend.Untyped(500)]),
            ("502, 502 untyped", [ScriptedSend.Untyped(502), ScriptedSend.Untyped(502)]),
            ("504, 504 untyped", [ScriptedSend.Untyped(504), ScriptedSend.Untyped(504)]),
            ("unheard, 403, unheard", [ScriptedSend.NoAnswer, StopRuleOracle.Budget403, ScriptedSend.NoAnswer]),
            ("unheard, unheard, 403", [ScriptedSend.NoAnswer, ScriptedSend.NoAnswer, StopRuleOracle.Budget403]),
            ("unheard, unheard, unheard", [ScriptedSend.NoAnswer, ScriptedSend.NoAnswer, ScriptedSend.NoAnswer]),
            ("503, 403, 503, 503", [StoreUnavailable503, StopRuleOracle.Budget403, StoreUnavailable503, StoreUnavailable503]),
        ];

        foreach (var (label, script) in runs)
            problems.AddRange(await StopProblemsAsync(label, script, "stop", "header"));

        None(problems, "the stop sentence from the second consecutive unanswered send, naming that count and the sends in all, on a refusal that is itself unanswered, and nowhere else");
    }

    /// <summary>ASA54, the tools' half: <c>attempts</c> counts every send, <c>unanswered</c> the trailing run, the other members and the mode are unchanged, a header with no <c>unanswered</c> reads as 0, and <c>curia_acts</c> says sent n time(s) equal to <c>attempts</c>.</summary>
    [Fact]
    public async Task R11_80_ASA54_TheHeaderCountsEverySendAndTheTrailingRunUnanswered()
    {
        var problems = new List<string>();
        ScriptedSend[] script = [StoreUnavailable503, StopRuleOracle.Budget403, ScriptedSend.NoAnswer, ScriptedSend.NoAnswer];
        using (var log = new StubLog())
        {
            var kit = KitOver(log);
            var sent = 0;
            kit.Handler.Intercept = (r, _) => Task.FromResult<HttpResponseMessage?>(IsSubmission(r) ? Scripted(script[Math.Min(sent++, script.Length - 1)]) : null);
            _ = await CallAsync(() => kit.Tools.AskAsync("b", "A title?", "A body.", null, null, Ct));
            var path = Directory.GetFiles(kit.ActsDirectory).Single();
            var digest = "sha256:" + Path.GetFileNameWithoutExtension(path);
            var steps = new List<StepResult> { StopRuleOracle.Result(string.Empty, path) };
            for (var i = 1; i < script.Length; i++)
            {
                var again = await CallAsync(() => kit.Tools.ResendAsync(digest, Ct));
                steps.Add(StopRuleOracle.Result(again.Text, path));
            }

            problems.AddRange(StopRuleOracle.Problems("a mixed run", script, steps, digest).Where(p => p.StartsWith("header:", StringComparison.Ordinal)));
            string[] members = ["v", "digest", "kind", "board", "forum", "author", "kept_at", "attempts", "last_attempt_at", "last_answer", "unanswered"];
            var names = StopRuleOracle.HeaderOf(path).Keys.Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(members.Order(StringComparer.Ordinal))) problems.Add($"header: the members are {string.Join(",", names)}, expected {string.Join(",", members.Order(StringComparer.Ordinal))}");
            if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite)) problems.Add($"header: the mode is {File.GetUnixFileMode(path)}, expected 0600");
            var listing = await CallAsync(() => kit.Tools.ActsAsync(Ct));
            if (!listing.Text.Contains("sent 4 time(s)", StringComparison.Ordinal)) problems.Add($"header: curia_acts does not say sent 4 time(s) ({listing.Text})");
        }

        using var oldLog = new StubLog();
        var old = KitOver(oldLog);
        var planted = Plant(old, attempts: 3, lastAnswer: "503");
        old.Handler.Intercept = (r, _) => Task.FromResult<HttpResponseMessage?>(IsSubmission(r) ? Scripted(ScriptedSend.NoAnswer) : null);
        var run = await CallAsync(() => old.Tools.ResendAsync(planted.PrefixedDigest, Ct));
        var after = StopRuleOracle.Result(run.Text, Path.Combine(old.ActsDirectory, planted.Digest + ".act"));
        if (after.Attempts != 4) problems.Add($"header: a planted file of 3 sends says {after.Attempts?.ToString(CultureInfo.InvariantCulture) ?? "missing"} after one more, expected 4");
        if (after.Unanswered != 1) problems.Add($"header: a planted file with no unanswered member says {after.Unanswered?.ToString(CultureInfo.InvariantCulture) ?? "missing"} after one unheard send, expected 1 (absent reads as 0)");
        if (StopRuleOracle.SaysStop(run.Text)) problems.Add("header: a file with no unanswered member and one unheard send printed a stop sentence");

        None(problems, "attempts counts every send, unanswered the trailing run, nothing else in the header moves, absent reads as 0, and the listing's sent n time(s) is attempts");
    }

    /// <summary>ASA55, the tools' half: the definite words once after a definite refusal of <c>curia_resend</c> with (a) first; never after an unanswered refusal or after a first send's refusal.</summary>
    [Fact]
    public async Task R11_80_ASA55_TheDefiniteWordsFollowADefiniteRefusalOfAResendAndNoOther()
    {
        var problems = new List<string>();
        foreach (var answer in StopRuleOracle.DefiniteAnswers)
            problems.AddRange(await StopProblemsAsync("unheard x3 then " + answer.Name, [ScriptedSend.NoAnswer, ScriptedSend.NoAnswer, ScriptedSend.NoAnswer, answer], "definite"));

        problems.AddRange(await StopProblemsAsync("unanswered refusals", [ScriptedSend.Untyped(502), StoreUnavailable503, ScriptedSend.NoAnswer], "definite"));

        // An unanswered refusal that is the first of its run (u=1) gets neither the stop sentence nor the definite words: after an answered send, and after a first send the Forum answered.
        problems.AddRange(await StopProblemsAsync("answered, then unanswered", [ScriptedSend.NoAnswer, StopRuleOracle.Budget403, StoreUnavailable503, ScriptedSend.NoAnswer, StopRuleOracle.Budget403, ScriptedSend.Untyped(502)], "definite"));
        problems.AddRange(await StopProblemsAsync("a first send answered 429, then unanswered", [ScriptedSend.Typed(429, "curia/rate/limited"), StoreUnavailable503], "definite"));

        var definite = Fold(PublishedResendWords.DefiniteWords);
        foreach (var first in new[] { ScriptedSend.Typed(429, "curia/rate/limited"), StopRuleOracle.Budget403 })
        {
            using var log = new StubLog();
            var kit = KitOver(log);
            kit.Handler.Intercept = (r, _) => Task.FromResult<HttpResponseMessage?>(IsSubmission(r) ? Scripted(first) : null);
            var run = await CallAsync(() => kit.Tools.AskAsync("b", "A title?", "A body.", null, null, Ct));
            if (StopRuleOracle.Count(run.Text, definite) != 0) problems.Add($"definite: a first send answered {first.Name} printed the definite words");
            if (StopRuleOracle.SaysStop(run.Text)) problems.Add($"definite: a first send answered {first.Name} printed a stop sentence");
        }

        None(problems, "the definite words once after a definite refusal of a resend, (a) before them, and nowhere else");
    }

    /// <summary>ASA56, the tools' half: <c>curia_acts</c> with two acts and a damaged file ends <c>2 kept, 1 damaged (3 files; at most 64 files are held)</c> and the published rest; the same at 64 files; the ordinary line with none damaged.</summary>
    [Fact]
    public async Task R11_79_ASA56_TheListingCountsFilesAndSaysHowManyAreDamaged()
    {
        var problems = new List<string>();
        foreach (var (acts, damaged) in new[] { (2, 1), (63, 1), (2, 0) })
        {
            using var log = new StubLog();
            var kit = KitOver(log);
            for (var i = 0; i < acts; i++) Plant(kit);
            if (damaged == 1) await File.WriteAllTextAsync(Path.Combine(kit.ActsDirectory, new string('c', 64) + ".act"), "not a header\n", Ct);

            var (fileCount, found) = StopRuleOracle.CountFiles(kit.ActsDirectory);
            var expected = damaged == 0
                ? Fold(PublishedResendWords.ListingFooterWords.Replace("<n>", fileCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                : Fold(PublishedResendWords.ListingFooterDamagedWords
                    .Replace("<a>", (fileCount - found).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("<k>", found.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                    .Replace("<n>", fileCount.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
            var listing = await kit.Tools.ActsAsync(Ct);
            var last = Fold(Text(listing).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd()).LastOrDefault() ?? string.Empty);
            if (!string.Equals(last, expected, StringComparison.Ordinal)) problems.Add($"{acts.ToString(CultureInfo.InvariantCulture)} acts and {damaged.ToString(CultureInfo.InvariantCulture)} damaged: the last line is \"{last}\", expected \"{expected}\"");
        }

        None(problems, "a last line that counts the acts that can be sent, the damaged files and the files held, equal to the probe's own count, and the ordinary line when none is damaged");
    }
}
