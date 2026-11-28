using System.Collections.Immutable;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Reads;

/// <summary>
/// One post's own stream, read in one snapshot with the published keys (errata G87, R11.113): the post, and whether moderation lets the Forum serve it. A moderation record is honoured only in the stream of
/// the post it names (R10.60), and its signature is held to the operator keys the log publishes (R6.67), so those two reads are everything the answer depends on.
/// </summary>
public sealed class PostStreamRead
{
    private PostStreamRead(string postId, ImmutableArray<PostView> posts, VerifiedLog verified)
    {
        Posts = posts;
        Servable = id => string.Equals(id, postId, StringComparison.Ordinal)
            ? verified.ModerationOf(postId) is not { } state || state.MayServe
            : throw new OutOfScopeException("another post's moderation");
    }

    /// <summary>The posts of the stream (at most one).</summary>
    public ImmutableArray<PostView> Posts { get; }

    /// <summary>Whether moderation lets the Forum serve a post: for the post this read was made for; a post nobody has moderated is servable. Asked of another post it refuses (<see cref="OutOfScopeException"/>).</summary>
    public Func<string, bool> Servable { get; }

    /// <summary>Reads the stream of <paramref name="postId"/>.</summary>
    public static async Task<PostStreamRead> ReadAsync(IPositionedEventReader reader, LifecycleVerifier verifier, PostId postId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(postId);

        var slice = await reader.ReadAsync(ReadPlan.Of(ReadClause.Aggregates([postId.Value]), ReadClause.Keys()), cancellationToken).ConfigureAwait(false);
        return new PostStreamRead(postId.Value, PostProjector.Fold([.. slice.Rows.Select(r => r.Event)]), verifier.OfSlice(slice));
    }
}
