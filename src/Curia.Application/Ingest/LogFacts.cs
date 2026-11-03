using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Ingest;

/// <summary>
/// Errata G57, ruling 20: what a request's checks read from the log it decided on -- the posts, the moderation state, and the sequence of the last event of the
/// list they were folded from -- as a value a guard can be handed. The route folds its read once (lazily: a kind that checks nothing but a digest never pays for
/// the fold, and the fold is forced when <c>SubmissionGuard.For</c> builds the guard of a revision or a vote, which is outside the log's lock); the guard folds
/// only the events appended since, with <see cref="Extend"/>. <see cref="PostProjector.Fold"/> and <see cref="FlagProjector.Fold"/> are defined as the
/// extension of nothing, so the whole fold and the incremental one are one code path and a second implementation of "served" or of the order of actions cannot
/// exist (trap 96).
/// </summary>
public sealed class LogFacts
{
    private sealed record State(ImmutableArray<PostView> Posts, ImmutableDictionary<string, PostModeration> Moderation);

    private readonly Lazy<State> _state;

    private LogFacts(EventSequence position, Lazy<State> state)
    {
        Position = position;
        _state = state;
    }

    /// <summary>The sequence of the last event of the list these facts were folded from; <see cref="EventSequence.Zero"/> for an empty list.</summary>
    public EventSequence Position { get; }

    /// <summary>The post read model of the list, in seq order. Forces the fold.</summary>
    public ImmutableArray<PostView> Posts => _state.Value.Posts;

    /// <summary>Per-post moderation state of the list. Forces the fold.</summary>
    public ImmutableDictionary<string, PostModeration> Moderation => _state.Value.Moderation;

    /// <summary>Whether the fold has been made (a probe reads it; nothing decides on it).</summary>
    public bool IsFolded => _state.IsValueCreated;

    /// <summary>
    /// R10.36's serving filter, by post id, over these facts: a post no moderation record names is servable (the projection's own answer for "nothing has been
    /// decided about this").
    /// </summary>
    public bool Servable(string postId) => !Moderation.TryGetValue(postId, out var state) || state.MayServe;

    /// <summary>Facts of <paramref name="read"/>, folded when first needed and never again. The list is checked as the projectors check a list.</summary>
    public static LogFacts Of(IReadOnlyList<AppendedEvent> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        return new LogFacts(
            read.Count == 0 ? EventSequence.Zero : read[^1].Seq,
            new Lazy<State>(() => new State(PostProjector.Fold(read), FlagProjector.Fold(read)), LazyThreadSafetyMode.ExecutionAndPublication));
    }

    /// <summary>
    /// These facts followed by <paramref name="delta"/>, the events appended after <see cref="Position"/>: the projectors' extension step over the delta only.
    /// Refuses (<see cref="ArgumentException"/>) a delta whose first event is not above <see cref="Position"/>, which is the check the projectors make of ascending
    /// order across the seam. An empty delta is these facts.
    /// </summary>
    public LogFacts Extend(IReadOnlyList<AppendedEvent> delta)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.Count == 0) return this;

        if (delta[0].Seq <= Position)
            throw new ArgumentException(
                "A delta must begin above the position of the facts it extends: the guard's events are those appended after the read.",
                nameof(delta));

        var basis = _state.Value;
        var extended = new State(PostProjector.Extend(basis.Posts, delta), FlagProjector.Extend(basis.Moderation, delta));
        return new LogFacts(delta[^1].Seq, new Lazy<State>(() => extended));
    }

    /// <summary>Forces the fold now. Called outside the log's lock, by the code that builds a guard needing the facts.</summary>
    public LogFacts Force()
    {
        _ = _state.Value;
        return this;
    }
}
