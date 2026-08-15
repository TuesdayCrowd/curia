using System.Collections.Concurrent;
using Curia.AuthN.Ports;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Tests.InMemory;

/// <summary>
/// The R11.4 in-memory adapter for <see cref="IReplayCache"/>: a real, if non-durable,
/// implementation with its own tests (<see cref="InMemoryReplayCacheTests"/>) -- mirrors
/// <c>Curia.Application.Tests.InMemory.InMemoryEventStore</c>'s placement and "first-class
/// implementation, not a mock" framing (CS-16).
///
/// R5.17's atomicity is the entire reason this uses
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> rather than a separate
/// <c>ContainsKey</c> check followed by an <c>Add</c>: <c>TryAdd</c> is a single atomic
/// compare-and-set, so two callers racing on the same <c>jti</c> can never both observe "not
/// present yet" and both proceed -- exactly the check-then-insert race R5.17 names. A production
/// Redis-backed adapter would use <c>SET NX PX</c> for the same single-round-trip atomicity;
/// there is no eviction sweep here for expired entries because nothing in this test suite reads
/// past a fake clock's simulated horizon far enough to need one.
/// </summary>
internal sealed class InMemoryReplayCache : IReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public Task<Result<bool>> TryInsertAsync(string jti, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<bool>.Ok(_seen.TryAdd(jti, expiresAt)));

    /// <summary>Test-only introspection: whether <paramref name="jti"/> has been recorded.</summary>
    public bool Contains(string jti) => _seen.ContainsKey(jti);
}
