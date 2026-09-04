using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

public static class EventReaderExtensions
{
    /// <summary>
    /// The page size <see cref="ReadAllAsync"/> walks the log in. A page, not a cap: the loop below
    /// keeps reading until a page comes back short, so the number is a memory-and-round-trip
    /// trade-off and never a bound on what is returned.
    /// </summary>
    public const int PageSize = 10_000;

    /// <summary>
    /// The whole log, forward from the beginning, however long it is.
    ///
    /// <para>This replaced a single <c>ReadForwardAsync(Zero, 10_000)</c> at every read path. That
    /// call was a silent truncation: the ten-thousand-and-first event was simply not there, and
    /// every projection folded over a prefix while reporting nothing. For the post model that
    /// would have been a post that vanished; for the Acta it would have been a <i>wrong root with
    /// no error</i> -- a tree over a truncated leaf list is a different tree, and every proof
    /// against it verifies perfectly. A read model that can be wrong without saying so is the
    /// failure R11.9 exists to prevent, so the page size is now a page.</para>
    /// </summary>
    public static async Task<Result<IReadOnlyList<AppendedEvent>>> ReadAllAsync(
        this IEventReader events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var all = new List<AppendedEvent>();
        var after = EventSequence.Zero;
        while (true)
        {
            var page = await events.ReadForwardAsync(after, PageSize, cancellationToken).ConfigureAwait(false);
            if (!page.TryGetValue(out var events_, out var error))
                return Result<IReadOnlyList<AppendedEvent>>.Fail(error!);

            all.AddRange(events_!);
            if (events_!.Count < PageSize)
                return Result<IReadOnlyList<AppendedEvent>>.Ok(all);

            after = events_[^1].Seq;
        }
    }
}
