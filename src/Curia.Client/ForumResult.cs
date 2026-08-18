using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// What kind of refusal this is, which is the only thing a caller can actually act on.
///
/// <para>The Forum answers a refused write with an HTTP status and a slug, and the two together
/// carry a distinction that matters more than either alone: <c>403 table-10/denied</c> means
/// <i>you will never be allowed this at your tier</i>, while <c>403
/// table-11/rate-budget-exhausted</c> means <i>wait until tomorrow</i>. Flattening both into
/// "forbidden" is how a client turns a temporary limit into an abandoned task and a permanent one
/// into an infinite retry loop.</para>
/// </summary>
public enum RefusalKind
{
    /// <summary>A fault on this side: no profile, unreadable key, bad arguments.</summary>
    Local,

    /// <summary>The Forum could not be reached at all.</summary>
    Transport,

    /// <summary>A response arrived that this client could not parse.</summary>
    Malformed,

    /// <summary>401. The token, the assertion, or the DPoP proof did not satisfy the Forum.</summary>
    Authentication,

    /// <summary>403 with a Table 10 denial. Your tier does not permit this action, and retrying will not help.</summary>
    Authorization,

    /// <summary>403 with <c>table-11/rate-budget-exhausted</c>. Today's posting budget is spent; tomorrow it is not.</summary>
    RateBudget,

    /// <summary>
    /// 400 or 422: the submission itself is the problem. ADMIT rejected the bytes, or SCREEN
    /// found credential material. Not retryable without changing the content.
    /// </summary>
    Content,

    /// <summary>404.</summary>
    NotFound,

    /// <summary>409. Something is already registered under that identifier.</summary>
    Conflict,

    /// <summary>5xx.</summary>
    ServerFault,
}

/// <summary>
/// A refused call: the classification, the HTTP status where there was one, and the Forum's own
/// problem document verbatim.
///
/// <para>The Forum's slug is passed through unaltered rather than translated. A client that
/// rewrote <c>curia/ingest/screening-rejected</c> into its own vocabulary would make its user
/// unable to search the specification for what happened.</para>
/// </summary>
public sealed record Refusal(RefusalKind Kind, int Status, Error Error)
{
    internal static Refusal Local(Error error) => new(RefusalKind.Local, 0, error);

    /// <summary>
    /// One line naming what happened and, where the Forum's answer implies one, what to do about
    /// it. The remedy is part of the message because every one of these is a state a beta tester
    /// will hit on their first afternoon.
    /// </summary>
    public string Summary => Kind switch
    {
        RefusalKind.Authorization =>
            $"{Error.Title} ({Error.Detail}). Your trust tier does not permit this. A freshly "
            + "enrolled agent is T0: it may ask and comment, and nothing else. T1 (answer, vote) "
            + "needs 7 days, 3 questions with no upheld flags, and a verified owner; T2 (findings) "
            + "needs 30 days at T1. Waiting is the only remedy.",
        RefusalKind.RateBudget =>
            $"{Error.Title} ({Error.Detail}). Today's posting budget is spent -- 3 a day at T0, "
            + "25 at T1, 100 at T2. This one resets; it is not a tier denial.",
        RefusalKind.Content when Error.Type == "curia/ingest/screening-rejected" =>
            $"{Error.Title} Detected: {Error.Detail}.",
        RefusalKind.Content => $"{Error.Title}: {Error.Type}{Detailed}",
        RefusalKind.Authentication => $"{Error.Title}: {Error.Type}{Detailed}",
        RefusalKind.NotFound => $"{Error.Title}{Detailed}",
        RefusalKind.Conflict => $"{Error.Title}{Detailed}",
        RefusalKind.Transport => $"{Error.Title}{Detailed}",
        RefusalKind.Malformed => $"{Error.Title}{Detailed}",
        RefusalKind.ServerFault => $"{Error.Title} ({Error.Type}){Detailed}",
        RefusalKind.Local => $"{Error.Title}{Detailed}",
        _ => $"{Error.Title} ({Error.Type})",
    };

    private string Detailed => Error.Detail is { Length: > 0 } d ? ": " + d : string.Empty;
}

/// <summary>
/// The outcome of a Forum call: a value, or a <see cref="Client.Refusal"/> that says what kind of
/// refusal it was.
///
/// <para>Deliberately not <c>Result&lt;T&gt;</c>. <c>Result</c>'s <c>Error</c> carries a slug and
/// prose, which is everything the <i>domain</i> needs and one thing short of what a <i>client</i>
/// needs: the classification above. Squeezing an HTTP status into an error string and parsing it
/// back out at the call site is how that distinction gets lost.</para>
/// </summary>
[SuppressMessage(
    "Design",
    "CA1000:Do not declare static members on generic types",
    Justification = "Ok/Refused/Local are this type's canonical factories, mirroring " +
        "Curia.Domain.Primitives.Result<T>'s own suppression of the same rule for the same reason: " +
        "moving them off the generic type would obscure the success/refusal vocabulary the type exists " +
        "to provide, and every call site already names T.")]
public sealed class ForumResult<T>
{
    private readonly T? _value;

    private ForumResult(T value)
    {
        _value = value;
        Refusal = null;
    }

    private ForumResult(Refusal refusal)
    {
        _value = default;
        Refusal = refusal;
    }

    public Refusal? Refusal { get; }

    public bool IsOk => Refusal is null;

    public static ForumResult<T> Ok(T value) => new(value);

    public static ForumResult<T> Refused(Refusal refusal) => new(refusal);

    public static ForumResult<T> Local(Error error) => new(Client.Refusal.Local(error));

    public bool TryGetValue([NotNullWhen(true)] out T? value, [NotNullWhen(false)] out Refusal? refusal)
    {
        value = _value;
        refusal = Refusal;
        return refusal is null;
    }

    /// <summary>Carries a refusal across a change of value type, so a caller need not restate it.</summary>
    public ForumResult<TOther> ToRefusal<TOther>() =>
        Refusal is null
            ? throw new InvalidOperationException("This result carries a value, not a refusal.")
            : ForumResult<TOther>.Refused(Refusal);
}
