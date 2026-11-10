using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder;
using Xunit;
using Emb = Curia.Application.Ports.Embedding;
using Vector = System.Collections.Generic.IReadOnlyList<float>;

namespace Curia.Embedding.Tests;

/// <summary>
/// Errata G65 (D10a), SEM30 to SEM34, SEM36 to SEM38 and SEM46 on the real model: roles, truncation, determinism, separation (by rank, with no threshold), latency (recorded, not asserted), totality, normalisation,
/// the dedupe reading the model, and the stop of a cancelled run. Numbers that D10b gates (the 16-pair table, the fixture's cosines, the latencies) are written to a file in the output directory and printed, and
/// asserted nowhere.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BehaviourTests(RealModelFixture model) : IClassFixture<RealModelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] CommonWords =
    [
        "the", "of", "and", "to", "in", "is", "that", "it", "for", "was", "on", "are", "with", "as", "his", "they", "be", "at", "one", "have",
        "this", "from", "or", "had", "by", "not", "but", "what", "all", "were", "when", "we", "there", "can", "an", "your", "which", "their", "said", "if",
        "do", "will", "each", "about", "how", "up", "out", "them", "then", "she",
    ];

    private async Task<Vector> EmbedAsync(string text, EmbeddingRole role) => Probe.VectorOf(await model.Embedder.EmbedAsync(text, role, Ct), text.Length > 40 ? text[..40] : text);

    private static string Words(int count) => string.Join(' ', Enumerable.Range(0, count).Select(i => CommonWords[(i * 7 + i / 50) % CommonWords.Length]));

    private static void Record(string name, IEnumerable<string> lines)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllLines(path, lines);
        TestContext.Current.SendDiagnosticMessage($"{name}:{Environment.NewLine}{string.Join(Environment.NewLine, File.ReadLines(path))}");
    }

    /// <summary>
    /// SEM30. The query vector of a sentence is not its passage vector (cosine below 0.999) and each equals its reference: the manifest's <c>plain</c> canary (passage) and <c>plain-query</c> canary (query role,
    /// same text). Predicted red on the seam: <c>expected a vector, got curia/embedding/model-not-ready</c>.
    /// </summary>
    [Fact]
    public async Task R9_63_SEM30_TheQueryVectorOfASentenceIsNotItsPassageVectorAndEachEqualsItsReference()
    {
        var manifest = EmbeddingManifest.Embedded();
        var passage = manifest.Canaries.SingleOrDefault(c => c.Id == "plain");
        var query = manifest.Canaries.SingleOrDefault(c => c.Id == "plain-query");
        Assert.True(passage is not null && query is not null, "the manifest holds no 'plain' and 'plain-query' canary pair");
        Assert.Equal(passage!.Text, query!.Text);

        var asPassage = await EmbedAsync(passage.Text, EmbeddingRole.Passage);
        var asQuery = await EmbedAsync(passage.Text, EmbeddingRole.Query);

        Assert.True(Probe.Cosine(asPassage, asQuery) < 0.999, $"the same text in the two roles has cosine {Probe.Describe(Probe.Cosine(asPassage, asQuery))}, expected below 0.999");
        Assert.True(Probe.MaxDifference(asPassage, passage.Vector) <= 1e-4, "the passage vector is not the reference's");
        Assert.True(Probe.MaxDifference(asQuery, query.Vector) <= 1e-4, "the query vector is not the reference's");
    }

    /// <summary>
    /// SEM31. A 3,000-token text and its own first 510 content tokens give the same vector within 1e-6; a 300-token prefix gives a different one (cosine below 0.9999); a 256 KiB text is tokenized by reading no
    /// more characters than those of its first 510 content tokens and the delimiter that ends the 510th (an internal counter, asserted; the time is recorded, not asserted). Predicted red on the seam:
    /// <c>expected a vector, got curia/embedding/model-not-ready</c>.
    /// </summary>
    [Fact]
    public async Task R9_63_SEM31_OnlyTheFirst510TokensAreEmbeddedAndOnlyTheirCharactersAreRead()
    {
        var first510 = Words(510);
        var long3000 = await EmbedAsync(Words(3000), EmbeddingRole.Passage);
        var head = await EmbedAsync(first510, EmbeddingRole.Passage);
        Assert.True(Probe.MaxDifference(long3000, head) <= 1e-6, $"a 3,000-token text differs from its first 510 tokens by {Probe.MaxDifference(long3000, head):E2}");

        foreach (var word in CommonWords)
            Assert.True(model.Embedder.Tokenizer.Tokenize(word, 10).ContentIds.Count == 1, $"NOT REACHED: '{word}' is not one token, so a word count is not a token count");

        var prefix300 = await EmbedAsync(Words(300), EmbeddingRole.Passage);
        Assert.True(Probe.Cosine(head, prefix300) < 0.9999, $"a 300-token prefix has cosine {Probe.Describe(Probe.Cosine(head, prefix300))} with the head, expected below 0.9999");

        var huge = string.Join(' ', Enumerable.Range(0, 90_000).Select(i => CommonWords[(i * 7 + i / 50) % CommonWords.Length]));
        Assert.True(huge.Length > 256 * 1024, "NOT REACHED: the text is not 256 KiB");
        var read = model.Embedder.Tokenizer.Tokenize(huge, 510);
        var bound = first510.Length + 1;
        Assert.True(read.CharactersConsumed <= bound, $"the tokenizer read {read.CharactersConsumed} characters of a 256 KiB text, expected at most {bound}");

        var watch = Stopwatch.StartNew();
        await EmbedAsync(huge, EmbeddingRole.Passage);
        var big = watch.Elapsed;
        watch.Restart();
        await EmbedAsync(first510, EmbeddingRole.Passage);
        Record("sem31-times.txt", [$"256 KiB: {big.TotalMilliseconds:0.0} ms", $"510 tokens: {watch.Elapsed.TotalMilliseconds:0.0} ms"]);
    }

    /// <summary>
    /// SEM32. The same 50 texts, embedded one after another and from 8 threads at once, give vectors equal within 1e-6. Predicted red on the seam: <c>expected a vector, got
    /// curia/embedding/model-not-ready</c>.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM32_TheSameTextsGiveTheSameVectorsOneAtATimeAndFromEightThreads()
    {
        var texts = Enumerable.Range(0, 50).Select(i => Words(5 + (i * 11 % 120)) + " " + i.ToString(CultureInfo.InvariantCulture)).ToArray();
        var alone = new Vector[texts.Length];
        for (var i = 0; i < texts.Length; i++) alone[i] = await EmbedAsync(texts[i], EmbeddingRole.Passage);

        var together = new ConcurrentDictionary<int, Vector>();
        await Parallel.ForEachAsync(Enumerable.Range(0, texts.Length), new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = Ct }, async (i, _) => together[i] = await EmbedAsync(texts[i], EmbeddingRole.Passage));

        for (var i = 0; i < texts.Length; i++)
            Assert.True(Probe.MaxDifference(alone[i], together[i]) <= 1e-6, $"text {i} differs by {Probe.MaxDifference(alone[i], together[i]):E2} between one thread and eight");
    }

    private sealed record Pair(string Id, string Class, string A, string B);

    private static string Text(JsonElement e) => e.GetProperty("title").GetString() + "\n" + e.GetProperty("body").GetString();

    private static List<Pair> Pairs() =>
        [.. File.ReadLines(Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", "dedupe-pairs.jsonl")).Where(l => l.Length > 0).Select(l =>
        {
            using var json = JsonDocument.Parse(l);
            var r = json.RootElement;
            return new Pair(r.GetProperty("id").GetString()!, r.GetProperty("class").GetString()!, Text(r.GetProperty("a")), Text(r.GetProperty("b")));
        })];

    /// <summary>
    /// SEM33. By rank and with no threshold: each of the four <c>paraphrase</c> pairs' cosine exceeds the greatest cosine over the three <c>distinct</c> pairs; each paraphrase, as a query in the query role over the
    /// ten-post corpus of <c>conformance/retrieval/corpus.jsonl</c> plus that pair's own post (read as: the pair's first text is the post to find), finds its post in the vector top 3. The 16-pair table is
    /// written to a file, printed, and asserted nowhere (D10b gates it). Predicted red on the seam: <c>expected a vector, got curia/embedding/model-not-ready</c>.
    /// </summary>
    [Fact]
    public async Task R9_63_SEM33_AParaphraseIsCloserThanAnythingDistinctAndFindsItsPostInTheTopThree()
    {
        var pairs = Pairs();
        Assert.Equal((16, 4, 3), (pairs.Count, pairs.Count(p => p.Class == "paraphrase"), pairs.Count(p => p.Class == "distinct")));

        var table = new List<string>();
        var cosines = new Dictionary<string, double>();
        foreach (var pair in pairs)
        {
            var cosine = Probe.Cosine(await EmbedAsync(pair.A, EmbeddingRole.Passage), await EmbedAsync(pair.B, EmbeddingRole.Passage));
            cosines[pair.Id] = cosine;
            table.Add($"{pair.Class,-11} {pair.Id,-26} {cosine:0.000}");
        }

        Record("sem33-pairs.txt", table);
        var greatestDistinct = pairs.Where(p => p.Class == "distinct").Max(p => cosines[p.Id]);
        foreach (var pair in pairs.Where(p => p.Class == "paraphrase"))
            Assert.True(cosines[pair.Id] > greatestDistinct, $"{pair.Id}: cosine {cosines[pair.Id]:0.000} does not exceed the greatest distinct pair's {greatestDistinct:0.000}");

        var corpus = (await File.ReadAllLinesAsync(Path.Combine(ModelDirectory.RepositoryRoot(), "conformance", "retrieval", "corpus.jsonl"), Ct)).Where(l => l.Length > 0).Select(l =>
        {
            using var json = JsonDocument.Parse(l);
            var r = json.RootElement;
            return (r.GetProperty("id").GetString()!, (r.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() + "\n" : string.Empty) + r.GetProperty("body").GetString());
        }).ToList();
        Assert.Equal(10, corpus.Count);

        var embeddedCorpus = new List<(string Id, Vector Vector)>();
        foreach (var (id, text) in corpus) embeddedCorpus.Add((id, await EmbedAsync(text, EmbeddingRole.Passage)));
        foreach (var pair in pairs.Where(p => p.Class == "paraphrase"))
        {
            var withPost = embeddedCorpus.Append((Id: pair.Id, Vector: await EmbedAsync(pair.A, EmbeddingRole.Passage))).ToList();
            var query = await EmbedAsync(pair.B, EmbeddingRole.Query);
            var ranked = withPost.OrderByDescending(p => Probe.Cosine(query, p.Vector)).Select(p => p.Id).ToList();
            Assert.True(ranked.IndexOf(pair.Id) is >= 0 and < 3, $"{pair.Id}: its post ranks {ranked.IndexOf(pair.Id) + 1} of {ranked.Count} for its paraphrase as a query");
        }
    }

    /// <summary>
    /// SEM34. Latency is recorded and nothing is asserted (a ratio of two times on a shared runner measures the runner, ruling 13): the p50 and p99 of 20 calls at 16, 128 and 510 tokens, at concurrency 1, 2 and 8,
    /// written to a file. Each call must answer a vector, so the recording is of work done. Predicted red on the seam: <c>expected a vector, got curia/embedding/model-not-ready</c>.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM34_LatencyIsRecordedAndNothingIsAsserted()
    {
        var lines = new List<string>();
        foreach (var tokens in new[] { 16, 128, 510 })
        {
            foreach (var concurrency in new[] { 1, 2, 8 })
            {
                var times = new ConcurrentBag<double>();
                await Parallel.ForEachAsync(Enumerable.Range(0, 20), new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = Ct }, async (i, _) =>
                {
                    var watch = Stopwatch.StartNew();
                    await EmbedAsync(Words(tokens) + " " + i.ToString(CultureInfo.InvariantCulture), EmbeddingRole.Passage);
                    times.Add(watch.Elapsed.TotalMilliseconds);
                });
                var sorted = times.Order().ToArray();
                lines.Add($"{tokens,4} tokens x{concurrency}: p50 {sorted[sorted.Length / 2]:0.0} ms  p99 {sorted[^1]:0.0} ms");
            }
        }

        Record("sem34-latency.txt", lines);
    }

    /// <summary>
    /// SEM36. With the real tokenizer: an emoji-only text is <c>NoFeatures</c>; U+FFFE, a lone surrogate and NUL answer without throwing (a vector or no features); a 256 KiB body completes with a vector.
    /// Predicted red on the seam: <c>emoji-only: expected curia/embedding/no-features, got curia/embedding/model-not-ready</c>.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM36_TheRealTokenizerIsTotalAndAnEmojiOnlyTextHasNoFeatures()
    {
        var emoji = await model.Embedder.EmbedAsync(Probe.Cp(0x1F600) + Probe.Cp(0x1F680), EmbeddingRole.Passage, Ct);
        Assert.True(emoji.Match(_ => "(a vector)", e => e.Type) == EmbeddingErrors.NoFeatures().Type, "emoji-only: expected curia/embedding/no-features, got " + emoji.Match(_ => "(a vector)", e => e.Type));

        foreach (var (name, text) in new[]
        {
            ("U+FFFE", "hello " + Probe.Cp(0xFFFE) + " world"),
            ("a lone surrogate", "hello " + new string((char)0xD800, 1) + " world"),
            ("NUL", "hello" + new string((char)0, 1) + "world"),
        })
        {
            var result = await model.Embedder.EmbedAsync(text, EmbeddingRole.Passage, Ct);
            var type = result.Match(_ => "(a vector)", e => e.Type);
            Assert.True(type is "(a vector)" || type == EmbeddingErrors.NoFeatures().Type, $"{name}: expected a vector or no features, got {type}");
        }

        var body = string.Join(' ', Enumerable.Range(0, 40_000).Select(i => CommonWords[i % CommonWords.Length]));
        Assert.True(body.Length > 128 * 1024, "NOT REACHED: the body is not large");
        _ = await EmbedAsync(body + " " + string.Concat(Enumerable.Repeat("x", 128 * 1024)), EmbeddingRole.Passage);
    }

    /// <summary>
    /// SEM37. A composed and a decomposed spelling give vectors equal within 1e-6, as the reference does: <c>caf</c> + U+00E9 and <c>cafe</c> + U+0301, <c>a</c> + U+00F1 + <c>o</c> and <c>ano</c> + U+0303, and a Hangul syllable string and its
    /// decomposed jamo. Predicted red on the seam: <c>expected a vector, got curia/embedding/model-not-ready</c>.
    /// </summary>
    [Fact]
    public async Task R9_63_SEM37_ComposedAndDecomposedSpellingsGiveTheSameVector()
    {
        var pairs = new[]
        {
            ("caf" + Probe.Cp(0xE9), "cafe" + Probe.Cp(0x301)),
            ("a" + Probe.Cp(0xF1) + "o", "ano" + Probe.Cp(0x303)),
            (Probe.Cp(0xD55C) + Probe.Cp(0xAD6D), Probe.Cp(0x1112) + Probe.Cp(0x1161) + Probe.Cp(0x11AB) + Probe.Cp(0x1100) + Probe.Cp(0x116E) + Probe.Cp(0x11A8)),
        };

        foreach (var (composed, decomposed) in pairs)
        {
            var a = await EmbedAsync("the " + composed, EmbeddingRole.Passage);
            var b = await EmbedAsync("the " + decomposed, EmbeddingRole.Passage);
            Assert.True(Probe.MaxDifference(a, b) <= 1e-6, $"{composed} and its decomposed spelling differ by {Probe.MaxDifference(a, b):E2}");
        }
    }

    private sealed class MemoryIndex : IVectorIndex
    {
        private readonly List<(string Digest, string PostId, long Sequence, Emb Embedding)> _rows = [];

        public Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Emb embedding, CancellationToken cancellationToken = default)
        {
            _rows.Add((digest, postId, sequence, embedding));
            return Task.FromResult(Result<IndexedVector>.Ok(new IndexedVector(digest, postId, sequence)));
        }

        public Task<Result<ImmutableArray<VectorMatch>>> NearestAsync(Emb query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ImmutableArray<VectorMatch>>.Ok([.. _rows
                .Where(r => r.Embedding.Model == query.Model)
                .Select(r => new VectorMatch(r.Digest, r.PostId, r.Sequence, 1.0 - Probe.Cosine(r.Embedding.Vector, query.Vector)))
                .OrderBy(m => m.Distance).Take(limit)]));

        public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => Task.FromResult(Result<long>.Ok(_rows.Count));

        public Task<Result<IReadOnlySet<string>>> IndexedDigestsAsync(EmbeddingModel model, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlySet<string>>.Ok(_rows.Select(r => r.Digest).ToHashSet(StringComparer.Ordinal)));
    }

    private static T Req<T>(Result<T> result) => result.Match(v => v, e => throw new Xunit.Sdk.XunitException($"{e.Type}: {e.Title}"));

    private static string CanonicalQuestion(string title, string body)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String("question")));
        members.Add(new("author", new JsonValue.String("https://agents.example/asker")));
        members.Add(new("board", new JsonValue.String("postgres")));
        members.Add(new("title", new JsonValue.String(title)));
        members.Add(new("body", new JsonValue.String(body)));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero).ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));
        return Encoding.UTF8.GetString(Req(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable()))).Span);
    }

    /// <summary>
    /// SEM38. The near-duplicate check reads the model: the five <c>literal</c> and four <c>paraphrase</c> conformance pairs go through <c>DuplicateCheck</c> with the thresholds unchanged (the first text stored as
    /// a post, the second submitted), and no <c>related</c> or <c>distinct</c> pair is refused (the ceiling); the 16 verdicts are printed. The literal pairs' refusal is not asserted (D10b). Predicted red on the seam:
    /// <c>curia/embedding/model-not-ready: The embedding model is not ready</c>.
    /// </summary>
    [Fact]
    public async Task R8_18_SEM38_NoRelatedOrDistinctPairIsRefusedAtTheUnchangedThresholds()
    {
        var verdicts = new List<string>();
        var refused = new List<string>();
        foreach (var pair in Pairs())
        {
            var index = new MemoryIndex();
            var indexer = new EmbeddingIndexer(model.Embedder, index);
            var (titleA, bodyA) = Split(pair.A);
            var (titleB, bodyB) = Split(pair.B);
            var stored = Req(PostEnvelope.Read((JsonValue.Object)Req(JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(CanonicalQuestion(titleA, bodyA))))));
            var post = new SearchablePost(
                "stored-" + pair.Id, "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(pair.Id))), stored.Board, PostKind.Question, titleA, bodyA, [], "https://agents.example/other", 1);
            Req(await indexer.IndexAsync(post, Ct));

            var envelope = Req(PostEnvelope.Read((JsonValue.Object)Req(JsonReader.ParseUnrestricted(Encoding.UTF8.GetBytes(CanonicalQuestion(titleB, bodyB))))));
            var assessed = Req(await new DuplicateCheck(model.Embedder, index, DuplicateThresholds.Published).AssessAsync(ImmutableArray.Create(post), envelope, Ct));
            verdicts.Add($"{pair.Class,-11} {pair.Id,-26} {assessed.Verdict,-9} cosine {assessed.Cosine:0.000} overlap {assessed.LexicalOverlap:0.000}");
            if (pair.Class is "related" or "distinct" && assessed.Verdict == DuplicateVerdict.Refuse) refused.Add(pair.Id);
        }

        Record("sem38-verdicts.txt", verdicts);
        Assert.True(refused.Count == 0, "refused a related or distinct pair: " + string.Join(", ", refused));
    }

    private static (string Title, string Body) Split(string text)
    {
        var cut = text.IndexOf('\n', StringComparison.Ordinal);
        return (text[..cut], text[(cut + 1)..]);
    }

    /// <summary>
    /// SEM46 (ruling 23). A 510-token text embedded with a token cancelled at 5 ms throws <c>OperationCanceledException</c> (not a fault, not an ONNX Runtime exception) within 100 ms of the cancel; its place is free
    /// at once (the next text gives the reference vector of the <c>plain</c> canary); a caller cancelled while both places are busy leaves within 100 ms and runs nothing. Falsified by FSEM36 and FSEM37. Predicted red
    /// on the seam: <c>Assert.ThrowsAny() Failure: No exception was thrown</c>.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM46_ACancelledRunIsStoppedItsPlaceIsFreeAndAQueuedCallerLeaves()
    {
        var long510 = Words(510);
        using var cancel = new CancellationTokenSource();
        var running = model.Embedder.EmbedAsync(long510, EmbeddingRole.Passage, cancel.Token);
        await Task.Delay(5, Ct);
        var cancelledAt = Stopwatch.StartNew();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(cancelledAt.ElapsedMilliseconds <= 100, $"the run was stopped {cancelledAt.ElapsedMilliseconds} ms after the cancel, expected within 100");

        var canary = EmbeddingManifest.Embedded().Canaries.SingleOrDefault(c => c.Id == "plain");
        Assert.True(canary is not null, "the manifest holds no 'plain' canary");
        var next = await EmbedAsync(canary!.Text, canary.Role);
        Assert.True(Probe.MaxDifference(next, canary.Vector) <= 1e-4, "the call after a stopped run does not return the reference vector");

        var busy = new[]
        {
            model.Embedder.EmbedAsync(long510 + " one", EmbeddingRole.Passage, Ct),
            model.Embedder.EmbedAsync(long510 + " two", EmbeddingRole.Passage, Ct),
        };
        using var queued = new CancellationTokenSource();
        var waiting = model.Embedder.EmbedAsync(long510 + " three", EmbeddingRole.Passage, queued.Token);
        await Task.Delay(5, Ct);
        var left = Stopwatch.StartNew();
        await queued.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(left.ElapsedMilliseconds <= 100, $"a queued caller left {left.ElapsedMilliseconds} ms after its cancel, expected within 100");
        await Task.WhenAll(busy);
    }
}
