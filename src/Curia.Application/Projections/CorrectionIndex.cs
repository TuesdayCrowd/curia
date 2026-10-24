using System.Collections.Immutable;
using Curia.Domain.Serving;

namespace Curia.Application.Projections;

/// <summary>
/// COMPILE-ONLY STUB (D66 Task 0): <see cref="For"/> returns nothing so the probes SU1 to SU3 compile and run red. Task 1
/// replaces this file with R9.32's one rule and the per-request index.
/// </summary>
public sealed class CorrectionIndex
{
    private readonly ImmutableArray<PostView> _posts;

    private CorrectionIndex(ImmutableArray<PostView> posts) => _posts = posts;

    /// <summary>An index that lists nothing.</summary>
    public static CorrectionIndex Empty { get; } = new([]);

    /// <summary>The index of a request's posts.</summary>
    public static CorrectionIndex Fold(ImmutableArray<PostView> posts, Func<string, bool> servable) => new(posts);

    /// <summary>The revisions that supersede <paramref name="original"/>, in log order.</summary>
    public ImmutableArray<Successor> For(PostView original) => _posts.IsDefault ? [] : [];
}
