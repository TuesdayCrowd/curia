using Curia.Application.Ports;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// The R11.4 in-memory adapter for <see cref="IStoreFaultClassifier"/> (errata G52, R11.71): an in-memory store cannot fault the way a database does, so
/// this classifier names no exception type and answers <see langword="false"/> for every exception, wrapped or not.
/// </summary>
internal sealed class InMemoryStoreFaultClassifier : IStoreFaultClassifier
{
    public bool IsUnavailable(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return false;
    }
}
