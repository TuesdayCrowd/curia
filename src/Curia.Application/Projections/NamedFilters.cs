using System.Collections.Immutable;
using Curia.Domain.Content;
using Curia.Domain.Search;

namespace Curia.Application.Projections;

/// <summary>
/// R8.88, R9.55 (errata G62): which of a request's named filters no served post bears, and whether a board is new. Two shapes of one predicate (the
/// <see cref="PostView"/> projection the search and inbox routes fold, and the <see cref="SearchablePost"/> corpus the near-duplicate check folds), pure and total
/// over any text: a value no board name or agent identifier can hold bears no post, because the comparison is ordinal against what posts hold.
///
/// <para>A post counts when it is of a discussion kind and moderation serves it (R10.36), the predicate the board listing shows its posts by, so a board whose only
/// posts are withheld, or are votes, bears none. Each filter is judged on its own and never with the terms, the other filter, the floor or the page (R9.55).</para>
/// </summary>
public static class NamedFilters
{
    /// <summary>The names of the filters (<c>board</c>, then <c>author</c>, in that order) the request named that no served post bears.</summary>
    public static ImmutableArray<string> Unmatched(ImmutableArray<PostView> posts, Func<string, bool> servable, string? board, string? author)
    {
        ArgumentNullException.ThrowIfNull(servable);

        var unmatched = ImmutableArray.CreateBuilder<string>(2);
        if (board is not null && !BoardHasServedPost(posts, servable, board)) unmatched.Add("board");
        if (author is not null && !AuthorHasServedPost(posts, servable, author)) unmatched.Add("author");
        return unmatched.ToImmutable();
    }

    /// <summary>Whether a post of a discussion kind that moderation serves is on <paramref name="board"/>, over the projection the search and inbox routes fold.</summary>
    public static bool BoardHasServedPost(ImmutableArray<PostView> posts, Func<string, bool> servable, string board)
    {
        ArgumentNullException.ThrowIfNull(servable);
        ArgumentNullException.ThrowIfNull(board);

        return posts.Any(p => string.Equals(p.Board, board, StringComparison.Ordinal) && IsServedDiscussion(p, servable));
    }

    /// <summary>The same predicate over the corpus <see cref="SearchProjector"/> folds (which holds only the servable discussion posts).</summary>
    public static bool BoardHasServedPost(ImmutableArray<SearchablePost> corpus, string board)
    {
        ArgumentNullException.ThrowIfNull(board);

        return corpus.Any(p => string.Equals(p.Board, board, StringComparison.Ordinal));
    }

    /// <summary>
    /// R9.58 (errata G63): the boards a served discussion post is on, one row each, by the predicate <see cref="BoardHasServedPost(ImmutableArray{PostView}, Func{string, bool}, string)"/> uses.
    /// Seam (PR 7, Task 1): lists nothing; Task 2 replaces it with the fold.
    /// </summary>
    public static ImmutableArray<BoardRow> ServedBoards(ImmutableArray<PostView> posts, Func<string, bool> servable)
    {
        ArgumentNullException.ThrowIfNull(servable);

        return [];
    }

    private static bool AuthorHasServedPost(ImmutableArray<PostView> posts, Func<string, bool> servable, string author) =>
        posts.Any(p => string.Equals(p.Author, author, StringComparison.Ordinal) && IsServedDiscussion(p, servable));

    private static bool IsServedDiscussion(PostView post, Func<string, bool> servable) =>
        PostKinds.TryParse(post.Kind, out var kind) && PostKinds.IsDiscussion(kind) && servable(post.PostId);
}
