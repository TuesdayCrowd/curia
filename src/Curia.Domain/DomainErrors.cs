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
    /// The problem type <see cref="ConcurrencyConflict"/> reports, named so a caller can recognize
    /// the condition without reconstructing the whole <see cref="Error"/> -- its detail carries the
    /// aggregate id and both versions, which a caller deciding "was this a version race?" does not
    /// have and should not have to guess at.
    /// </summary>
    public const string ConcurrencyConflictType = "curia/domain/concurrency-conflict";

    /// <summary>
    /// R4.38 (add.): the problem type the event store answers when an append carries a lifecycle kind
    /// its gate does not enable. The append writes nothing (spec section 4.13).
    /// </summary>
    public const string LifecycleAppendRefusedType = "curia/domain/lifecycle-append-refused";

    /// <summary>
    /// R4.38 (add.): an append carrying a lifecycle kind the store's gate does not enable. The detail
    /// names the kind (<c>rotation</c>, <c>retirement</c> or <c>compromise</c>) and nothing of the
    /// append's payload, stream or caller (spec section 4.13).
    /// </summary>
    public static Error LifecycleAppendRefused(string kind) => new(
        LifecycleAppendRefusedType,
        "The append carries a lifecycle kind the guard does not enable",
        $"kind={kind}");

    /// <summary>
    /// R11.6/CS-15's optimistic-concurrency failure: the caller's belief about how many events
    /// an aggregate already has does not match the store's. A <see cref="Result{T}"/> failure,
    /// never an exception and never a silent overwrite (per Stage 1's brief).
    /// </summary>
    public static Error ConcurrencyConflict(AggregateId aggregateId, AggregateVersion expected, AggregateVersion actual) => new(
        ConcurrencyConflictType,
        "Append targeted an aggregate at an unexpected version",
        $"aggregate={aggregateId.Value} expected={expected.Value.ToString(CultureInfo.InvariantCulture)} actual={actual.Value.ToString(CultureInfo.InvariantCulture)}");
}
