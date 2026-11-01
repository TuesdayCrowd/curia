using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// Errata G51 (R8.81, R11.69) against real pgvector, with the faults a production database can make and a fake cannot (the D92 PR, rulings 13 and 14):
/// a grant revoked (a real <c>PostgresException</c>, 42501), a <c>SHARE</c> lock held on <c>post_embeddings</c> from a separate admin connection (it blocks
/// the upsert's <c>ROW EXCLUSIVE</c> and leaves the near-duplicate read alone), and a command timeout shorter than the lock (an <c>NpgsqlException</c>, the
/// exception the adapter's own <c>catch</c> lets through). PIL16, PIL34, PIL35 and PIL36. The oracle is a row count by digest from the admin connection;
/// the bound is read from the entry. These facts run on the system clock because the object under test is a real database wait; the fake-clock bound is PIL6.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage(
    "Security",
    "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "Every interpolated identifier is a schema the fixture generated from a literal prefix and a GUID, or the fixture's own role name; the digest is a parameter.")]
[SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "A probe records what the code under test threw, whatever it threw: that is the observation, and the red text names its type.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class CommittedIndexInfrastructureTests(PostgresDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Start = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The upper edge of PIL34's and PIL36's windows is the bound plus this (controller's ruling 2 of the build brief): a loaded CI machine delays a real
    /// database wait, and the lower edge (PIL36: the bound less 0.2 s) is what keeps a step that returned early red.
    /// </summary>
    private static readonly TimeSpan CiLoad = TimeSpan.FromSeconds(5);

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    /// <summary>The pieces over one events schema and one retrieval schema, with the index reached through a data source whose command timeout the fact chooses.</summary>
    private sealed class Rig(string vectorSchema, PostgresEventStore store, IndexBacklog backlog, EmbeddingIndexer indexer, CommittedPostIndexer committed, EmbeddingCatchUp catchUp, NpgsqlDataSource? own) : IAsyncDisposable
    {
        internal string VectorSchema { get; } = vectorSchema;

        internal PostgresEventStore Store { get; } = store;

        internal IndexBacklog Backlog { get; } = backlog;

        internal EmbeddingIndexer Indexer { get; } = indexer;

        internal CommittedPostIndexer Committed { get; } = committed;

        internal EmbeddingCatchUp CatchUp { get; } = catchUp;

        public async ValueTask DisposeAsync()
        {
            if (own is not null) await own.DisposeAsync();
        }
    }

    private async Task<Rig> NewRigAsync(int? commandTimeoutSeconds)
    {
        var vectorSchema = await fixture.CreateIsolatedRetrievalSchemaAsync(Ct);
        var eventsSchema = await fixture.CreateIsolatedSchemaAsync(Ct);
        NpgsqlDataSource? own = null;
        var source = fixture.AppRoleDataSource;
        if (commandTimeoutSeconds is { } seconds)
        {
            var builder = new NpgsqlConnectionStringBuilder(fixture.AppRoleConnectionString) { CommandTimeout = seconds, Pooling = false };
            own = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
            source = own;
        }

        var index = new PostgresVectorIndex(source, TimeProvider.System, vectorSchema);
        var indexer = new EmbeddingIndexer(new HashedNGramEmbedder(), index);
        var backlog = new IndexBacklog();
        var sweep = new IndexSweep(TimeProvider.System.GetUtcNow());
        var store = new PostgresEventStore(fixture.AppRoleDataSource, TimeProvider.System, fixture.PermissiveLifecycleGate(), eventsSchema);
        return new Rig(vectorSchema, store, backlog, indexer, new CommittedPostIndexer(indexer, backlog, TimeProvider.System, sweep), new EmbeddingCatchUp(indexer, backlog, sweep), own);
    }

    /// <summary>Appends a discussion post to the rig's log and returns it as the route hands it to the indexer, without indexing it.</summary>
    private static async Task<SearchablePost> AppendAsync(Rig rig)
    {
        var id = "01" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();
        var author = "https://agents.example/author";
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(PostKind.Question))));
        members.Add(new("author", new JsonValue.String(author)));
        members.Add(new("board", new JsonValue.String("board-" + id)));
        members.Add(new("title", new JsonValue.String("About " + id)));
        members.Add(new("body", new JsonValue.String("A question about quokka habitats " + id)));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([new JsonValue.String("jcs")])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(Start.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));
        var canonical = System.Text.Encoding.UTF8.GetString(Require(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable()))).Span);
        var digest = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(id)));

        var appended = Require(await rig.Store.AppendAsync(
            Require(AggregateId.Create(id)),
            AggregateVersion.New,
            [new DomainEvent(
                Require(EventId.Create(id)),
                Require(EventType.Create(PostProjector.PostAcceptedType)),
                Require(ActorId.Create(author)),
                new JsonValue.Object(
                [
                    new("post_id", new JsonValue.String(id)),
                    new("canonical", new JsonValue.String(canonical)),
                    new("signature", new JsonValue.String("sig")),
                    new("digest", new JsonValue.String(digest)),
                    new("author", new JsonValue.String(author)),
                    new("board", new JsonValue.String("board-" + id)),
                    new("kind", new JsonValue.String("question")),
                ]))],
            Ct));
        return SearchProjector.TryRead(appended[0]) ?? throw new InvalidOperationException("the appended event is not a searchable post");
    }

    private async Task<long> RowsAsync(Rig rig, string digest)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{rig.VectorSchema}\".post_embeddings WHERE digest = @digest;", connection);
        command.Parameters.AddWithValue("digest", digest);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }

    private string AppRole() => new NpgsqlConnectionStringBuilder(fixture.AppRoleConnectionString).Username!;

    /// <summary>A transaction on a separate admin connection that holds <c>SHARE</c> on the rig's <c>post_embeddings</c> until released or until <paramref name="holdFor"/> has passed.</summary>
    private async Task<LockHold> HoldShareAsync(Rig rig, TimeSpan holdFor)
    {
        var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        var transaction = await connection.BeginTransactionAsync(Ct);
        await using (var command = new NpgsqlCommand($"LOCK TABLE \"{rig.VectorSchema}\".post_embeddings IN SHARE MODE;", connection, transaction))
            await command.ExecuteNonQueryAsync(Ct);

        return new LockHold(connection, transaction, holdFor);
    }

    private sealed class LockHold : IAsyncDisposable
    {
        private readonly NpgsqlConnection _connection;
        private readonly NpgsqlTransaction _transaction;
        private readonly CancellationTokenSource _release = new();
        private readonly Task _holding;

        internal LockHold(NpgsqlConnection connection, NpgsqlTransaction transaction, TimeSpan holdFor)
        {
            _connection = connection;
            _transaction = transaction;
            _holding = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(holdFor, _release.Token);
                }
                catch (OperationCanceledException)
                {
                }

                await _transaction.RollbackAsync(CancellationToken.None);
            });
        }

        internal Task ReleasedAsync() => _holding;

        public async ValueTask DisposeAsync()
        {
            await _release.CancelAsync();
            await _holding;
            await _transaction.DisposeAsync();
            await _connection.DisposeAsync();
            _release.Dispose();
        }
    }

    /// <summary>
    /// PIL16. Two repairers store one post: no error, one row. A control on the seam (the upsert is idempotent already, and the catch-ups join it once they
    /// store anything); FPIL16-style mutations of the upsert make it red.
    /// </summary>
    [Fact]
    public async Task R11_69_PIL16_TwoRepairersStoringOnePostLeaveOneRowAndNoError()
    {
        await using var rig = await NewRigAsync(null);
        var post = await AppendAsync(rig);
        var other = new EmbeddingIndexer(new HashedNGramEmbedder(), new PostgresVectorIndex(fixture.AppRoleDataSource, TimeProvider.System, rig.VectorSchema));

        var both = await Task.WhenAll(rig.Indexer.IndexAsync(post, Ct), other.IndexAsync(post, Ct));
        _ = await rig.CatchUp.RunOnceAsync(rig.Store, DateTimeOffset.UtcNow.AddMinutes(1), Ct);

        Said(
            "2 stores without error; 1 row",
            $"{both.Count(r => r.TryGetValue(out _, out _))} stores without error; {await RowsAsync(rig, post.Digest)} row");
    }

    /// <summary>
    /// PIL34. The exception arm against the real adapter: a command timeout of one second, shorter than the published bound, against a held <c>SHARE</c> lock.
    /// The upsert throws an <c>NpgsqlException</c> (not a <c>PostgresException</c>, which the adapter's own <c>catch</c> would have turned into a result); the
    /// indexer returns pending, does not throw, and names that type, so the exception and not the bound ended the step (ruling 14).
    /// </summary>
    [Fact]
    public async Task R8_81_PIL34_AnExceptionThatIsNotAPostgresExceptionIsPendingAndNeverThrown()
    {
        var bound = PublishedIndexLag.StepBound();
        await using var rig = await NewRigAsync(1);
        var post = await AppendAsync(rig);
        await using var held = await HoldShareAsync(rig, TimeSpan.FromSeconds(8));

        var timer = Stopwatch.StartNew();
        string what;
        try
        {
            var outcome = await rig.Committed.IndexAsync(post, Ct);
            what = $"returned {outcome.State} {(timer.Elapsed < bound + CiLoad ? "within the window" : "outside the window")}, naming {outcome.FaultType}";
        }
        catch (Exception ex)
        {
            what = $"threw {ex.GetType().Name} {(timer.Elapsed < bound + CiLoad ? "within the window" : "outside the window")}";
        }

        Said("returned Pending within the window, naming NpgsqlException", what.Replace("Npgsql.NpgsqlException", "NpgsqlException", StringComparison.Ordinal));
    }

    /// <summary>
    /// PIL35. The real adapter with a revoked grant: the upsert fails with 42501, the outcome is pending and the backlog holds the post; the grant restored,
    /// one catch-up pass stores it. The grant is restored whatever the fact finds.
    /// </summary>
    [Fact]
    public async Task R8_81_PIL35_ARevokedGrantIsPendingAndOneCatchUpPassStoresItOnceRestored()
    {
        var wait = PublishedIndexLag.FirstWait();
        await using var rig = await NewRigAsync(null);
        var post = await AppendAsync(rig);
        var role = AppRole();

        string observed;
        await using (var admin = await fixture.AdminDataSource.OpenConnectionAsync(Ct))
        {
            await using (var revoke = new NpgsqlCommand($"REVOKE INSERT ON \"{rig.VectorSchema}\".post_embeddings FROM \"{role}\";", admin))
                await revoke.ExecuteNonQueryAsync(Ct);
            try
            {
                var outcome = await rig.Committed.IndexAsync(post, Ct);
                observed = $"{outcome.State} with {rig.Backlog.Count} held and {await RowsAsync(rig, post.Digest)} rows";
            }
            catch (Exception ex)
            {
                observed = $"threw {ex.GetType().Name}";
            }
            finally
            {
                await using var grant = new NpgsqlCommand($"GRANT INSERT ON \"{rig.VectorSchema}\".post_embeddings TO \"{role}\";", admin);
                await grant.ExecuteNonQueryAsync(Ct);
            }
        }

        // The failure's instant is the committed indexer's clock reading inside the call, so the retry's instant is taken after it (the first draft took it before the revoke, a few milliseconds early).
        var pass = await rig.CatchUp.RunOnceAsync(rig.Store, DateTimeOffset.UtcNow + wait, Ct);
        Said(
            "Pending with 1 held and 0 rows; after the grant is restored one pass stores 1 and the table holds 1 row",
            $"{observed}; after the grant is restored one pass stores {pass.StoredDigests.Count} and the table holds {await RowsAsync(rig, post.Digest)} row");
    }

    /// <summary>
    /// PIL36. The real adapter with a held lock: <c>SHARE</c> on <c>post_embeddings</c> for eight seconds and a command timeout of thirty, so the bound and not the
    /// timeout ends the step (ruling 13): the outcome is pending between a little under the bound and a little over it, not after the lock; the lock released,
    /// one catch-up pass stores the post. <c>ACCESS EXCLUSIVE</c> is not used: it blocks the near-duplicate read before the append, where the bound does not apply.
    /// </summary>
    [Fact]
    public async Task R8_81_PIL36_AHeldLockEndsAtTheBoundAndNotAtTheLocksLength()
    {
        var bound = PublishedIndexLag.StepBound();
        var wait = PublishedIndexLag.FirstWait();
        await using var rig = await NewRigAsync(30);
        var post = await AppendAsync(rig);
        var failedAt = DateTimeOffset.UtcNow;
        await using var held = await HoldShareAsync(rig, TimeSpan.FromSeconds(8));

        var timer = Stopwatch.StartNew();
        string what;
        try
        {
            var outcome = await rig.Committed.IndexAsync(post, Ct);
            var seconds = timer.Elapsed.TotalSeconds;
            var inWindow = seconds >= bound.TotalSeconds - 0.2 && seconds <= bound.TotalSeconds + CiLoad.TotalSeconds;
            what = $"{outcome.State} {(inWindow ? "at the bound" : $"after {seconds:0.0} s, which is outside the bound's window")}";
        }
        catch (Exception ex)
        {
            what = $"threw {ex.GetType().Name}";
        }

        await held.ReleasedAsync();
        var pass = await rig.CatchUp.RunOnceAsync(rig.Store, failedAt + wait + TimeSpan.FromSeconds(10), Ct);
        Said(
            "Pending at the bound; the lock released, one pass stores 1 and the table holds 1 row",
            $"{what}; the lock released, one pass stores {pass.StoredDigests.Count} and the table holds {await RowsAsync(rig, post.Digest)} row");
    }
}
