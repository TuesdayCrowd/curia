using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Retrieval;
using Curia.Domain.Search;
using Curia.Domain.Verification;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;
using Curia.Tests.Shared;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// R9.47 (errata G45), probe RVO42: every structure the Forum keeps by an envelope digest tolerates a log that holds two posts of one digest, resolves
/// the digest to the first post in log order, and counts distinct digests. The two sites that did not are <c>DuplicateCheck</c>'s candidate map and
/// <c>HybridSearch</c>'s vector channel (D87). Each fact asserts its precondition first (the log holds exactly two posts of the digest), so a seeding
/// that failed is a red and not a vacuous green.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class StoredDuplicateDigestTests
{
    private const string Digest = "sha256:0000000000000000000000000000000000000000000000000000000000000d87";
    private const string Title = "ECONNRESET from npgsql";
    private const string Body = "npgsql throws ECONNRESET after the pooler idles the connection";

    private sealed record Harness(InMemoryEventStore Store, EmbeddingIndexer Indexer, DuplicateCheck Check, HybridSearch Search);

    private static Harness NewHarness()
    {
        var embedder = new HashedNGramEmbedder();
        var index = new InMemoryVectorIndex();
        return new Harness(
            new InMemoryEventStore(Clock(), new PermissiveLifecycleAppendGate()),
            new EmbeddingIndexer(embedder, index),
            new DuplicateCheck(embedder, index, DuplicateThresholds.Published),
            new HybridSearch(embedder, index, RetrievalFloors.Published));
    }

    private static async Task<IReadOnlyList<AppendedEvent>> SeedTwoAsync(Harness h, CancellationToken ct)
    {
        await AcceptAsync(h.Store, h.Indexer, "first", ct, title: Title, body: Body, digestOverride: Digest).ConfigureAwait(false);
        await AcceptAsync(h.Store, h.Indexer, "second", ct, title: Title, body: Body, digestOverride: Digest).ConfigureAwait(false);
        var log = await LogAsync(h.Store, ct).ConfigureAwait(false);

        Assert.Equal(["first", "second"], SearchProjector.Fold(log).Where(p => p.Digest == Digest).Select(p => p.PostId));
        return log;
    }

    [Fact]
    public async Task R9_47_TheDuplicateCheckOverTwoPostsOfOneDigestAnswersAndNamesTheFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        var log = await SeedTwoAsync(h, ct);
        var root = (Curia.Canon.Json.JsonValue.Object)Require(Curia.Canon.Json.JsonReader.ParseUnrestricted(
            System.Text.Encoding.UTF8.GetBytes(Canonical(PostKind.Question, "https://agents.example/asker", "postgres", Title, Body, []))));
        var envelope = Require(PostEnvelope.Read(root));

        var assessed = await h.Check.AssessAsync(log, envelope, ct);

        var assessment = Require(assessed);
        Assert.Equal(
            "first; 1 distinct digest considered",
            $"{assessment.Nearest?.PostId}; {assessment.CandidatesConsidered} distinct digest considered");
    }

    [Fact]
    public async Task R9_47_SearchOverTwoPostsOfOneDigestAnswersAndNamesTheDigestOnceAtTheFirstPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        var log = await SeedTwoAsync(h, ct);
        var verification = VerificationProjector.Fold(PostProjector.Fold(log), AgentStandingProjector.Fold(log), _ => true, DisputedPosts.None);
        var query = new SearchQuery("npgsql ECONNRESET", null, [], [], null, null, null, 25);

        var page = Require(await h.Search.SearchAsync(log, RetrievalSurface.RestSearch, query, verification, ct));

        Assert.Equal(
            ["first"],
            page.Results.Where(r => r.Post.Digest == Digest).Select(r => r.Post.PostId));
    }
}
