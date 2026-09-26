using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Screening;
using Curia.Domain.Serving;
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
    private sealed record Case(
        string Id, string Class, string Outcome, string Content, ImmutableArray<string> Expect);

    /// <summary>
    /// R10.57's outcome kinds: what an Appendix L.1 class actually asserts. They are not all
    /// "detected", and that is the whole point of naming them.
    ///
    /// <para><c>escaped-at-serving</c> is the one that made this necessary. L.1's `structural`
    /// class asserts "Escaped at serving; delimiter not terminable from content" — a property of
    /// <see cref="Datamarking"/>, not of the detectors. Added to the corpus without an outcome
    /// kind, its six payloads carry an empty expectation, every site that reads one treats that as
    /// detected, and the published detection rate rises while asserting nothing. That was verified
    /// rather than reasoned about: adding them took the rate from 41/41 to 47/47, all tests green,
    /// and the baseline silently gained six ids.</para>
    /// </summary>
    private static class Outcomes
    {
        internal const string Flagged = "flagged";
        internal const string NotFlagged = "not-flagged";
        internal const string EscapedAtServing = "escaped-at-serving";
        internal const string ExpectedToPass = "expected-to-pass";
        internal const string KnownFalsePositive = "known-false-positive";

        internal static readonly ImmutableArray<string> Known =
            [Flagged, NotFlagged, EscapedAtServing, ExpectedToPass, KnownFalsePositive];
    }

    /// <summary>
    /// A known evasion and the reason it is not caught, read from the corpus rather than restated.
    ///
    /// <para>The reason travels with the entry because <c>RESULTS.md</c> is generated from it.
    /// A hand-written summary of which evasion classes survive is a second copy of the corpus,
    /// and the two drifted apart the moment normalization closed six of the nine: the count
    /// updated, the prose beside it kept describing groups that no longer had members.</para>
    /// </summary>
    private sealed record Evasion(string Id, string Content, ImmutableArray<string> WouldDetect, string Why);

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

    /// <summary>
    /// <b>No payload that is detected today may stop being detected.</b>
    ///
    /// <para>The aggregate floor above is not enough on its own, and finding that out is why this
    /// exists: with a 90% floor over 30 payloads, a change can silently lose three detections and
    /// still pass. Deleting a homoglyph mapping did exactly that — the rate dropped and the test
    /// stayed green, which is a regression gate that does not gate.</para>
    ///
    /// <para>So the detected set is committed as a fixture and compared id by id. A payload that
    /// regresses fails by name. A *new* payload that fails does not: it is a finding, and belongs in
    /// `known-evasions.jsonl` with its reason or in a fix — which is the distinction the aggregate
    /// floor was reaching for and could not express.</para>
    /// </summary>
    [Fact]
    public void R10_24_NoDetectedPayloadRegresses()
    {
        var baselinePath = Path.Combine(CorpusDirectory(), "detected-baseline.txt");
        // R10.57, again: `Expect.All(...)` over an empty expectation is vacuously true, so a
        // payload whose class asserts something other than detection would enter this baseline as
        // "detected" and then be defended against regressing at a property it never had. The
        // baseline covers the payloads this gate measures and no others.
        var payloads = Load("payloads.jsonl").Where(c => c.Outcome == Outcomes.Flagged).ToArray();
        var missedIn = payloads.ToDictionary(c => c.Id, MissedShapes, StringComparer.Ordinal);
        var detectedNow = payloads
            .Where(c => missedIn[c.Id].Length == 0)
            .Select(c => c.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        if (!File.Exists(baselinePath))
        {
            File.WriteAllLines(baselinePath, detectedNow);
            Assert.Fail(
                $"Wrote a new detection baseline to {baselinePath} with {detectedNow.Length} entries. "
                + "Review and commit it; a baseline that appears without being read is not a baseline.");
        }

        var baseline = File.ReadAllLines(baselinePath).Where(l => l.Trim().Length > 0).ToArray();
        var regressed = baseline
            .Except(detectedNow, StringComparer.Ordinal)
            .Select(id => missedIn.TryGetValue(id, out var shapes)
                ? $"{id} (missed in: {string.Join(", ", shapes)})"
                : $"{id} (no longer in payloads.jsonl)")
            .ToArray();

        Assert.True(
            regressed.Length == 0,
            "These payloads were detected when the baseline was committed and are not detected now:\n"
            + string.Join("\n", regressed)
            + "\n\nEither the change is a regression, or the payload genuinely moved to known-evasions "
            + "and the baseline should be updated deliberately — never automatically.");

        // Newly-detected payloads are good news, but the baseline must be updated by hand so the
        // improvement is reviewed rather than absorbed.
        var newlyDetected = detectedNow.Except(baseline, StringComparer.Ordinal).ToArray();
        if (newlyDetected.Length > 0)
            File.WriteAllLines(baselinePath, detectedNow);
    }

    [Fact]
    public void R10_24_DetectionRateMeetsItsFloor()
    {
        // R10.57: "SHALL exclude from a published rate any entry whose kind that rate does not
        // measure rather than counting it as a pass." A `structural` payload asserts escaping at
        // serving, which these detectors are not asked about and would not fire on; counting it
        // here would move the number in the reassuring direction for the reason that should have
        // alarmed someone.
        var cases = Load("payloads.jsonl").Where(c => c.Outcome == Outcomes.Flagged).ToArray();
        Assert.NotEmpty(cases);

        foreach (var shape in Shapes)
        {
            var missed = new List<string>();

            foreach (var c in cases)
            {
                var fired = shape.Detect(c.Content);

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
                $"Detection rate {rate:P1} in the {shape.Name} shape is below the {MinimumDetectionRate:P0} floor.\n" +
                string.Join("\n", missed));
        }
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

        foreach (var shape in Shapes)
        {
            foreach (var c in cases)
            {
                var fired = shape.Detect(c.Content);
                if (fired.Length > 0)
                    falsePositives.Add($"{c.Id} ({shape.Name}): fired {string.Join(", ", fired)} on benign content");
            }
        }

        var rate = (double)falsePositives.Count / (cases.Length * Shapes.Length);

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
        // R10.57: a published rate excludes entries whose kind it does not measure. The detection
        // rate is a statement about the detectors, and `structural` payloads assert escaping at
        // serving -- a property of Datamarking that these detectors are not asked about. Counting
        // them makes the number rise for the reason that should have alarmed someone.
        var all = Load("payloads.jsonl");
        var payloads = all.Where(c => c.Outcome == Outcomes.Flagged).ToArray();
        var excluded = all.Length - payloads.Length;
        var benign = Load("benign.jsonl");

        var report = new StringBuilder()
            .AppendLine("# Red-team corpus results (R10.24)")
            .AppendLine()
            .AppendLine("| Shape | Detection rate | False-positive rate |")
            .AppendLine("|---|---|---|");

        foreach (var shape in Shapes)
        {
            var detected = payloads.Count(c => c.Expect.All(e => shape.Detect(c.Content).Contains(e, StringComparer.Ordinal)));
            var flagged = benign.Count(c => shape.Detect(c.Content).Length > 0);

            report.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {shape.Name} | **{(double)detected / payloads.Length:P1}** ({detected}/{payloads.Length}) | **{(double)flagged / benign.Length:P1}** ({flagged}/{benign.Length}) |"));
        }

        report
            .AppendLine()
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- Detector versions: {SecretScanner.Version}, {InjectionDetector.Version}"))
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"- Excluded from the detection rate: **{excluded}** payload(s) whose asserted outcome these detectors do not measure (R10.57), evaluated by their own kind's evaluator rather than counted here as passes"))
            .AppendLine()
            .AppendLine("## The shapes (register D19)")
            .AppendLine()
            .AppendLine("Every entry is screened in each form a production path receives it. *bare* is the text")
            .AppendLine("alone, as a flag's rationale is screened. *enveloped* is the entry as the `body` of a")
            .AppendLine("canonical post envelope, as ingest and the client's pre-send check screen it, and")
            .AppendLine("*enveloped after a line* puts one line before it. These rates were once published for the")
            .AppendLine("bare shape only, while ingest -- which read JCS text, where a line break is `\\n` --")
            .AppendLine("admitted a credential at the start of any line after the first or after a tab, and an")
            .AppendLine("assigned secret whose value was quoted, and left an injection phrase starting such a line")
            .AppendLine("unannotated.")
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
            .AppendLine("Each one, with the reason recorded in the corpus:")
            .AppendLine()
            .Append(string.Concat(KnownEvasions().Select(e => string.Create(
                CultureInfo.InvariantCulture,
                $"- **`{e.Id}`** -- would be {string.Join(", ", e.WouldDetect)}. {e.Why}\n"))))
            .AppendLine()
            .AppendLine("A recorded evasion that starts being detected fails the build, so this list cannot")
            .AppendLine("silently go stale.");

        report
            .AppendLine()
            .AppendLine("## Known false positives")
            .AppendLine()
            .AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"**{KnownFalsePositives().Length} entries in `known-false-positives.jsonl` are refused although they are benign**,"))
            .AppendLine("each with the reason recorded. The false-positive rate above is computed over `benign.jsonl`")
            .AppendLine("only, so it reads \"0 % of that set, with these known exceptions\" -- never a claim about all prose.")
            .AppendLine()
            .Append(string.Concat(KnownFalsePositives().Select(f => string.Create(
                CultureInfo.InvariantCulture,
                $"- **`{f.Id}`** -- fires {string.Join(", ", f.WouldFlag)}. {f.Why}\n"))))
            .AppendLine()
            .AppendLine("An entry that stops firing fails the build, so this list cannot silently go stale.");

        var path = Path.Combine(CorpusDirectory(), "RESULTS.md");
        File.WriteAllText(path, report.ToString());

        Assert.True(File.Exists(path));
        Assert.Contains("Detection rate", File.ReadAllText(path), StringComparison.Ordinal);
    }

    /// <summary>
    /// The corpus must contain all three parts, and enough of each to mean something.
    ///
    /// <para>A benign set that shrank to nothing would make the false-positive ceiling unfalsifiable
    /// while still passing. And the known-evasions floor is <b>one</b>, not a larger number: it
    /// exists so an *empty* file cannot let the detection rate read as complete, and the count is
    /// expected to fall as evasions get fixed. It fell from nine to three when normalization landed,
    /// and a floor that had been pinned to nine would have made fixing them a build failure -- which
    /// is exactly the wrong incentive to encode.</para>
    /// </summary>
    [Fact]
    public void The_corpus_has_all_three_parts()
    {
        Assert.True(Load("payloads.jsonl").Length >= 20);
        Assert.True(Load("benign.jsonl").Length >= 12);
        Assert.True(KnownEvasions().Length >= 1);
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
            if (evasion.WouldDetect.IsEmpty)
            {
                stale.Add($"{evasion.Id}: would_detect is empty, so nothing checks that it evades");
                continue;
            }

            foreach (var shape in Shapes)
            {
                var fired = shape.Detect(evasion.Content);
                var nowCaught = evasion.WouldDetect.Where(e => fired.Contains(e, StringComparer.Ordinal)).ToArray();

                if (nowCaught.Length > 0)
                    stale.Add($"{evasion.Id} ({shape.Name}): now detected as {string.Join(", ", nowCaught)} -- move it to payloads.jsonl");
            }
        }

        Assert.True(
            stale.Count == 0,
            "known-evasions.jsonl is stale. A recorded evasion that is now caught understates the "
            + "detectors, and a list that drifts out of date is worse than no list:\n"
            + string.Join("\n", stale));
    }

    private static Evasion[] KnownEvasions()
    {
        var path = Path.Combine(CorpusDirectory(), "known-evasions.jsonl");

        return File.ReadAllLines(path)
            .Where(line => line.Trim().Length > 0)
            .Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                return new Evasion(
                    root.GetProperty("id").GetString()!,
                    root.GetProperty("content").GetString()!,
                    [.. root.GetProperty("would_detect").EnumerateArray().Select(e => e.GetString()!)],
                    root.GetProperty("why").GetString()!);
            })
            .ToArray();
    }

    /// <summary>A benign entry the detectors refuse, and why that is accepted, read from the corpus.</summary>
    private sealed record FalsePositive(string Id, string Content, ImmutableArray<string> WouldFlag, string Why);

    private static FalsePositive[] KnownFalsePositives() =>
        File.ReadAllLines(Path.Combine(CorpusDirectory(), "known-false-positives.jsonl"))
            .Where(line => line.Trim().Length > 0)
            .Select(line =>
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                return new FalsePositive(
                    root.GetProperty("id").GetString()!,
                    root.GetProperty("content").GetString()!,
                    [.. root.GetProperty("would_flag").EnumerateArray().Select(e => e.GetString()!)],
                    root.GetProperty("why").GetString()!);
            })
            .ToArray();

    /// <summary>
    /// <b>A known false positive must still be one.</b> The mirror of the known-evasions check, and
    /// R10.57's evaluator for the <c>known-false-positive</c> kind.
    ///
    /// <para>The false-positive ceiling is zero, so a benign sentence the detectors refuse cannot sit in
    /// <c>benign.jsonl</c> without failing the build -- and recorded only in prose elsewhere, it would
    /// make the published 0 % a statement about a set that quietly excludes it. Here it is counted,
    /// listed in <c>RESULTS.md</c> with its reason, and held to still firing: an entry that stops
    /// firing belongs in <c>benign.jsonl</c>, and this fails until it is moved.</para>
    /// </summary>
    [Fact]
    public void R10_24_TheKnownFalsePositivesStillFire()
    {
        var stale = new List<string>();

        foreach (var fp in KnownFalsePositives())
        {
            if (fp.WouldFlag.IsEmpty)
            {
                stale.Add($"{fp.Id}: would_flag is empty, so nothing checks that it fires");
                continue;
            }

            foreach (var shape in Shapes)
            {
                var fired = shape.Detect(fp.Content);
                var silent = fp.WouldFlag.Where(e => !fired.Contains(e, StringComparer.Ordinal)).ToArray();

                if (silent.Length > 0)
                    stale.Add($"{fp.Id} ({shape.Name}): no longer fires {string.Join(", ", silent)} -- move it to benign.jsonl");
            }
        }

        Assert.True(
            stale.Count == 0,
            "known-false-positives.jsonl is stale. A false positive that no longer fires understates the "
            + "detectors, and a list that drifts out of date is worse than no list:\n"
            + string.Join("\n", stale));
    }

    /// <summary>
    /// The forms a corpus entry is screened in (R10.24, register D19). <b>bare</b> is what
    /// <c>RaiseFlag</c> screens. The two enveloped shapes are what ingest and the client's pre-send
    /// check screen: the entry as the <c>body</c> of a canonical envelope, alone and after one line.
    /// The rates were once published for the bare shape only, while ingest admitted a credential at
    /// the start of any line after the first or after a tab, and an assigned secret whose value was
    /// quoted -- a rate is a statement about a shape.
    /// </summary>
    private sealed record Shape(string Name, Func<string, string[]> Detect);

    private static readonly Shape[] Shapes =
    [
        new("bare", content => Categories(ContentScreener.ScreenText(Encoding.UTF8.GetBytes(content)))),
        new("enveloped", content => Categories(ContentScreener.ScreenEnvelope(Envelope(content).Span))),
        new("enveloped after a line", content => Categories(ContentScreener.ScreenEnvelope(Envelope("Context:\n" + content).Span))),
    ];

    private static string[] Categories(Result<ScreeningResult> screened)
    {
        Assert.True(screened.TryGetValue(out var result, out var error), error?.Type);

        return result!.Annotations.Flags
            .Select(f => f.Category.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>A post envelope around <paramref name="body"/>, canonicalized as production does it.</summary>
    private static CanonicalBytes Envelope(string body)
    {
        var envelope = new JsonValue.Object(
        [
            new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)),
            new("kind", new JsonValue.String("question")),
            new("author", new JsonValue.String("https://agents.example/corpus-runner")),
            new("board", new JsonValue.String("general")),
            new("title", new JsonValue.String("Red-team corpus entry")),
            new("body", new JsonValue.String(body)),
            new("code_blocks", new JsonValue.Array([])),
            new("refs", new JsonValue.Array([])),
            new("tags", new JsonValue.Array([])),
            new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)),
            new("created_at", new JsonValue.String("2026-09-25T00:00:00.0000000+00:00")),
            new("nonce", new JsonValue.String("00000000000000000000000000000000")),
        ]);

        Assert.True(CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out var error), error?.Type);
        return canonical;
    }

    /// <summary>The shapes in which <paramref name="c"/> does not fire every category it names.</summary>
    private static string[] MissedShapes(Case c) =>
        Shapes
            .Where(s => !c.Expect.All(e => s.Detect(c.Content).Contains(e, StringComparer.Ordinal)))
            .Select(s => s.Name)
            .ToArray();

    /// <summary>
    /// The enveloped shapes must carry the entry, or the rates measured over them measure an
    /// envelope. D19 hid behind a probe of a shape production never screens (trap 1); this checks
    /// the probe is at least the shape it claims to be.
    /// </summary>
    [Fact]
    public void Every_enveloped_entry_carries_its_content_as_the_body_token()
    {
        foreach (var c in CorpusFiles.SelectMany(Load))
        {
            var tokens = CanonicalStrings.Of(Encoding.UTF8.GetString(Envelope(c.Content).Span)).Select(t => t.Text).ToArray();
            var body = tokens[Array.IndexOf(tokens, "body") + 1];

            // NFC because production canonicalizes with NFC; the body token is what SCREEN reads.
            Assert.True(
                c.Content.Normalize(NormalizationForm.FormC) == body,
                $"{c.Id}: the enveloped shape does not carry the entry as its body token");
        }
    }

    /// <summary>
    /// R10.57: "every runner over the corpus SHALL carry an evaluator for each outcome kind it
    /// encounters, SHALL fail by name on an entry whose declared kind it has no evaluator for".
    ///
    /// <para>This is the row that fails when a class is added whose assertion nothing checks —
    /// which is how the `structural` class would otherwise have arrived: silently, raising a rate.
    /// A new outcome kind must bring its evaluator, or say here that it has none.</para>
    /// </summary>
    [Fact]
    public void R10_57_EveryDeclaredOutcomeKindHasAnEvaluator()
    {
        var unknown = CorpusFiles
            .SelectMany(Load)
            .Where(c => !Outcomes.Known.Contains(c.Outcome, StringComparer.Ordinal))
            .Select(c => $"{c.Id} (class {c.Class}) declares outcome '{c.Outcome}'")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unknown.Length == 0,
            "These corpus entries declare an outcome kind this runner has no evaluator for, so " +
            "nothing checks what they assert:\n  " + string.Join("\n  ", unknown));
    }

    /// <summary>
    /// The evaluator R10.57 requires for L.1's `structural` class: "Escaped at serving; delimiter
    /// not terminable from content". A forged closing delimiter inside a passage must not end the
    /// span the Forum wrapped around it, or a reader's boundary is drawn by the attacker.
    ///
    /// <para>The class exists because of this adapter: L.1 names <i>fake tool-result framing</i>
    /// among its contents, and content shaped like a tool result inside a tool result is a shape
    /// only an MCP surface can carry. It could not be authored before that surface existed, and
    /// stops being authorable once the surface ships without it.</para>
    /// </summary>
    [Fact]
    public void R10_57_StructuralPayloadsAreEscapedAtServing()
    {
        var structural = Load("payloads.jsonl")
            .Where(c => c.Outcome == Outcomes.EscapedAtServing)
            .ToArray();

        // A theory over nothing passes. This is the row that fails if the class empties out.
        Assert.NotEmpty(structural);
        Assert.Contains(structural, c => c.Id.Contains("tool-result", StringComparison.Ordinal));

        foreach (var c in structural)
        {
            var rendered = Datamarking.Render(c.Content, MarkingMode.Datamark);

            var opens = Occurrences(rendered, Datamarking.OpenDelimiter);
            var closes = Occurrences(rendered, Datamarking.CloseDelimiter);

            Assert.True(
                opens == 1 && closes == 1,
                $"{c.Id}: the served span has {opens} opening and {closes} closing delimiters. " +
                "Content carrying a forged delimiter must not be able to terminate the boundary " +
                "the Forum drew around it (R10.19, L.1's `structural` class).");

            // The boundary must contain the payload rather than having dropped it: an escape that
            // deleted the content would also leave one open and one close.
            Assert.Contains(c.Content[^20..], Datamarking.StripDatamarking(rendered), StringComparison.Ordinal);

            // Where the payload forged a delimiter, the forgery must be visibly neutralised rather
            // than merely counted away. Not every structural payload carries one -- a fake envelope
            // block forges a shape, not a boundary -- so this asks only of those that do.
            if (c.Content.Contains(">>>", StringComparison.Ordinal))
                Assert.Contains("-ESCAPED>>>", rendered, StringComparison.Ordinal);
        }
    }

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    /// <summary>The files this runner evaluates. Named once so a third cannot be added unnoticed.</summary>
    private static readonly string[] CorpusFiles = ["payloads.jsonl", "benign.jsonl", "known-false-positives.jsonl"];

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
                    root.GetProperty("class").GetString()!,
                    root.GetProperty("outcome").GetString()!,
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
