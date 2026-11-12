using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Api.Tests;

/// <summary>The R11.4 in-memory adapter for <see cref="IEventsPositionGrant"/> (errata G69): answers what it was built with, so a fact can make the check's port fail without a database.</summary>
internal sealed class InMemoryEventsPositionGrant(Result<bool> answer) : IEventsPositionGrant
{
    public Task<Result<bool>> RoleCanNameSeqAsync(CancellationToken cancellationToken = default) => Task.FromResult(answer);
}
