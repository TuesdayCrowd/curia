using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>
/// The Stage 3 read model: a per-aggregate summary derived purely from the event stream -- how
/// many events an aggregate has, the first and last <see cref="EventSequence"/> it was touched
/// at, and the <see cref="ServerTimestamp"/> of its most recently appended event. Chosen for the
/// R11.9 replay-rebuild drill specifically because it is simple enough that any failure to
/// rebuild correctly can only be a bug in the rebuild *mechanism* -- there is no projection-level
/// business logic here to get subtly wrong instead.
///
/// A <see langword="record"/>, not a class: every field is itself a value-equality type
/// (<see cref="AggregateId"/>, <see cref="EventSequence"/>, <see cref="ServerTimestamp"/> are all
/// <c>readonly record struct</c>s), so two summaries built from the same events -- whether from
/// the same rebuild or two independently run ones -- compare equal by value with a plain
/// <c>Assert.Equal</c>, which is exactly the comparison the replay-determinism drill needs.
/// </summary>
/// <param name="AggregateId">The aggregate (stream) this summary is about.</param>
/// <param name="EventCount">How many events have been appended to this aggregate.</param>
/// <param name="FirstSeq">The <c>seq</c> of the first event ever appended to this aggregate.</param>
/// <param name="LastSeq">The <c>seq</c> of the most recently appended event -- the one total
/// order the event table offers (R11.9), and the only thing "most recent" is defined against
/// here; see <see cref="AggregateSummaryProjector"/>'s remarks for why this is <c>seq</c>, never
/// <c>server_ts</c>.</param>
/// <param name="LastServerTimestamp">The <c>server_ts</c> recorded on the event at
/// <see cref="LastSeq"/> -- not the maximum <c>server_ts</c> seen for this aggregate, which can
/// differ from it (see <see cref="AggregateSummaryProjector"/>).</param>
public sealed record AggregateSummary(
    AggregateId AggregateId,
    long EventCount,
    EventSequence FirstSeq,
    EventSequence LastSeq,
    ServerTimestamp LastServerTimestamp);
