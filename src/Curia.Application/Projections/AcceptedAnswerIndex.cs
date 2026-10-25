using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Domain;
using Curia.Domain.Serving;

namespace Curia.Application.Projections;

/// <summary>
/// R9.36 (errata G36): which answer stands as each question's accepted one, defined once, so the <c>accepted_answer</c> member of a
/// question and the <c>accepted</c> flag of the answer it names cannot answer differently. It folds the log's acceptances
/// (<see cref="AcceptanceProjector"/>) against the post read model and is built once per request.
///
/// <para><b>Seam, not yet the rule.</b> The index exists so the probes that need it fail by assertion: it folds nothing and names no
/// answer. The rule is written in the stage that builds item 15.</para>
/// </summary>
public sealed class AcceptedAnswerIndex
{
    private AcceptedAnswerIndex()
    {
    }

    /// <summary>An index that names nothing. For the falsification rows; a caller that builds a response must pass an index it folded.</summary>
    public static AcceptedAnswerIndex Empty { get; } = new();

    /// <summary>The index of a request's log.</summary>
    /// <param name="log">The event log, in seq order.</param>
    /// <param name="posts">The post read model, in seq order.</param>
    /// <param name="servable">R10.36's serving filter, by post id.</param>
    public static AcceptedAnswerIndex Fold(IReadOnlyList<AppendedEvent> log, ImmutableArray<PostView> posts, Func<string, bool> servable)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(servable);
        _ = posts;
        return Empty;
    }

    /// <summary>The standing acceptance of a question's thread: the answer's digest, and its post id only where the Forum serves it, or none.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The seam holds no state yet; the rule that replaces it reads the folded acceptances.")]
    public AcceptedAnswerMember StandingOf(PostView question)
    {
        ArgumentNullException.ThrowIfNull(question);
        return AcceptedAnswerMember.None;
    }
}
