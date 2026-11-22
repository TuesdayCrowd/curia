using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder;

namespace Curia.Embedding.Tests;

/// <summary>
/// D10b's threshold rule (<c>conformance/retrieval/THRESHOLD-RULE.md</c>) as procedures over tables, and the measurement that fills the tables from the repository's files and the model.
///
/// Grid arithmetic is in integer basis points (Amendment 1, ruling 7): a multiple of 0.005 is a multiple of 50 bp, a multiple of 0.05 is a multiple of 500 bp. A cosine or an overlap is
/// compared, unrounded, against the grid value's double (n / 10000.0, the nearest double to the rational, which is what a literal of the same value is); no float a grid value was derived from is
/// printed. No parameter is copied into this file: the table of the rule file is parsed at run time.
/// </summary>
internal static class RetrievalRule
{
    internal const double BasisPoints = 10_000.0;

    // ------------------------------------------------------------------------------------------------ the parameter table

    /// <summary>The rule's parameter table, every value in basis points (a decimal with at most four places).</summary>
    internal sealed class Parameters
    {
        private readonly IReadOnlyDictionary<string, int> _bp;

        private Parameters(IReadOnlyDictionary<string, int> bp) => _bp = bp;

        internal IReadOnlyCollection<string> Names => [.. _bp.Keys];

        /// <summary>The parameter's value in basis points; a name absent from the table fails and says which.</summary>
        internal int Bp(string name) => _bp.TryGetValue(name, out var value) ? value : throw new InvalidOperationException($"the rule's parameter table has no '{name}'");

