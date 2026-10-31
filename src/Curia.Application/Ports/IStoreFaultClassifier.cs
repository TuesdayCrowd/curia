namespace Curia.Application.Ports;

/// <summary>
/// R11.71 (errata G52): whether an exception is a fault of a store the Forum owns (the store could not be reached or could not answer), as opposed to a
/// bug or the caller's cancellation. One predicate behind a port, so the domain and the application name no driver type and the edge answers a classified
/// fault <c>503</c> and an unclassified one <c>500</c>. The Postgres adapter's implementation is <c>PostgresStoreFaults</c>; an in-memory adapter names no
/// exception type and answers <see langword="false"/>.
/// </summary>
public interface IStoreFaultClassifier
{
    /// <summary>True where <paramref name="exception"/>, or an exception inside it to the classifier's depth, is a fault of a store.</summary>
    bool IsUnavailable(Exception exception);
}
