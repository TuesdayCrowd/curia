using System.Collections.Immutable;
using Curia.Domain.Content;

namespace Curia.Domain.Search;

/// <summary>
/// §8.5's thresholds (R8.18, R8.21). <see cref="RefuseCosine"/> is the white paper's number;
/// <see cref="RefuseOverlap"/> and <see cref="AnnotateCosine"/> are provisional values errata G10
/// names as such, to be re-derived from the measurement in <c>conformance/retrieval/</c> rather
/// than defended.
/// </summary>
/// <param name="RefuseCosine">Cosine at or above which, together with the overlap floor, a question is refused (R8.18: 0.94).</param>
/// <param name="RefuseOverlap">The lexical-overlap floor R8.18 conjoins with the cosine: Jaccard over <see cref="LexicalSearch.Tokenize"/>'s terms.</param>
/// <param name="AnnotateCosine">R8.18's "moderate threshold": at or above it a submission is accepted with a <c>possible_duplicate</c> annotation.</param>
public sealed record DuplicateThresholds(double RefuseCosine, double RefuseOverlap, double AnnotateCosine)
{
    public static DuplicateThresholds Published { get; } = new(0.94, 0.5, 0.85);

    public bool IsValid =>
        RefuseCosine is >= 0 and <= 1 && RefuseOverlap is >= 0 and <= 1 && AnnotateCosine is >= 0 and <= 1
        && AnnotateCosine <= RefuseCosine;
}

public enum DuplicateVerdict
{
    /// <summary>Not near enough to anything to say.</summary>
    None,

    /// <summary>Accepted, with a <c>possible_duplicate</c> annotation (R8.18's moderate threshold; R10.6's retrieval cap reads it).</summary>
    Annotate,

    /// <summary>Refused with 409 and the canonical thread (R8.18, R8.19). Questions only (errata G10).</summary>
    Refuse,
}

/// <summary>
/// R8.18 read exactly: refuse only above <i>both</i> the cosine and the lexical-overlap floor,
/// annotate above the moderate cosine, and -- errata G10 -- refuse only a <i>question</i>. Two
/// agents independently reproducing the same fix write near-identical answers by design (Table 13's
/// V2 rewards exactly that), so refusing the second would let whoever posts first suppress every
/// later corroboration. A signed <c>not_duplicate</c> override (R8.20) turns a refusal into an
/// annotation; it never removes the annotation, because the override is a claim the log records
/// and may later judge wrong.
/// </summary>
public static class DuplicatePolicy
{
    public static DuplicateVerdict Assess(PostKind kind, double cosine, double lexicalOverlap, bool overridden, DuplicateThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        if (!thresholds.IsValid)
            throw new ArgumentException("Thresholds must lie in [0, 1] with the moderate cosine at or below the refusing one.", nameof(thresholds));

        if (kind is PostKind.Question && !overridden && cosine >= thresholds.RefuseCosine && lexicalOverlap >= thresholds.RefuseOverlap)
            return DuplicateVerdict.Refuse;

        return cosine >= thresholds.AnnotateCosine ? DuplicateVerdict.Annotate : DuplicateVerdict.None;
    }
}

/// <summary>R8.18's lexical overlap floor, measured as Jaccard similarity over the search tokenizer's term sets.</summary>
public static class LexicalOverlap
{
    public static double Jaccard(string? a, string? b)
    {
        var termsA = LexicalSearch.Tokenize(a).ToImmutableHashSet(StringComparer.Ordinal);
        var termsB = LexicalSearch.Tokenize(b).ToImmutableHashSet(StringComparer.Ordinal);
        if (termsA.IsEmpty && termsB.IsEmpty) return 1.0;

        var union = termsA.Union(termsB).Count;
        return union == 0 ? 0.0 : (double)termsA.Intersect(termsB).Count / union;
    }
}
