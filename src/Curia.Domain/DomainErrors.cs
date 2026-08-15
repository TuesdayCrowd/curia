using System.Globalization;
using Curia.Domain.Primitives;

namespace Curia.Domain;

/// <summary>
/// RFC 9457 problem-type slugs for the event model, mirroring <c>Curia.Canon.CanonErrors</c>'
/// one-factory-per-condition shape so every domain rejection names the rule it enforces.
/// </summary>
public static class DomainErrors
{
    public static Error EmptyIdentifier(string kind) => new(
        "curia/domain/empty-identifier",
        $"{kind} must not be empty or all whitespace",
        kind);

    public static Error NegativeSequence(long value) => new(
        "curia/domain/negative-sequence",
        "An event sequence number cannot be negative",
        value.ToString(CultureInfo.InvariantCulture));

    public static Error NegativeVersion(long value) => new(
        "curia/domain/negative-version",
        "An aggregate version cannot be negative",
        value.ToString(CultureInfo.InvariantCulture));

    public static Error EmptyAppendBatch() => new(
        "curia/domain/empty-append-batch",
        "Append requires at least one event");

    /// <summary>
    /// R11.6/CS-15's optimistic-concurrency failure: the caller's belief about how many events
    /// an aggregate already has does not match the store's. A <see cref="Result{T}"/> failure,
    /// never an exception and never a silent overwrite (per Stage 1's brief).
    /// </summary>
    public static Error ConcurrencyConflict(AggregateId aggregateId, AggregateVersion expected, AggregateVersion actual) => new(
        "curia/domain/concurrency-conflict",
        "Append targeted an aggregate at an unexpected version",
        $"aggregate={aggregateId.Value} expected={expected.Value.ToString(CultureInfo.InvariantCulture)} actual={actual.Value.ToString(CultureInfo.InvariantCulture)}");
}
