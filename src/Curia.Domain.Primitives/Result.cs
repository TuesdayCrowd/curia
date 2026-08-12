using System.Diagnostics.CodeAnalysis;

namespace Curia.Domain.Primitives;

/// <summary>
/// Domain-owned fallibility (CS-10). A signature that fails to verify is a value,
/// not an exception, because the security suite asserts on it in a hundred tests.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "Ok/Fail are the canonical Result<T> factory methods (CS-10); moving them off " +
        "the generic type would obscure the success/failure vocabulary this library exists to provide.")]
[SuppressMessage(
    "Usage",
    "CA1815:Override equals and operator equals on value types",
    Justification = "Result<T> is a control-flow value consumed via Match/Map/Bind, not compared " +
        "for equality; adding equality members would grow the API beyond what CS-10 specifies.")]
public readonly struct Result<T>
{
    private readonly T _value;
    private readonly Error? _error;
    private readonly bool _initialized;

    private Result(T value) { _value = value; _error = null; _initialized = true; }
    private Result(Error error) { _value = default!; _error = error; _initialized = true; }

    public bool IsOk
    {
        get
        {
            EnsureInitialized();
            return _error is null;
        }
    }

    public static Result<T> Ok(T value) => new(value);
    public static Result<T> Fail(Error error) => new(error);

    public TOut Match<TOut>(Func<T, TOut> ok, Func<Error, TOut> fail)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(ok);
        ArgumentNullException.ThrowIfNull(fail);
        return _error is null ? ok(_value) : fail(_error);
    }

    public Result<TNext> Map<TNext>(Func<T, TNext> f)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(f);
        return _error is null ? Result<TNext>.Ok(f(_value)) : Result<TNext>.Fail(_error);
    }

    public Result<TNext> Bind<TNext>(Func<T, Result<TNext>> f)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(f);
        return _error is null ? f(_value) : Result<TNext>.Fail(_error);
    }

    /// <summary>
    /// Re-types a failure so it can propagate through a signature returning a different T.
    /// Throws when called on a success, because that is a caller bug, not a domain failure.
    /// </summary>
    public Result<TOther> ToFailure<TOther>()
    {
        EnsureInitialized();
        return _error is null
            ? throw new InvalidOperationException("ToFailure called on a successful result")
            : Result<TOther>.Fail(_error);
    }

    /// <summary>Test and adapter convenience; prefer <see cref="Match{TOut}"/> in domain code.</summary>
    public bool TryGetValue(out T value, out Error? error)
    {
        EnsureInitialized();
        value = _value;
        error = _error;
        return _error is null;
    }

    /// <summary>
    /// Rejects <c>default(Result&lt;T&gt;)</c> — an uninitialized struct, e.g. from a never-assigned
    /// field, <c>new Result&lt;T&gt;[n]</c>, or a dictionary miss — which would otherwise be
    /// indistinguishable from a real <see cref="Ok"/> because its backing <c>_error</c> field is
    /// also null. In a type whose entire premise is "failure is a value the caller must handle,"
    /// the zero value must not silently read as success, so this is a caller bug, not a domain
    /// failure, exactly like the success case in <see cref="ToFailure{TOther}"/>.
    /// </summary>
    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(
                $"{nameof(Result<T>)}<{typeof(T).Name}> was never initialized. " +
                $"Use {nameof(Result<T>)}<{typeof(T).Name}>.{nameof(Ok)} or .{nameof(Fail)} " +
                "instead of default/new().");
        }
    }
}