        /// <summary>Parses the <c>| parameter | value |</c> block of the rule file's text, or fails naming the line it could not read.</summary>
        internal static Parameters Parse(string markdown)
        {
            var found = new Dictionary<string, int>(StringComparer.Ordinal);
            var inTable = false;
            foreach (var raw in markdown.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith('|')) { if (inTable) break; continue; }
                var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
                if (cells.Length != 2) throw new FormatException($"a parameter row does not have two cells: {line}");
                if (cells[0] == "parameter" && cells[1] == "value") { inTable = true; continue; }
                if (!inTable) continue;
                if (cells[0].All(c => c == '-')) continue;
                if (!decimal.TryParse(cells[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
                    throw new FormatException($"the value of '{cells[0]}' is not a decimal: {cells[1]}");
                var scaled = value * 10_000m;
                if (scaled != decimal.Truncate(scaled)) throw new FormatException($"the value of '{cells[0]}' is not a whole number of basis points: {cells[1]}");
                if (!found.TryAdd(cells[0], (int)scaled)) throw new FormatException($"'{cells[0]}' appears twice in the table");
            }

            if (!inTable) throw new FormatException("the rule file has no `| parameter | value |` table");
            return new Parameters(found);
        }

        internal static Parameters FromRepository() => Parse(File.ReadAllText(Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", "THRESHOLD-RULE.md")));
    }

    // ------------------------------------------------------------------------------------------------ the split

    /// <summary>Derivation is the first byte of the SHA-256 of the id's UTF-8 being even, Check odd. A function of the id alone.</summary>
    internal static bool IsDerivation(string id) => SHA256.HashData(Encoding.UTF8.GetBytes(id))[0] % 2 == 0;

    // ------------------------------------------------------------------------------------------------ classes

    internal static bool IsRepeat(string cls) => cls is "literal" or "near-copy";

    internal static bool IsNonDuplicate(string cls) => cls is "related" or "near-miss" or "distinct";

    internal static bool IsRelevantClass(string cls) => cls is "paraphrase" or "near-copy";

    // ------------------------------------------------------------------------------------------------ tables

    /// <summary>One pair: its class, its id, the passage-passage cosine of its two texts and their Jaccard overlap.</summary>
    internal sealed record PairRow(string Class, string Id, double Cosine, double Overlap);

    /// <summary>One score: a relevant score (a paraphrase or near-copy pair's query-role cosine) or a noise query's greatest cosine.</summary>
    internal sealed record ScoreRow(string Id, double Score);

    /// <summary>What the rule derives from Derivation: the overlap floor O, the refusing cosine R and the annotating cosine A, in basis points.</summary>
    internal sealed record Derived(int OverlapBp, int RefuseBp, int AnnotateBp);

    // ------------------------------------------------------------------------------------------------ the grid, in integers

    /// <summary>The largest multiple of <paramref name="gridBp"/> whose value does not exceed <paramref name="x"/>, by comparison with the unrounded value; never below zero.</summary>
    internal static int GridFloorBp(double x, int gridBp)
    {
        var k = 0;
        while ((double)((k + 1) * gridBp) / BasisPoints <= x) k++;
        return k * gridBp;
    }

    /// <summary>The least multiple of <paramref name="gridBp"/> that is at least <paramref name="x"/> (x in decimal: the margin step is where a float sum would misplace an exact boundary).</summary>
    internal static int GridCeilBp(decimal x, int gridBp)
    {
        var k = 0;
        while ((decimal)(k * gridBp) / 10_000m < x) k++;
        return k * gridBp;
    }

    // ------------------------------------------------------------------------------------------------ the procedure

    /// <summary>
    /// O, R and A from the Derivation rows. Overlap floor O: the largest multiple of the overlap grid not above the least overlap of any repeat, clamped to the table's range. Refusing cosine R: the larger
    /// of the least R and the least multiple of the cosine grid at least M + the margin, M being the greatest cosine of a non-duplicate whose overlap is at least O (no such non-duplicate: the least R).
    /// Annotating cosine A: the least multiple of the grid at which no more than floor(share x n) of the n non-duplicates are at or above it, capped at R.
    /// </summary>
    internal static Derived Derive(IReadOnlyList<PairRow> derivation, Parameters p)
    {
        var repeats = derivation.Where(r => IsRepeat(r.Class)).ToList();
        var nonDuplicates = derivation.Where(r => IsNonDuplicate(r.Class)).ToList();

        var overlapBp = p.Bp("overlap_max");
        if (repeats.Count > 0) overlapBp = GridFloorBp(repeats.Min(r => r.Overlap), p.Bp("grid_overlap"));
        overlapBp = Math.Clamp(overlapBp, p.Bp("overlap_min"), p.Bp("overlap_max"));

        var refuseBp = p.Bp("refuse_cosine_least");
        var overlapping = nonDuplicates.Where(r => r.Overlap >= overlapBp / BasisPoints).ToList();
        if (overlapping.Count > 0)
        {
            var m = (decimal)overlapping.Max(r => r.Cosine);
            refuseBp = Math.Max(refuseBp, GridCeilBp(m + p.Bp("refuse_margin") / 10_000m, p.Bp("grid_cosine")));
        }

        var allowed = nonDuplicates.Count * p.Bp("annotate_false_share_derivation") / 10_000;
        var annotateBp = 0;
        while (nonDuplicates.Count(r => r.Cosine >= annotateBp / BasisPoints) > allowed) annotateBp += p.Bp("grid_cosine");
        return new Derived(overlapBp, refuseBp, Math.Min(annotateBp, refuseBp));
    }

    internal enum Verdict { None, Annotate, Refuse }

    /// <summary>What the near-duplicate check does with a pair at these numbers: refuse at or above R with overlap at or above O, else annotate at or above A.</summary>
    internal static Verdict Decide(PairRow row, Derived d) =>
        row.Cosine >= d.RefuseBp / BasisPoints && row.Overlap >= d.OverlapBp / BasisPoints ? Verdict.Refuse
        : row.Cosine >= d.AnnotateBp / BasisPoints ? Verdict.Annotate
        : Verdict.None;

    /// <summary>The share of the pairs of <paramref name="cls"/> at or above A, as (at or above, all): the rule publishes it for Derivation and for Check separately.</summary>
    internal static (int Hit, int Total) Recall(IReadOnlyList<PairRow> rows, string cls, int annotateBp)
    {
        var of = rows.Where(r => r.Class == cls).ToList();
        return (of.Count(r => r.Cosine >= annotateBp / BasisPoints), of.Count);
    }

    /// <summary>F: the larger of the least F and the largest multiple of the grid not above the percentile's relevant score (nearest rank) of the Derivation relevant scores.</summary>
    internal static int Floor(IReadOnlyList<ScoreRow> relevantDerivation, Parameters p)
    {
        if (relevantDerivation.Count == 0) return p.Bp("floor_least");
        var sorted = relevantDerivation.Select(r => r.Score).Order().ToList();
        var rank = Math.Max(1, (p.Bp("floor_relevant_percentile") * sorted.Count + 9_999) / 10_000);
        return Math.Max(p.Bp("floor_least"), GridFloorBp(sorted[rank - 1], p.Bp("grid_cosine")));
    }

    /// <summary>The Check's five conditions: the three of the thresholds and the floor's two. A field is true when its condition holds.</summary>
    internal sealed record Verdicts(bool NoNonDuplicateRefused, bool AnnotatedWithinCeiling, bool RepeatsCaught, bool FloorRemoves, bool FloorKeeps)
    {
        /// <summary>The three thresholds' conditions: if any fails, no number ships.</summary>
        internal bool ThresholdsShip => NoNonDuplicateRefused && AnnotatedWithinCeiling && RepeatsCaught;

        /// <summary>The floor does something if removal and keep both hold; then it ships.</summary>
        internal bool FloorDoesSomething => FloorRemoves && FloorKeeps;
    }

    /// <summary>The Check conditions on the Check rows with the Derivation's R, O, A and F.</summary>
    internal static Verdicts Check(IReadOnlyList<PairRow> check, IReadOnlyList<ScoreRow> relevantCheck, IReadOnlyList<ScoreRow> noiseCheck, Derived d, int floorBp, Parameters p)
    {
        var nonDuplicates = check.Where(r => IsNonDuplicate(r.Class)).ToList();
        var repeats = check.Where(r => IsRepeat(r.Class)).ToList();

        var noRefused = nonDuplicates.All(r => Decide(r, d) != Verdict.Refuse);
        var ceiling = (nonDuplicates.Count * p.Bp("annotate_false_share_check") + 9_999) / 10_000;
        var withinCeiling = nonDuplicates.Count(r => Decide(r, d) == Verdict.Annotate) <= ceiling;
        var caught = repeats.Count(r => Decide(r, d) != Verdict.None) * 10_000 >= p.Bp("repeat_recall_check_least") * repeats.Count;

        var floor = floorBp / BasisPoints;
        var removed = noiseCheck.Count(r => r.Score < floor);
        var kept = relevantCheck.Count(r => r.Score >= floor);
        var removes = noiseCheck.Count > 0 && removed * 10_000 >= p.Bp("floor_removal_least") * noiseCheck.Count;
        var keeps = relevantCheck.Count > 0 && kept * 10_000 >= p.Bp("floor_keep_least") * relevantCheck.Count;
        return new Verdicts(noRefused, withinCeiling, caught, removes, keeps);
    }

    // ------------------------------------------------------------------------------------------------ the measurement

    /// <summary>One pair's measurements: the passage-passage cosine, the query-passage cosine (b as a query against a as a passage) and the Jaccard overlap.</summary>
    internal sealed record PairMeasure(string Id, string Class, string Source, double Passage, double QueryPassage, double Overlap);

    /// <summary>One noise query's score: its greatest cosine, query role, to any post of the rule's post set.</summary>
    internal sealed record NoiseMeasure(string Id, string Class, double Score);

    internal sealed record Measurements(IReadOnlyList<PairMeasure> Pairs, IReadOnlyList<NoiseMeasure> Noise);

    private sealed record RawPair(string Id, string Class, string Source, string A, string B);

    private static string Joined(JsonElement side) => side.GetProperty("title").GetString() + "\n" + side.GetProperty("body").GetString();

    private static List<JsonElement> Lines(string file)
    {
        var path = Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", file);
        return [.. File.ReadLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone())];
    }

    /// <summary>
    /// Reads dedupe-pairs.jsonl, blind-pairs.jsonl and corpus.jsonl and embeds with the Forum's own embedder: passage role for the pairs' texts and the post set, query role for a pair's relevant score and for
    /// a noise query. It computes no threshold: this is the table's source and nothing the rule would call O, R, A, F, a recall, a removal or a keep.
    /// </summary>
    internal static async Task<Measurements> MeasureAsync(OnnxTextEmbedder embedder, CancellationToken cancellationToken)
    {
        var cache = new Dictionary<(bool, string), IReadOnlyList<float>>();

        async Task<IReadOnlyList<float>> Vec(string text, EmbeddingRole role)
        {
            if (!cache.TryGetValue((role == EmbeddingRole.Query, text), out var v))
                cache[(role == EmbeddingRole.Query, text)] = v = Probe.VectorOf(await embedder.EmbedAsync(text, role, cancellationToken), "a measured text");
            return v;
        }

        var raw = new List<RawPair>();
        var blind = Lines("blind-pairs.jsonl");
        foreach (var r in Lines("dedupe-pairs.jsonl"))
            raw.Add(new RawPair(r.GetProperty("id").GetString()!, r.GetProperty("class").GetString()!, "legacy", Joined(r.GetProperty("a")), Joined(r.GetProperty("b"))));
        foreach (var r in blind.Where(r => r.TryGetProperty("a", out _)))
            raw.Add(new RawPair(r.GetProperty("id").GetString()!, r.GetProperty("class").GetString()!, "blind", Joined(r.GetProperty("a")), Joined(r.GetProperty("b"))));

        var pairs = new List<PairMeasure>();
        foreach (var r in raw)
        {
            var a = await Vec(r.A, EmbeddingRole.Passage);
            var b = await Vec(r.B, EmbeddingRole.Passage);
            var bq = await Vec(r.B, EmbeddingRole.Query);
            pairs.Add(new PairMeasure(r.Id, r.Class, r.Source, Probe.Cosine(a, b), Probe.Cosine(bq, a), LexicalOverlap.Jaccard(r.A, r.B)));
        }

        var posts = new List<IReadOnlyList<float>>();
        foreach (var r in raw.Where(r => r.Source == "blind"))
        {
            posts.Add(await Vec(r.A, EmbeddingRole.Passage));
            posts.Add(await Vec(r.B, EmbeddingRole.Passage));
        }

        foreach (var c in Lines("corpus.jsonl"))
        {
            var title = c.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() + "\n" : string.Empty;
            posts.Add(await Vec(title + c.GetProperty("body").GetString(), EmbeddingRole.Passage));
        }

        var noise = new List<NoiseMeasure>();
        foreach (var q in blind.Where(r => !r.TryGetProperty("a", out _)))
        {
            var text = q.TryGetProperty("text", out var tx) ? tx.GetString()! : Joined(q);
            var qv = await Vec(text, EmbeddingRole.Query);
            noise.Add(new NoiseMeasure(q.GetProperty("id").GetString()!, q.GetProperty("class").GetString()!, posts.Max(p => Probe.Cosine(qv, p))));
        }

        return new Measurements(pairs, noise);
    }

    /// <summary>
    /// The rule over a measurement table: R, O, A, F from Derivation and the five conditions on Check. Task 4 is the first to call this on the blind data; here it is exercised on literals only.
    /// </summary>
    internal static (Derived Derived, int FloorBp, Verdicts Verdicts) Compute(Measurements m, Parameters p)
    {
        var rows = m.Pairs.Select(x => new PairRow(x.Class, x.Id, x.Passage, x.Overlap)).ToList();
        var derivation = rows.Where(r => IsDerivation(r.Id)).ToList();
        var check = rows.Where(r => !IsDerivation(r.Id)).ToList();
        var relevant = m.Pairs.Where(x => IsRelevantClass(x.Class)).Select(x => new ScoreRow(x.Id, x.QueryPassage)).ToList();
        var noise = m.Noise.Select(x => new ScoreRow(x.Id, x.Score)).ToList();

        var d = Derive(derivation, p);
        var floorBp = Floor([.. relevant.Where(r => IsDerivation(r.Id))], p);
        var verdicts = Check(check, [.. relevant.Where(r => !IsDerivation(r.Id))], [.. noise.Where(r => !IsDerivation(r.Id))], d, floorBp, p);
        return (d, floorBp, verdicts);
    }

    // ------------------------------------------------------------------------------------------------ the measurements file

    /// <summary>The measurements as the committed file holds them: one JSON line per pair or noise query, cosines and overlaps at round-trip precision. No label, threshold or verdict.</summary>
    internal static IEnumerable<string> MeasurementLines(Measurements m) =>
        m.Pairs.Select(p => string.Create(CultureInfo.InvariantCulture,
                $"{{\"kind\":\"pair\",\"id\":{JsonSerializer.Serialize(p.Id)},\"class\":{JsonSerializer.Serialize(p.Class)},\"source\":{JsonSerializer.Serialize(p.Source)},\"passage\":{p.Passage:R},\"query_passage\":{p.QueryPassage:R},\"overlap\":{p.Overlap:R}}}"))
            .Concat(m.Noise.Select(n => string.Create(CultureInfo.InvariantCulture,
                $"{{\"kind\":\"noise\",\"id\":{JsonSerializer.Serialize(n.Id)},\"class\":{JsonSerializer.Serialize(n.Class)},\"score\":{n.Score:R}}}")));

    /// <summary>Reads the committed measurements file back into the runner's own table.</summary>
    internal static Measurements ReadMeasurements(string path)
    {
        var pairs = new List<PairMeasure>();
        var noise = new List<NoiseMeasure>();
        foreach (var line in File.ReadLines(path).Where(l => l.Length > 0))
        {
            using var json = JsonDocument.Parse(line);
            var r = json.RootElement;
            if (r.GetProperty("kind").GetString() == "pair")
                pairs.Add(new PairMeasure(r.GetProperty("id").GetString()!, r.GetProperty("class").GetString()!, r.GetProperty("source").GetString()!,
                    r.GetProperty("passage").GetDouble(), r.GetProperty("query_passage").GetDouble(), r.GetProperty("overlap").GetDouble()));
            else
                noise.Add(new NoiseMeasure(r.GetProperty("id").GetString()!, r.GetProperty("class").GetString()!, r.GetProperty("score").GetDouble()));
        }

        return new Measurements(pairs, noise);
    }

    // ------------------------------------------------------------------------------------------------ the reference file

    /// <summary>The measurements <c>tools/model/pair-cosines.py</c> wrote: one JSON line per pair or noise query.</summary>
    internal sealed record Reference(IReadOnlyDictionary<string, (double Passage, double QueryPassage)> Pairs, IReadOnlyDictionary<string, double> Noise)
    {
        internal static Reference Read(string path)
        {
            var pairs = new Dictionary<string, (double, double)>(StringComparer.Ordinal);
            var noise = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var line in File.ReadLines(path).Where(l => l.Length > 0))
            {
                using var json = JsonDocument.Parse(line);
                var r = json.RootElement;
                var id = r.GetProperty("id").GetString()!;
                if (r.GetProperty("kind").GetString() == "pair") pairs[id] = (r.GetProperty("passage").GetDouble(), r.GetProperty("query_passage").GetDouble());
                else noise[id] = r.GetProperty("score").GetDouble();
            }

            return new Reference(pairs, noise);
        }
    }
}
