using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Ingest;

/// <summary>
/// R6.56: decision and append are one act, so an append that lost the race to another entry in the
/// author's stream (<see cref="DomainErrors.ConditionConflictType"/>) is decided again from VERIFY, on
/// the stream as it now stands. Three attempts, then <see cref="IngestErrors.Contended"/>: a stream
/// moving that fast is the Forum's to refuse, not the caller's to be told was a bad key.
/// </summary>
public static class IngestAttempts
{
    /// <summary>How many times VERIFY through PERSIST is run before the submission is refused as contended.</summary>
    public const int Attempts = 3;

    /// <summary>
    /// Runs <paramref name="attempt"/> until it answers anything but a condition conflict. Every other
    /// outcome, a success or a refusal, is returned as it came.
    /// </summary>
    public static async Task<Result<T>> RunAsync<T>(
        Func<CancellationToken, Task<Result<T>>> attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        for (var i = 0; i < Attempts; i++)
        {
            var result = await attempt(cancellationToken).ConfigureAwait(false);
            if (result.TryGetValue(out _, out var error)
                || !string.Equals(error!.Type, DomainErrors.ConditionConflictType, StringComparison.Ordinal))
                return result;
        }

        return Result<T>.Fail(IngestErrors.Contended());
    }
}
