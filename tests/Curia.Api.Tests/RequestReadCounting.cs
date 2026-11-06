using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Curia.Api.Tests;

/// <summary>The log reads a request made, counted by the tests that hold a route to a number of reads.</summary>
internal sealed class Counter
{
    internal int Count { get; set; }
}

/// <summary>
/// Counts a read only inside a request. <c>EmbeddingCatchUpService</c> takes the same <see cref="IEventReader"/> from the container and reads on its own tick, so a counter that
/// took every read could count one the request did not make; <c>ResendAtASpentBudgetTests</c> ASA37 failed that way once in a whole run.
/// </summary>
internal sealed class CountingReader(IEventReader inner, Counter counter, IHttpContextAccessor requests) : IEventReader
{
    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default)
    {
        Count();
        return inner.ReadByAggregateAsync(aggregateId, cancellationToken);
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
    {
        Count();
        return inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }

    private void Count()
    {
        if (requests.HttpContext is not null)
            counter.Count++;
    }
}

internal static class RequestReadCounting
{
    /// <summary>Replaces the host's reader with one that counts the reads requests make.</summary>
    internal static IServiceCollection CountRequestReads(this IServiceCollection services, Counter counter)
    {
        services.AddHttpContextAccessor();
        return services.AddSingleton<IEventReader>(sp => new CountingReader(sp.GetRequiredService<IEventStore>(), counter, sp.GetRequiredService<IHttpContextAccessor>()));
    }
}
