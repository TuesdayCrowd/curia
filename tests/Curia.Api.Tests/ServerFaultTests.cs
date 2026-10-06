using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R11.33 (errata G17): a 5xx problem carries its type and title, and its detail goes to the log.
///
/// <para><b>What this closes.</b> The vector index folded Postgres's own error text into the problem
/// an anonymous search received, and a text whose features cancelled answered <c>503</c> with
/// <c>"detail":"22000: NaN not allowed in vector"</c> (register D25, found beside D24). The index
/// still says what failed; the boundary that serves a server fault no longer passes it on.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ServerFaultTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    /// <summary>Postgres's words, as the vector index folded them into its refusal.</summary>
    private const string BackendText = "22000: NaN not allowed in vector";

    [Fact]
    public async Task R11_33_AnAnonymousSearchTheIndexFailsIsServedWithoutTheBackendsWords()
    {
        var ct = TestContext.Current.CancellationToken;
        var logged = new ConcurrentQueue<string>();

        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IVectorIndex>(sp => new FailingNearest(sp.GetRequiredService<Curia.Infrastructure.PostgresAdapters>().VectorIndex));
            services.AddSingleton<ILoggerProvider>(_ => new Captured(logged));
        }));
        using var client = host.CreateClient();

        using var response = await client.GetAsync(new Uri("/v1/search?q=anything", UriKind.Relative), ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(
            "503 {\"type\":\"curia/retrieval/index-unavailable\",\"title\":\"The vector index could not be queried\",\"detail\":null}",
            $"{(int)response.StatusCode} {body}");

        // And the words are where an operator reads: withheld from the caller, not lost.
        Assert.Contains(logged, line => line.Contains(BackendText, StringComparison.Ordinal));
    }

    /// <summary>The host's own index, except that a nearest-neighbour query fails as Postgres failed it under D24.</summary>
    private sealed class FailingNearest(IVectorIndex inner) : IVectorIndex
    {
        public Task<Result<IndexedVector>> UpsertAsync(
            string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default) =>
            inner.UpsertAsync(digest, postId, sequence, embedding, cancellationToken);

        public Task<Result<ImmutableArray<VectorMatch>>> NearestAsync(
            Embedding query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ImmutableArray<VectorMatch>>.Fail(RetrievalErrors.IndexUnavailable(BackendText)));

        public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) =>
            inner.CountAsync(model, cancellationToken);

        public Task<Result<long>> MaxSequenceAsync(EmbeddingModel model, CancellationToken cancellationToken = default) =>
            inner.MaxSequenceAsync(model, cancellationToken);
    }

    /// <summary>Every log line the host writes, formatted.</summary>
    private sealed class Captured(ConcurrentQueue<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue(formatter(state, exception));
        }
    }
}
