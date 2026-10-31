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

    [LoggerMessage(EventId = 4, EventName = "VectorIndexCatchUpFailed", Level = LogLevel.Error,
        Message = "A catch-up pass failed and the loop continues: {FaultType}: {FaultDetail}")]
    internal static partial void PassFailed(ILogger logger, string faultType, string faultDetail);
}
