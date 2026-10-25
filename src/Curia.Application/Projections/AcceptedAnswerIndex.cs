using System.Collections.Immutable;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Serving;

namespace Curia.Application.Projections;

/// <summary>
/// R9.36 (errata G36): which answer stands as each question's accepted one, defined once, so the <c>accepted_answer</c> member of a
/// question and the <c>accepted</c> flag of the answer it names cannot answer differently. It folds the log's acceptances
/// (<see cref="AcceptanceProjector"/>) against the post read model and is built once per request, beside <see cref="CorrectionIndex"/>.
/// </summary>
public sealed class AcceptedAnswerIndex
{
    private readonly ImmutableDictionary<string, string> _acceptedByThread;
    private readonly ImmutableDictionary<string, PostView> _postsById;
    private readonly Func<string, bool> _servable;

    private AcceptedAnswerIndex(
        ImmutableDictionary<string, string> acceptedByThread,
        ImmutableDictionary<string, PostView> postsById,
        Func<string, bool> servable)
    {
        _acceptedByThread = acceptedByThread;
        _postsById = postsById;
        _servable = servable;
    }

    /// <summary>An index that names nothing. For the falsification rows; a caller that builds a response must pass an index it folded.</summary>
    public static AcceptedAnswerIndex Empty { get; } = new(
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, PostView>.Empty.WithComparers(StringComparer.Ordinal),
        static _ => false);

    /// <summary>The index of a request's log.</summary>
    /// <param name="log">The event log, in seq order.</param>
    /// <param name="posts">The post read model, in seq order.</param>
    /// <param name="servable">R10.36's serving filter, by post id.</param>
    public static AcceptedAnswerIndex Fold(IReadOnlyList<AppendedEvent> log, ImmutableArray<PostView> posts, Func<string, bool> servable)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(servable);

        var byId = new Dictionary<string, PostView>(StringComparer.Ordinal);
        foreach (var post in posts.IsDefault ? [] : posts) byId[post.PostId] = post;

        return new AcceptedAnswerIndex(AcceptanceProjector.Fold(log), byId.ToImmutableDictionary(StringComparer.Ordinal), servable);
    }

    /// <summary>
    /// The standing acceptance of a question's thread: the answer's digest, and its post id only where the Forum serves it
    /// (R10.36, as <see cref="CorrectionIndex"/> does for a successor), or none when no acceptance stands or the model does not
    /// hold the answer it names (a replay of a log this code did not write must not throw).
    /// </summary>
    public AcceptedAnswerMember StandingOf(PostView question)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (!_acceptedByThread.TryGetValue(question.PostId, out var answerId)) return AcceptedAnswerMember.None;
        if (!_postsById.TryGetValue(answerId, out var answer)) return AcceptedAnswerMember.None;

        var served = PostKinds.TryParse(answer.Kind, out var kind) && PostKinds.IsServedToReaders(kind) && _servable(answer.PostId);
        return new AcceptedAnswerMember(answer.Digest, served ? answer.PostId : null);
    }

    /// <summary>Whether <paramref name="answer"/> is the answer its thread's standing acceptance names: the rule <see cref="StandingOf"/> shares.</summary>
    public bool IsAccepted(PostView answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        return answer.Parent is { } parent
            && _acceptedByThread.TryGetValue(parent, out var accepted)
            && string.Equals(accepted, answer.PostId, StringComparison.Ordinal);
    }
}
