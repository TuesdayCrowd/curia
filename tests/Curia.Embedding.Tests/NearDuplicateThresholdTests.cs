using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Application.Retrieval;
using Curia.Domain.Search;
using Xunit;
using static Curia.Embedding.Tests.RetrievalRule;

namespace Curia.Embedding.Tests;

/// <summary>
/// Where <c>RESULTS.md</c>'s bge section is read from. Task 4 writes the section between these two marker lines, and its statements as <c>- key: value</c> lines (the keys the probes below read are named
/// where they are read). Until it exists every reader fails on <c>Expected: RESULTS.md carries a bge-small-en-v1.5@1 section between markers  Actual: no such section</c>.
/// </summary>
internal static partial class ResultsSection
{
    internal const string Model = "bge-small-en-v1.5@1";
    internal const string Begin = "<!-- bge-small-en-v1.5@1:begin -->";
    internal const string End = "<!-- bge-small-en-v1.5@1:end -->";

    /// <summary>The text between the markers, or null when the file has no such section.</summary>
    internal static string? Read()
    {
        var text = File.ReadAllText(Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", "RESULTS.md"));
        var from = text.IndexOf(Begin, StringComparison.Ordinal);
        var to = text.IndexOf(End, StringComparison.Ordinal);
        return from < 0 || to < from ? null : text[(from + Begin.Length)..to];
    }

    internal static string Required() =>
        Read() ?? throw new Xunit.Sdk.XunitException("Expected: RESULTS.md carries a bge-small-en-v1.5@1 section between markers  Actual: no such section");

    [GeneratedRegex(@"^\s*-\s*(?<k>[a-z_0-9]+):\s*(?<v>\S.*?)\s*$", RegexOptions.Multiline)]
    private static partial Regex Statement();

    /// <summary>
    /// NDT6's threshold half (Amendment 4 ruling 3), a biconditional: the section says <c>thresholds_ship: true</c> exactly when the code's row has <paramref name="rowDerived"/>. When it ships, the section's three
    /// numbers are the runner's and the row's (the original assertion, kept); when it does not, the section carries no threshold number.
    /// </summary>
    internal static void AssertThresholds(string section, Derived derived, bool rowDerived, (int Refuse, int Overlap, int Annotate) row)
    {
        var ships = Value(section, "thresholds_ship");
        Assert.True(ships is "true" or "false", "Expected: RESULTS.md's bge section states thresholds_ship: true or false  Actual: " + (ships ?? "none"));
        Assert.True(rowDerived == (ships == "true"), $"Expected: the section says thresholds_ship: {(rowDerived ? "true" : "false")} exactly when the code's row has ThresholdsDerived == {rowDerived}  Actual: thresholds_ship: {ships}");

        if (ships == "true")
        {
            Assert.Equal(derived.OverlapBp.ToString(CultureInfo.InvariantCulture), Value(section, "overlap_floor_bp"));
            Assert.Equal(derived.RefuseBp.ToString(CultureInfo.InvariantCulture), Value(section, "refuse_cosine_bp"));
            Assert.Equal(derived.AnnotateBp.ToString(CultureInfo.InvariantCulture), Value(section, "annotate_cosine_bp"));
            Assert.Equal((derived.RefuseBp, derived.OverlapBp, derived.AnnotateBp), row);
        }
        else
        {
            foreach (var key in new[] { "overlap_floor_bp", "refuse_cosine_bp", "annotate_cosine_bp" })
                Assert.True(Value(section, key) is null, $"Expected: a section that ships no threshold carries no {key}  Actual: {Value(section, key)}");
        }
    }

    /// <summary>The value of a <c>- key: value</c> statement in the section, or null.</summary>
    internal static string? Value(string section, string key) =>
        Statement().Matches(section).Where(m => m.Groups["k"].Value == key).Select(m => m.Groups["v"].Value).FirstOrDefault();
}

/// <summary>
/// D10b's rule as procedures, probes that need no model (NDT2, NDT3, NDT5, NDT7) and the probes that wait for <c>RESULTS.md</c>'s bge section (NDT7's file half, NDT10, NDT12). No fixture is constructed.
/// Arithmetic in the comments is written out by hand against the rule's sentences, in basis points where it is on the grid.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RetrievalRuleTests
{
    private static PairRow R(string cls, string id, double cosine, double overlap) => new(cls, id, cosine, overlap);

    private static readonly Parameters P = Parameters.FromRepository();

    // ------------------------------------------------------------------------------------------------ NDT2

    private static readonly string[] ExpectedNames =
    [
        "grid_cosine", "grid_overlap", "overlap_min", "overlap_max", "refuse_cosine_least", "refuse_margin", "annotate_false_share_derivation", "annotate_false_share_check",
        "repeat_recall_check_least", "paraphrase_recall_claim_least", "floor_least", "floor_relevant_percentile", "floor_removal_least", "floor_keep_least",
    ];

    /// <summary>The problems of a rule file: a name of the expected set absent from the table or present beyond it, a backticked identifier of the prose that is not a table name, a value that is not a decimal.</summary>
    internal static List<string> RuleProblems(string markdown)
    {
        var problems = new List<string>();
        Parameters table;
        try { table = Parameters.Parse(markdown); }
        catch (FormatException ex) { return [ex.Message]; }

        problems.AddRange(ExpectedNames.Except(table.Names).Select(n => $"missing:{n}"));
        problems.AddRange(table.Names.Except(ExpectedNames).Select(n => $"unexpected:{n}"));

        var prose = string.Join('\n', markdown.Split('\n').Where(l => !l.TrimStart().StartsWith('|')));
        foreach (Match m in Regex.Matches(prose, "`(?<n>[a-z]+(?:_[a-z]+)+)`"))
            if (!table.Names.Contains(m.Groups["n"].Value)) problems.Add($"prose-names-absent:{m.Groups["n"].Value}");

        // the decimals the prose states are the table's values, and each of the table's decimals is stated in the prose
        var stated = Regex.Matches(prose, @"(?<![\d.])0\.\d+").Select(m => (int)(decimal.Parse(m.Value, CultureInfo.InvariantCulture) * 10_000m)).ToHashSet();
        foreach (var name in new[] { "overlap_min", "overlap_max", "refuse_cosine_least", "refuse_margin", "annotate_false_share_check", "repeat_recall_check_least", "floor_least", "floor_removal_least", "floor_keep_least", "grid_cosine", "grid_overlap" })
            if (table.Names.Contains(name) && !stated.Contains(table.Bp(name))) problems.Add($"not-in-prose:{name}");
        return problems;
    }

    /// <summary>NDT2. Green at once (the rule file exists): information through a FNDT8-style edit of the table. Its own red is the synthetic table below.</summary>
    [Fact]
    public void R8_89_NDT2_TheRuleFilesParameterTableMatchesItsProse()
    {
        Assert.Empty(RuleProblems(File.ReadAllText(Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", "THRESHOLD-RULE.md"))));
    }

    [Fact]
    public void R8_89_NDT2_ATableMissingOneNameIsReported()
    {
        var real = File.ReadAllText(Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", "THRESHOLD-RULE.md"));
        var without = string.Join('\n', real.Split('\n').Where(l => !l.StartsWith("| floor_keep_least", StringComparison.Ordinal)));
        Assert.Contains("missing:floor_keep_least", RuleProblems(without));
        var bad = real.Replace("| floor_least | 0.20 |", "| floor_least | twenty |", StringComparison.Ordinal);
        Assert.Contains(RuleProblems(bad), x => x.Contains("floor_least", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------------------------------------ NDT3

    /// <summary>
    /// "Overlap floor O. The largest multiple of 0.05 not above the least overlap of any repeat in Derivation, clamped to 0.25 at the lowest and 0.50 at the highest."
    /// Least repeat overlap 0.12: the largest multiple of 0.05 not above it is 0.10, clamped up to 0.25: 2500. Least 0.83: 0.80, clamped down to 0.50: 5000. Least 0.37: 0.35, inside the range: 3500.
    /// Least 0.40 (on the grid): 0.40: 4000. NDT3 is green at once (new code over literals).
    /// </summary>
    [Fact]
    public void R8_89_NDT3_TheOverlapFloorIsTheGridFloorOfTheLeastRepeatClampedToTheRange()
    {
        Assert.Equal(2500, Derive([R("near-copy", "a", 0.99, 0.12), R("literal", "b", 0.99, 0.90)], P).OverlapBp);
        Assert.Equal(5000, Derive([R("near-copy", "a", 0.99, 0.83), R("literal", "b", 0.99, 0.95)], P).OverlapBp);
        Assert.Equal(3500, Derive([R("near-copy", "a", 0.99, 0.37), R("literal", "b", 0.99, 0.90)], P).OverlapBp);
        Assert.Equal(4000, Derive([R("near-copy", "a", 0.99, 0.40), R("literal", "b", 0.99, 0.90)], P).OverlapBp);
    }

    /// <summary>
    /// "Refusing cosine R. Let M be the greatest cosine of any non-duplicate in Derivation whose overlap is at least O. R is the larger of 0.94 and the least multiple of 0.005 that is at least M + 0.01; if no
    /// non-duplicate has overlap at least O, R is 0.94." With the repeats' least overlap 0.30 (O = 0.30):
    /// M = 0.953 (overlap 0.45): M + 0.01 = 0.963, the least multiple of 0.005 at least 0.963 is 0.965: R = 9650 (the 0.99 non-duplicate has overlap 0.20, below O, and is not looked at).
    /// M = 0.955: 0.965 exactly, on the grid: 9650. M = 0.93: 0.94, the larger of 0.94 and 0.94: 9400. M = 0.80: 0.81, the larger of 0.94 and 0.81: 9400. No non-duplicate at overlap 0.30 or more: 9400.
    /// </summary>
    [Fact]
    public void R8_89_NDT3_TheRefusingCosineRisesOnlyWithAnOverlappingNonDuplicateAndNeverBelowTheLeast()
    {
        PairRow[] repeats = [R("near-copy", "r1", 0.99, 0.30), R("literal", "r2", 0.99, 0.90)];
        int R_(params PairRow[] nonDuplicates) => Derive([.. repeats, .. nonDuplicates], P).RefuseBp;
        Assert.Equal(9650, R_(R("related", "n1", 0.953, 0.45), R("near-miss", "n2", 0.99, 0.20)));
        Assert.Equal(9650, R_(R("related", "n1", 0.955, 0.45)));
        Assert.Equal(9400, R_(R("related", "n1", 0.93, 0.45)));
        Assert.Equal(9400, R_(R("related", "n1", 0.80, 0.45)));
        Assert.Equal(9400, R_(R("near-miss", "n1", 0.99, 0.20)));
        Assert.Equal(9400, R_());
    }

    private static List<PairRow> TwentyThreeNonDuplicates(double second, double top)
    {
        // 21 non-duplicates at 0.30, 0.31, ... 0.50 and two above them; every overlap 0.10, below any O, so R stays 0.94.
        var rows = Enumerable.Range(0, 21).Select(i => R("related", $"n{i}", 0.30 + (i * 0.01), 0.10)).ToList();
        rows.Add(R("near-miss", "n21", second, 0.10));
        rows.Add(R("distinct", "n22", top, 0.10));
        rows.Add(R("near-copy", "r1", 0.99, 0.60));   // a repeat, overlap 0.60: O = 0.50
        return rows;
    }

    /// <summary>
    /// "Annotating cosine A. The least multiple of 0.005 at which no more than floor(0.05 x n) of the n non-duplicates in Derivation have cosine at or above it; if that value exceeds R, A is R."
    /// n = 23: floor(0.05 x 23) = floor(1.15) = 1. With the top two at 0.72 and 0.80: at 0.720 two are at or above (0.72 and 0.80), at 0.725 one (0.80): A = 7250.
    /// n = 19 (four of the low ones removed): floor(0.95) = 0, so none may be at or above: the least multiple above 0.80 is 0.805: 8050.
    /// Top two 0.98 and 0.99 (n = 23): at 0.980 two, at 0.985 one: 0.985 exceeds R = 0.94 (no overlapping non-duplicate), so A is R: 9400.
    /// </summary>
    [Fact]
    public void R8_89_NDT3_TheAnnotatingCosineIsSetByTheCeilingOnNonDuplicatesAndCappedAtTheRefusingCosine()
    {
        Assert.Equal(7250, Derive(TwentyThreeNonDuplicates(0.72, 0.80), P).AnnotateBp);
        Assert.Equal(8050, Derive([.. TwentyThreeNonDuplicates(0.72, 0.80).Where(r => r.Id is not ("n0" or "n1" or "n2" or "n3"))], P).AnnotateBp);
        var capped = Derive(TwentyThreeNonDuplicates(0.98, 0.99), P);
        Assert.Equal((9400, 9400), (capped.RefuseBp, capped.AnnotateBp));
    }

    /// <summary>"It does not try to separate paraphrases from related questions. Whether or not their cosines overlap, A is set by the ceiling on non-duplicates alone." Four paraphrases at 0.95 (separated from the related questions) and at 0.40 (overlapping them): the same O, R and A.</summary>
    [Fact]
    public void R8_89_NDT3_AnOverlappingParaphraseTableYieldsTheSameAnnotatingCosineAsASeparatedOne()
    {
        var table = TwentyThreeNonDuplicates(0.72, 0.80);
        var separated = Derive([.. table, .. Enumerable.Range(0, 4).Select(i => R("paraphrase", $"p{i}", 0.95, 0.20))], P);
        var overlapping = Derive([.. table, .. Enumerable.Range(0, 4).Select(i => R("paraphrase", $"p{i}", 0.40, 0.20))], P);
        Assert.Equal(separated, overlapping);
        Assert.Equal(7250, separated.AnnotateBp);
    }

    private static List<ScoreRow> Scores(params double[] scores) => [.. scores.Select((s, i) => new ScoreRow($"s{i}", s))];

    /// <summary>
    /// "F_keep is the largest multiple of 0.005 not above the 5th percentile (nearest rank) of the relevant scores; F is the larger of 0.2 and F_keep." Nearest rank: rank = ceil(0.05 x N).
    /// N = 21: ceil(1.05) = 2, the second least score, 0.5033: the largest multiple of 0.005 not above it is 0.500: 5000. N = 20: ceil(1.00) = 1, the least, 0.4172: 0.415: 4150.
    /// N = 20 with the least 0.12: 0.12, below 0.2: F = 2000. N = 20 with the least 0.45, on the grid: 4500.
    /// </summary>
    [Fact]
    public void R8_89_NDT3_TheFloorIsThe5thPercentileByNearestRankOnTheGridAndNeverBelowTheLeast()
    {
        double[] high = [.. Enumerable.Range(0, 19).Select(i => 0.60 + (i * 0.01))];
        Assert.Equal(5000, Floor(Scores([.. high, 0.5033, 0.3012]), P));
        Assert.Equal(4150, Floor(Scores([.. high.Skip(1), 0.4172, 0.9]), P));
        Assert.Equal(2000, Floor(Scores([.. high.Skip(1), 0.12, 0.9]), P));
        Assert.Equal(4500, Floor(Scores([.. high.Skip(1), 0.45, 0.9]), P));
    }

    /// <summary>
    /// "The floor does something if removal is at least 0.50 and keep is at least 0.90." F = 0.30; ten noise queries and ten relevant scores. Removal 5 of 10 = 0.50 exactly and keep 9 of 10 = 0.90 exactly: it does something.
    /// Removal 4 of 10 = 0.40: it does not. Keep 8 of 10 = 0.80: it does not.
    /// </summary>
    [Fact]
    public void R8_89_NDT3_TheFloorDoesSomethingAtExactlyHalfRemovedAndNineTenthsKept()
    {
        var d = new Derived(3000, 9400, 8500);
        Verdicts Run(int below, int kept) =>
            Check([], Scores([.. Enumerable.Repeat(0.60, kept), .. Enumerable.Repeat(0.25, 10 - kept)]), Scores([.. Enumerable.Repeat(0.10, below), .. Enumerable.Repeat(0.40, 10 - below)]), d, 3000, P);

        Assert.True(Run(5, 9).FloorDoesSomething);
        Assert.False(Run(4, 9).FloorDoesSomething);
        Assert.False(Run(5, 8).FloorDoesSomething);
        Assert.False(Run(4, 9).FloorRemoves);
        Assert.False(Run(5, 8).FloorKeeps);
    }

    // ------------------------------------------------------------------------------------------------ NDT5

    /// <summary>Three ids hashed by hand: <c>python3 -c "import hashlib; print(hashlib.sha256(b'bp-01').digest()[0])"</c> gives 76 (even: Derivation); `bn-01` 7 (odd: Check); `literal-identical` 219 (odd: Check).</summary>
    [Fact]
    public void R8_89_NDT5_TheSplitIsTheFirstByteOfTheSha256OfTheId()
    {
        Assert.True(IsDerivation("bp-01"));
        Assert.False(IsDerivation("bn-01"));
        Assert.False(IsDerivation("literal-identical"));
    }

    private static Measurements Table(Func<string, double, double> perturb)
    {
        // ids chosen by the split, so each side's rows are known: 23 Derivation and 20 Check non-duplicates, repeats on both sides, relevant scores and noise on both sides.
        var derivation = Enumerable.Range(0, 5000).Select(i => $"t{i}").Where(IsDerivation).ToList();
        var check = Enumerable.Range(0, 5000).Select(i => $"t{i}").Where(i => !IsDerivation(i)).ToList();
        var pairs = new List<PairMeasure>();
        for (var i = 0; i < 23; i++) pairs.Add(new PairMeasure(derivation[i], "related", "blind", perturb(derivation[i], 0.30 + (i * 0.01)), 0.5, 0.10));
        pairs.Add(new PairMeasure(derivation[23], "near-copy", "blind", 0.99, 0.55, 0.60));
        for (var i = 0; i < 20; i++) pairs.Add(new PairMeasure(check[i], "near-miss", "blind", perturb(check[i], 0.30 + (i * 0.01)), 0.5, 0.10));
        for (var i = 0; i < 20; i++) pairs.Add(new PairMeasure(check[20 + i], "paraphrase", "blind", 0.6, perturb(check[20 + i], 0.5 + (i * 0.01)), 0.10));
        for (var i = 0; i < 20; i++) pairs.Add(new PairMeasure(derivation[24 + i], "paraphrase", "blind", 0.6, perturb(derivation[24 + i], 0.5 + (i * 0.01)), 0.10));
        var noise = Enumerable.Range(0, 8).Select(i => new NoiseMeasure(derivation[50 + i], "made-up", 0.2)).Concat(Enumerable.Range(0, 8).Select(i => new NoiseMeasure(check[50 + i], "made-up", perturb(check[50 + i], 0.2)))).ToList();
        return new Measurements(pairs, noise);
    }

    /// <summary>
    /// "Check may confirm or fail a result; it may never change one." Changing the cosine of a Check item (a non-duplicate, a relevant score, a noise query) changes none of O, R, A, F; changing the extreme
    /// Derivation non-duplicate (the greatest, 0.52, to 0.10) changes A, and the Derivation relevant score the percentile reads (the second least of 21) changes F. Green at once (new code over literals).
    /// </summary>
    [Fact]
    public void R8_89_NDT5_ACheckItemNeverMovesAThresholdAndAnExtremeDerivationItemDoes()
    {
        var p = Parameters.FromRepository();
        var baseline = Compute(Table((_, v) => v), p);

        var checkTouched = Compute(Table((id, v) => IsDerivation(id) ? v : 0.99), p);
        Assert.Equal((baseline.Derived, baseline.FloorBp), (checkTouched.Derived, checkTouched.FloorBp));

        var derivationTop = Table((_, v) => v).Pairs.Where(x => IsDerivation(x.Id) && x.Class == "related").MaxBy(x => x.Passage)!.Id;
        // 21 Derivation relevant scores (20 paraphrases and one near-copy): nearest rank ceil(0.05 x 21) = 2, so the percentile reads the second least score.
        var derivationRankTwo = Table((_, v) => v).Pairs.Where(x => IsDerivation(x.Id) && x.Class is "paraphrase" or "near-copy").OrderBy(x => x.QueryPassage).Skip(1).First().Id;
        var annotateMoved = Compute(Table((id, v) => id == derivationTop ? 0.10 : v), p);
        Assert.NotEqual(baseline.Derived.AnnotateBp, annotateMoved.Derived.AnnotateBp);
        Assert.NotEqual(baseline.Derived.AnnotateBp, annotateMoved.Derived.AnnotateBp);
        var floorMoved = Compute(Table((id, v) => id == derivationRankTwo ? 0.30 : v), p);
        Assert.NotEqual(baseline.FloorBp, floorMoved.FloorBp);
    }

    /// <summary>The split's sizes are facts about ids, and may be committed: non-duplicates Derivation 23, Check 20; repeats 9 and 14; paraphrases 24 and 16; noise queries 8 and 16 (Amendment 3, ruling 2).</summary>
    [Fact]
    public void R8_89_NDT5_TheSplitOfTheRealIdsHasTheSizesAmendment3Counted()
    {
        var items = new List<(string Class, string Id)>();
        foreach (var file in new[] { "dedupe-pairs.jsonl", "blind-pairs.jsonl" })
            foreach (var line in File.ReadLines(Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", file)).Where(l => l.Length > 0))
            {
                using var json = JsonDocument.Parse(line);
                items.Add((json.RootElement.GetProperty("class").GetString()!, json.RootElement.GetProperty("id").GetString()!));
            }

        (int, int) Sizes(Func<string, bool> inKind) => (items.Count(i => inKind(i.Class) && IsDerivation(i.Id)), items.Count(i => inKind(i.Class) && !IsDerivation(i.Id)));
        Assert.Equal((23, 20), Sizes(IsNonDuplicate));
        Assert.Equal((9, 14), Sizes(IsRepeat));
        Assert.Equal((24, 16), Sizes(c => c == "paraphrase"));
        Assert.Equal((8, 16), Sizes(c => c is "off-topic" or "made-up"));
    }

    // ------------------------------------------------------------------------------------------------ the runner over the committed measurements

    /// <summary>
    /// The runner's own derivation over the committed measurements file, needing no model: O, R, A, F, the five Check conditions and the recalls, written as <c>name = value</c> lines to the path
    /// <c>CURIA_WRITE_DERIVED</c> names when it is set (so <c>tools/retrieval/derive-thresholds.py</c> can be compared with the runner value by value). Fails, never skips, without the committed file.
    /// </summary>
    [Fact]
    public void R8_89_TheRunnerDerivesFromTheCommittedMeasurements()
    {
        var committed = Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", "measured", ResultsSection.Model + ".measurements.jsonl");
        Assert.True(File.Exists(committed), "Expected: the committed measurements file " + ResultsSection.Model + ".measurements.jsonl  Actual: absent");
        var m = ReadMeasurements(committed);
        var (d, floorBp, v) = Compute(m, P);
        var rows = m.Pairs.Select(x => new PairRow(x.Class, x.Id, x.Passage, x.Overlap)).ToList();
        var der = rows.Where(r => IsDerivation(r.Id)).ToList();
        var chk = rows.Where(r => !IsDerivation(r.Id)).ToList();
        (int, int) Repeats(List<PairRow> of) => (of.Count(r => IsRepeat(r.Class) && Decide(r, d) != Verdict.None), of.Count(r => IsRepeat(r.Class)));
        var lines = new List<string>
        {
            $"overlap_floor_bp = {d.OverlapBp}", $"refuse_cosine_bp = {d.RefuseBp}", $"annotate_cosine_bp = {d.AnnotateBp}", $"floor_bp = {floorBp}",
            $"check_no_nondup_refused = {v.NoNonDuplicateRefused}", $"check_annotated_within_ceiling = {v.AnnotatedWithinCeiling}", $"check_repeats_caught = {v.RepeatsCaught}",
            $"floor_removal_check = {v.FloorRemoves}", $"floor_keep_check = {v.FloorKeeps}",
            $"paraphrase_recall_derivation = {Recall(der, "paraphrase", d.AnnotateBp).Hit}/{Recall(der, "paraphrase", d.AnnotateBp).Total}",
            $"paraphrase_recall_check = {Recall(chk, "paraphrase", d.AnnotateBp).Hit}/{Recall(chk, "paraphrase", d.AnnotateBp).Total}",
            $"repeat_recall_derivation = {Repeats(der).Item1}/{Repeats(der).Item2}", $"repeat_recall_check = {Repeats(chk).Item1}/{Repeats(chk).Item2}",
            $"thresholds_ship = {v.ThresholdsShip}", $"floor_does_something = {v.FloorDoesSomething}",
        };
        var path = Environment.GetEnvironmentVariable("CURIA_WRITE_DERIVED");
        if (!string.IsNullOrEmpty(path)) File.WriteAllLines(path, lines);
        Assert.NotEmpty(lines);
    }

    // ------------------------------------------------------------------------------------------------ NDT7

    private static (PairRow[] Rows, double[] Relevant, double[] Noise) Passing() =>
    (
        [
            .. Enumerable.Range(0, 9).Select(i => R("related", $"n{i}", 0.30, 0.10)),
            R("near-miss", "n9", 0.86, 0.40),
            .. Enumerable.Range(0, 4).Select(i => R("near-copy", $"r{i}", 0.99, 0.90)),
            R("literal", "r4", 0.50, 0.20),
        ],
        [.. Enumerable.Repeat(0.60, 9), 0.25],
        [.. Enumerable.Repeat(0.10, 5), .. Enumerable.Repeat(0.40, 5)]
    );

    private static Verdicts Run((PairRow[] Rows, double[] Relevant, double[] Noise) t) =>
        Check(t.Rows, Scores(t.Relevant), Scores(t.Noise), new Derived(3000, 9400, 8500), 3000, P);

    /// <summary>
    /// "no non-duplicate may be refused; at most ceil(0.10 x m) of the m non-duplicates in Check may be annotated; and at least 0.80 of the repeats in Check must be refused or annotated"; "removal at least 0.50 and keep
    /// at least 0.90". O = 0.30, R = 0.94, A = 0.85, F = 0.30. Passing table: m = 10, ceil(1.0) = 1 annotated allowed, one at 0.86 is annotated; five repeats, four refused (0.99, overlap 0.90) and one at 0.50 not caught: 4/5 = 0.80,
    /// exactly the least; noise 5 of 10 below 0.30 = 0.50, relevant 9 of 10 at or above = 0.90. Each of the five conditions then fails alone: a non-duplicate at 0.95 with overlap 0.60 is refused; a second at 0.88 is annotated
    /// (2 > 1); a second repeat at 0.50 leaves 3/5 = 0.60; one noise query raised to 0.40 leaves 4/10 = 0.40; a relevant score lowered to 0.25 leaves 8/10 = 0.80. Green at once (new code).
    /// </summary>
    [Fact]
    public void R8_89_NDT7_EachCheckConditionFailsAloneAndAllPassTogether()
    {
        var pass = Passing();
        Assert.Equal(new Verdicts(true, true, true, true, true), Run(pass));

        Assert.Equal(new Verdicts(false, true, true, true, true), Run(pass with { Rows = [.. pass.Rows.Skip(1), R("near-miss", "x", 0.95, 0.60)] }));
        Assert.Equal(new Verdicts(true, false, true, true, true), Run(pass with { Rows = [.. pass.Rows.Skip(1), R("near-miss", "x", 0.88, 0.10)] }));
        Assert.Equal(new Verdicts(true, true, false, true, true), Run(pass with { Rows = [.. pass.Rows.Where(r => r.Id != "r0"), R("near-copy", "x", 0.50, 0.20)] }));
        Assert.Equal(new Verdicts(true, true, true, false, true), Run(pass with { Noise = [.. Enumerable.Repeat(0.10, 4), .. Enumerable.Repeat(0.40, 6)] }));
        Assert.Equal(new Verdicts(true, true, true, true, false), Run(pass with { Relevant = [.. Enumerable.Repeat(0.60, 8), 0.25, 0.25] }));
        Assert.True(Run(pass).ThresholdsShip);
        Assert.False(Run(pass with { Rows = [.. pass.Rows.Skip(1), R("near-miss", "x", 0.95, 0.60)] }).ThresholdsShip);
    }

    /// <summary>
    /// NDT7's file half. "If any of the three fails, no number ships": the section states <c>- thresholds_ship: true|false</c> and, when false, says "no number ships". Predicted red:
    /// <c>Expected: RESULTS.md carries a bge-small-en-v1.5@1 section between markers  Actual: no such section</c> (the same cause as NDT6, NDT10 and NDT12).
    /// </summary>
    [Fact]
    public void R8_89_NDT7_ResultsStatesNoNumberShipsWhenAnyCheckConditionFails()
    {
        var section = ResultsSection.Required();
        var ships = ResultsSection.Value(section, "thresholds_ship");
        Assert.True(ships is "true" or "false", "Expected: RESULTS.md's bge section states thresholds_ship: true or false  Actual: " + (ships ?? "none"));
        if (ships == "false") Assert.Contains("no number ships", section, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------------------------------ NDT6's parser

    private const string ShippedSection = "- thresholds_ship: true\n- overlap_floor_bp: 5000\n- refuse_cosine_bp: 9400\n- annotate_cosine_bp: 8800\n- floor_bp: 6400\n";
    private const string UnshippedSection = "- thresholds_ship: false\n- floor_bp: 6400\n";

    /// <summary>NDT6's shipped branch over a literal section (the branch is not dead while the real section ships no threshold): green when the row is derived and equals the section, red on a flag, a number or a missing member.</summary>
    [Fact]
    public void R8_89_NDT6_TheShippedBranchAcceptsASectionThatAgreesWithTheRowAndRefusesOneThatDoesNot()
    {
        var derived = new Derived(5000, 9400, 8800);
        ResultsSection.AssertThresholds(ShippedSection, derived, rowDerived: true, (9400, 5000, 8800));

        Assert.ThrowsAny<Exception>(() => ResultsSection.AssertThresholds(ShippedSection, derived, rowDerived: false, (9400, 5000, 8800)));
        Assert.ThrowsAny<Exception>(() => ResultsSection.AssertThresholds(ShippedSection, derived, rowDerived: true, (9400, 5000, 8500)));
        Assert.ThrowsAny<Exception>(() => ResultsSection.AssertThresholds(ShippedSection.Replace("annotate_cosine_bp: 8800", "annotate_cosine_bp: 8500", StringComparison.Ordinal), derived, rowDerived: true, (9400, 5000, 8800)));
        Assert.ThrowsAny<Exception>(() => ResultsSection.AssertThresholds("- floor_bp: 6400\n", derived, rowDerived: false, (9400, 5000, 8500)));
    }

    /// <summary>NDT6's other branch over a literal section: a section that ships nothing agrees with a row whose thresholds are not derived, and is refused beside a derived row or when it carries a threshold number.</summary>
    [Fact]
    public void R8_89_NDT6_TheUnshippedBranchAcceptsASectionWithNoThresholdAndRefusesOneThatCarriesAnOrADerivedRow()
    {
        var derived = new Derived(5000, 9400, 8800);
        ResultsSection.AssertThresholds(UnshippedSection, derived, rowDerived: false, (9400, 5000, 8500));

        Assert.ThrowsAny<Exception>(() => ResultsSection.AssertThresholds(UnshippedSection, derived, rowDerived: true, (9400, 5000, 8500)));
        Assert.ThrowsAny<Exception>(() => ResultsSection.AssertThresholds(UnshippedSection + "- annotate_cosine_bp: 8800\n", derived, rowDerived: false, (9400, 5000, 8500)));
    }

    // ------------------------------------------------------------------------------------------------ NDT10, NDT12

    private static string ClientWording() => File.ReadAllText(Path.Combine(ModelDirectory.RepositoryRoot(), "src", "Curia.Client", "ModelWording.cs"));

    /// <summary>
    /// NDT10: SEM59 and SEM60 reversed or confirmed by outcome, read from the published <c>- floor_does_something: true|false</c> and never inferred by running the floor over the blind data. If the floor does something the
    /// Forum's sentence must not say that a long list is no evidence; if not it must, and the section carries the measured removal (<c>- floor_removal_bp</c>). Predicted red:
    /// <c>Expected: RESULTS.md's bge section names the floor's outcome  Actual: none</c>.
    /// </summary>
    [Fact]
    public void R11_93_NDT10_TheFloorsOutcomeIsPublishedAndTheForumsSentenceAgreesWithIt()
    {
        var section = ResultsSection.Read();
        var outcome = section is null ? null : ResultsSection.Value(section, "floor_does_something");
        Assert.True(outcome is "true" or "false", "Expected: RESULTS.md's bge section names the floor's outcome  Actual: none");

        var wording = ClientWording();
        const string Clause = "no evidence that the Forum holds anything on the topic";
        if (outcome == "true")
        {
            Assert.DoesNotContain(Clause, wording, StringComparison.Ordinal);

            // the sentence states the floor and the two percentages the section publishes, each read from the section and typed in the sentence once
            Assert.Contains((int.Parse(ResultsSection.Value(section!, "floor_bp")!, CultureInfo.InvariantCulture) / 10_000m).ToString("0.00", CultureInfo.InvariantCulture), wording, StringComparison.Ordinal);
            foreach (var key in new[] { "floor_removal_bp", "floor_keep_bp" })
            {
                var percent = int.Parse(Regex.Match(ResultsSection.Value(section!, key)!, @"^\d+").Value, CultureInfo.InvariantCulture) / 100;
                Assert.Contains($"({percent}%)", wording, StringComparison.Ordinal);
            }
        }
        else
        {
            Assert.Contains(Clause, wording, StringComparison.Ordinal);
            Assert.NotNull(ResultsSection.Value(section!, "floor_removal_bp"));
        }
    }

    /// <summary>
    /// NDT12: the paraphrase block as a real-model fact. The section states <c>- paraphrase_recall_check: hit/total</c>; "If the paraphrase recall on Check is under one half, the near-duplicate check is described,
    /// everywhere it is described, as a check for repeats that does not detect paraphrase": the Forum's wording says so exactly when it is. Predicted red:
    /// <c>Expected: RESULTS.md's bge section states the paraphrase recall on Check  Actual: none</c>.
    /// </summary>
    [Fact]
    public void R8_21_NDT12_TheParaphraseRecallIsPublishedAndTheForumsWordingFollowsIt()
    {
        var section = ResultsSection.Read();
        var recall = section is null ? null : ResultsSection.Value(section, "paraphrase_recall_check");
        Assert.True(recall is not null && Regex.IsMatch(recall, @"^\d+/\d+$"), "Expected: RESULTS.md's bge section states the paraphrase recall on Check  Actual: none");

        var parts = recall!.Split('/');
        var hit = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var total = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var underHalf = hit * 10_000 < P.Bp("paraphrase_recall_claim_least") * total;
        Assert.Equal(underHalf, ClientWording().Contains("does not detect paraphrase", StringComparison.Ordinal));
    }

    /// <summary>
    /// NDT13 (errata G66, R9.66's three obligations about the floor's words). R9.66 is read from the errata by its <c>**R9.66**</c> marker and must carry each obligation as the errata words it: "what the floor removed as
    /// vector candidates and not as results of a page" (<c>vector candidates</c>), "keyword matches are not floored" (<c>keyword matches are not floored</c>), and "its `removed by floor` count is the verification floor's"
    /// (<c>removed by floor</c>). The client's sentence for <c>bge-small-en-v1.5@1</c> must then carry one fragment per obligation: <c>vector channel's candidates</c>, <c>The keyword channel has no floor</c> with
    /// <c>does not say which results the vector channel chose</c>, and <c>The `removed by floor` line counts the verification floor</c>. The pairing is by hand, not parsed from the prose (a parser from prose to predicate is a
    /// second implementation). Predicted red before the words: <c>Assert.Contains() Failure: Sub-string not found</c> naming the first absent fragment.
    /// </summary>
    [Fact]
    public void R9_66_NDT13_TheErratasThreeObligationsAboutTheFloorAreInTheSentence()
    {
        var errata = File.ReadAllText(Path.Combine(ModelDirectory.RepositoryRoot(), "curia-whitepaper-ERRATA-AND-ADDENDUM.md"));
        var start = errata.IndexOf("**R9.66**", StringComparison.Ordinal);
        Assert.True(start >= 0, "Expected: the errata carries a paragraph that begins **R9.66**  Actual: none");
        var end = errata.IndexOf("\n\n", start, StringComparison.Ordinal);
        var paragraph = errata[start..(end < 0 ? errata.Length : end)];

        var pairs = new[]
        {
            ("vector candidates", new[] { "vector channel's candidates" }),
            ("keyword matches are not floored", new[] { "The keyword channel has no floor", "does not say which results the vector channel chose" }),
            ("removed by floor", new[] { "The `removed by floor` line counts the verification floor" }),
        };

        var sentence = ClientWording();
        foreach (var (obligation, fragments) in pairs)
        {
            Assert.Contains(obligation, paragraph, StringComparison.Ordinal);
            foreach (var fragment in fragments)
                Assert.Contains(fragment, sentence, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// The probes that run the real model over the repository's files: the reference agreement (NDT4), the role control (NDT4b), RESULTS.md against the runner (NDT6) and the canaries (NDT11). The measurement is made
/// once per class. No threshold, recall, removal or keep is computed from the blind data by any probe here: NDT6 does so only after its own first assertion, which fails while no section exists.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NearDuplicateThresholdTests(RealModelFixture model) : IClassFixture<RealModelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<Measurements>? _measured;

    private Task<Measurements> MeasuredAsync() => _measured ??= MeasureAsync(model.Embedder, CancellationToken.None);

    private const string ReferenceVariable = "CURIA_PAIR_COSINES_REFERENCE";

    /// <summary>
    /// NDT4. Two artifacts: the runner's measurement of every pair and query equals <c>tools/model/pair-cosines.py</c>'s within 1e-3; and (a third artifact: the plan's introduction, quoting D10a's recorded table) the sixteen
    /// legacy pairs fall in the recorded passage-role ranges. Fails, never skips, without the reference file. The runner's measurements are written to the file <c>CURIA_PAIR_COSINES_RUNNER_OUT</c> names, if set;
    /// the one number printed is the greatest absolute difference over all measurements.
    /// </summary>
    [Fact]
    public async Task R8_89_NDT4_TheRunnersCosinesEqualTheIndependentReferencesAndD10aRecordedRanges()
    {
        // The committed reference (conformance/retrieval/measured/) is the default, so CI needs no variable; the variable overrides it with a fresh run of the script.
        var path = Environment.GetEnvironmentVariable(ReferenceVariable);
        if (string.IsNullOrEmpty(path))
            path = Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", "measured", ResultsSection.Model + ".reference.jsonl");
        Assert.True(File.Exists(path),
            $"Expected: the reference file tools/model/pair-cosines.py wrote, at {ReferenceVariable} or the committed conformance/retrieval/measured/ copy  Actual: no such file ({path}). Run: python3.12 -I tools/model/pair-cosines.py --hf-dir <snapshot> --model-dir \"$CURIA_EMBEDDING_MODEL_DIR\" --out <file>; see the script's header.");

        var reference = Reference.Read(path!);
        var measured = await MeasuredAsync();

        var outPath = Environment.GetEnvironmentVariable("CURIA_PAIR_COSINES_RUNNER_OUT");
        if (!string.IsNullOrEmpty(outPath))
            await File.WriteAllLinesAsync(outPath, measured.Pairs.Select(p => JsonSerializer.Serialize(new { kind = "pair", id = p.Id, passage = p.Passage, query_passage = p.QueryPassage }))
                .Concat(measured.Noise.Select(n => JsonSerializer.Serialize(new { kind = "noise", id = n.Id, score = n.Score }))), Ct);

        Assert.Equal((106, 24), (measured.Pairs.Count, measured.Noise.Count));
        Assert.Equal((106, 24), (reference.Pairs.Count, reference.Noise.Count));

        var worst = 0.0;
        var failures = new List<string>();
        void Compare(string id, double mine, double theirs)
        {
            var d = Math.Abs(mine - theirs);
            worst = Math.Max(worst, d);
            if (d > 1e-3) failures.Add($"{id}: runner {mine:R} reference {theirs:R}");
        }

        foreach (var p in measured.Pairs)
        {
            Assert.True(reference.Pairs.TryGetValue(p.Id, out var r), $"the reference has no pair {p.Id}");
            Compare(p.Id + " passage", p.Passage, r.Passage);
            Compare(p.Id + " query-passage", p.QueryPassage, r.QueryPassage);
        }

        foreach (var n in measured.Noise)
        {
            Assert.True(reference.Noise.TryGetValue(n.Id, out var r), $"the reference has no query {n.Id}");
            Compare(n.Id, n.Score, r);
        }

        TestContext.Current.SendDiagnosticMessage($"NDT4: {measured.Pairs.Count} pairs and {measured.Noise.Count} queries; greatest absolute difference between the runner and the reference {worst:E3}");
        Assert.True(failures.Count == 0, "the runner and the reference disagree: " + string.Join("; ", failures));

        // D10a's recorded table (the plan's introduction): passage role, over the sixteen legacy pairs.
        (string Class, double Lo, double Hi)[] ranges = [("literal", 0.985, 1.000), ("paraphrase", 0.689, 0.776), ("related", 0.688, 0.783), ("distinct", 0.529, 0.550)];
        foreach (var (cls, lo, hi) in ranges)
            foreach (var p in measured.Pairs.Where(x => x.Source == "legacy" && x.Class == cls))
                Assert.True(p.Passage >= lo - 0.0005 && p.Passage <= hi + 0.0005, $"legacy pair {p.Id} ({cls}) is outside D10a's recorded range {lo:0.000} to {hi:0.000}");
    }

    private const string WriteVariable = "CURIA_WRITE_MEASUREMENTS";

    /// <summary>
    /// The committed measurements (<c>conformance/retrieval/measured/bge-small-en-v1.5@1.measurements.jsonl</c>) are what the runner measures now, within 1e-9 on every value. When
    /// <c>CURIA_WRITE_MEASUREMENTS</c> names a path the file is first written there. Fails, never skips, when the committed file is absent.
    /// </summary>
    [Fact]
    public async Task R8_89_TheCommittedMeasurementsAreWhatTheRunnerMeasuresNow()
    {
        var measured = await MeasuredAsync();
        var writeTo = Environment.GetEnvironmentVariable(WriteVariable);
        if (!string.IsNullOrEmpty(writeTo)) await File.WriteAllLinesAsync(writeTo, MeasurementLines(measured), Ct);

        var committed = Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", "measured", ResultsSection.Model + ".measurements.jsonl");
        Assert.True(File.Exists(committed), "Expected: the committed measurements file " + ResultsSection.Model + ".measurements.jsonl  Actual: absent");
        var file = ReadMeasurements(committed);

        Assert.Equal((measured.Pairs.Count, measured.Noise.Count), (file.Pairs.Count, file.Noise.Count));
        var worst = 0.0;
        for (var i = 0; i < measured.Pairs.Count; i++)
        {
            var (a, b) = (measured.Pairs[i], file.Pairs[i]);
            Assert.Equal((a.Id, a.Class, a.Source), (b.Id, b.Class, b.Source));
            worst = Math.Max(worst, Math.Max(Math.Abs(a.Passage - b.Passage), Math.Max(Math.Abs(a.QueryPassage - b.QueryPassage), Math.Abs(a.Overlap - b.Overlap))));
        }

        for (var i = 0; i < measured.Noise.Count; i++)
        {
            var (a, b) = (measured.Noise[i], file.Noise[i]);
            Assert.Equal((a.Id, a.Class), (b.Id, b.Class));
            worst = Math.Max(worst, Math.Abs(a.Score - b.Score));
        }

        Assert.True(worst <= 1e-9, $"the committed measurements differ from the runner's by {worst:E3}");
    }

    /// <summary>NDT4b. Role control for FNDT7: the dedupe's passage-passage cosine and the floor's query-passage cosine differ by more than 1e-3 for at least one blind pair, because the prefix changes the vector. Green.</summary>
    [Fact]
    public async Task R9_63_NDT4b_ThePassageCosineAndTheQueryCosineOfAPairDiffer()
    {
        var measured = await MeasuredAsync();
        Assert.Contains(measured.Pairs.Where(p => p.Source == "blind"), p => Math.Abs(p.Passage - p.QueryPassage) > 1e-3);
    }

    /// <summary>
    /// NDT6. RESULTS.md's bge section against what the runner computes now, and the code's table against the published numbers. The first assertion is the section's existence. The threshold half is a
    /// biconditional (Amendment 4 ruling 3): <c>thresholds_ship</c> is true exactly when the row's <c>ThresholdsDerived</c> is, the runner's verdict is the section's, a shipped section's numbers are the runner's and the
    /// row's, and a section that ships none carries none. The floor half: the row's floor, the section's <c>floor_bp</c> and the runner's are one number.
    /// </summary>
    [Fact]
    public async Task R8_89_NDT6_ResultsEqualsTheRunnerAndTheCodeHoldsThePublishedBasisPoints()
    {
        var section = ResultsSection.Required();
        var row = RetrievalRows.For(ResultsSection.Model);
        Assert.True(row is not null, "Expected: RetrievalRows.For(\"" + ResultsSection.Model + "\") answers a row  Actual: none");

        var (derived, floorBp, verdicts) = Compute(await MeasuredAsync(), Parameters.FromRepository());
        Assert.Equal(verdicts.ThresholdsShip ? "true" : "false", ResultsSection.Value(section, "thresholds_ship"));
        ResultsSection.AssertThresholds(section, derived, row!.ThresholdsDerived, (row.RefuseCosineBp, row.RefuseOverlapBp, row.AnnotateCosineBp));

        Assert.Equal(floorBp.ToString(CultureInfo.InvariantCulture), ResultsSection.Value(section, "floor_bp"));
        Assert.Equal(row.FloorBp.ToString(CultureInfo.InvariantCulture), ResultsSection.Value(section, "floor_bp"));
        Assert.True(row.FloorDerived, "Expected: the row's floor is derived (the section's floor_does_something is true)  Actual: FloorDerived is false");
    }

    /// <summary>NDT6, the code's half: <c>RetrievalRows.For("bge-small-en-v1.5@1")</c> is a row of its own, and not the hashed row the code held for every model before D10b.</summary>
    [Fact]
    public void R8_89_NDT6_TheCodeHoldsAnotherRowForTheDeployedModel()
    {
        var row = RetrievalRows.For(ResultsSection.Model);
        Assert.True(row is not null, "Expected: RetrievalRows.For(\"" + ResultsSection.Model + "\") answers a row  Actual: none");
        Assert.NotEqual(RetrievalRows.Hashed.FloorBp, row!.FloorBp);
    }

    /// <summary>
    /// NDT11. Each of the six canaries ranks its expected post in the top three, by the vector channel alone, under the deployed model over the committed corpus with the floor of the model's row
    /// (<c>RetrievalRows.Bge</c>). The outcome is recorded either way; a canary that fails under the model is a finding, never a retune.
    /// </summary>
    [Fact]
    public async Task R10_5_NDT11_EachCanaryRanksItsExpectedPostInTheTopThreeUnderTheDeployedModel()
    {
        var root = ModelDirectory.RepositoryRoot();
        var posts = new List<(string Id, IReadOnlyList<float> Vector)>();
        foreach (var line in (await File.ReadAllLinesAsync(Path.Combine(root, "conformance", "retrieval", "corpus.jsonl"), Ct)).Where(l => l.Length > 0))
        {
            using var json = JsonDocument.Parse(line);
            var r = json.RootElement;
            var tags = r.TryGetProperty("tags", out var t) ? string.Join(' ', t.EnumerateArray().Select(x => x.GetString())) : string.Empty;
            var title = r.TryGetProperty("title", out var ti) && ti.ValueKind == JsonValueKind.String ? ti.GetString()! : string.Empty;
            var text = string.Join('\n', new[] { title, r.GetProperty("body").GetString()!, tags }.Where(x => x.Length > 0));
            posts.Add((r.GetProperty("id").GetString()!, Probe.VectorOf(await model.Embedder.EmbedAsync(text, EmbeddingRole.Passage, Ct), "a corpus post")));
        }

        var outcomes = new List<string>();
        var failed = new List<string>();
        foreach (var line in (await File.ReadAllLinesAsync(Path.Combine(root, "conformance", "retrieval", "canaries.jsonl"), Ct)).Where(l => l.Length > 0))
        {
            using var json = JsonDocument.Parse(line);
            var id = json.RootElement.GetProperty("id").GetString()!;
            var expected = json.RootElement.GetProperty("expected").GetString()!;
            var query = Probe.VectorOf(await model.Embedder.EmbedAsync(json.RootElement.GetProperty("query").GetString()!, EmbeddingRole.Query, Ct), "a canary query");
            var ranked = posts.Select(p => (p.Id, Cosine: Probe.Cosine(query, p.Vector))).Where(p => p.Cosine >= RetrievalRows.Bge.Floor).OrderByDescending(p => p.Cosine).Select(p => p.Id).ToList();
            var rank = ranked.IndexOf(expected) + 1;
            outcomes.Add($"{id}: expected {expected} ranks {(rank == 0 ? "absent" : rank.ToString(CultureInfo.InvariantCulture))}");
            if (rank is 0 or > 3) failed.Add($"{id} (rank {(rank == 0 ? "absent" : rank.ToString(CultureInfo.InvariantCulture))})");
        }

        TestContext.Current.SendDiagnosticMessage("NDT11: " + string.Join("; ", outcomes));
        Assert.True(failed.Count == 0, "canaries outside the top three under the deployed model (a finding, not a retune): " + string.Join(", ", failed));
    }
}
