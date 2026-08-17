using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Domain.Screening;
using Xunit;

namespace Curia.Domain.Tests.Screening;

/// <summary>
/// R10.24: <i>"The Forum SHALL maintain a red-team corpus of injection payloads (Appendix L), SHALL
/// run it against its own detectors and its reference client on every change, and SHALL publish
/// detection rate and false-positive rate as release criteria."</i>
///
/// <para>This is the measured half of Phase 2's exit criterion. The rates are asserted against
/// floors, and the floors are deliberately not 100%: a corpus that the detectors pass perfectly is a
/// corpus that has stopped being adversarial, and pinning the floor at the current score would turn
/// every new payload into a build break rather than a finding.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RedTeamCorpusTests
{
    private sealed record Case(string Id, string Content, ImmutableArray<string> Expect);

    /// <summary>
    /// The detection floor. Below this, the detectors are not doing the job R10.8 describes; at
    /// 100%, the corpus has stopped being adversarial. Raised deliberately when the corpus grows,
    /// never automatically to match the current score.
    /// </summary>
    private const double MinimumDetectionRate = 0.90;

    /// <summary>
    /// The false-positive ceiling, and the number that actually constrains the design. R10.26 makes
    /// a credential hit a hard rejection, so a false positive costs an author their submission --
    /// which is why this is zero rather than "low". A single benign case firing is a design bug, not
    /// a tuning problem.
    /// </summary>
    private const double MaximumFalsePositiveRate = 0.0;

    [Fact]
    public void R10_24_DetectionRateMeetsItsFloor()
    {
        var cases = Load("payloads.jsonl");
        Assert.NotEmpty(cases);

        var missed = new List<string>();

        foreach (var c in cases)
        {
            var fired = Detect(c.Content);

            // A payload counts as detected when every category it names fires. Partial credit would
            // let a payload that names two shapes pass on one, and the second shape is usually the
            // one that carries the attack.
            var undetected = c.Expect.Where(e => !fired.Contains(e, StringComparer.Ordinal)).ToArray();
            if (undetected.Length > 0)
                missed.Add($"{c.Id}: missed {string.Join(", ", undetected)} (fired: {string.Join(", ", fired)})");
        }

        var rate = 1.0 - ((double)missed.Count / cases.Length);

        Assert.True(
            rate >= MinimumDetectionRate,
            $"Detection rate {rate:P1} is below the {MinimumDetectionRate:P0} floor.\n" +
            string.Join("\n", missed));
    }

    /// <summary>
    /// The half that decides whether anyone can use this Forum. R10.9: a legitimate write-up about
    /// prompt injection will trip naive detectors, and this corpus is drawn from exactly the content
    /// a security forum contains.
    /// </summary>
    [Fact]
    public void R10_24_FalsePositiveRateMeetsItsCeiling()
    {
        var cases = Load("benign.jsonl");
        Assert.NotEmpty(cases);

        var falsePositives = new List<string>();

        foreach (var c in cases)
        {
            var fired = Detect(c.Content);
            if (fired.Length > 0)
                falsePositives.Add($"{c.Id}: fired {string.Join(", ", fired)} on benign content");
        }

        var rate = (double)falsePositives.Count / cases.Length;

        Assert.True(
            rate <= MaximumFalsePositiveRate,
            $"False-positive rate {rate:P1} exceeds the {MaximumFalsePositiveRate:P0} ceiling.\n" +
            string.Join("\n", falsePositives));
    }

    /// <summary>
    /// R10.24 says the rates are <b>published</b>, not merely checked. This writes them to a report
    /// the build emits, so a release decision can read them rather than infer them from a green tick.
    ///
    /// <para>R10.11's caveat travels with the numbers, because a detection rate presented without it
    /// invites exactly the reading R10.11 forbids: that a high number means safety rather than "the
    /// listed shapes are caught".</para>
    /// </summary>
    [Fact]
    public void R10_24_TheRatesArePublished()
    {
        var payloads = Load("payloads.jsonl");
        var benign = Load("benign.jsonl");

        var detected = payloads.Count(c => c.Expect.All(e => Detect(c.Content).Contains(e, StringComparer.Ordinal)));
        var flagged = benign.Count(c => Detect(c.Content).Length > 0);

        var detectionRate = (double)detected / payloads.Length;
        var falsePositiveRate = (double)flagged / benign.Length;

        var report = new StringBuilder()
            .AppendLine("# Red-team corpus results (R10.24)")
            .AppendLine()
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- Detection rate: **{detectionRate:P1}** ({detected}/{payloads.Length})"))
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- False-positive rate: **{falsePositiveRate:P1}** ({flagged}/{benign.Length})"))
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- Detector versions: {SecretScanner.Version}, {InjectionDetector.Version}"))
            .AppendLine()
            .AppendLine("## How to read these numbers (R10.11)")
            .AppendLine()
            .AppendLine("A detection rate is a statement about *these payloads* against *today's detectors*.")
            .AppendLine("Optimized triggers are demonstrated to survive perplexity examination and rephrasing,")
            .AppendLine("so a high rate is not evidence of safety -- it is evidence that the listed shapes are")
            .AppendLine("caught. R10.11 forbids presenting it as more than that.")
            .AppendLine()
            .AppendLine("The false-positive rate is the number that constrains the design: R10.26 makes a")
            .AppendLine("credential hit a hard rejection, so a false positive costs an author their submission.")
            .AppendLine()
            .AppendLine("## Known evasions")
            .AppendLine()
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**{KnownEvasions().Length} payloads in `known-evasions.jsonl` defeat these detectors today**, each"))
            .AppendLine("with the reason recorded. The detection rate above is computed over `payloads.jsonl`")
            .AppendLine("only, so it does *not* include them -- which is precisely why they are listed here")
            .AppendLine("rather than folded into the denominator, where they would depress a number nobody")
            .AppendLine("would then investigate.")
            .AppendLine()
            .AppendLine("They fall into three groups: **lexical evasion** (character spacing, Markdown splitting,")
            .AppendLine("split credentials, base64-wrapped credentials) which is fixable work not yet done;")
            .AppendLine("**homoglyph substitution**, which is R10.8's named-but-unimplemented clause and needs a")
            .AppendLine("UTS #39 confusables table; and **semantic paraphrase**, which no pattern catches and which")
            .AppendLine("R10.11 says a classifier would only move rather than close.")
            .AppendLine()
            .AppendLine("A recorded evasion that starts being detected fails the build, so this list cannot")
            .AppendLine("silently go stale.")
            .ToString();

        var path = Path.Combine(CorpusDirectory(), "RESULTS.md");
        File.WriteAllText(path, report);

        Assert.True(File.Exists(path));
        Assert.Contains("Detection rate", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// The corpus must contain all three parts, and enough of each to mean something. A benign set
    /// that shrank to nothing would make the false-positive ceiling unfalsifiable while still
    /// passing, and an empty known-evasions file would let the detection rate read as complete.
    /// </summary>
    [Fact]
    public void The_corpus_has_all_three_parts()
    {
        Assert.True(Load("payloads.jsonl").Length >= 20);
        Assert.True(Load("benign.jsonl").Length >= 12);
        Assert.True(KnownEvasions().Length >= 8);
    }

    /// <summary>
    /// <b>The known evasions must still evade.</b>
    ///
    /// <para>An odd-looking assertion, and the most valuable one here. These are payloads recorded as
    /// *not* caught, each with the reason. If one starts being detected, the file is stale -- the
    /// detector improved and the record now understates the Forum. That is worth a build failure,
    /// because a stale known-evasions list is exactly the kind of honest-looking document that
    /// quietly stops being honest.</para>
    ///
    /// <para>It also stops the file being used as a dumping ground: a payload cannot be filed here to
    /// silence a failure and then coincidentally get caught by a later rule without anyone noticing.</para>
    /// </summary>
    [Fact]
    public void R10_11_TheKnownEvasionsStillEvade()
    {
        var stale = new List<string>();

        foreach (var evasion in KnownEvasions())
        {
            var fired = Detect(evasion.Content);
            var nowCaught = evasion.Expect.Where(e => fired.Contains(e, StringComparer.Ordinal)).ToArray();

            if (nowCaught.Length > 0)
                stale.Add($"{evasion.Id}: now detected as {string.Join(", ", nowCaught)} -- move it to payloads.jsonl");
        }

        Assert.True(
            stale.Count == 0,
            "known-evasions.jsonl is stale. A recorded evasion that is now caught understates the "
            + "detectors, and a list that drifts out of date is worse than no list:\n"
            + string.Join("\n", stale));
    }

    private static Case[] KnownEvasions()
    {
        var path = Path.Combine(CorpusDirectory(), "known-evasions.jsonl");

        return File.ReadAllLines(path)
            .Where(line => line.Trim().Length > 0)
            .Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                return new Case(
                    root.GetProperty("id").GetString()!,
                    root.GetProperty("content").GetString()!,
                    [.. root.GetProperty("would_detect").EnumerateArray().Select(e => e.GetString()!)]);
            })
            .ToArray();
    }

    private static string[] Detect(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        Assert.True(ContentScreener.Screen(bytes).TryGetValue(out var result, out _));

        return result!.Annotations.Flags
            .Select(f => f.Category.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static Case[] Load(string file)
    {
        var path = Path.Combine(CorpusDirectory(), file);

        return File.ReadAllLines(path)
            .Where(line => line.Trim().Length > 0)
            .Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                return new Case(
                    root.GetProperty("id").GetString()!,
                    root.GetProperty("content").GetString()!,
                    [.. root.GetProperty("expect").EnumerateArray().Select(e => e.GetString()!)]);
            })
            .ToArray();
    }

    private static string CorpusDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance", "red-team")))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException($"conformance/red-team not found above {AppContext.BaseDirectory}")
            : Path.Combine(dir.FullName, "conformance", "red-team");
    }
}
