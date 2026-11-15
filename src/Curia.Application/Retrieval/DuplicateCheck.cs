using System.Collections.Immutable;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>What the dedupe measured (R8.17): the nearest servable post of the same kind on the same board, and both measures against it.</summary>
public sealed record DuplicateAssessment(
    DuplicateVerdict Verdict,
    SearchablePost? Nearest,
    double Cosine,
    double LexicalOverlap,
    DuplicateThresholds Thresholds,
    EmbeddingModel Model,
    bool Overridden,
    int CandidatesConsidered);

/// <summary>
/// §8.5 before PERSIST: R8.17's near-duplicate search over hybrid retrieval's vector channel, and
/// R8.18's verdict. Runs after VERIFY and authorization and before SCREEN, beside the signal
/// refusal, for the reasons that one gives: the author must be established before the content is
/// trusted, a refused submission should not be screened, and nothing here touches the signed
/// bytes -- the embedding is a derived artifact (R6.14) computed from them.
///
/// <para><b>Candidates are the same board and the same kind.</b> A cross-board duplicate is a
/// different audience (R8.58's own precedent for signals), and an answer near a question is not
/// the case R10.6 caps. Servable only: a withheld post is not a canonical thread to point at.</para>
/// </summary>
public sealed class DuplicateCheck
{
    /// <summary>How many nearest neighbours to look through for one on the right board and of the right kind.</summary>
    public const int NeighbourDepth = 50;

    private readonly ITextEmbedder _embedder;
    private readonly IVectorIndex _index;
    private readonly DuplicateThresholds _thresholds;

    public DuplicateCheck(ITextEmbedder embedder, IVectorIndex index, DuplicateThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(thresholds);
        if (!thresholds.IsValid)
            throw new ArgumentException("Thresholds must lie in [0, 1] with the moderate cosine at or below the refusing one.", nameof(thresholds));
        _embedder = embedder;
        _index = index;
        _thresholds = thresholds;
    }

    public DuplicateThresholds Thresholds => _thresholds;

    /// <summary>
    /// The assessment over a corpus the caller folded (errata G62, ruling 18): the route folds once and shares the corpus with <c>board_new</c>. There is no overload that takes a log: the corpus is folded from a
    /// verified log (<c>SearchProjector.FoldHonoured</c>), and a duplicate check that folded its own would read operator acts nobody verified (errata G70, ruling 75). This is
    /// <see cref="PrepareAsync"/> then <see cref="Assess"/>, with the corpus handed in before the wait; a route that reads the log after the wait calls the two itself (errata G71, R9.65).
    /// </summary>
    public async Task<Result<DuplicateAssessment>> AssessAsync(
        ImmutableArray<SearchablePost> corpus, PostEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareAsync(envelope, cancellationToken).ConfigureAwait(false);
        return prepared.TryGetValue(out var ready, out var error) ? Assess(corpus, ready!) : Result<DuplicateAssessment>.Fail(error!);
    }

    /// <summary>
    /// Every step of the check at which it can be made to wait (errata G71, R9.65): the text embedded as a passage and the index's neighbours of it. It takes no corpus, so it asks the index
    /// even where the board holds no candidate, and it reads nothing: the caller reads the log after this returns. <c>NoFeatures</c> is a text with nothing to compare, not a failure.
    /// </summary>
    public async Task<Result<PreparedDuplicate>> PrepareAsync(PostEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var overridden = envelope.NotDuplicate == true;
        var text = EmbeddedText.Of(envelope);

        var embedded = await _embedder.EmbedAsync(text, EmbeddingRole.Passage, cancellationToken).ConfigureAwait(false);
        if (!embedded.TryGetValue(out var embedding, out var embedError))
        {
            return string.Equals(embedError!.Type, EmbeddingErrors.NoFeatures().Type, StringComparison.Ordinal)
                ? Result<PreparedDuplicate>.Ok(new PreparedDuplicate(envelope.Kind, envelope.Board, text, overridden, embedded: false, []))
                : Result<PreparedDuplicate>.Fail(embedError);
        }

        var nearest = await _index.NearestAsync(embedding!, NeighbourDepth, cancellationToken).ConfigureAwait(false);
        return nearest.TryGetValue(out var matches, out var nearestError)
            ? Result<PreparedDuplicate>.Ok(new PreparedDuplicate(envelope.Kind, envelope.Board, text, overridden, embedded: true, matches))
            : Result<PreparedDuplicate>.Fail(nearestError!);
    }

    /// <summary>
    /// The rest of the check, synchronous: the candidates of <paramref name="corpus"/> (the same board and kind), the walk of the prepared neighbours that the corpus holds, and the verdict.
    /// The neighbours are over the whole index, withheld posts included, so the corpus filter after the read is what keeps a withheld post from being offered as canonical (errata G71, R9.65).
    /// A board with no candidate is answered <c>none</c> here and not before the index is asked.
    /// </summary>
    public Result<DuplicateAssessment> Assess(ImmutableArray<SearchablePost> corpus, PreparedDuplicate prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);

        var none = new DuplicateAssessment(DuplicateVerdict.None, null, 0, 0, _thresholds, _embedder.Model, prepared.Overridden, 0);
        if (!prepared.Embedded)
            return Result<DuplicateAssessment>.Ok(none);

        // R9.47: two stored posts of one digest resolve to the first in log order; a repeat never throws.
        var candidates = new Dictionary<string, SearchablePost>(StringComparer.Ordinal);
        foreach (var post in corpus
            .Where(p => p.Kind == prepared.Kind && string.Equals(p.Board, prepared.Board, StringComparison.Ordinal)))
        {
            candidates.TryAdd(post.Digest, post);
        }

        if (candidates.Count == 0)
            return Result<DuplicateAssessment>.Ok(none);

        foreach (var match in prepared.Neighbours)
        {
            if (!candidates.TryGetValue(match.Digest, out var candidate)) continue;

            var cosine = 1.0 - match.Distance;
            var overlap = LexicalOverlap.Jaccard(prepared.Text, EmbeddedText.Of(candidate));
            var verdict = DuplicatePolicy.Assess(prepared.Kind, cosine, overlap, prepared.Overridden, _thresholds);
            return Result<DuplicateAssessment>.Ok(new DuplicateAssessment(
                verdict, candidate, cosine, overlap, _thresholds, _embedder.Model, prepared.Overridden, candidates.Count));
        }

        return Result<DuplicateAssessment>.Ok(none with { CandidatesConsidered = candidates.Count });
    }
}
