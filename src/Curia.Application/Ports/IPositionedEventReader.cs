using Curia.Application.Reads;

namespace Curia.Application.Ports;

/// <summary>
/// The read a request decides from (errata G87, R11.113): a <see cref="ReadPlan"/> of at most three stages, all taken in one snapshot, each row returned with its R6.47 index computed in that
/// snapshot. It throws <see cref="LogUnreadableException"/> on a fault of the store and never returns an empty slice for one (R11.71): an empty read is a different claim from a failed one.
/// A route reads through this and not through a whole-log read; the operator verbs, the start-up scans, the head signer and the Acta's own routes read whole, by design.
///
/// <para><b>The default is the reference semantics and not an adapter.</b> The default implementation answers the plan over the whole log, one row at a time through
/// <see cref="IEventReader.ReadForwardAsync"/>, so that an in-memory adapter and a test's wrapper that injects a fault into a read stay correct without a line of their own. It is correct and
/// unbounded, which is exactly what a route must never ship with: every implementation under <c>src/</c> overrides it (a fence holds that), and <c>PostgresEventStore</c>'s is the bounded one.</para>
/// </summary>
public interface IPositionedEventReader
{
    /// <summary>Reads <paramref name="plan"/> in one snapshot.</summary>
    Task<LogSlice> ReadAsync(ReadPlan plan, CancellationToken cancellationToken = default) =>
        WholeLogPlanReader.RunAsync((IEventReader)this, plan, cancellationToken);
}
