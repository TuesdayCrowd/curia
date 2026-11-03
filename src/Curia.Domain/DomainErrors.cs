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

    /// <summary>The problem type <see cref="ConditionConflict"/> reports (R6.56).</summary>
    public const string ConditionConflictType = "curia/domain/condition-conflict";

    /// <summary>
    /// R6.56: an append conditioned on another stream's version found that stream elsewhere. The detail
    /// names the condition's aggregate and both versions, as <see cref="ConcurrencyConflict"/> does.
    /// </summary>
    public static Error ConditionConflict(AggregateId conditionAggregate, AggregateVersion expected, AggregateVersion actual) => new(
        ConditionConflictType,
        "Append was conditioned on a stream that has moved",
        $"aggregate={conditionAggregate.Value} expected={expected.Value.ToString(CultureInfo.InvariantCulture)} actual={actual.Value.ToString(CultureInfo.InvariantCulture)}");

    /// <summary>R6.60 (errata G57): the problem type a guarded append returns for <c>Redecide</c>. No response carries it.</summary>
    public const string AppendRedecideType = "curia/append/redecide";

    /// <summary>R6.60: an append whose guard asked the caller to decide again from a fresh read; nothing was written.</summary>
    public static Error AppendRedecide() => new(
        AppendRedecideType,
        "The append must be decided again on a fresh read; nothing was recorded");

    /// <summary>R6.60 (errata G57): the problem type a guarded append returns for a delta over the cap.</summary>
    public const string AppendDeltaTooLargeType = "curia/append/delta-too-large";

    /// <summary>R6.60: a guarded append whose delta (the events appended since the caller's read) is over the cap; the delta was not read and nothing was written.</summary>
    public static Error AppendDeltaTooLarge() => new(
        AppendDeltaTooLargeType,
        "The log moved by more than 10,000 events while the request waited; nothing was recorded");

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
