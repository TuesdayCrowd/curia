using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Domain.Content;
using Curia.Domain.Search;

namespace Curia.Application.Tests.Retrieval;

/// <summary>A parameter of <c>conformance/retrieval/STOPWORD-RULE.md</c>'s table, exact: the values are decimals and a comparison against a count is cross-multiplied, so nothing is a float.</summary>
internal sealed record RuleParams(IReadOnlyDictionary<string, decimal> Values)
{
    public decimal this[string name] => Values[name];

    public int Window => (int)Values["keep_window"];

    public RuleParams With(string name, decimal value) =>
        new(new Dictionary<string, decimal>(Values) { [name] = value });
}

internal sealed record MaterialQuery(string Id, string Class, string Form, string Text, string? TargetId);

/// <summary>One query's integers: what the two artifacts of the rule must agree on.</summary>
internal sealed record QueryOutcome(
    MaterialQuery Query,
    ImmutableArray<string> Tokens,
    ImmutableArray<string> Terms,
    ImmutableArray<string> Ignored,
    bool HadToken,
    long? TargetSeq,
    ImmutableArray<(long Seq, int Score)> Without,
    ImmutableArray<(long Seq, int Score)> With)
{
    public bool AllIgnored => HadToken && Terms.IsEmpty;

    public int? RankWithout => RankOf(Without);

    public int? RankWith => RankOf(With);

    private int? RankOf(ImmutableArray<(long Seq, int Score)> hits)
    {
        if (TargetSeq is not { } target) return null;
        for (var i = 0; i < hits.Length; i++)
            if (hits[i].Seq == target) return i + 1;
        return null;
    }
}

internal enum StopwordVerdict { Ships, No, Undecided }

internal sealed record RemovalMeasure(int Queries, int With, int Without, bool Ok);

internal sealed record KeepOnTopicMeasure(int Queries, int Real, int StopwordOnly, int Base, int Kept, int AllIgnored, string MrrFall, bool BaseOk, bool KeptOk, bool MrrOk);

internal sealed record KeepHazardMeasure(int Queries, int M, int Lost, int Allowed, int LostAllIgnored, int LostWithOwnTerm, int StopwordOnly, bool Ok);

internal sealed record StopwordMeasures(
    ImmutableArray<string> Listings,
    RemovalMeasure Removal,
    KeepOnTopicMeasure KeepOnTopic,
    KeepHazardMeasure KeepHazard,
    StopwordVerdict Verdict);

