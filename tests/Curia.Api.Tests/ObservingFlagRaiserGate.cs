using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain.Primitives;

namespace Curia.Api.Tests;

/// <summary>
/// A test-only decorator over the Forum's real <see cref="IFlagRaiserGate"/> (R7.22; errata G18, review
/// of 980fb0e), which observes the store at the moment a raiser's hold is released.
///
/// <para><b>Entries.</b> Every raiser whose flag reached the gate is recorded in <see cref="Entered"/>,
/// so a fact can tell a server fault under the hold from one before it (authentication, the kind).</para>
///
/// <para><b>Releases</b>, when the stores are given. The hold's first dispose reads the whole log and
/// the private store, joins them, and records how many flags the raiser has, and only then disposes the
/// inner hold. The join, not a row count: <c>RaiseFlag.RecordAsync</c> writes the private row before
/// the log append, so a row count would pass a release between the two writes. A second dispose
/// records nothing, as the real hold releases once, so a patch that releases early and leaves the
/// endpoint's <c>await using</c> in place is seen at its first release. A read that fails is recorded
/// as <see cref="ReadFailed"/>, never as 0, and the inner hold is disposed whatever happens, so a
/// failed read cannot leave the lock held.</para>
/// </summary>
internal sealed class ObservingFlagRaiserGate(IFlagRaiserGate inner, IEventReader? events = null, IFlagDetailStore? details = null)
    : IFlagRaiserGate
{
    /// <summary>The count recorded when the observer could not read the stores.</summary>
    public const int ReadFailed = -1;

    /// <summary>Raisers whose flag reached the gate, whether or not it entered.</summary>
    public ConcurrentDictionary<string, int> Entered { get; } = new(StringComparer.Ordinal);

    /// <summary>Per raiser, the joined count of its flags at each release, in order.</summary>
    public ConcurrentDictionary<string, ConcurrentQueue<int>> Releases { get; } = new(StringComparer.Ordinal);

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The inner hold is owned by the returned ObservedHold, which disposes it; the caller owns that, as it owns the real gate's hold.")]
    public async Task<Result<IAsyncDisposable>> TryEnterAsync(string raisedBy, CancellationToken cancellationToken)
    {
        Entered.AddOrUpdate(raisedBy, 1, (_, n) => n + 1);

        var entered = await inner.TryEnterAsync(raisedBy, cancellationToken).ConfigureAwait(false);
        if (events is null || details is null || !entered.TryGetValue(out var hold, out _))
            return entered;

        return Result<IAsyncDisposable>.Ok(new ObservedHold(this, raisedBy, hold!));
    }

    private async Task<int> JoinedFlagsByAsync(string raisedBy)
    {
        try
        {
            var log = await events!.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
            var rows = await details!.ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
            if (!log.TryGetValue(out var entries, out _) || !rows.TryGetValue(out var flagRows, out _))
                return ReadFailed;

            return FlagDirectory.Join(entries!, flagRows!).Flags
                .Count(f => string.Equals(f.RaisedBy, raisedBy, StringComparison.Ordinal));
        }
#pragma warning disable CA1031 // A failed observation is recorded as ReadFailed and fails the fact by its own message.
        catch (Exception)
#pragma warning restore CA1031
        {
            return ReadFailed;
        }
    }

    private sealed class ObservedHold(ObservingFlagRaiserGate gate, string raisedBy, IAsyncDisposable inner) : IAsyncDisposable
    {
        private int _released;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            try
            {
                var joined = await gate.JoinedFlagsByAsync(raisedBy).ConfigureAwait(false);
                gate.Releases.GetOrAdd(raisedBy, _ => new ConcurrentQueue<int>()).Enqueue(joined);
            }
            finally
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
