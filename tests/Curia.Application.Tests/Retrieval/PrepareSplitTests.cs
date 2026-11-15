using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
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
/// Register item 286 (errata G71, R9.65), SQW5 and SQW6: where the steps of a search and of the near-duplicate check are. The waits (the embedding and the index's answer)
/// are in <c>PrepareAsync</c> and nowhere else, and <c>Search</c> and <c>Assess</c> are the synchronous remainder, so a caller reads the log between the two.
///
/// <para><b>What these carry.</b> They are green by assertion on the seam and on the fix alike; they carry information only through the falsification rows (a
/// <c>PrepareAsync</c> that skips the embedding or the index while <c>Search</c> or <c>Assess</c> make the call themselves, FSQW2 to FSQW5). The oracle is the
/// counting fakes' own counters, a different artifact from the code that makes the calls.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PrepareSplitTests
{
    private const string Terms = "npgsql ECONNRESET";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingEmbedder(ITextEmbedder inner, Result<Embedding>? fixedAnswer = null) : ITextEmbedder
    {
        internal List<EmbeddingRole> Roles { get; } = [];

        public EmbeddingModel Model => inner.Model;

        public Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken)
        {
            Roles.Add(role);
            return fixedAnswer is { } answer ? Task.FromResult(answer) : inner.EmbedAsync(text, role, cancellationToken);
        }
    }

    private sealed class RecordingIndex(IVectorIndex inner) : IVectorIndex
    {
        internal int Nearest { get; set; }

        public Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default) =>
            inner.UpsertAsync(digest, postId, sequence, embedding, cancellationToken);

        public Task<Result<ImmutableArray<VectorMatch>>> NearestAsync(Embedding query, int limit, CancellationToken cancellationToken = default)
        {
            Nearest++;
            return inner.NearestAsync(query, limit, cancellationToken);
        }

        public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.CountAsync(model, cancellationToken);

        public Task<Result<IReadOnlySet<string>>> IndexedDigestsAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.IndexedDigestsAsync(model, cancellationToken);
    }

    private sealed class Harness
    {
        internal InMemoryEventStore Store { get; } = new(Clock(), new PermissiveLifecycleAppendGate());

        internal EmbeddingIndexer Indexer { get; }

        internal RecordingEmbedder Embedder { get; }

        internal RecordingIndex Index { get; }

        internal HybridSearch Search { get; }

        internal DuplicateCheck Check { get; }

        internal Harness(Result<Embedding>? embedderAnswer = null)
        {
            var raw = new HashedNGramEmbedder();
            var inner = new InMemoryVectorIndex();
            Indexer = new EmbeddingIndexer(raw, inner);
            Embedder = new RecordingEmbedder(raw, embedderAnswer);
            Index = new RecordingIndex(inner);
            Search = new HybridSearch(Embedder, Index, RetrievalFloors.Published, TestVerifier.Empty);
            Check = new DuplicateCheck(Embedder, Index, DuplicateThresholds.Published);
        }

        internal void Reset()
        {
            Embedder.Roles.Clear();
            Index.Nearest = 0;
        }

        internal async Task<VerifiedLog> VerifiedAsync() => TestVerifier.Empty.OfLog(await LogAsync(Store, Ct).ConfigureAwait(false));
    }

    private static SearchQuery Query(string? text = Terms, RetrievalCursor? cursor = null, int limit = 2) =>
        new(text, Typed.Board("postgres"), [], [], null, null, cursor, limit);

    private static VerificationFold NoVerification(VerifiedLog log) =>
        VerificationProjector.Fold(PostProjector.Fold(log.Log), AgentStandingProjector.Fold(log.Log), _ => true, DisputedPosts.None);

    private static async Task<Harness> SeededAsync(Result<Embedding>? embedderAnswer = null)
    {
        var h = new Harness(embedderAnswer);
        for (var i = 0; i < 4; i++)
            await AcceptAsync(h.Store, h.Indexer, $"p{i}", Ct, title: $"{Terms} case {i}", body: $"variant {i} of the {Terms} question", author: $"https://agents.example/a{i}").ConfigureAwait(false);
        return h;
    }

    private static PostEnvelope Envelope(string title, string body, string board = "postgres")
    {
        var json = Canonical(PostKind.Question, "https://agents.example/asker", board, title, body, []);
        var root = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(json)));
        return Require(PostEnvelope.Read(root));
    }

    /// <summary>SQW5, search half. The wait is in <c>PrepareAsync</c> (one embedding in the query role, one index call) and <c>Search</c> asks nothing.</summary>
    [Fact]
    public async Task R9_65_SQW5_PrepareAsyncMakesTheOneEmbeddingAndTheOneIndexCallAndSearchMakesNone()
    {
        var h = await SeededAsync();
        h.Reset();

        var prepared = Require(await h.Search.PrepareAsync(RetrievalSurface.RestSearch, Query(), Ct));

        Assert.Equal([EmbeddingRole.Query], h.Embedder.Roles);
        Assert.Equal(1, h.Index.Nearest);

        var verified = await h.VerifiedAsync();
        var page = Require(h.Search.Search(verified, prepared, NoVerification(verified)));

        Assert.Equal([EmbeddingRole.Query], h.Embedder.Roles);
        Assert.Equal(1, h.Index.Nearest);
        Assert.NotEmpty(page.Results);   // control: the page was cut, from the neighbours Prepare fetched, so zero further calls is not an empty search
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task R9_65_SQW5_ABlankTextQueryEmbedsNothingAndAsksTheIndexNothing(string? text)
    {
        var h = await SeededAsync();
        h.Reset();

        var prepared = Require(await h.Search.PrepareAsync(RetrievalSurface.RestSearch, Query(text), Ct));
        var verified = await h.VerifiedAsync();
        var page = Require(h.Search.Search(verified, prepared, NoVerification(verified)));

        Assert.Equal((0, 0), (h.Embedder.Roles.Count, h.Index.Nearest));
        Assert.NotEmpty(page.Results);   // control: a filter is a search of the board's posts
    }

    /// <summary>SQW5, dedupe half. <c>PrepareAsync</c> asks the index even on a board whose corpus holds no candidate (it has no corpus), and <c>Assess</c> asks nothing.</summary>
    [Fact]
    public async Task R9_65_SQW5_TheDuplicatePrepareEmbedsAsAPassageAndAsksTheIndexEvenOnABoardWithNoCandidateAndAssessAsksNothing()
    {
        var h = new Harness();
        h.Reset();

        var prepared = Require(await h.Check.PrepareAsync(Envelope("ECONNRESET from npgsql", "npgsql throws ECONNRESET after the pooler idles the connection"), Ct));

        Assert.Equal([EmbeddingRole.Passage], h.Embedder.Roles);
        Assert.Equal(1, h.Index.Nearest);

        var assessment = Require(h.Check.Assess([], prepared));

        Assert.Equal([EmbeddingRole.Passage], h.Embedder.Roles);
        Assert.Equal(1, h.Index.Nearest);
        Assert.Equal((DuplicateVerdict.None, 0), (assessment.Verdict, assessment.CandidatesConsidered));
    }

    /// <summary>Control for the dedupe half: with a candidate on the board the same two steps refuse, so zero calls in <c>Assess</c> is not an assessment that never looked.</summary>
    [Fact]
    public async Task R9_65_SQW5_TheDuplicateAssessOverACorpusWithTheCandidateRefusesWithoutAskingAnything()
    {
        var h = new Harness();
        var original = await AcceptAsync(h.Store, h.Indexer, "orig", Ct, title: "ECONNRESET from npgsql", body: "npgsql throws ECONNRESET after the pooler idles the connection");
        h.Reset();

        var prepared = Require(await h.Check.PrepareAsync(Envelope("ECONNRESET from npgsql", "npgsql throws ECONNRESET after the pooler idles the connection"), Ct));
        var corpus = SearchProjector.Fold(await LogAsync(h.Store, Ct));
        var assessment = Require(h.Check.Assess(corpus, prepared));

        Assert.Equal((1, 1), (h.Embedder.Roles.Count, h.Index.Nearest));
        Assert.Equal((DuplicateVerdict.Refuse, original), (assessment.Verdict, assessment.Nearest!.Digest));
    }

    public static TheoryData<string> Members() => ["q", "board", "author", "kind", "tags", "requested floor", "limit"];

    /// <summary>SQW6. The members come from <c>HybridSearchCursorBindingTests</c>'s table, less "floor in force" (a second searcher, not a second query).</summary>
    [Theory]
    [MemberData(nameof(Members))]
    public async Task R9_65_SQW6_APrepareUnderACursorOfAnotherQueryIsRefusedWithTheMismatchAndEmbedsNothing(string member)
    {
        var h = await SeededAsync();
        var first = Require(await h.Search.PrepareAsync(RetrievalSurface.RestSearch, Query(), Ct));
        var verified = await h.VerifiedAsync();
        var minted = Require(h.Search.Search(verified, first, NoVerification(verified)));
        Assert.NotNull(minted.Next);   // control: the cursor exists, and the identical query below continues

        var presented = Query(cursor: minted.Next);
        var changed = member switch
        {
            "q" => presented with { Text = Terms + " keepalive" },
            "board" => presented with { Board = Typed.Board("other") },
            "author" => presented with { Author = Typed.Agent("https://agents.example/a1") },
            "kind" => presented with { Kinds = [PostKind.Question] },
            "tags" => presented with { Tags = ["tls"] },
            "requested floor" => presented with { RequestedFloor = VerificationLevel.V1 },
            "limit" => presented with { Limit = 3 },
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, "not a member this fact varies"),
        };

        h.Reset();
        var refused = await h.Search.PrepareAsync(RetrievalSurface.RestSearch, changed, Ct);

        Assert.True(!refused.TryGetValue(out _, out var error), $"expected a refusal under a changed {member}, got a prepared search");
        Assert.Equal(RetrievalCursor.QueryMismatch().Type, error!.Type);
        Assert.Equal((0, 0), (h.Embedder.Roles.Count, h.Index.Nearest));

        h.Reset();
        Require(await h.Search.PrepareAsync(RetrievalSurface.RestSearch, presented, Ct));
        Assert.Equal((1, 1), (h.Embedder.Roles.Count, h.Index.Nearest));
    }

    [Fact]
    public async Task R9_65_SQW6_NoFeaturesFromTheEmbedderIsNotARefusalAndLeavesNoNeighbours()
    {
        var h = await SeededAsync(Result<Embedding>.Fail(EmbeddingErrors.NoFeatures()));
        h.Reset();

        var prepared = Require(await h.Search.PrepareAsync(RetrievalSurface.RestSearch, Query(), Ct));

        Assert.Equal(0, h.Index.Nearest);   // nothing to ask the index with
        var verified = await h.VerifiedAsync();
        var page = Require(h.Search.Search(verified, prepared, NoVerification(verified)));
        Assert.NotEmpty(page.Results);      // control: the lexical channel still answers
    }

    [Fact]
    public async Task R9_65_SQW6_AnyOtherEmbedderErrorFailsThePrepareWithThatErrorsType()
    {
        var fault = EmbeddingErrors.ModelFault();
        Assert.NotEqual(EmbeddingErrors.NoFeatures().Type, fault.Type);   // control: the two errors are told apart by type
        var h = await SeededAsync(Result<Embedding>.Fail(fault));

        var refused = await h.Search.PrepareAsync(RetrievalSurface.RestSearch, Query(), Ct);

        Assert.True(!refused.TryGetValue(out _, out var error), "expected a failure, got a prepared search");
        Assert.Equal(fault.Type, error!.Type);
    }
}
