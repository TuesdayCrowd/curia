using System.Collections.Immutable;
using Curia.Domain.Search;

namespace Curia.Application.Projections;

/// <summary>
/// R8.88, R9.55 (errata G62): which of a request's named filters no served post bears, and whether a board is new. Two shapes of one predicate (the
/// <see cref="PostView"/> projection the search and inbox routes fold, and the <see cref="SearchablePost"/> corpus the near-duplicate check folds), pure and total
/// over any text: a value no board name or agent identifier can hold bears no post.
///
/// <para><b>The seam.</b> This is the behaviour-preserving form the probes of PR 6 are written against: nothing is unmatched and every board has a served post, so
/// every probe of the rule fails for the rule and not for a missing symbol. Task 2 replaces the bodies.</para>
/// </summary>
public static class NamedFilters
{
    /// <summary>The names of the filters (<c>board</c>, then <c>author</c>, in that order) the request named that no served post bears; empty on the seam.</summary>
    public static ImmutableArray<string> Unmatched(ImmutableArray<PostView> posts, Func<string, bool> servable, string? board, string? author)
    {
        ArgumentNullException.ThrowIfNull(servable);
        return [];
    }

    /// <summary>Whether a post of a discussion kind that moderation serves is on <paramref name="board"/>, over the projection the search and inbox routes fold; always true on the seam.</summary>
    public static bool BoardHasServedPost(ImmutableArray<PostView> posts, Func<string, bool> servable, string board)
    {
        ArgumentNullException.ThrowIfNull(servable);
        return true;
    }

    /// <summary>The same predicate over the corpus <see cref="SearchProjector"/> folds (which holds only the servable discussion posts); always true on the seam.</summary>
    public static bool BoardHasServedPost(ImmutableArray<SearchablePost> corpus, string board) => true;
}
