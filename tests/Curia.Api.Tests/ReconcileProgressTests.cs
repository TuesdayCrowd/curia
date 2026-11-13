using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Api.Adapters;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G65 (D10a), SEM47: the first start under a new model id reindexes the whole log before the Forum serves, and says how far it is (Amendment 1, ruling 24). Over a 2,500-post log and the
/// hashed adapter as the embedder and the Forum's own Postgres event store as the log (the posts are appended through it, one aggregate each), the reconcile logs <c>Reconciling 1000 of 2500</c> and <c>Reconciling 2000 of 2500</c> and one last line with the count, the elapsed time and the rate, and
/// nothing else at the information level; no line holds a post's title, body, digest or a path.
///
/// <para>The half of the plan's SEM47 that asks a request at the first progress line to be refused needs a listener the in-process host does not have: it is Task 0 M7 and M9's measurement (the port
/// refused for 48 of 48 polls over a 2,000-post reconcile) and Task 7's L1, and is recorded as such in the report.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ReconcileProgressTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const int Posts = 2500;
    private const string Canary = "CANARYTEXT7731";

    private sealed class Capture<T> : ILogger<T>
    {
        internal List<(LogLevel Level, string Message)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class DroppingIndex : IVectorIndex
    {
        public Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IndexedVector>.Ok(new IndexedVector(digest, postId, sequence)));

        public Task<Result<ImmutableArray<VectorMatch>>> NearestAsync(Embedding query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ImmutableArray<VectorMatch>>.Ok([]));

        public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => Task.FromResult(Result<long>.Ok(0));

        public Task<Result<IReadOnlySet<string>>> IndexedDigestsAsync(EmbeddingModel model, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlySet<string>>.Ok(new HashSet<string>()));
    }

    private static T Req<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    /// <summary>Appends the posts through the Forum's own event store, one aggregate each, so the reconcile reads a real log.</summary>
    private static async Task SeedAsync(IEventStore store, CancellationToken ct)
    {
        var stamp = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        for (var seq = 1; seq <= Posts; seq++)
        {
            var postId = $"progress-{seq:D5}";
            var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
            members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
            members.Add(new("kind", new JsonValue.String("question")));
            members.Add(new("author", new JsonValue.String("https://agents.example/a")));
            members.Add(new("board", new JsonValue.String("postgres")));
            members.Add(new("title", new JsonValue.String($"{Canary} title {seq}")));
            members.Add(new("body", new JsonValue.String($"{Canary} body {seq}")));
            members.Add(new("code_blocks", new JsonValue.Array([])));
            members.Add(new("refs", new JsonValue.Array([])));
            members.Add(new("tags", new JsonValue.Array([])));
            members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
            members.Add(new("created_at", new JsonValue.String(stamp.ToString("o", CultureInfo.InvariantCulture))));
            members.Add(new("nonce", new JsonValue.String("00112233445566778899aabbccddeeff")));
            var canonical = System.Text.Encoding.UTF8.GetString(Req(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(members.ToImmutable()))).Span);
            var digest = "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(postId)));
            var payload = new JsonValue.Object(
            [
                new("post_id", new JsonValue.String(postId)),
                new("canonical", new JsonValue.String(canonical)),
                new("signature", new JsonValue.String("sig")),
                new("digest", new JsonValue.String(digest)),
                new("author", new JsonValue.String("https://agents.example/a")),
                new("board", new JsonValue.String("postgres")),
                new("kind", new JsonValue.String("question")),
            ]);
            Req(await store.AppendAsync(
                Req(AggregateId.Create(postId)),
                AggregateVersion.New,
                [new DomainEvent(Req(Curia.Domain.EventId.Create(postId)), Req(EventType.Create(PostProjector.PostAcceptedType)), Req(ActorId.Create("https://agents.example/a")), payload)],
                ct));
        }
    }

    /// <summary>
    /// SEM47. Predicted red on the seam: <c>the information lines were [Vector index reconciled under hashed-ngram@1: 2500 post(s) embedded at startup], expected the two progress lines and one last line
    /// with the count, the elapsed time and the rate</c>.
    /// </summary>
    [Fact]
    public async Task R11_10_SEM47_TheReconcileSaysHowFarItIsAndRevealsNoPostText()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = forum.Services.GetRequiredService<IEventStore>();
        await SeedAsync(store, ct);

        var log = new Capture<EmbeddingReconcileService>();
        var start = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var service = new EmbeddingReconcileService(
            new EmbeddingIndexer(new HashedNGramEmbedder(), new DroppingIndex()), store, new IndexSweep(start), TimeProvider.System, log);

        await service.StartAsync(ct);

        var information = log.Lines.Where(l => l.Level == LogLevel.Information).Select(l => l.Message).ToList();
        var shape = string.Join(" | ", information);
        Assert.True(
            information.Contains("Reconciling 1000 of 2500", StringComparer.Ordinal) && information.Contains("Reconciling 2000 of 2500", StringComparer.Ordinal),
            $"the information lines were [{shape}], expected the two progress lines and one last line with the count, the elapsed time and the rate");
        Assert.True(information.Count == 3, $"expected exactly three information lines (two progress lines and the last), saw {information.Count}: [{shape}]");
        Assert.Matches(@"^Vector index reconciled under hashed-ngram@1: 2500 post\(s\) embedded in [0-9.]+ s \([0-9.]+ per second\)", information[^1]);

        foreach (var (_, message) in log.Lines)
        {
            Assert.DoesNotContain(Canary, message, StringComparison.Ordinal);
            Assert.DoesNotContain("sha256:", message, StringComparison.Ordinal);
            Assert.DoesNotContain("progress-", message, StringComparison.Ordinal);
            Assert.DoesNotContain(Path.GetTempPath(), message, StringComparison.Ordinal);
        }
    }
}
