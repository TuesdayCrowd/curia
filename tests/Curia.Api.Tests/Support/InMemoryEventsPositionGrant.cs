using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Api.Tests;

/// <summary>The R11.4 in-memory adapter for <see cref="IEventsPositionGrant"/> (errata G69): answers what it was built with, so a fact can make the check's port fail without a database.</summary>
internal sealed class InMemoryEventsPositionGrant(Result<bool> answer) : IEventsPositionGrant
{
    public Task<Result<bool>> RoleCanNameSeqAsync(CancellationToken cancellationToken = default) => Task.FromResult(answer);

    /// <summary>R6.69: the leaf column is there and writable (the Api fixtures apply db/0007), unless a test says the store cannot be asked.</summary>
    public Task<Result<bool>> RoleCanWriteLeafAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(answer.IsOk ? Result<bool>.Ok(true) : answer);
}
