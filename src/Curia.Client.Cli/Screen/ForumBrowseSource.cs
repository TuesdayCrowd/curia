using System.Collections.Immutable;
using Curia.Client;
using Curia.Domain.Serving;

namespace Curia.Client.Cli;

/// <summary>
/// The screen's posts, fetched and verified exactly as the line form does it: the same client calls, the same marking the
/// verbs default to, and <see cref="Program.VerifyAllAsync"/> for the signatures, so the outcome of every passage is the one
/// <c>curia read</c>, <c>thread</c> and <c>board --titles</c> would give it. It holds a client and nothing that identifies
/// an agent (R10.73).
/// </summary>
internal sealed class ForumBrowseSource(ForumClient client) : IBrowseSource
{
    public async Task<LoadResult> LoadAsync(ViewSource source, CancellationToken ct)
    {
        ImmutableArray<ProvenancePost> posts;
        switch (source.Kind)
        {
            case ViewKind.Board:
            {
                var board = await client.GetBoardAsync(source.Id, MarkingMode.Datamark, ct).ConfigureAwait(false);
                if (!board.TryGetValue(out var value, out var refusal)) return LoadResult.Refused(refusal!);
                posts = value;
                break;
            }

            case ViewKind.Thread:
            {
                var thread = await client.GetThreadAsync(source.Id, MarkingMode.Datamark, ct).ConfigureAwait(false);
                if (!thread.TryGetValue(out var value, out var refusal)) return LoadResult.Refused(refusal!);
                posts = value;
                break;
            }

            default:
            {
                var post = await client.GetPostAsync(source.Id, MarkingMode.Datamark, ct).ConfigureAwait(false);
                if (!post.TryGetValue(out var value, out var refusal)) return LoadResult.Refused(refusal!);
                posts = [value];
                break;
            }
        }

        var (passages, _) = await Program.VerifyAllAsync(client, posts, ct).ConfigureAwait(false);
        return LoadResult.Loaded(passages);
    }
}
