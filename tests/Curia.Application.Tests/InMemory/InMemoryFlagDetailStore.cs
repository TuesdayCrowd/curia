using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>R11.4's in-memory <see cref="IFlagDetailStore"/>: a dictionary, the shared admission rule, ordinal order.</summary>
internal sealed class InMemoryFlagDetailStore : IFlagDetailStore
{
    private readonly Dictionary<string, FlagDetail> _rows = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default)
    {
        if (!FlagDetailRules.Admit(detail).TryGetValue(out _, out var refusal))
            return Task.FromResult(Result<FlagDetail>.Fail(refusal!));

        lock (_gate)
        {
            if (!_rows.TryAdd(detail.EventId, detail))
                return Task.FromResult(Result<FlagDetail>.Fail(FlagDetailRules.Exists(detail.EventId)));
        }

        return Task.FromResult(Result<FlagDetail>.Ok(detail));
    }

    public Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<FlagDetail> rows = [.. _rows.Values.OrderBy(d => d.EventId, StringComparer.Ordinal)];
            return Task.FromResult(Result<IReadOnlyList<FlagDetail>>.Ok(rows));
        }
    }
}
