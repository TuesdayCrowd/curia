using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives.Boundary;
using Curia.Domain.Serving;

namespace Curia.Application.Projections;

/// <summary>
/// R9.36 (errata G36): which answer stands as each question's accepted one, defined once, so the <c>accepted_answer</c> member of a
/// question and the <c>accepted</c> flag of the answer it names cannot answer differently. It folds the log's acceptances
/// (<see cref="AcceptanceProjector"/>) against the post read model and is built once per request, beside <see cref="CorrectionIndex"/>.
/// </summary>
public sealed class AcceptedAnswerIndex
{
    private readonly ImmutableDictionary<string, StandingAcceptance> _acceptedByThread;
    private readonly ImmutableDictionary<string, PostView> _postsById;
    private readonly Func<string, bool> _servable;
    private readonly CorrectionIndex _corrections;

    // Errata G76: three folds over what the request already holds, each made at the first question that needs it and once per index, so a response of N posts is not N scans.
    private readonly Lazy<IReadOnlyDictionary<string, EventSequence>> _postPositions;
    private readonly Lazy<ILookup<string, PostView>> _servedAnswers;

    private AcceptedAnswerIndex(
        ImmutableDictionary<string, StandingAcceptance> acceptedByThread,
        ImmutableDictionary<string, PostView> postsById,
        Func<string, bool> servable,
        CorrectionIndex corrections,
        IReadOnlyList<AppendedEvent> log,
        ImmutableArray<PostView> posts)
    {
        _acceptedByThread = acceptedByThread;
        _postsById = postsById;
        _servable = servable;
        _corrections = corrections;
        _postPositions = new Lazy<IReadOnlyDictionary<string, EventSequence>>(() => PostPositions(log));
        _servedAnswers = new Lazy<ILookup<string, PostView>>(() => ServedAnswersByQuestion(posts.IsDefault ? [] : posts));
    }

    /// <summary>An index that names nothing. For the falsification rows; a caller that builds a response must pass an index it folded.</summary>
    public static AcceptedAnswerIndex Empty { get; } = new(
        ImmutableDictionary<string, StandingAcceptance>.Empty.WithComparers(StringComparer.Ordinal),
        ImmutableDictionary<string, PostView>.Empty.WithComparers(StringComparer.Ordinal),
        static _ => false,
        CorrectionIndex.Empty,
        [],
        []);

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

