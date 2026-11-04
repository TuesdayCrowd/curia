using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>One send of an act in a probe's script: what the Forum did with it. Unheard is no answer read; any status is a response.</summary>
internal sealed record ScriptedSend(string Name, int Status, string? Type = null, bool Unheard = false)
{
    internal static readonly ScriptedSend NoAnswer = new("unheard", 0, null, true);

    internal static ScriptedSend Typed(int status, string type) => new($"{status.ToString(CultureInfo.InvariantCulture)} {type}", status, type);

    internal static ScriptedSend Untyped(int status) => new($"{status.ToString(CultureInfo.InvariantCulture)} untyped", status);

    /// <summary>
    /// Errata G58, R11.80: a send is unanswered when no answer was read (the unheard refusal) or the Forum answered <c>5xx</c>, typed or not. Every <c>4xx</c> is an answer. Written here from the
    /// published list, never read from the code a probe is about.
    /// </summary>
    internal bool IsUnanswered => Unheard || Status >= 500;

    /// <summary>A definite answer (R11.80 definite words): a <c>4xx</c> that is not <c>already-recorded</c>.</summary>
    internal bool IsDefinite => !Unheard && Status is >= 400 and <= 499 && Type != "curia/ingest/already-recorded";
}

/// <summary>What one step of a script printed and what the kept file said after it.</summary>
internal sealed record StepResult(string Text, int? Attempts, int? Unanswered, bool FileKept);

/// <summary>
/// The oracle of the stop rule and the definite words (errata G58, R11.80), one for both clients: given a script of sends (the first send, then resends) and what each step printed and left in
/// the file, the problems found. Each problem begins with its category (<c>stop</c>, <c>definite</c>, <c>header</c>) so a probe reports the ones it holds. Words are read from the errata.
/// </summary>
internal static class StopRuleOracle
{
    internal static string Fold(string text) => Regex.Replace(text, @"\s+", " ", RegexOptions.CultureInvariant);

    /// <summary>The expected <c>unanswered</c> after step <paramref name="i"/>: the length of the run of unanswered sends ending with it (0 after an answered one).</summary>
    internal static int ExpectedUnanswered(IReadOnlyList<ScriptedSend> script, int i)
    {
        var run = 0;
        for (var k = i; k >= 0 && script[k].IsUnanswered; k--) run++;
        return run;
    }

    /// <summary>Whether step <paramref name="i"/> is followed by the stop sentence: from the second consecutive unanswered send, and only on a refusal that is itself unanswered.</summary>
    internal static bool ExpectedStop(IReadOnlyList<ScriptedSend> script, int i) => script[i].IsUnanswered && ExpectedUnanswered(script, i) >= 2;

    /// <summary>Whether step <paramref name="i"/> is followed by the definite words: a resend (not the first send) refused by a definite answer.</summary>
    internal static bool ExpectedDefinite(IReadOnlyList<ScriptedSend> script, int i) => i >= 1 && script[i].IsDefinite;

