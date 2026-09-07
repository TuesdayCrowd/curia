using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Content;
using Curia.Domain.Retrieval;
using Curia.Domain.Search;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// Phase 3's "dedupe measured on a real query set", and R10.5's canaries: <c>conformance/retrieval/</c>
/// run against the deployed embedder and thresholds, held to its baselines by name. See that
/// directory's README for what each file is and what the numbers mean.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RetrievalQuerySetTests
{
    private sealed record Pair(string Id, string Class, string Verdict, string A, string B);
    private sealed record Measured(Pair Pair, double Cosine, double Overlap, DuplicateVerdict Verdict);

    private static readonly string Root = FindRoot();
    private static readonly DuplicateThresholds Thresholds = DuplicateThresholds.Published;
    private static readonly string[] Verdicts = ["duplicate", "related", "distinct"];

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;
        return dir is null
            ? throw new InvalidOperationException("conformance/ not found above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "conformance", "retrieval");
    }

    private static IEnumerable<JsonElement> Lines(string file) =>
        File.ReadLines(Path.Combine(Root, file))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l).RootElement.Clone());

    private static string Text(JsonElement q) =>
        string.Join('\n', new[] { q.GetProperty("title").GetString() ?? string.Empty, q.GetProperty("body").GetString() ?? string.Empty }.Where(s => s.Length > 0));

    private static List<Pair> Pairs() =>
        [.. Lines("dedupe-pairs.jsonl").Select(e => new Pair(
            e.GetProperty("id").GetString()!, e.GetProperty("class").GetString()!, e.GetProperty("verdict").GetString()!,
            Text(e.GetProperty("a")), Text(e.GetProperty("b"))))];

    private static List<Measured> Measure()
    {
        var embedder = new HashedNGramEmbedder();
        var measured = new List<Measured>();
        foreach (var pair in Pairs())
        {
            var a = Require(embedder.Embed(pair.A)).Vector;
            var b = Require(embedder.Embed(pair.B)).Vector;
            var cosine = HashedNGramEmbedding.Cosine(a, b);
            var overlap = LexicalOverlap.Jaccard(pair.A, pair.B);
            measured.Add(new Measured(pair, cosine, overlap, DuplicatePolicy.Assess(PostKind.Question, cosine, overlap, false, Thresholds)));
        }
        return measured;
    }

    private static string Report(Measured m) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{m.Pair.Id,-26} {m.Pair.Class,-10} {m.Pair.Verdict,-9} cosine {m.Cosine:0.000} overlap {m.Overlap:0.000} -> {m.Verdict}");

    [Fact]
    public void R8_21_TheQuerySetIsWellFormedAndCitesNoExpectedSimilarity()
    {
        var pairs = Pairs();
        Assert.True(pairs.Count >= 12);
        Assert.Equal(pairs.Count, pairs.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(pairs, p => Assert.Contains(p.Verdict, Verdicts));
        Assert.Contains(pairs, p => p.Class == "paraphrase");

        // Authored, not derived: nothing in the set says what a model should measure.
        Assert.DoesNotContain(Lines("dedupe-pairs.jsonl"), e => e.TryGetProperty("cosine", out _) || e.TryGetProperty("expected_cosine", out _));
    }

    /// <summary>The constraining number: a false refusal costs an author their question.</summary>
    [Fact]
    public void R8_18_NoRelatedOrDistinctPairIsRefused()
    {
        var measured = Measure();
        var falseRefusals = measured.Where(m => m.Pair.Verdict != "duplicate" && m.Verdict == DuplicateVerdict.Refuse).ToList();
        Assert.True(falseRefusals.Count == 0, "false refusals:\n" + string.Join('\n', falseRefusals.Select(Report)));
    }

    /// <summary>Every pair the baseline says is refused is still refused, by name.</summary>
    [Fact]
    public void R8_18_TheRefusedBaselineHolds()
    {
        var measured = Measure().ToDictionary(m => m.Pair.Id, StringComparer.Ordinal);
        var baseline = File.ReadLines(Path.Combine(Root, "refused-baseline.txt"))
            .Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.NotEmpty(baseline);

        foreach (var id in baseline)
        {
            Assert.True(measured.ContainsKey(id), $"refused-baseline.txt names {id}, which is not in dedupe-pairs.jsonl");
            Assert.True(measured[id].Verdict == DuplicateVerdict.Refuse, "no longer refused: " + Report(measured[id]));
        }

        // Every literal duplicate is in the baseline: the class any embedding must catch.
        foreach (var m in measured.Values.Where(m => m.Pair.Class == "literal"))
            Assert.Contains(m.Pair.Id, baseline);
    }

    /// <summary>The set is measuring semantics only if the deployed model can be told apart from a lexical one.</summary>
    [Fact]
    public void R9_5_TheParaphraseBlockRecordsWhatTheDeployedModelCannotDo()
    {
        var measured = Measure();
        var paraphrases = measured.Where(m => m.Pair.Class == "paraphrase").ToList();
        Assert.NotEmpty(paraphrases);

        // hashed-ngram@1 is a lexical geometry: it misses every paraphrase. When a semantic model
        // is configured this assertion is the one that flips, and RESULTS.md with it.
        Assert.Equal(HashedNGramEmbedding.Model, new HashedNGramEmbedder().Model);
        Assert.All(paraphrases, m => Assert.NotEqual(DuplicateVerdict.Refuse, m.Verdict));
    }

    [Fact]
    public void R8_21_LiteralDuplicatesSeparateFromDistinctPairs()
    {
        var measured = Measure();
        var minLiteral = measured.Where(m => m.Pair.Class == "literal").Min(m => m.Cosine);
        var maxDistinct = measured.Where(m => m.Pair.Class == "distinct").Max(m => m.Cosine);
        Assert.True(minLiteral > maxDistinct, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"ranges overlap: min literal cosine {minLiteral:0.000} <= max distinct cosine {maxDistinct:0.000}; no threshold works"));
        Assert.True(minLiteral >= Thresholds.RefuseCosine, "a literal duplicate falls below the refusing cosine");
    }

    [Fact]
    public void TheKnownCollisionsListIsCurrent()
    {
        var measured = Measure().ToDictionary(m => m.Pair.Id, StringComparer.Ordinal);
        foreach (var e in Lines("known-collisions.jsonl"))
        {
            var id = e.GetProperty("id").GetString()!;
            Assert.True(measured[id].Verdict == DuplicateVerdict.Refuse, "known collision no longer collides; remove it: " + Report(measured[id]));
        }
    }

    /// <summary>The measurement, printed so RESULTS.md can be checked against it line by line.</summary>
    [Fact]
    public void TheMeasurementIsPrinted()
    {
        var measured = Measure();
        var report = string.Join('\n', measured.Select(Report));
        var refused = measured.Count(m => m.Verdict == DuplicateVerdict.Refuse);
        Assert.True(refused > 0, report);
    }

    [Fact]
    public async Task R10_5_EveryCanarysExpectedPostRanksInTheTopThree()
    {
        var ct = TestContext.Current.CancellationToken;
        var embedder = new HashedNGramEmbedder();
        var index = new InMemoryVectorIndex();
        var store = new InMemoryEventStore(Clock());
        var indexer = new EmbeddingIndexer(embedder, index);
        var search = new HybridSearch(embedder, index, RetrievalFloors.Published);

        foreach (var post in Lines("corpus.jsonl"))
        {
            await AcceptAsync(store, indexer, post.GetProperty("id").GetString()!, ct,
                kind: PostKinds.TryParse(post.GetProperty("kind").GetString()!, out var kind) ? kind : throw new InvalidOperationException("kind"),
                author: post.GetProperty("author").GetString()!,
                board: post.GetProperty("board").GetString()!,
                title: post.GetProperty("title").ValueKind == JsonValueKind.Null ? null : post.GetProperty("title").GetString(),
                body: post.GetProperty("body").GetString()!,
                tags: [.. post.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!)],
                parent: post.TryGetProperty("parent", out var parent) ? parent.GetString() : null);
        }

        var log = await LogAsync(store, ct);
        var fold = VerificationProjector.Fold(PostProjector.Fold(log), AgentStandingProjector.Fold(log), _ => true);

        var failures = new List<string>();
        foreach (var canary in Lines("canaries.jsonl"))
        {
            var query = new SearchQuery(canary.GetProperty("query").GetString(), null, [], [], null, null, null, 10);
            var page = Require(await search.SearchAsync(log, RetrievalSurface.RestSearch, query, fold, ct));
            var ids = page.Results.Select(r => r.Post.PostId).ToList();
            var rank = ids.IndexOf(canary.GetProperty("expected").GetString()!) + 1;
            if (rank is < 1 or > 3)
                failures.Add($"{canary.GetProperty("id").GetString()}: expected {canary.GetProperty("expected").GetString()} at rank {rank} of [{string.Join(", ", ids)}]");
        }

        Assert.True(failures.Count == 0, string.Join('\n', failures));
    }
}