        // R9.41: the accepted answer's corrections come from the rule that sets the answer's own `superseded_by`, folded here from
        // the posts and the filter already held, so no call site has to remember to pair two indices.
        return new AcceptedAnswerIndex(
            AcceptanceProjector.FoldWithInstants(log),
            byId.ToImmutableDictionary(StringComparer.Ordinal),
            servable,
            CorrectionIndex.Fold(posts, servable),
            log,
            posts);
    }

    /// <summary>
    /// The standing acceptance of a question's thread: the answer's digest, and its post id only where the Forum serves it
    /// (R10.36, as <see cref="CorrectionIndex"/> does for a successor), or none when no acceptance stands or the model does not
    /// hold the answer it names (a replay of a log this code did not write must not throw). R9.41: the member lists the answer's
    /// corrections as <see cref="CorrectionIndex"/> lists them for the answer's own envelope. R9.71: it also carries the acceptance's instant and,
    /// where the post has a correction, its order against the acceptance (<see cref="AcceptanceOf"/>, the one function).
    /// </summary>
    public AcceptedAnswerMember StandingOf(PostView question)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (!_acceptedByThread.TryGetValue(question.PostId, out var standing)) return AcceptedAnswerMember.None;
        if (!_postsById.TryGetValue(standing.AnswerId, out var answer)) return AcceptedAnswerMember.None;

        // R9.46: the kind is the Forum's metadata beside the digest, so it is stated also where the post id is left out.
        var kindWord = PostKinds.TryParse(answer.Kind, out var acceptedKind) && acceptedKind is PostKind.Answer or PostKind.Revision ? answer.Kind : null;
        var fact = AcceptanceOf(question);
        return new AcceptedAnswerMember(
            answer.Digest,
            IsServed(answer) ? answer.PostId : null,
            _corrections.For(answer),
            kindWord,
            fact is null ? null : AcceptanceFact.InstantText(fact.AcceptedAt),
            fact?.FirstRevision);
    }

    /// <summary>
    /// Errata G76 (R9.69): the posts of kind <c>answer</c> attached to <paramref name="question"/> that the Forum serves (R10.36), in log order. A revision of an answer is no
    /// answer (R9.28), so one correction chain contributes its root alone; a comment is none; a withheld answer is not listed, nor is a revision of it. The one function that lists
    /// the answers: the duplicate refusal lists them from here and <see cref="ServedAnswersTo"/> is their count, so the two cannot disagree.
    /// </summary>
    public ImmutableArray<PostView> ServedAnswersOf(PostView question)
    {
        ArgumentNullException.ThrowIfNull(question);

        return [.. _servedAnswers.Value[question.PostId]];
    }

    /// <summary>R9.69: how many answers the Forum serves to <paramref name="question"/> (<see cref="ServedAnswersOf"/>'s length).</summary>
    public int ServedAnswersTo(PostView question)
    {
        ArgumentNullException.ThrowIfNull(question);

        return _servedAnswers.Value[question.PostId].Count();
    }

    /// <summary>
    /// Errata G76 (R9.70): for a post of kind <c>revision</c> whose correction chain ends at a question, <c>revises</c>: the chain's root (R8.75), named by id only where the
    /// Forum serves it (R10.36). It is never read from the revision's author-written <c>parent</c> nor from its <c>prev</c> where that is another revision. Null for every other post,
    /// a revision of an answer or a comment included.
    /// </summary>
    public RevisesFact? RevisesOf(PostView post)
    {
        ArgumentNullException.ThrowIfNull(post);

        if (!PostKinds.TryParse(post.Kind, out var kind) || kind is not PostKind.Revision) return null;

        var root = _corrections.ChainRootOf(post);
        return PostKinds.TryParse(root.Kind, out var rootKind) && rootKind is PostKind.Question
            ? new RevisesFact(root.Kind, IsServed(root) ? root.PostId : null)
            : null;
    }

    /// <summary>
    /// Errata G76 (R9.71 (a)): the instant of the acceptance that stands for <paramref name="question"/>'s thread (R8.68) and, where the post that acceptance names has a correction,
    /// whether the first correction in log order was recorded before or after the acceptance event. <b>The relation is the order of two events' <c>seq</c>, never a comparison of two
    /// instants</b> (register D94: an event holds two, and events of one batch share a clock read); the instant is carried to be printed. Null where no acceptance stands or the model
    /// does not hold the post it names.
    /// </summary>
    public AcceptanceFact? AcceptanceOf(PostView question)
    {
        ArgumentNullException.ThrowIfNull(question);

        if (!_acceptedByThread.TryGetValue(question.PostId, out var standing)) return null;
        if (!_postsById.TryGetValue(standing.AnswerId, out var named)) return null;

        string? relation = null;
        if (_corrections.FirstCorrectionOf(named) is { } first && _postPositions.Value.TryGetValue(first.PostId, out var firstSeq))
            relation = firstSeq < standing.Seq ? AcceptanceFact.BeforeAcceptance : AcceptanceFact.AfterAcceptance;

        return new AcceptanceFact(standing.AcceptedAt, relation);
    }

    /// <summary>
    /// Errata G76 (R9.71 (b)): an answer's relation to its question's first correction, by the order of the two events in the log (never by comparing instants). Not revised where the
    /// question has no correction (R9.28's rule: only the question's own author's revision counts), and then the member is <c>null</c> on the wire; otherwise the first correction in log
    /// order (the fork not picked), named by digest and by id where the Forum serves it, and <c>before</c> where the answer's event precedes it.
    /// </summary>
    public QuestionRevisedFact QuestionRevisedOf(PostView answer)
    {
        ArgumentNullException.ThrowIfNull(answer);

        if (QuestionIdOfAnswer(answer) is not { } questionId || !_postsById.TryGetValue(questionId, out var question)) return QuestionRevisedFact.NotRevised;
        if (_corrections.FirstCorrectionOf(question) is not { } first) return QuestionRevisedFact.NotRevised;

        var positions = _postPositions.Value;
        if (!positions.TryGetValue(answer.PostId, out var answerSeq) || !positions.TryGetValue(first.PostId, out var firstSeq)) return QuestionRevisedFact.NotRevised;

        return new QuestionRevisedFact(
            Revised: true,
            FirstRevisionDigest: first.Digest,
            FirstRevisionPostId: IsServed(first) ? first.PostId : null,
            AnswerPosted: answerSeq < firstSeq ? QuestionRevisedFact.Before : QuestionRevisedFact.After);
    }

    /// <summary>The question an answer is attached to, as its author wrote it: the one place the count and <see cref="QuestionRevisedOf"/> read an answer's attachment.</summary>
    private static string? QuestionIdOfAnswer(PostView answer) => answer.Parent;

    private bool IsServed(PostView post) =>
        PostKinds.TryParse(post.Kind, out var kind) && PostKinds.IsServedToReaders(kind) && _servable(post.PostId);

    private ILookup<string, PostView> ServedAnswersByQuestion(ImmutableArray<PostView> posts) =>
        posts
            .Where(p => PostKinds.TryParse(p.Kind, out var kind) && kind is PostKind.Answer && IsServed(p) && QuestionIdOfAnswer(p) is not null)
            .ToLookup(p => QuestionIdOfAnswer(p)!, StringComparer.Ordinal);

    /// <summary>The log position of each post's event: the post event's <c>seq</c>, by post id (the first, were one repeated). Folded from the log the index was folded from; no new column.</summary>
    private static Dictionary<string, EventSequence> PostPositions(IReadOnlyList<AppendedEvent> log)
    {
        var positions = new Dictionary<string, EventSequence>(StringComparer.Ordinal);
        foreach (var appended in log)
        {
            if (appended.Event.Type.Value != PostProjector.PostAcceptedType) continue;
            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            foreach (var member in payload.Members)
                if (member.Key == "post_id" && member.Value is JsonValue.String id)
                {
                    positions.TryAdd(id.Value, appended.Seq);
                    break;
                }
        }

        return positions;
    }

    /// <summary>
    /// R9.46 (errata G44): whether <paramref name="post"/> is the post the standing acceptance of its own thread names. A post's thread is its chain
    /// root's (<see cref="CorrectionIndex.ChainRootOf"/>): the post's own <c>parent</c> for an answer, the chain root's <c>parent</c> for a revision
    /// (a revision's own <c>parent</c> is written by its author and is read by nobody), and a chain that does not end at an answer has none.
    /// The rule <see cref="StandingOf"/> shares.
    /// </summary>
    public bool IsAccepted(PostView post) => ThreadOf(post) is { } thread && NamesPost(thread, post);

    /// <summary>
    /// R9.46: <see cref="IsAccepted"/>, and only where <paramref name="threadRoot"/> is the post's own thread, so a revision a thread route lists
    /// beneath another question (by its author-written <c>parent</c>) does not read accepted there.
    /// </summary>
    public bool IsAcceptedIn(PostView post, PostId threadRoot)
    {
        ArgumentNullException.ThrowIfNull(threadRoot);

        return ThreadOf(post) is { } thread
            && string.Equals(thread, threadRoot.Value, StringComparison.Ordinal)
            && NamesPost(thread, post);
    }

    private bool NamesPost(string thread, PostView post) =>
        _acceptedByThread.TryGetValue(thread, out var accepted) && string.Equals(accepted.AnswerId, post.PostId, StringComparison.Ordinal);

    private string? ThreadOf(PostView post)
    {
        ArgumentNullException.ThrowIfNull(post);

        var root = _corrections.ChainRootOf(post);
        return PostKinds.TryParse(root.Kind, out var kind) && kind is PostKind.Answer
            ? root.Parent
            : null;
    }
}
