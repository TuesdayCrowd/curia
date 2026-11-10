using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Retrieval;
using Curia.Domain.Search;
using Curia.Domain.Verification;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// Errata G65 (D10a), SEM1 to SEM3, model-free: the port carries a role and a token. A query is embedded in the query role and a stored post, and a question compared with the
/// posts of its board, in the passage role (R9.63); a text with no features is still "nothing to compare" at every site; the 2-second step of R8.81 is a bound on something that
/// can be cancelled (R11.92).
///
/// <para><b>Oracles.</b> The roles are recorded by an embedder the test wrote, not read from the code under test; every probe first asserts that its recorder was reached
/// (<c>NOT REACHED</c>), so a call site that stopped embedding cannot pass for one that embeds in the right role.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EmbedRoleTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingEmbedder(ITextEmbedder inner) : ITextEmbedder
    {
        internal List<(string Text, EmbeddingRole Role)> Calls { get; } = [];

        public EmbeddingModel Model => inner.Model;

        public Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken)
        {
            Calls.Add((text, role));
            return inner.EmbedAsync(text, role, cancellationToken);
        }
    }

    private sealed class NoFeaturesEmbedder : ITextEmbedder
    {
        internal int Calls { get; private set; }

        public EmbeddingModel Model => HashedNGramEmbedding.Model;

        public Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Result<Embedding>.Fail(EmbeddingErrors.NoFeatures()));
        }
    }

    /// <summary>An embedder that waits for its caller to cancel, as an inference queued behind a flood does.</summary>
    private sealed class WaitsUntilCancelledEmbedder : ITextEmbedder
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;

        public EmbeddingModel Model => HashedNGramEmbedding.Model;

        public async Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken)
        {
            _entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return Result<Embedding>.Fail(EmbeddingErrors.ModelFault());
        }
    }

    private static string Show(IEnumerable<(string Text, EmbeddingRole Role)> calls) =>
        string.Join("; ", calls.Select(c => $"{c.Role}: {c.Text}"));

    /// <summary>
    /// SEM1. The search embeds its query as <c>Query</c>; the near-duplicate check embeds the incoming question as <c>Passage</c>; the indexer embeds a post as <c>Passage</c> at both
    /// of its sites (<c>IndexAsync</c> and the absence scan). Predicted red on the seam: <c>HybridSearch embedded "npgsql ECONNRESET" as Passage, expected Query</c> (the seam
    /// passes <c>Passage</c> everywhere); the check and the indexer rows are green on the seam and are the controls.
    /// </summary>
    [Fact]
    public async Task R9_63_SEM1_AQueryIsEmbeddedAsAQueryAndAStoredPostAndAComparedQuestionAsPassages()
    {
        var store = new InMemoryEventStore(Clock(), new PermissiveLifecycleAppendGate());
        await AcceptAsync(store, new EmbeddingIndexer(new HashedNGramEmbedder(), new InMemoryVectorIndex()), "stored", Ct);
        var log = await LogAsync(store, Ct);
        var post = SearchProjector.Fold(log).Single();

        // The indexer, at both of its sites.
        var indexing = new RecordingEmbedder(new HashedNGramEmbedder());
        Require(await new EmbeddingIndexer(indexing, new InMemoryVectorIndex()).IndexAsync(post, Ct));
        Assert.True(indexing.Calls.Count == 1, $"NOT REACHED: IndexAsync embedded {indexing.Calls.Count} time(s), expected 1");
        Require(await new EmbeddingIndexer(indexing, new InMemoryVectorIndex()).ReconcileAsync(store, Ct));
        Assert.True(indexing.Calls.Count == 2, $"NOT REACHED: the absence scan embedded {indexing.Calls.Count - 1} time(s), expected 1");
        Assert.True(
            indexing.Calls.All(c => c.Role == EmbeddingRole.Passage),
            $"EmbeddingIndexer embedded a stored post in a role other than Passage: {Show(indexing.Calls)}");

        // The near-duplicate check.
        var checking = new RecordingEmbedder(new HashedNGramEmbedder());
        var check = new DuplicateCheck(checking, new InMemoryVectorIndex(), DuplicateThresholds.Published);
        var envelope = Require(PostEnvelope.Read((Curia.Canon.Json.JsonValue.Object)Require(Curia.Canon.Json.JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(
            Canonical(PostKind.Question, "https://agents.example/asker", "postgres", "ECONNRESET from npgsql", "npgsql throws ECONNRESET after the pooler idles the connection", []))))));
        Require(await check.AssessAsync(log, envelope, Ct));
        Assert.True(checking.Calls.Count == 1, $"NOT REACHED: the near-duplicate check embedded {checking.Calls.Count} time(s), expected 1");
        Assert.True(
            checking.Calls[0].Role == EmbeddingRole.Passage,
            $"DuplicateCheck embedded the incoming question as {checking.Calls[0].Role}, expected Passage");

        // The search.
        var searching = new RecordingEmbedder(new HashedNGramEmbedder());
        var index = new InMemoryVectorIndex();
        Require(await new EmbeddingIndexer(new HashedNGramEmbedder(), index).IndexAsync(post, Ct));
        var search = new HybridSearch(searching, index, RetrievalFloors.Published);
        var fold = VerificationProjector.Fold(PostProjector.Fold(log), AgentStandingProjector.Fold(log), _ => true, DisputedPosts.None);
        Require(await search.SearchAsync(log, RetrievalSurface.RestSearch, new SearchQuery("npgsql ECONNRESET", null, [], [], null, null, null, 25), fold, Ct));
        Assert.True(searching.Calls.Count == 1, $"NOT REACHED: the search embedded {searching.Calls.Count} time(s), expected 1");
        Assert.True(
            searching.Calls[0].Role == EmbeddingRole.Query,
            $"HybridSearch embedded \"{searching.Calls[0].Text}\" as {searching.Calls[0].Role}, expected Query");
    }

    /// <summary>
    /// SEM2. A text the embedder finds no features in is "nothing to compare" at all three sites, with a role: the search answers its lexical page, the check answers none, the indexer
    /// skips the post (a null, not a failure). Control: green on the seam.
    /// </summary>
    [Fact]
    public async Task R9_64_SEM2_NoFeaturesIsStillNothingToCompareAtEverySite()
    {
        var store = new InMemoryEventStore(Clock(), new PermissiveLifecycleAppendGate());
        await AcceptAsync(store, new EmbeddingIndexer(new HashedNGramEmbedder(), new InMemoryVectorIndex()), "stored", Ct);
        var log = await LogAsync(store, Ct);
        var post = SearchProjector.Fold(log).Single();
        var none = new NoFeaturesEmbedder();

        var indexed = Require(await new EmbeddingIndexer(none, new InMemoryVectorIndex()).IndexAsync(post, Ct));
        Assert.Null(indexed);

        var envelope = Require(PostEnvelope.Read((Curia.Canon.Json.JsonValue.Object)Require(Curia.Canon.Json.JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(
            Canonical(PostKind.Question, "https://agents.example/asker", "postgres", "ECONNRESET from npgsql", "npgsql throws ECONNRESET after the pooler idles the connection", []))))));
        var assessed = Require(await new DuplicateCheck(none, new InMemoryVectorIndex(), DuplicateThresholds.Published).AssessAsync(log, envelope, Ct));
        Assert.Equal(DuplicateVerdict.None, assessed.Verdict);

        var fold = VerificationProjector.Fold(PostProjector.Fold(log), AgentStandingProjector.Fold(log), _ => true, DisputedPosts.None);
        var page = Require(await new HybridSearch(none, new InMemoryVectorIndex(), RetrievalFloors.Published)
            .SearchAsync(log, RetrievalSurface.RestSearch, new SearchQuery("npgsql ECONNRESET", null, [], [], null, null, null, 25), fold, Ct));
        Assert.NotEmpty(page.Results);
        Assert.All(page.Results, r => Assert.Equal(0, r.VectorRank));
        Assert.True(none.Calls == 3, $"NOT REACHED: the embedder was asked {none.Calls} time(s), expected once at each of the three sites");
    }

    /// <summary>
    /// SEM3 (Application half). The 2-second step is a bound on something cancellable: an embedder that waits for its caller's cancellation, and a clock advanced 2 seconds, give
    /// <c>Pending</c> with <c>curia/retrieval/index-bound</c>, the caller's shutdown token not cancelled, the post in the backlog. The runner half (a stop request reaches the run and
    /// frees its place) is <c>OnnxEmbedderSeamTests.SEM3_...</c> in Curia.Infrastructure.Tests. Control: green on the seam, because the seam already passes the token through the port.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM3_TheBoundExpiresOnAnEmbedderThatWaitsForItsCaller()
    {
        var store = new InMemoryEventStore(Clock(), new PermissiveLifecycleAppendGate());
        await AcceptAsync(store, new EmbeddingIndexer(new HashedNGramEmbedder(), new InMemoryVectorIndex()), "stored", Ct);
        var post = SearchProjector.Fold(await LogAsync(store, Ct)).Single();

        var start = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var clock = new TimerManualTimeProvider(start);
        var embedder = new WaitsUntilCancelledEmbedder();
        var backlog = new IndexBacklog();
        var committed = new CommittedPostIndexer(new EmbeddingIndexer(embedder, new InMemoryVectorIndex()), backlog, clock, new IndexSweep(start));
        using var shutdown = new CancellationTokenSource();

        var step = committed.IndexAsync(post, shutdown.Token);
        await embedder.Entered.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        clock.Advance(CommittedPostIndexer.Bound);
        var outcome = await step.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal(CommittedIndexState.Pending, outcome.State);
        Assert.Equal("curia/retrieval/index-bound", outcome.FaultType);
        Assert.False(shutdown.IsCancellationRequested);
        Assert.Contains(post.Digest, backlog.Due(DateTimeOffset.MaxValue).Select(e => e.Digest));
    }
}
