namespace Curia.Application.Ports;

/// <summary>
/// R11.71 (errata G52): the event log could not be read. The event store throws this for a fault of the store behind it and does not return a failed
/// <see cref="Curia.Domain.Primitives.Result{T}"/> for one, because a failed result can be read as an empty log and so as a denial.
///
/// <para><see cref="Cause"/> is the exception's type name and, for a Postgres fault, its SQLSTATE: never a message, which a driver can fill with what it read.
/// The driver's own exception is the <see cref="Exception.InnerException"/>, so the operator keeps every word it said; the response carries none of them.</para>
/// </summary>
public sealed class LogUnreadableException : Exception
{
    public LogUnreadableException()
        : this("unspecified", null)
    {
    }

    public LogUnreadableException(string cause)
        : this(cause, null)
    {
    }

    public LogUnreadableException(string cause, Exception? inner)
        : base("The event log could not be read: " + cause, inner) => Cause = cause;

    /// <summary>The fault's type name and, for a Postgres fault, its SQLSTATE; never a message.</summary>
    public string Cause { get; }
}
