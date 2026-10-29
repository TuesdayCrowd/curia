using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Moderation;

/// <summary>
/// The refusals of an acceptance that are the use case's own (R8.79, errata G46). Returned by the loop in <see cref="AcceptAnswer"/> past its bound.
/// </summary>
public static class AcceptErrors
{
    /// <summary>The slug of <see cref="Contended"/>, which the accept route maps to 503.</summary>
    public const string ContendedType = "curia/accept/contended";

    /// <summary>
    /// R8.79: the thread's stream moved under every attempt to decide and append. Nothing was recorded; the acceptance may be sent again.
    /// The detail is the Forum's own thread id and nothing the store said.
    /// </summary>
    public static Error Contended(PostId threadRoot)
    {
        ArgumentNullException.ThrowIfNull(threadRoot);
        return new Error(
            ContendedType,
            "The thread's acceptances changed under every attempt; nothing was recorded",
            $"thread={threadRoot.Value}");
    }
}
