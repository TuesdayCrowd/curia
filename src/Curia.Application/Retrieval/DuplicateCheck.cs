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

    public async Task<Result<DuplicateAssessment>> AssessAsync(
        IReadOnlyList<AppendedEvent> log, PostEnvelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(envelope);

        var overridden = envelope.NotDuplicate == true;
        var text = EmbeddedText.Of(envelope);
        var none = new DuplicateAssessment(DuplicateVerdict.None, null, 0, 0, _thresholds, _embedder.Model, overridden, 0);

        var embedded = _embedder.Embed(text);
        if (!embedded.TryGetValue(out var embedding, out var embedError))
        {
            return string.Equals(embedError!.Type, EmbeddingErrors.NoFeatures().Type, StringComparison.Ordinal)
                ? Result<DuplicateAssessment>.Ok(none)
                : Result<DuplicateAssessment>.Fail(embedError);
        }

        var candidates = SearchProjector.Fold(log)
            .Where(p => p.Kind == envelope.Kind && string.Equals(p.Board, envelope.Board, StringComparison.Ordinal))
            .ToDictionary(p => p.Digest, StringComparer.Ordinal);
        if (candidates.Count == 0)
            return Result<DuplicateAssessment>.Ok(none);

        var nearest = await _index.NearestAsync(embedding!, NeighbourDepth, cancellationToken).ConfigureAwait(false);
        if (!nearest.TryGetValue(out var matches, out var nearestError))
            return Result<DuplicateAssessment>.Fail(nearestError!);

        foreach (var match in matches!)
        {
            if (!candidates.TryGetValue(match.Digest, out var candidate)) continue;

            var cosine = 1.0 - match.Distance;
            var overlap = LexicalOverlap.Jaccard(text, EmbeddedText.Of(candidate));
            var verdict = DuplicatePolicy.Assess(envelope.Kind, cosine, overlap, overridden, _thresholds);
            return Result<DuplicateAssessment>.Ok(new DuplicateAssessment(
                verdict, candidate, cosine, overlap, _thresholds, _embedder.Model, overridden, candidates.Count));
        }

        return Result<DuplicateAssessment>.Ok(none with { CandidatesConsidered = candidates.Count });
    }
}
