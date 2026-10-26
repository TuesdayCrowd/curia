using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Ingest;

/// <summary>
/// R8.70 (errata G38): reads the post a submission names as its <c>parent</c>, from that post's own stream. Typed on
/// <see cref="IEventReader"/>, so CS-15 holds.
/// </summary>
public static class ParentLookup
{
    /// <returns>The parent's id and stored kind, <see langword="null"/> where no post has the id, or a failure where the log could not be read.</returns>
    public static async Task<Result<ParentFacts?>> FindAsync(IEventReader events, ParentReference parent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(parent);

        // A parent that is not shaped as a post id names no post, and costs no read.
        if (!PostId.Parse(parent.Value).TryGetValue(out var postId, out _))
            return Result<ParentFacts?>.Ok(null);

        if (!AggregateId.Create(postId!.Value).TryGetValue(out var aggregate, out var aggregateError))
            return Result<ParentFacts?>.Fail(aggregateError!);

        var read = await events.ReadByAggregateAsync(aggregate!, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var stream, out var readError))
            return Result<ParentFacts?>.Fail(readError!);

        // A stream is a post only if it holds a post.accepted event naming that post (D39): the aggregate id column also holds
        // agents, flags and key lifecycles. The kind is the one that event stored.
        foreach (var appended in stream!)
        {
            if (appended.Event.Type.Value != PostProjector.PostAcceptedType || appended.Event.Payload is not JsonValue.Object payload)
                continue;

            string? id = null;
            string? kind = null;
            foreach (var member in payload.Members)
            {
                if (member.Value is not JsonValue.String text)
                    continue;
                if (member.Key == "post_id")
                    id = text.Value;
                else if (member.Key == "kind")
                    kind = text.Value;
            }

            if (string.Equals(id, postId.Value, StringComparison.Ordinal) && kind is not null && PostKinds.TryParse(kind, out var parsed))
                return Result<ParentFacts?>.Ok(new ParentFacts(postId.Value, parsed));
        }

        return Result<ParentFacts?>.Ok(null);
    }
}
