using Curia.Application.Ports;
using Npgsql;

namespace Curia.Infrastructure;

/// <summary>
/// R11.71 (errata G52): the one predicate for a fault of a Postgres store. An exception is a store fault where it, or an exception in its
/// <see cref="Exception.InnerException"/> chain to a depth of <see cref="MaxDepth"/> levels, is an <see cref="NpgsqlException"/> (which covers
/// <see cref="PostgresException"/>: a refused, killed or denied connection) or a <see cref="TimeoutException"/> (a pool timeout is an
/// <see cref="NpgsqlException"/> around one). By type, not by SQLSTATE: a list of states drifts.
/// </summary>
public sealed class PostgresStoreFaults : IStoreFaultClassifier
{
    /// <summary>How many levels of <see cref="Exception.InnerException"/> the walk looks into: a fault eight wrappers deep is found, one nine deep is not.</summary>
    public const int MaxDepth = 8;

    public bool IsUnavailable(Exception exception) => Is(exception);

    /// <summary>True where <paramref name="exception"/> or an exception inside it, to <see cref="MaxDepth"/>, is a fault of the store.</summary>
    public static bool Is(Exception? exception)
    {
        for (var depth = 0; exception is not null && depth <= MaxDepth; depth++, exception = exception.InnerException)
        {
            if (exception is NpgsqlException or TimeoutException) return true;
        }

        return false;
    }

    /// <summary>
    /// The cause the log and a typed refusal carry: the type name of the driver's fault found in <paramref name="exception"/> and, for a
    /// <see cref="PostgresException"/>, its SQLSTATE. Never a message, which a driver can fill with what it read.
    /// </summary>
    public static string Cause(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var found = exception;
        var current = exception;
        for (var depth = 0; current is not null && depth <= MaxDepth; depth++, current = current.InnerException)
        {
            if (current is NpgsqlException or TimeoutException)
            {
                found = current;
                if (current is PostgresException) break;
            }
        }

        return found is PostgresException postgres
            ? $"{nameof(PostgresException)} sqlstate={postgres.SqlState}"
            : found.GetType().Name;
    }

    /// <summary>R11.71: the event store's throw for a fault of the store behind it, the driver's exception inside.</summary>
    internal static LogUnreadableException Unreadable(Exception exception) => new(Cause(exception), exception);

    /// <summary>
    /// True where <paramref name="exception"/> is a store fault that is the store's and not the caller's: a cancellation of the caller's own token is never
    /// one, whatever the driver wrapped inside the <see cref="OperationCanceledException"/> it raised.
    /// </summary>
    internal static bool IsStoreFault(Exception exception, CancellationToken caller) =>
        exception is not OperationCanceledException && !caller.IsCancellationRequested && Is(exception);
}
