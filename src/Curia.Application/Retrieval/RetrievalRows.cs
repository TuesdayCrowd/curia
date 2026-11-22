using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>
/// One embedding model's retrieval numbers (errata G66, R11.102), held as integer basis points: the near-duplicate thresholds and the vector channel's floor.
/// <see cref="ThresholdsDerived"/> says whether the three thresholds were checked under this model by the rule in <c>conformance/retrieval/THRESHOLD-RULE.md</c>;
/// <see cref="FloorDerived"/> says the same of the floor. A row whose thresholds are not derived carries the hashed era's numbers, and says so (R8.91).
/// </summary>
public sealed record RetrievalRow(
    string ModelId,
    int RefuseCosineBp,
    int RefuseOverlapBp,
    int AnnotateCosineBp,
    int FloorBp,
    bool ThresholdsDerived,
    bool FloorDerived)
{
    /// <summary>The thresholds as the policy reads them.</summary>
    public DuplicateThresholds Thresholds => new(RefuseCosineBp / 10_000.0, RefuseOverlapBp / 10_000.0, AnnotateCosineBp / 10_000.0);

    /// <summary>The vector channel's floor as a cosine: a neighbour below it is not a candidate (R9.22).</summary>
    public double Floor => FloorBp / 10_000.0;
}

/// <summary>
/// The table of retrieval numbers keyed by model id (errata G66, R11.102): the only place a dedupe threshold or the vector floor is written. An id with no row refuses nothing and
/// removes nothing, and the Forum says so once at start. An IL fence (<c>RetrievalNumbersFenceTests</c>) holds every other production type to no such literal.
/// </summary>
public static class RetrievalRows
{
    /// <summary>
    /// <c>hashed-ngram@1</c>, the test adapter's row: R8.18's 0.94 and the provisional 0.5 and 0.85 of errata G10, and the floor 0.2 of R9.22, fitted to a sample and never checked
    /// by the rule, so neither flag is true.
    /// </summary>
    public static RetrievalRow Hashed { get; } = new(HashedNGramEmbedding.Model.Id, 9400, 5000, 8500, 2000, ThresholdsDerived: false, FloorDerived: false);

    /// <summary>
    /// <c>bge-small-en-v1.5@1</c> (D10b, errata G66). The three thresholds are the hashed era's numbers, carried: the rule's Check failed (<c>thresholds_ship: false</c> in
    /// <c>conformance/retrieval/RESULTS.md</c>'s bge block), so none is published and none is derived. The floor, 6400 bp, is copied from that block's <c>floor_bp</c>, where it passed.
    /// </summary>
    public static RetrievalRow Bge { get; } = new("bge-small-en-v1.5@1", 9400, 5000, 8500, 6400, ThresholdsDerived: false, FloorDerived: true);

    /// <summary>The row for <paramref name="modelId"/>, or <see langword="null"/> for an id with no row.</summary>
    public static RetrievalRow? For(string modelId) => modelId switch
    {
        _ when string.Equals(modelId, Hashed.ModelId, StringComparison.Ordinal) => Hashed,
        _ when string.Equals(modelId, Bge.ModelId, StringComparison.Ordinal) => Bge,
        _ => null,
    };
}