    internal static int Count(string text, string words) => Regex.Count(text, Regex.Escape(words), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    /// <summary>The words that would say to stop or that the Forum is not answering, however phrased: text before the first placeholder of the stop words, the imperative and the wait.</summary>
    internal static bool SaysStop(string text) =>
        text.Contains(Fold(PublishedResendWords.StopWords[..PublishedResendWords.StopWords.IndexOf("<u>", StringComparison.Ordinal)]), StringComparison.Ordinal)
        || text.Contains("Stop here", StringComparison.Ordinal)
        || text.Contains("not answering", StringComparison.Ordinal)
        || text.Contains("5 minutes", StringComparison.Ordinal);

    internal static IReadOnlyList<string> Problems(string label, IReadOnlyList<ScriptedSend> script, IReadOnlyList<StepResult> steps, string digest)
    {
        var problems = new List<string>();
        var definiteWords = Fold(PublishedResendWords.DefiniteWords);
        var notResolved = Fold(PublishedResendWords.Slug(PublishedResendWords.ResendRefusalWords.Replace("<digest>", digest, StringComparison.Ordinal), PublishedResendWords.KitAgent));
        for (var i = 0; i < steps.Count && i < script.Count; i++)
        {
            var at = $"{label}, send {(i + 1).ToString(CultureInfo.InvariantCulture)} ({script[i].Name})";
            var text = steps[i].Text;
            var expectedU = ExpectedUnanswered(script, i);

            if (ExpectedStop(script, i))
            {
                var stop = Fold(PublishedResendWords.StopFor(expectedU, i + 1, digest));
                var said = Count(text, stop);
                if (said != 1) problems.Add($"stop: {at}: the published stop sentence for u={expectedU.ToString(CultureInfo.InvariantCulture)} n={(i + 1).ToString(CultureInfo.InvariantCulture)} occurs {said.ToString(CultureInfo.InvariantCulture)} times, expected 1");
            }
            else if (SaysStop(text))
            {
                problems.Add($"stop: {at}: a stop sentence is printed (u={expectedU.ToString(CultureInfo.InvariantCulture)}, the refusal is {(script[i].IsUnanswered ? "unanswered" : "an answer")})");
            }

            var definite = Count(text, definiteWords);
            var wantDefinite = ExpectedDefinite(script, i) ? 1 : 0;
            if (definite != wantDefinite) problems.Add($"definite: {at}: the published definite words occur {definite.ToString(CultureInfo.InvariantCulture)} times, expected {wantDefinite.ToString(CultureInfo.InvariantCulture)}");

            if (i >= 1)
            {
                // R11.81 (a) is the resend's own sentence after every refusal that is not 201 or already-recorded, and comes before the definite words.
                var a = Count(text, notResolved);
                if (a != 1) problems.Add($"definite: {at}: R11.81 (a) occurs {a.ToString(CultureInfo.InvariantCulture)} times, expected 1");
                else if (wantDefinite == 1 && definite == 1 && text.IndexOf(notResolved, StringComparison.Ordinal) > text.IndexOf(definiteWords, StringComparison.Ordinal)) problems.Add($"definite: {at}: (a) does not come first");
            }

            if (!steps[i].FileKept) problems.Add($"header: {at}: the file was not kept");
            if (steps[i].Attempts != i + 1) problems.Add($"header: {at}: attempts is {steps[i].Attempts?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, expected {(i + 1).ToString(CultureInfo.InvariantCulture)}");
            if (steps[i].Unanswered != expectedU) problems.Add($"header: {at}: unanswered is {steps[i].Unanswered?.ToString(CultureInfo.InvariantCulture) ?? "missing"}, expected {expectedU.ToString(CultureInfo.InvariantCulture)}");
        }

        if (steps.Count != script.Count) problems.Add($"header: {label}: {steps.Count.ToString(CultureInfo.InvariantCulture)} steps ran of {script.Count.ToString(CultureInfo.InvariantCulture)}");
        return problems;
    }

    /// <summary>The members of line 1 of a kept file, by name.</summary>
    internal static Dictionary<string, JsonElement> HeaderOf(string path)
    {
        var line = File.ReadLines(path).First();
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
    }

    /// <summary>The number of <c>*.act</c> files in <paramref name="directory"/> and of those whose first line is no header, counted by the probe and not by the code under test.</summary>
    internal static (int Files, int Damaged) CountFiles(string directory)
    {
        var files = Directory.GetFiles(directory, "*.act");
        var damaged = 0;
        foreach (var file in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadLines(file).First());
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("digest", out _)) damaged++;
            }
            catch (JsonException)
            {
                damaged++;
            }
        }

        return (files.Length, damaged);
    }

    internal static StepResult Result(string text, string path)
    {
        if (!File.Exists(path)) return new StepResult(text, null, null, false);
        var header = HeaderOf(path);
        int? Member(string name) => header.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        return new StepResult(text, Member("attempts"), Member("unanswered"), true);
    }

    // ----- the scripts, from the published list of unanswered and answered sends -----

    internal static readonly ScriptedSend Budget403 = new("403 rate-budget-exhausted", 403, "curia/authz/denied");

    /// <summary>The answers a resend can get that are definite: ASA52's list.</summary>
    internal static IReadOnlyList<ScriptedSend> DefiniteAnswers { get; } =
    [
        Budget403,
        ScriptedSend.Typed(401, "curia/auth/unauthorized"),
        ScriptedSend.Typed(400, "curia/content/invalid"),
        ScriptedSend.Typed(404, "curia/posts/not-found"),
        ScriptedSend.Typed(409, "curia/budget/agent-in-flight"),
        ScriptedSend.Typed(429, "curia/rate/limited"),
    ];

    /// <summary>The detail the 403 carries, so the Forum's own line names a slot.</summary>
    internal const string BudgetDetail = "table-11/rate-budget-exhausted next_slot_at=2026-01-01T00:00:00Z";
}
