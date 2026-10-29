using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Npgsql;

namespace Curia.Infrastructure;

/// <summary>
/// <see cref="IRevisionGate"/> (R8.77; errata G45), declared as a seam by the plan's Task 0 and not yet a gate: every entry is admitted
/// and its hold releases nothing. Task 1 replaces the body with the flag gate's shape (a try-only transaction-scoped advisory lock on the
/// gate pool, key <c>&lt;schema&gt;:revision_prev:&lt;digest&gt;</c>); until then the route does not call it either, so nothing in the Forum changes.
/// </summary>
public sealed class PostgresRevisionGate : IRevisionGate
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;

    public PostgresRevisionGate(NpgsqlDataSource dataSource, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        _dataSource = dataSource;
        _schema = schema;
    }

    /// <summary>The advisory-lock key a revision of <paramref name="prevDigest"/> holds: one key per digest per schema, in a prefix of its own.</summary>
    public static string RevisionLockKey(string schema, string prevDigest) => schema + ":revision_prev:" + prevDigest;

    /// <summary>The data source the gate will take its locks on; read so the seam holds what Task 1 needs.</summary>
    internal (NpgsqlDataSource DataSource, string Schema) Target => (_dataSource, _schema);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The hold is the result: the caller owns it and disposes it.")]
    public Task<Result<IAsyncDisposable>> TryEnterAsync(string prevDigest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prevDigest);

        return Task.FromResult(Result<IAsyncDisposable>.Ok(new AdmittedHold()));
    }

    private sealed class AdmittedHold : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
