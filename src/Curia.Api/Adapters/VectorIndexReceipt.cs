using Curia.Application.Retrieval;

namespace Curia.Api.Adapters;

/// <summary>R8.81: the three values of a receipt's <c>vector_index</c> member, spelled as the entry spells them.</summary>
internal static class VectorIndexWire
{
    internal const string Indexed = "indexed";
    internal const string Pending = "pending";
    internal const string NotApplicable = "not_applicable";

    internal static string Of(CommittedIndexState state) => state switch
    {
        CommittedIndexState.Indexed => Indexed,
        CommittedIndexState.Pending => Pending,
        CommittedIndexState.NotApplicable => NotApplicable,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "An index state this Forum does not state."),
    };
}

/// <summary>R8.81, R11.69: the log lines of the vector step. They carry a post id, a digest, an error's type and its detail and never any text a post carried (R11.33).</summary>
internal static partial class VectorIndexLog
{
    [LoggerMessage(EventId = 2, EventName = "VectorIndexPending", Level = LogLevel.Warning,
        Message = "The log holds post {PostId} and its vector is not stored; it is pending and will be retried from the log ({FaultType}: {FaultDetail})")]
    internal static partial void Pending(ILogger logger, string postId, string faultType, string faultDetail);

    [LoggerMessage(EventId = 3, EventName = "VectorIndexCaughtUp", Level = LogLevel.Information,
        Message = "The catch-up stored the vector of {Digest}, a post that was pending")]
    internal static partial void CaughtUp(ILogger logger, string digest);

    [LoggerMessage(EventId = 5, EventName = "VectorIndexRetryFailed", Level = LogLevel.Warning,
        Message = "The catch-up could not store the vector of post {PostId} ({Digest}); it stays pending ({FaultType}: {FaultDetail})")]
    internal static partial void RetryFailed(ILogger logger, string postId, string digest, string faultType, string faultDetail);

    [LoggerMessage(EventId = 6, EventName = "VectorIndexCheckOwed", Level = LogLevel.Warning,
        Message = "A discussion post's append left the route by an exception, so the log may hold a post no answer reported; a check of the log against the vector index is owed ({FaultType})")]
    internal static partial void CheckOwed(ILogger logger, string faultType);

    [LoggerMessage(EventId = 7, EventName = "VectorIndexCheckFound", Level = LogLevel.Information,
        Message = "The check of the log against the vector index that began at {Began:o} enrolled {Enrolled} post(s) the index lacked")]
    internal static partial void CheckFound(ILogger logger, int enrolled, DateTimeOffset began);

    [LoggerMessage(EventId = 8, EventName = "VectorIndexCheckFailed", Level = LogLevel.Warning,
        Message = "The check of the log against the vector index could not complete and will be attempted again ({FaultType}: {FaultDetail})")]
    internal static partial void CheckFailed(ILogger logger, string faultType, string faultDetail);

    [LoggerMessage(EventId = 4, EventName = "VectorIndexCatchUpFailed", Level = LogLevel.Error,
        Message = "A catch-up pass failed and the loop continues: {FaultType}: {FaultDetail}")]
    internal static partial void PassFailed(ILogger logger, string faultType, string faultDetail);
}

/// <summary>
/// R8.86 and R10.94 (errata G57): the log lines of the append guard, ids 5006 and 5007, as R8.86 words and R10.94 words publish them. The first carries the post's kind and
/// the refusal's type and never a digest, a post id or any text a post carried; the second carries a pass count and nothing else.
/// </summary>
internal static partial class AppendGuardLog
{
    [LoggerMessage(EventId = 5006, EventName = "AppendGuardRefused", Level = LogLevel.Warning,
        Message = "A {Kind} post's append was refused inside the log's lock by a check the route had made on an older read ({RefusalType}); nothing was stored")]
    internal static partial void Refused(ILogger logger, string kind, string refusalType);

    /// <summary>R10.94 (errata G57): a flag's append found a flag of its kind appended since its read, id 5007; carries neither the post nor the raiser.</summary>
    [LoggerMessage(EventId = 5007, EventName = "AppendGuardRedecided", Level = LogLevel.Information,
        Message = "A flag's append found a flag of its kind appended since the log it was decided on; it was decided again (pass {Pass} of {Passes})")]
    internal static partial void Redecided(ILogger logger, int pass, int passes);
}

/// <summary>
/// R8.84 (errata G55): the log lines of the confirm of a faulted append, ids 5004 and 5005 as R8.84 words publishes them. They carry the post's kind, the exception's type
/// and the confirm's outcome and never any text a post carried; the confirmed line carries the exception itself.
/// </summary>
internal static partial class AppendConfirmLog
{
    [LoggerMessage(EventId = 5004, EventName = "AppendConfirmed", Level = LogLevel.Warning,
        Message = "A {Kind} post's append left the route by an exception and the log holds the post; it was answered from the log ({FaultType})")]
    internal static partial void Confirmed(ILogger logger, Exception exception, string kind, string faultType);

    [LoggerMessage(EventId = 5005, EventName = "AppendNotConfirmed", Level = LogLevel.Warning,
        Message = "A {Kind} post's append left the route by an exception and a read of the log made after it did not confirm the post ({FaultType}; {Outcome}); the original fault is answered")]
    internal static partial void NotConfirmed(ILogger logger, string kind, string faultType, string outcome);
}
