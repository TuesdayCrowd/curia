using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Infrastructure.Embedder;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests.Embedder;

/// <summary>
/// Errata G65 (D10a), SEM26, on a real Postgres with pgvector: a database that holds 256-dimension rows under <c>hashed-ngram@1</c> keeps them when the Forum indexes 384-dimension rows under the
/// new id, and neither model's queries meet the other's rows (R11.10, Ruling 11). Task 0 M13 found the plan a sequential scan with the model filter applied first and no dimension error.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TwoModelsTests(PostgresDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EmbeddingModel Old = HashedNGramEmbedding.Model;
    private static readonly EmbeddingModel New = OnnxTextEmbedder.BgeModel;

    private static string DigestOf(string name) => "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));

    private static ImmutableArray<float> Unit(int dimensions, int hot) =>
        [.. Enumerable.Range(0, dimensions).Select(i => i == hot ? 1f : 0f)];

    /// <summary>A model that is not the hashed one: 384 dimensions, a vector per text that depends on its first letter.</summary>
    private sealed class BgeShapedEmbedder : ITextEmbedder
    {
        public EmbeddingModel Model => New;

        public Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding>.Ok(new Embedding(New, Unit(384, text.Length % 384))));
    }

    private static T Req<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    /// <summary>
    /// SEM26. Rows of 256 dimensions under the old model and 384 under the new id share a table: a nearest-neighbour query under either model returns only that model's rows with no error; the
    /// digests and counts are per model; a reconcile under the new id indexes the posts it lacks and leaves the old model's count unchanged (the old rows are kept); and the application role's
    /// <c>DELETE</c> is refused, by attempt (R11.6). Expected on the seam: green, a control for the grant and for M13's finding; it turns red only if a later change types the column or drops the
    /// <c>WHERE model</c>.
    /// </summary>
    [Fact]
    public async Task R11_10_SEM26_TwoModelsOfTwoDimensionsShareATableAndMeetNoneOfEachOthersRows()
    {
        var schema = await fixture.CreateIsolatedRetrievalSchemaAsync(Ct);
        var index = new PostgresVectorIndex(fixture.AppRoleDataSource, TimeProvider.System, schema);

        for (var i = 0; i < 3; i++)
        {
            Req(await index.UpsertAsync(DigestOf("old-" + i), "old-" + i, i + 1, new Embedding(Old, Unit(256, i)), Ct));
            Req(await index.UpsertAsync(DigestOf("new-" + i), "new-" + i, i + 10, new Embedding(New, Unit(384, i)), Ct));
        }

        var underOld = Req(await index.NearestAsync(new Embedding(Old, Unit(256, 0)), 10, Ct));
        var underNew = Req(await index.NearestAsync(new Embedding(New, Unit(384, 0)), 10, Ct));
        Assert.Equal(3, underOld.Length);
        Assert.All(underOld, m => Assert.StartsWith("old-", m.PostId, StringComparison.Ordinal));
        Assert.Equal(3, underNew.Length);
        Assert.All(underNew, m => Assert.StartsWith("new-", m.PostId, StringComparison.Ordinal));
        Assert.Equal((3L, 3L), (Req(await index.CountAsync(Old, Ct)), Req(await index.CountAsync(New, Ct))));
        Assert.Equal(3, Req(await index.IndexedDigestsAsync(Old, Ct)).Count);
        Assert.True(Req(await index.IndexedDigestsAsync(New, Ct)).All(d => d == DigestOf("new-0") || d == DigestOf("new-1") || d == DigestOf("new-2")), "the new model's digests include another model's");

        // A post the old model holds is absent under the new id (what makes a reconcile by absence a reindex); indexing it under the new id leaves the old model's rows where they are.
        var post = new SearchablePost("old-0", DigestOf("old-0"), "postgres", PostKind.Question, "title", "body", [], "https://agents.example/a", 1);
        Assert.DoesNotContain(post.Digest, Req(await index.IndexedDigestsAsync(New, Ct)));
        Req(await new EmbeddingIndexer(new BgeShapedEmbedder(), index).IndexAsync(post, Ct));
        Assert.Contains(post.Digest, Req(await index.IndexedDigestsAsync(New, Ct)));
        Assert.Equal((3L, 4L), (Req(await index.CountAsync(Old, Ct)), Req(await index.CountAsync(New, Ct))));

        await using var connection = await fixture.AppRoleDataSource.OpenConnectionAsync(Ct);
#pragma warning disable CA2100 // the schema name is generated by the fixture and the model id is a parameter
        await using var delete = new NpgsqlCommand($"DELETE FROM \"{schema}\".post_embeddings WHERE model = @model;", connection);
#pragma warning restore CA2100
        delete.Parameters.AddWithValue("model", Old.Id);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(Ct));
        Assert.Equal("42501", refused.SqlState);
        Assert.Equal(3L, Req(await index.CountAsync(Old, Ct)));
    }
}