/// <summary>
/// The C# artifact of the stopword rule (errata G81, <c>conformance/retrieval/STOPWORD-RULE.md</c>): the rule's corpus in the rule's order, each query run through
/// <see cref="LexicalSearch.Rank"/> with and without the list, and the rule's numbers. <c>tools/retrieval/lexical-measure.py</c> is the other artifact; they share only the committed files.
///
/// <para><b>Where "with the list" comes from before the stage is built.</b> <c>LexicalSearch.SearchTerms</c> does not exist yet, so <see cref="Run"/> removes the list's words from a query's
/// tokens here and hands the remaining terms to the unmodified <see cref="LexicalSearch.Rank"/> as text (a term is a token, so it tokenizes to itself). A query with a token and no term
/// left lists nothing: that is the rule's state (ii), written here because the unmodified <c>Rank</c> would answer it with a listing. Task 5 replaces the two lines that do this with
/// <c>LexicalSearch.SearchTerms</c> and the product's own switch, and this file then measures the product and not a copy of it.</para>
/// </summary>
internal static partial class StopwordMeasure
{
    private static readonly string[] ClassOrder = ["target", "hazard", "off-topic", "vague"];

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("conformance/ not found above " + AppContext.BaseDirectory);
    }

    internal static string Retrieval => Path.Combine(RepoRoot(), "conformance", "retrieval");

    internal static string RuleText => File.ReadAllText(Path.Combine(Retrieval, "STOPWORD-RULE.md"), Encoding.UTF8);

    // ----- the rule file -----

    [GeneratedRegex(@"^- [^()]*\((\d+)\): (.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupLine();

    [GeneratedRegex("`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Span();

    /// <summary>The words of the rule file's grouped block under <c>The list</c>, sorted ordinally (the groups are for reading only); each group's stated size is checked against its words.</summary>
    internal static ImmutableArray<string> ListBlock(string ruleText)
    {
        var words = ImmutableArray.CreateBuilder<string>();
        var inList = false;
        foreach (var line in ruleText.Split('\n'))
        {
            if (line.StartsWith("**The list (", StringComparison.Ordinal)) { inList = true; continue; }
            if (!inList) continue;
            var m = GroupLine().Match(line);
            if (m.Success)
            {
                var group = Span().Matches(m.Groups[2].Value).SelectMany(s => s.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
                if (group.Length != int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
                    throw new InvalidOperationException($"a group of the list states {m.Groups[1].Value} words and holds {group.Length}");
                words.AddRange(group);
            }
            else if (words.Count > 0 && !line.StartsWith("- ", StringComparison.Ordinal)) break;
        }

        return [.. words.OrderBy(w => w, StringComparer.Ordinal)];
    }

    internal static RuleParams Params(string ruleText)
    {
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var inTable = false;
        foreach (var raw in ruleText.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "| parameter | value |") { inTable = true; continue; }
            if (!inTable) continue;
            if (!line.StartsWith('|')) break;
            var cells = line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
            if (cells[0].StartsWith('-')) continue;
            values[cells[0]] = decimal.Parse(cells[1], NumberStyles.Number, CultureInfo.InvariantCulture);
        }

        return new RuleParams(values);
    }

    /// <summary>The parameters the rule's prose names (code spans that are snake_case identifiers), outside the table.</summary>
    internal static ImmutableSortedSet<string> ParametersNamedInProse(string ruleText) =>
        [.. ruleText.Split('\n').Where(l => !l.TrimStart().StartsWith('|'))
            .SelectMany(l => Span().Matches(l).SelectMany(m => SnakeCase().Matches(m.Groups[1].Value).Select(i => i.Value)))];

    [GeneratedRegex("[a-z]+(_[a-z]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex SnakeCase();

    // ----- the material and the corpus -----

    private static List<JsonElement> Lines(string path) =>
        [.. File.ReadLines(path, Encoding.UTF8).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => JsonDocument.Parse(l).RootElement.Clone())];

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>The material's records in the rule's order (class, then id; a post's two searches terse first), with the corpus built around them.</summary>
    internal static (ImmutableArray<SearchablePost> Corpus, ImmutableArray<MaterialQuery> Queries, IReadOnlyDictionary<string, long> TargetSeq) Build(string retrievalDir, string queriesPath)
    {
        var records = Lines(queriesPath)
            .OrderBy(r => Array.IndexOf(ClassOrder, Str(r, "class")))
            .ThenBy(r => Str(r, "id"), StringComparer.Ordinal)
            .ToList();

        var posts = new List<SearchablePost>();
        var byText = new Dictionary<string, long>(StringComparer.Ordinal);
        long Add(string id, string? title, string body, IEnumerable<string> tags)
        {
            var text = (title ?? string.Empty) + "\n" + body;
            if (byText.TryGetValue(text, out var existing)) return existing;
            var seq = posts.Count;
            posts.Add(new SearchablePost(id, "sha256:" + id, "b", PostKind.Question, title, body, [.. tags], "a", seq));
            byText[text] = seq;
            return seq;
        }

        foreach (var p in Lines(Path.Combine(retrievalDir, "corpus.jsonl")))
            Add("corpus:" + Str(p, "id"), Str(p, "title"), Str(p, "body")!, p.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!));
        foreach (var r in Lines(Path.Combine(retrievalDir, "blind-pairs.jsonl")))
        {
            if (!r.TryGetProperty("a", out var a) || !r.TryGetProperty("b", out var b)) continue;
            Add(Str(r, "id") + ":a", Str(a, "title"), Str(a, "body")!, []);
            Add(Str(r, "id") + ":b", Str(b, "title"), Str(b, "body")!, []);
        }

        var targetSeq = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var r in records.Where(r => Str(r, "class") is "target" or "hazard"))
        {
            var post = r.GetProperty("post");
            targetSeq[Str(r, "id")!] = Add(Str(r, "id")!, Str(post, "title"), Str(post, "body")!, []);
        }

        var queries = ImmutableArray.CreateBuilder<MaterialQuery>();
        foreach (var r in records)
        {
            var cls = Str(r, "class")!;
            var id = Str(r, "id")!;
            if (cls is "target" or "hazard")
            {
                queries.Add(new MaterialQuery(id, cls, "terse", Str(r, "terse")!, id));
                queries.Add(new MaterialQuery(id, cls, "sentence", Str(r, "sentence")!, id));
            }
            else
            {
                queries.Add(new MaterialQuery(id, cls, "query", Str(r, "query")!, null));
            }
        }

        return ([.. posts], queries.ToImmutable(), targetSeq);
    }

    // ----- one query, both ways -----

    private static ImmutableArray<(long Seq, int Score)> Hits(IReadOnlyList<SearchablePost> corpus, string text) =>
        [.. LexicalSearch.Rank(corpus, new LexicalQuery(Text: text)).Select(h => (h.Post.Sequence, h.Score))];

    internal static ImmutableArray<QueryOutcome> Run(IReadOnlyList<SearchablePost> corpus, IEnumerable<MaterialQuery> queries, IReadOnlyDictionary<string, long> targetSeq, ISet<string> list)
    {
        var outcomes = ImmutableArray.CreateBuilder<QueryOutcome>();
        foreach (var q in queries)
        {
            var tokens = LexicalSearch.Tokenize(q.Text);
            var terms = tokens.Where(t => !list.Contains(t)).ToImmutableArray();
            var ignored = tokens.Where(list.Contains).Distinct(StringComparer.Ordinal).ToImmutableArray();
            var hadToken = !tokens.IsEmpty;

            // Without the list: the unmodified ranking over every token. A query with no token is a listing and measures nothing.
            var without = hadToken ? Hits(corpus, q.Text) : [];

            // With the list: the remaining terms, as text; nothing left (state ii) lists nothing.
            var with = hadToken && !terms.IsEmpty ? Hits(corpus, string.Join(' ', terms)) : [];

            outcomes.Add(new QueryOutcome(q, tokens, terms, ignored, hadToken, q.TargetId is { } t ? targetSeq[t] : null, without, with));
        }

        return outcomes.ToImmutable();
    }

    // ----- the rule's numbers -----

    private readonly record struct Rational(BigInteger Num, BigInteger Den)
    {
        public static Rational Of(long n, long d) => new(n, d);

        public static Rational operator +(Rational a, Rational b) => new((a.Num * b.Den) + (b.Num * a.Den), a.Den * b.Den);

        public static Rational operator -(Rational a, Rational b) => new((a.Num * b.Den) - (b.Num * a.Den), a.Den * b.Den);

        public int CompareTo(Rational other) => (Num * other.Den).CompareTo(other.Num * Den);

        public Rational Over(long n) => new(Num, Den * n);

        public override string ToString() => $"{Num}/{Den}";
    }

    private static Rational Exact(decimal value)
    {
        var scale = 1L;
        while (value * scale != decimal.Truncate(value * scale)) scale *= 10;
        return new Rational((long)(value * scale), scale);
    }

    /// <summary>Removal, both Keeps and the verdict from the per-query integers alone, in the arithmetic <c>lexical-measure.py</c> uses.</summary>
    internal static StopwordMeasures Compute(IReadOnlyList<QueryOutcome> all, int corpusSize, RuleParams p)
    {
        var listings = all.Where(o => !o.HadToken).Select(o => o.Query.Id).ToImmutableArray();
        var queries = all.Where(o => o.HadToken).ToList();

        var off = queries.Where(o => o.Query.Class == "off-topic").ToList();
        var thr = Exact(p["removal_page_share_most"]);
        var narrowWith = off.Count(o => Rational.Of(o.With.Length, corpusSize).CompareTo(thr) <= 0);
        var narrowWithout = off.Count(o => Rational.Of(o.Without.Length, corpusSize).CompareTo(thr) <= 0);
        var shareWith = off.Count == 0 ? Rational.Of(0, 1) : Rational.Of(narrowWith, off.Count);
        var shareWithout = off.Count == 0 ? Rational.Of(0, 1) : Rational.Of(narrowWithout, off.Count);
        var removalOk = off.Count > 0
            && shareWith.CompareTo(Exact(p["removal_share_least"])) >= 0
            && (shareWith - shareWithout).CompareTo(Exact(p["removal_improvement_least"])) >= 0;

        var window = p.Window;
        var on = queries.Where(o => o.Query.Class == "target").ToList();
        int stopwordOnly = 0, real = 0, baseCount = 0, kept = 0;
        var rrWithout = Rational.Of(0, 1);
        var rrWith = Rational.Of(0, 1);
        foreach (var o in on)
        {
            if (o.RankWith is not { } rWith)
            {
                if (o.RankWithout is not null) stopwordOnly++;
                continue;
            }

            var rWithout = o.RankWithout ?? throw new InvalidOperationException("a target listed with the list is listed without it");
            real++;
            rrWithout += Rational.Of(1, rWithout);
            rrWith += Rational.Of(1, rWith);
            if (rWithout > window) continue;
            baseCount++;
            if (rWith <= window) kept++;
        }

        var mrrFall = real == 0 ? Rational.Of(0, 1) : (rrWithout - rrWith).Over(real);
        var baseOk = baseCount >= p["keep_base_least"];
        var keptOk = baseCount > 0 && Rational.Of(kept, baseCount).CompareTo(Exact(p["keep_on_topic_least"])) >= 0;
        var mrrOk = mrrFall.CompareTo(Exact(p["keep_mrr_fall_most"])) <= 0;

        var haz = queries.Where(o => o.Query.Class == "hazard").ToList();
        int m = 0, lost = 0, lostAllIgnored = 0, lostOwnTerm = 0;
        foreach (var o in haz)
        {
            if (o.RankWithout is null) continue;
            m++;
            if (o.RankWith is not null) continue;
            lost++;
            if (o.AllIgnored) lostAllIgnored++;
            else if (!o.Terms.IsEmpty) lostOwnTerm++;
        }

        var allowed = (int)Math.Ceiling(p["hazard_lost_share_most"] * m);
        var hazardOk = lost <= allowed;

        var keepOk = baseOk && keptOk && mrrOk;
        var verdict = !baseOk ? StopwordVerdict.Undecided : removalOk && keepOk && hazardOk ? StopwordVerdict.Ships : StopwordVerdict.No;

        return new StopwordMeasures(
            listings,
            new RemovalMeasure(off.Count, narrowWith, narrowWithout, removalOk),
            new KeepOnTopicMeasure(on.Count, real, stopwordOnly, baseCount, kept, on.Count(o => o.AllIgnored), mrrFall.ToString(), baseOk, keptOk, mrrOk),
            new KeepHazardMeasure(haz.Count, m, lost, allowed, lostAllIgnored, lostOwnTerm, lost, hazardOk),
            verdict);
    }

    /// <summary>The text RESULTS.md carries between the <c>lexical-stopwords</c> markers: numbers with their denominators, and the verdict the rule gives.</summary>
    internal static string Render(StopwordMeasures m) =>
        string.Join('\n',
            $"Removal: {m.Removal.With} of {m.Removal.Queries} off-topic queries at most a quarter of the corpus with the list, {m.Removal.Without} without; ok {m.Removal.Ok}.",
            $"Keep, on-topic: real {m.KeepOnTopic.Real} of {m.KeepOnTopic.Queries}, stopword-only {m.KeepOnTopic.StopwordOnly}, all-ignored {m.KeepOnTopic.AllIgnored}; base {m.KeepOnTopic.Base}, kept {m.KeepOnTopic.Kept}, MRR fall {m.KeepOnTopic.MrrFall}.",
            $"Keep, hazard: m {m.KeepHazard.M} of {m.KeepHazard.Queries}, lost {m.KeepHazard.Lost} (allowed {m.KeepHazard.Allowed}), all-ignored {m.KeepHazard.LostAllIgnored}, with a term of their own {m.KeepHazard.LostWithOwnTerm}, stopword-only {m.KeepHazard.StopwordOnly}.",
            $"Listings excluded: {m.Listings.Length}.",
            $"Verdict: {m.Verdict}.");
}
